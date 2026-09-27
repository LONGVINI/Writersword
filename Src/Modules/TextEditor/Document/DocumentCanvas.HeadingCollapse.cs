using SkiaSharp;
using System;
using System.Collections.Generic;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Services;
using Writersword.Modules.TextEditor.ViewModels;
using Writersword.Modules.TextEditor.ViewModels.Blocks;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Стрелка свёртки у заголовка — как в Word.
    ///
    /// Наведёшь указатель на заголовок — слева от него, на поле листа, появляется
    /// маленький треугольник. Нажатие сворачивает раздел: всё, что идёт под заголовком
    /// до следующего заголовка того же или более высокого уровня, пропадает с листа, а
    /// треугольник поворачивается остриём вправо и остаётся виден и без наведения —
    /// иначе свёрнутый раздел было бы не отличить от пустого. Повторное нажатие
    /// разворачивает.
    ///
    /// Свёртка — вид, а не правка (DocumentViewModel, Services.HeadingCollapseService).
    /// Скрытые блоки просто не раскладываются: лист за свёрнутым разделом подтягивается
    /// вверх, страницы пересчитываются, как в Word в режиме разметки.
    ///
    /// Каретка в скрытом тексте не бывает:
    /// — сворачивая раздел, в котором стоит каретка, её уводят в конец заголовка;
    /// — когда каретку ведут внутрь свёрнутого раздела (поиск, навигатор, отмена, Enter
    ///   в конце свёрнутого заголовка), раздел раскрывается сам.
    ///
    /// Треугольники рисуются поверх готового снимка листа, как выделение и каретка:
    /// наведение меняет только их, и перерисовывать ради него весь лист незачем.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        // Зазор между треугольником и левым краем текста заголовка, pt.
        private const float HeadingToggleGapPt = 4f;

        // Размер треугольника, pt: от кегля заголовка, в разумных границах.
        private const float HeadingToggleMinPt = 5f;
        private const float HeadingToggleMaxPt = 9f;

        // Запас попадания вокруг треугольника, pt: сам он мал, и целиться в него
        // вплотную неудобно.
        private const float HeadingToggleHitPadPt = 3f;

        private static readonly SKColor HeadingToggleColor = new(0x80, 0x80, 0x80);
        private static readonly SKColor HeadingToggleHotColor = new(0x2B, 0x57, 0x9A);

        private static readonly HashSet<Guid> NoCollapsedHeadings = new();

        // Блоки, скрытые свёрнутыми заголовками, — для прохода раскладки. null — скрытого нет.
        private HashSet<BlockModel>? _collapsedBlocks;

        // Свёрнутые заголовки для отрисовки. Ссылка подменяется целиком на UI-потоке,
        // поток отрисовки только читает её.
        private HashSet<Guid> _collapsedHeadingIdsForRender = NoCollapsedHeadings;

        // Заголовок под указателем и то, стоит ли указатель на самом треугольнике.
        private ParagraphViewModel? _headingHoverVm;
        private bool _headingToggleHot;

        /// <summary>
        /// Стрелки свёртки работают в правке. В чтении и в книге их нет: там лист
        /// показывают целиком, и свёртка правки на него не переносится.
        /// </summary>
        private bool HeadingTogglesAvailable => DocVm is not null && !ReadingActive && !SpreadMode;

        /// <summary>Скрыт ли блок свёрнутым заголовком в текущей раскладке.</summary>
        private bool IsCollapsedBlock(BlockModel block)
            => _collapsedBlocks is { } hidden && hidden.Contains(block);

        // ── Что скрыто ────────────────────────────────────────────────────

        /// <summary>
        /// Пересчитывает скрытые блоки перед проходом раскладки.
        ///
        /// Забытые заголовки (удалены или перестали быть заголовками) выбрасываются.
        /// Раздел, в который попал абзац каретки, раскрывается: так бывает, когда
        /// каретку привела туда правка — Enter в конце свёрнутого заголовка, слияние
        /// абзацев через границу раздела.
        /// </summary>
        private void RefreshCollapsedBlocks()
        {
            var docVm = DocVm;
            if (docVm is null || !docVm.HasCollapsedHeadings)
            {
                _collapsedBlocks = null;
                _collapsedHeadingIdsForRender = NoCollapsedHeadings;
                return;
            }

            docVm.PruneCollapsedHeadingsSilently();

            var active = docVm.ActiveParagraph?.Model;
            if (active is not null && docVm.RevealBlockSilently(active))
                _logger.Debug("[OUTLINE] Каретка в свёрнутом разделе — раздел раскрыт");

            _collapsedHeadingIdsForRender = docVm.HasCollapsedHeadings
                ? docVm.CollapsedHeadingIds
                : NoCollapsedHeadings;

            if (ReadingActive || !docVm.HasCollapsedHeadings)
            {
                _collapsedBlocks = null;
                return;
            }

            var hidden = HeadingCollapseService.HiddenBlocks(docVm.Document, _collapsedHeadingIdsForRender);
            _collapsedBlocks = hidden.Count > 0 ? new HashSet<BlockModel>(hidden.Keys) : null;
        }

        /// <summary>
        /// Каретку ведут в абзац. Если он внутри свёрнутого раздела, раздел
        /// раскрывается и раскладка пересобирается — иначе каретке негде встать.
        /// </summary>
        /// <returns>true — раздел раскрыт, раскладка пересобрана.</returns>
        private bool RevealCollapsedParagraph(ParagraphViewModel? pvm)
        {
            var docVm = DocVm;
            if (docVm is null || pvm?.Model is null || !docVm.HasCollapsedHeadings) return false;
            if (!docVm.RevealBlockSilently(pvm.Model)) return false;

            _logger.Debug("[OUTLINE] Переход в свёрнутый раздел — раздел раскрыт");

            RebuildLayouts();
            InvalidateMeasure();
            InvalidateFull();
            return true;
        }

        /// <summary>
        /// Набор свёрнутых заголовков изменился. Раскладка пересобирается, каретка и
        /// выделение возвращаются на свои абзацы: номера слайсов после свёртки другие.
        /// </summary>
        private void OnHeadingCollapseChanged()
        {
            if (DocVm is null) return;

            var anchor = CaptureCaretAnchor();

            RebuildLayouts();
            ApplyCaretAnchor(anchor);
            UpdatePreferredX();

            // Каретка остаётся где была, и лист к ней не едет. ResetCaret здесь не
            // годится: он заодно прокручивает к каретке, а человек, нажавший стрелку,
            // смотрит на заголовок, и каретка у него часто совсем в другом месте —
            // лист уезжал к ней наверх.
            RestartCaretBlinkWithoutScroll();

            InvalidateMeasure();
            InvalidateFull();
        }

        /// <summary>
        /// Перезапуск мигания каретки без прокрутки к ней — то же, что ResetCaret,
        /// кроме прокрутки.
        /// </summary>
        private void RestartCaretBlinkWithoutScroll()
        {
            _caretVisible = true;
            _caretTimer.Stop();
            _caretTimer.Start();
            NotifyInputMethod();
        }

        // ── Свернуть и развернуть ─────────────────────────────────────────

        /// <summary>
        /// Нажатие по треугольнику. Координаты логические, как у всех попаданий.
        /// </summary>
        /// <returns>true — нажатие было по треугольнику и обработано.</returns>
        private bool HeadingTogglePointerPressed(float xPt, float yPt)
        {
            var vm = FindHeadingUnderPointer(xPt, yPt, out bool onToggle);
            if (vm is null || !onToggle) return false;

            ToggleHeadingCollapse(vm);
            return true;
        }

        private void ToggleHeadingCollapse(ParagraphViewModel headingVm)
        {
            var docVm = DocVm;
            var heading = headingVm.Model;
            if (docVm is null || heading is null) return;

            Guid id = headingVm.BlockId;
            bool collapse = !docVm.IsHeadingCollapsed(id);

            if (collapse)
            {
                var (start, end) = HeadingCollapseService.SectionRange(docVm.Document, heading);

                // Под заголовком сразу идёт заголовок того же уровня — скрывать пока
                // нечего. Свёртка всё равно запоминается и скроет то, что под заголовком
                // напишут позже.
                if (end > start)
                    MoveCaretOutOfSection(docVm, headingVm, start, end);

                // Номера страниц снимаются, пока раскладка ещё полная.
                if (!docVm.HasCollapsedHeadings)
                    docVm.CollapsedPageMap = LiveBlockPageNumbers();
            }

            _logger.Debug(
                "[OUTLINE] Заголовок «{Text}» {Action}",
                Shorten(headingVm.PlainText), collapse ? "свёрнут" : "развёрнут");

            docVm.SetHeadingCollapsed(id, collapse);
        }

        /// <summary>
        /// Каретка или выделение внутри сворачиваемого раздела уходят в конец
        /// заголовка — как в Word. Иначе после свёртки каретке негде было бы стоять.
        /// </summary>
        private void MoveCaretOutOfSection(
            DocumentViewModel docVm, ParagraphViewModel headingVm, int sectionStart, int sectionEnd)
        {
            var layouts = _layouts;
            var blocks = docVm.Document.Sections[0].Blocks;

            int headingLast = -1;
            for (int i = 0; i < layouts.Count; i++)
            {
                var pl = layouts[i];
                if (pl.Cell is null && ReferenceEquals(pl.Vm, headingVm)) headingLast = i;
            }
            if (headingLast < 0) return;

            // Раздел в раскладке — от слайса после заголовка до первого слайса блока,
            // которым раздел кончается. Слайсы лежат в порядке документа, ячейки таблиц
            // — на месте своих таблиц, поэтому сравнения номеров хватает.
            int sectionEndSlice = layouts.Count;
            if (sectionEnd < blocks.Count && blocks[sectionEnd] is ParagraphBlock endPara)
            {
                for (int i = headingLast + 1; i < layouts.Count; i++)
                {
                    var pl = layouts[i];
                    if (pl.Cell is null && ReferenceEquals(pl.Vm.Model, endPara))
                    {
                        sectionEndSlice = i;
                        break;
                    }
                }
            }

            bool InSection(int sliceIdx) => sliceIdx > headingLast && sliceIdx < sectionEndSlice;

            bool caretInside = InSection(_caretPara);
            bool selectionInside = HasSel() && (InSection(_selStartPara) || InSection(_selEndPara));

            if (caretInside || selectionInside)
            {
                _caretPara = headingLast;
                _caretChar = headingVm.PlainText?.Length ?? 0;
                _caretLineHint = -1;
                SnapCaretToCorrectSlice();
                UpdatePreferredX();
                SyncSel();

                // Заголовок и так перед глазами — по нему только что нажали.
                RestartCaretBlinkWithoutScroll();

                docVm.SetActiveParagraph(headingVm);
                UpdateSelectionContext();
                return;
            }

            // Каретка снаружи, но абзац каретки у модели мог остаться внутри раздела
            // (каретка в ячейке таблицы, выход из таблицы). Пересборка приняла бы его
            // за переход в свёрнутый текст и тут же раскрыла бы раздел обратно.
            var activeBlock = docVm.ActiveParagraph?.Model;
            if (activeBlock is null) return;

            int activeIdx = blocks.IndexOf(activeBlock);
            if (activeIdx >= sectionStart && activeIdx < sectionEnd)
                docVm.SetActiveParagraph(headingVm);
        }

        private static string Shorten(string? text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Length > 40 ? text.Substring(0, 40) + "…" : text;
        }

        // ── Наведение ─────────────────────────────────────────────────────

        /// <summary>
        /// Обновляет заголовок под указателем. Координаты логические. Меняются только
        /// треугольники поверх снимка — кадр идёт коротким путём, без перерисовки листа.
        /// </summary>
        private void UpdateHeadingToggleHover(float xPt, float yPt)
        {
            var vm = FindHeadingUnderPointer(xPt, yPt, out bool onToggle);
            if (ReferenceEquals(vm, _headingHoverVm) && onToggle == _headingToggleHot) return;

            _headingHoverVm = vm;
            _headingToggleHot = onToggle;

            _caretOnlyRedraw = true;
            InvalidateVisual();
        }

        /// <summary>Указатель ушёл с полотна — треугольник наведения гаснет.</summary>
        private void ClearHeadingToggleHover()
        {
            if (_headingHoverVm is null && !_headingToggleHot) return;

            _headingHoverVm = null;
            _headingToggleHot = false;

            _caretOnlyRedraw = true;
            InvalidateVisual();
        }

        /// <summary>
        /// Заголовок под указателем — над его строками или слева от них, на поле листа.
        /// onToggle — указатель на самом треугольнике.
        /// </summary>
        private ParagraphViewModel? FindHeadingUnderPointer(float xPt, float yPt, out bool onToggle)
        {
            onToggle = false;
            if (!HeadingTogglesAvailable) return null;

            var docVm = DocVm!;
            List<ParaLayout> layouts;
            List<PageRect> pages;
            lock (_renderLock)
            {
                layouts = _layouts;
                pages = _pages;
            }

            bool pageLayout = docVm.ViewMode == EditorViewMode.Page;

            for (int i = 0; i < layouts.Count; i++)
            {
                var pl = layouts[i];
                if (pl.Cell is not null) continue;
                if (yPt < pl.Ypt || yPt > pl.Ypt + pl.HeightPt) continue;

                if (pageLayout && pl.PageIndex >= 0 && pl.PageIndex < pages.Count)
                {
                    var page = pages[pl.PageIndex];
                    if (xPt < page.PadLeftPt || xPt > page.PadLeftPt + page.WidthPt) continue;
                }

                var para = pl.Vm.Model;
                if (para is null) return null;
                if (HeadingCollapseService.LevelOf(para, docVm.Document) <= 0) return null;

                // Треугольник стоит у первой строки заголовка. Заголовок, разрезанный
                // листом, продолжения своего треугольника не носит.
                if (pl.LineFrom == 0 && HeadingToggleRect(pl) is { } rect)
                {
                    rect.Inflate(HeadingToggleHitPadPt, HeadingToggleHitPadPt);
                    onToggle = rect.Contains(xPt, yPt);
                }

                return pl.Vm;
            }

            return null;
        }

        /// <summary>
        /// Треугольник заголовка в логических координатах: слева от текста, по высоте —
        /// на середине заглавных букв первой строки. null — у слайса нет первой строки.
        /// </summary>
        private SKRect? HeadingToggleRect(ParaLayout pl)
        {
            if (pl.LineFrom != 0) return null;

            var layout = pl.Layout
                ?? (_layoutCache.TryGetValue(pl.Vm, out var cached) ? cached.Layout : null);
            if (layout is null || layout.Lines.Count == 0) return null;

            var line = layout.Lines[0];
            float ascent = line.TextAscentPt > 0.5f ? line.TextAscentPt : line.Height * 0.7f;

            // Верх первой строки слайса — pl.Ypt (тот же уговор, что у каретки).
            float centerY = pl.Ypt + line.Baseline - ascent * 0.36f;
            float size = Math.Clamp(ascent * 0.62f, HeadingToggleMinPt, HeadingToggleMaxPt);

            // Левый край текста: отступ абзаца, а при выступе первой строки — она.
            float textLeft = pl.AbsXPt + layout.LeftIndentPt + Math.Min(layout.FirstLineIndentPt, 0f);
            float right = textLeft - HeadingToggleGapPt;

            return new SKRect(right - size, centerY - size / 2f, right, centerY + size / 2f);
        }

        // ── Отрисовка ─────────────────────────────────────────────────────

        /// <summary>
        /// Треугольники свёрнутых заголовков и заголовка под указателем. Зовётся поверх
        /// снимка листа на всех путях кадра — рядом с выделением. Канвас уже в масштабе;
        /// сдвиги листа те же, что у выделения.
        /// </summary>
        private void DrawHeadingToggles(
            SKCanvas canvas,
            List<ParaLayout> layouts,
            List<PageRect> pages,
            double canvasWidth)
        {
            if (!HeadingTogglesAvailable) return;
            if (layouts.Count == 0) return;

            var collapsedIds = _collapsedHeadingIdsForRender;
            var hover = _headingHoverVm;
            bool hot = _headingToggleHot;
            if (collapsedIds.Count == 0 && hover is null) return;

            bool pageLayout = DocVm?.ViewMode == EditorViewMode.Page;

            float shiftPt = 0f;
            int firstPage = 0;
            int lastPage = int.MaxValue;
            float viewTopPt = float.MinValue;
            float viewBotPt = float.MaxValue;

            if (pageLayout)
            {
                // Тот же сдвиг до-центрирования, что и у листа: во время жеста масштаба
                // лист держится по центру без пересборки раскладки.
                if (_pagesPerRow <= 1)
                {
                    float canvasWPt = (float)(canvasWidth * PxToPt);
                    float curPageXPt = Math.Max((canvasWPt - GetPageWidthPt()) / 2f, 0f);
                    shiftPt = curPageXPt - _layoutPageXPt;
                }

                (firstPage, lastPage) = GetVisiblePageRange(pages);
            }
            else
            {
                float zoom = (float)Math.Max(Zoom, 0.01);
                viewTopPt = (float)(_scrollOffsetY / zoom * PxToPt) - FallbackLinePt * 5f;
                viewBotPt = (float)((_scrollOffsetY + Math.Max(_viewportHeight, 100)) / zoom * PxToPt)
                    + FallbackLinePt * 5f;
            }

            for (int i = 0; i < layouts.Count; i++)
            {
                var pl = layouts[i];
                if (pl.Cell is not null || pl.LineFrom != 0) continue;

                if (pageLayout)
                {
                    if (pl.PageIndex < firstPage || pl.PageIndex > lastPage) continue;
                }
                else
                {
                    if (pl.Ypt + pl.HeightPt < viewTopPt) continue;
                    if (pl.Ypt > viewBotPt) break;
                }

                bool collapsed = collapsedIds.Count > 0 && collapsedIds.Contains(pl.Vm.BlockId);
                bool hovered = ReferenceEquals(pl.Vm, hover);
                if (!collapsed && !hovered) continue;

                if (HeadingToggleRect(pl) is not { } rect) continue;

                if (pageLayout)
                {
                    if (_pagesPerRow <= 1)
                    {
                        rect.Offset(shiftPt, 0f);
                    }
                    else
                    {
                        var (dx, dy) = PageVisualDelta(pl.PageIndex, pages);
                        rect.Offset(dx, dy);
                    }
                }

                DrawHeadingToggle(canvas, rect, collapsed, hovered && hot);
            }
        }

        /// <summary>
        /// Развёрнутый заголовок — закрашенный уголок остриём вниз-вправо, свёрнутый —
        /// контурный треугольник остриём вправо. Под указателем — цветом выделения.
        /// </summary>
        private static void DrawHeadingToggle(SKCanvas canvas, SKRect rect, bool collapsed, bool hot)
        {
            var color = hot ? HeadingToggleHotColor : HeadingToggleColor;

            using var path = new SKPath();

            if (collapsed)
            {
                float inset = rect.Width * 0.15f;
                path.MoveTo(rect.Left + inset, rect.Top);
                path.LineTo(rect.Right, rect.MidY);
                path.LineTo(rect.Left + inset, rect.Bottom);
                path.Close();

                using var paint = new SKPaint
                {
                    IsAntialias = true,
                    Color = color,
                    Style = hot ? SKPaintStyle.StrokeAndFill : SKPaintStyle.Stroke,
                    StrokeWidth = 0.9f,
                    StrokeJoin = SKStrokeJoin.Round
                };
                canvas.DrawPath(path, paint);
            }
            else
            {
                path.MoveTo(rect.Right, rect.Top);
                path.LineTo(rect.Right, rect.Bottom);
                path.LineTo(rect.Left, rect.Bottom);
                path.Close();

                using var paint = new SKPaint
                {
                    IsAntialias = true,
                    Color = color,
                    Style = SKPaintStyle.Fill
                };
                canvas.DrawPath(path, paint);
            }
        }
    }
}
