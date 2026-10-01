using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Rendering;
using Writersword.Modules.TextEditor.Services;
using Writersword.Modules.TextEditor.ViewModels;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// PDF из листов полотна — ровно то, что видно в режиме страниц.
    ///
    /// Прежний экспорт в PDF верстал документ заново своим упрощённым движком: свои
    /// высоты строк, свой разбор стилей, табуляция пробелами, без рамок и заливок
    /// абзацев, без разрядки, со своими счётчиками списков. Строки и страницы в файле
    /// поэтому расходились с листом на экране уже на первых страницах, а оглавление
    /// выходило без отточий и без номеров у правого края.
    ///
    /// Здесь PDF не верстается вовсе: каждая страница файла — это лист полотна,
    /// нарисованный тем же проходом содержимого, что и на экране (текст, рамки,
    /// маркеры списков, таблицы, картинки, фигуры, колонтитулы), только в документ
    /// PDF и без того, что бывает лишь при правке: выделения, каретки, рамок и ручек
    /// выбранных объектов, непечатаемых знаков, меток правил колонтитулов, пометок
    /// «объект мимо листа», подложки оглавления под кареткой и цвета вида рабочей
    /// области. Картинки берутся из файлов проекта в исходном разрешении, а не
    /// уменьшенными экранными копиями.
    ///
    /// Разделы под свёрнутыми заголовками на время выгрузки раскрываются: свёртка —
    /// вид правки, а не содержимое книги. После выгрузки свёртка и место каретки
    /// возвращаются.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        // Поток, который сейчас рисует листы в PDF. 0 — выгрузки нет.
        //
        // Признак привязан к потоку намеренно: кадры экрана рисует свой поток и в эти
        // же мгновения, и для него всё должно оставаться как при правке — иначе на
        // кадр пропадали бы выделение и каретка, а снимок листа запекался бы без них.
        private int _exportThreadId;

        // Картинки выгрузки по имени файла: исходные байты проекта, а не экранные копии.
        private Func<string, SKImage?>? _exportImageResolver;

        // Свёрнутые разделы на время выгрузки раскладываются, как развёрнутые.
        private bool _exportIgnoreCollapse;

        // Скрытый текст на время выгрузки прячется, даже при показанных знаках ¶:
        // в PDF, как и на печать у Word, он не идёт.
        private bool _exportHideHiddenText;

        // Сколько раундов «дождаться прогрева — пересобрать» допускается при выгрузке.
        private const int ExportLayoutRounds = 4;

        // Шаг ожидания прогрева раскладки перед выгрузкой, мс.
        private const int ExportWarmupPollMs = 30;

        // Разрешение, в котором PDF растрирует то, что не выражается векторно.
        private const float ExportPdfRasterDpi = 300f;

        /// <summary>Идёт ли выгрузка листов в PDF в текущем потоке.</summary>
        private bool ExportPassActive
            => _exportThreadId != 0 && _exportThreadId == Environment.CurrentManagedThreadId;

        /// <summary>Выгружать листы можно только из режима страниц правки.</summary>
        private bool CanExportPagesAsPdf
            => DocVm is not null
               && DocVm.ViewMode == EditorViewMode.Page
               && !SpreadMode
               && !ReadingActive;

        /// <summary>Отдаёт вью-модели документа выгрузку листов в PDF.</summary>
        private void WirePdfExportDelegate()
        {
            if (DocVm is null) return;

            DocVm.ExportPdfFromPagesDelegate = ExportPdfFromPagesAsync;
        }

        /// <summary>
        /// Пишет листы полотна в PDF.
        /// </summary>
        /// <param name="outputPath">Путь к создаваемому файлу .pdf.</param>
        /// <param name="resolveImage">Байты картинки по имени её файла в проекте.</param>
        /// <returns>
        /// Итог выгрузки; null — листами выгрузить нельзя (не режим страниц, раскладки
        /// нет или она не успела собраться), и вызывающий выгружает прежним способом.
        /// </returns>
        private async Task<ExportResult?> ExportPdfFromPagesAsync(
            string outputPath, Func<string, byte[]?> resolveImage)
        {
            if (!CanExportPagesAsPdf) return null;

            var docVm = DocVm!;
            bool hadCollapsed = docVm.HasCollapsedHeadings;
            CaretAnchor? collapseAnchor = null;

            if (hadCollapsed)
            {
                collapseAnchor = CaptureCaretAnchor();
                _exportIgnoreCollapse = true;
            }

            _exportHideHiddenText = true;
            bool hiddenRelaid = ApplyHiddenTextRule();

            try
            {
                if (!await SettleLayoutForExportAsync(docVm, hadCollapsed || hiddenRelaid)) return null;

                // Номера страниц в оглавлении — по той раскладке, которая уйдёт в файл.
                // Проход по ним мог прийти, пока шёл прогрев, и взять прежние слайсы.
                FlushTocPageNumbersNow();

                if (!await SettleLayoutForExportAsync(docVm, false)) return null;

                return RenderPagesToPdf(outputPath, resolveImage);
            }
            finally
            {
                // Скрытый текст возвращается к правилу правки: при показанных знаках ¶
                // он снова виден.
                _exportHideHiddenText = false;
                bool hiddenRestore = ReferenceEquals(DocVm, docVm) && ApplyHiddenTextRule();

                if (hadCollapsed)
                {
                    _exportIgnoreCollapse = false;

                    if (ReferenceEquals(DocVm, docVm))
                    {
                        RebuildLayouts();
                        if (collapseAnchor is not null) ApplyCaretAnchor(collapseAnchor);
                        UpdatePreferredX();
                        hiddenRestore = false;
                    }
                }

                if (hiddenRestore) RebuildLayouts();

                InvalidateMeasure();
                InvalidateFull();
            }
        }

        /// <summary>
        /// Доводит раскладку до полной и актуальной: дожидается порционного прогрева
        /// кеша и пересобирает листы, если отпечаток раскладки устарел.
        /// </summary>
        /// <param name="docVm">Документ, который выгружается.</param>
        /// <param name="forceRebuild">Пересобрать, даже если отпечаток совпадает.</param>
        /// <returns>false — раскладку собрать не удалось.</returns>
        private async Task<bool> SettleLayoutForExportAsync(DocumentViewModel docVm, bool forceRebuild)
        {
            for (int round = 0; round < ExportLayoutRounds; round++)
            {
                if (!await WaitLayoutWarmupForExportAsync(docVm)) return false;

                if (!forceRebuild && !_marginSettlePending && LayoutsMatchCurrentState())
                    return true;

                RebuildLayouts();

                // Пересборка ушла в прогрев (холодный кеш большого документа): после
                // прогрева нужен ещё один полный проход — его и делает следующий раунд.
                forceRebuild = _layoutWarmupActive;

                if (!_layoutWarmupActive && LayoutsMatchCurrentState())
                    return true;
            }

            return false;
        }

        /// <summary>Ждёт конца прогрева кеша раскладки, не держа поток интерфейса.</summary>
        private async Task<bool> WaitLayoutWarmupForExportAsync(DocumentViewModel docVm)
        {
            while (_layoutWarmupActive)
            {
                if (!ReferenceEquals(DocVm, docVm)) return false;

                // Скрытый канвас прогрев не продвигает — ждать было бы нечего.
                if (!IsEffectivelyVisible) return false;

                await Task.Delay(ExportWarmupPollMs);
            }

            return ReferenceEquals(DocVm, docVm) && CanExportPagesAsPdf;
        }

        /// <summary>Рисует все листы текущей раскладки в файл PDF.</summary>
        private ExportResult? RenderPagesToPdf(string outputPath, Func<string, byte[]?> resolveImage)
        {
            List<ParaLayout> layouts;
            List<PageRect> pages;
            List<TableEntry> tables;
            List<ImageEntry> images;

            lock (_renderLock)
            {
                layouts = _layouts;
                pages = _pages;
                tables = _tables;
                images = _images;
            }

            if (pages.Count == 0 || layouts.Count == 0) return null;

            var decoded = new Dictionary<string, SKImage?>(StringComparer.OrdinalIgnoreCase);
            SKImage? ResolveExportImage(string fileName)
            {
                if (decoded.TryGetValue(fileName, out var known)) return known;

                SKImage? image = null;
                try
                {
                    var bytes = resolveImage(fileName);
                    if (bytes is { Length: > 0 })
                    {
                        // Закодированные данные, а не пиксели: JPEG уходит в PDF как
                        // есть, без перекодирования и без потери разрешения.
                        using var data = SKData.CreateCopy(bytes);
                        image = SKImage.FromEncodedData(data);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "[EXPORT] Картинка {File} не прочиталась для PDF", fileName);
                    image = null;
                }

                decoded[fileName] = image;
                return image;
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            bool written = false;

            using var neutral = NeutralRenderScope.Begin();

            _exportImageResolver = ResolveExportImage;
            _exportThreadId = Environment.CurrentManagedThreadId;

            try
            {
                using (var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    // От значений по умолчанию, а не с нуля: у пустых сведений качество
                    // сжатия картинок равно нулю, и перекодированный растр выходил кашей.
                    var metadata = SKDocumentPdfMetadata.Default;
                    metadata.Title = DocVm?.Document.Title ?? string.Empty;
                    metadata.Creator = "Writersword";
                    metadata.Producer = "Writersword";
                    metadata.Creation = DateTime.Now;
                    metadata.Modified = DateTime.Now;
                    metadata.RasterDpi = ExportPdfRasterDpi;

                    using var pdf = SKDocument.CreatePdf(stream, metadata)
                        ?? throw new InvalidOperationException("Не удалось создать PDF-документ (SkiaSharp вернул null).");

                    lock (ContentPassLock)
                    {
                        for (int pi = 0; pi < pages.Count; pi++)
                        {
                            var page = pages[pi];
                            var pageCanvas = pdf.BeginPage(page.WidthPt, page.HeightPt);

                            pageCanvas.Save();
                            pageCanvas.Translate(-page.PadLeftPt, -page.Ypt);
                            pageCanvas.ClipRect(new SKRect(
                                page.PadLeftPt, page.Ypt,
                                page.PadLeftPt + page.WidthPt, page.Ypt + page.HeightPt));

                            RenderPageContent(pageCanvas, layouts, pages, tables, images, pi, pi, false);

                            pageCanvas.Restore();
                            pdf.EndPage();
                        }
                    }

                    pdf.Close();
                }

                written = true;

                _logger.Information(
                    "[EXPORT] PDF из листов полотна: {Pages} стр. за {Ms} мс → {Path}",
                    pages.Count, stopwatch.ElapsedMilliseconds, outputPath);

                return ExportResult.Ok(outputPath);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[EXPORT] PDF из листов полотна не записан: {Path}", outputPath);
                return ExportResult.Fail(ex.Message);
            }
            finally
            {
                _exportThreadId = 0;
                _exportImageResolver = null;

                foreach (var image in decoded.Values)
                    image?.Dispose();

                if (!written)
                {
                    try
                    {
                        if (File.Exists(outputPath)) File.Delete(outputPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "[EXPORT] Недописанный PDF не удалился: {Path}", outputPath);
                    }
                }
            }
        }
    }
}
