using System;
using System.Linq;
using System.Threading.Tasks;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Views.Dialogs;

namespace Writersword.Modules.TextEditor.ViewModels
{
    /// <summary>
    /// Диаграммы: вставка новой и правка любой диаграммы документа, в том числе
    /// пришедшей из Word.
    ///
    /// Диаграмма живёт в картинке (<see cref="ImageBlock.Chart"/>): в строке текста
    /// она символ абзаца, как картинка, а обтекание, размер, поворот и рамка у неё
    /// картиночные. Новая диаграмма встаёт в место каретки размером диаграммы Word по
    /// умолчанию — 15,24 × 8,89 см, — а если не помещается в ширину текста, уменьшается
    /// с сохранением пропорций.
    /// </summary>
    public sealed partial class DocumentViewModel
    {
        /// <summary>Ширина новой диаграммы, пт: шесть дюймов, как у Word.</summary>
        private const double DefaultChartWidthPt = 432.0;

        /// <summary>Высота новой диаграммы, пт: три с половиной дюйма, как у Word.</summary>
        private const double DefaultChartHeightPt = 252.0;

        /// <summary>
        /// Выделенная на листе картинка или диаграмма. Ставится полотном: выделение
        /// объектов живёт там.
        /// </summary>
        public Func<ImageBlock?>? SelectedImageDelegate { get; set; }

        /// <summary>Модель диаграммы сменилась: окном, лентой или удалением.</summary>
        public event Action? ChartChanged;

        /// <summary>
        /// Правка свойств одной картинки отдельным шагом отмены: картинка, описание шага
        /// и сама правка. Ставится полотном — там живёт команда свойств картинки.
        ///
        /// Правка диаграммы идёт ею, а не снимком документа: снимок при отмене
        /// возвращал картинки новыми объектами, и выделение с лентой оставались на
        /// прежнем, уже выброшенном объекте — следующие нажатия ленты уходили в
        /// пустоту, а шаги размера и поворота той же картинки после такой отмены
        /// не отменялись.
        /// </summary>
        public Action<ImageBlock, string, Action>? ImageEditDelegate { get; set; }

        /// <summary>Ставит картинке новую модель диаграммы одним шагом отмены и обновляет вид.</summary>
        private void ApplyChartToImage(ImageBlock image, ChartModel chart, string description)
        {
            if (ImageEditDelegate is { } edit)
            {
                edit(image, description, () => image.Chart = chart);
            }
            else
            {
                BeginEditDelegate?.Invoke(description);
                image.Chart = chart;
                CommitEditDelegate?.Invoke();
            }

            if (FindInlineImageOwner(image) is { } owner) InlineObjectsChanged?.Invoke(owner.Para);
            StructureChanged?.Invoke();
            ChartChanged?.Invoke();
        }

        /// <summary>Открывает окно новой диаграммы заданного вида и вставляет её в место каретки.</summary>
        public void InsertChart(ChartKind kind) => _ = InsertChartAsync(ChartModel.CreateDefault(kind));

        /// <summary>Открывает окно новой комбинированной диаграммы и вставляет её в место каретки.</summary>
        public void InsertComboChart() => _ = InsertChartAsync(ChartModel.CreateCombo());

        /// <summary>Открывает окно правки выделенной диаграммы — с ленты.</summary>
        public void EditSelectedChart()
        {
            if (SelectedImageDelegate?.Invoke() is { IsChart: true } image)
                RequestChartEdit(image);
        }

        /// <summary>Выделенный объект — диаграмма.</summary>
        public bool IsSelectedChart() => SelectedImageDelegate?.Invoke() is { IsChart: true };

        /// <summary>Модель выделенной диаграммы; null — выделена не диаграмма.</summary>
        public ChartModel? GetSelectedChart() => SelectedImageDelegate?.Invoke()?.Chart;

        /// <summary>Показ выделенной диаграммы с другой моделью — ставит полотно.</summary>
        public Action<ChartModel?>? ChartPreviewDelegate { get; set; }

        /// <summary>
        /// Предпросмотр стиля или цвета на выделенной диаграмме, пока указатель на образце.
        /// Документ не меняется; null — снять предпросмотр.
        /// </summary>
        public void PreviewSelectedChart(ChartModel? preview) => ChartPreviewDelegate?.Invoke(preview);

        /// <summary>
        /// Ставит выделенной диаграмме новую модель — с ленты «Диаграмма». Шаг отмены
        /// один на нажатие; та же модель, что уже стоит, правкой не считается.
        /// </summary>
        public void ApplySelectedChart(ChartModel chart, string description)
        {
            if (IsReadOnly || chart is null) return;
            if (SelectedImageDelegate?.Invoke() is not { Chart: { } before } image) return;
            if (chart.ComputeFingerprint() == before.ComputeFingerprint()) return;

            ApplyChartToImage(image, chart, description);
        }

        /// <summary>Открывает окно правки диаграммы: по двойному щелчку на ней или из ленты.</summary>
        public void RequestChartEdit(ImageBlock image)
        {
            if (image?.Chart is null) return;
            _ = EditChartAsync(image);
        }

        private async Task InsertChartAsync(ChartModel template)
        {
            try
            {
                if (IsReadOnly || _document.Sections.Count == 0) return;

                // Место диаграммы запоминается до окна: окно забирает фокус.
                var target = GetCaretTargetDelegate?.Invoke();
                ParagraphBlock? para = target?.Para;
                int at = target?.CharIndex ?? 0;

                if (para is null && _activeParagraph is not null)
                {
                    para = _activeParagraph.Model;
                    at = _activeParagraph.SelectionStart;
                }

                var (widthPt, heightPt) = NewChartSize();

                var result = await ChartEditorDialog.ShowAsync(template, widthPt, heightPt, canDelete: false, StoreImageFile, ReadImageFile);

                // Окно закрылось — клавиатура снова у текста: без этого Ctrl+Z и ввод
                // уходили в никуда, пока по тексту не щёлкнут.
                FocusEditor();
                if (result?.Chart is null || result.Delete || IsReadOnly) return;

                var image = new ImageBlock
                {
                    Chart = result.Chart,
                    WidthPt = widthPt,
                    HeightPt = heightPt,
                    LockAspectRatio = false,
                    AltText = result.Chart.DisplayTitle
                };

                BeginEditDelegate?.Invoke("Вставка диаграммы");

                MarkNewImageInserted(image);
                var section = _document.Sections[0];

                // Диаграмма в строке — символ абзаца в запомненном месте каретки, как
                // картинка. Абзац мог уйти, пока окно было открыто, — тогда блоком.
                if (para is not null && EnumerateAllParagraphs().Any(p => ReferenceEquals(p, para)))
                {
                    at = Math.Max(0, Math.Min(at, para.TotalLength));
                    section.InlineObjects.Add(image);
                    para.InsertInlineObject(at, image.Id);

                    if (image.Inserted is { } inserted)
                    {
                        Services.RevisionService.MarkInserted(para, at, at + 1, inserted);
                        image.Inserted = null;
                    }

                    InlineImageInserted?.Invoke(para, at);
                }
                else
                {
                    int idx = _activeParagraph is not null ? section.Blocks.IndexOf(_activeParagraph.Model) : -1;
                    if (idx >= 0)
                        section.Blocks.Insert(idx + 1, image);
                    else
                        section.Blocks.Add(image);
                }

                CommitEditDelegate?.Invoke();
                StructureChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[CHART] Диаграмму вставить не удалось");
            }
        }

        private async Task EditChartAsync(ImageBlock image)
        {
            try
            {
                if (IsReadOnly || image.Chart is not { } before) return;

                var result = await ChartEditorDialog.ShowAsync(before, image.WidthPt, image.HeightPt, canDelete: true, StoreImageFile, ReadImageFile);
                FocusEditor();
                if (result is null || IsReadOnly) return;

                // Диаграмма могла уйти из документа или смениться, пока окно было открыто.
                if (!ReferenceEquals(image.Chart, before)) return;

                if (result.Delete)
                {
                    // Выделенная диаграмма удаляется тем же путём, что клавишей Delete:
                    // так снимается выделение, прячется вкладка ленты и работает запись
                    // исправлений.
                    if (ReferenceEquals(SelectedImageDelegate?.Invoke(), image) && DeleteSelectedImageDelegate is not null)
                        DeleteSelectedImage();
                    else
                        RemoveImage(image);
                    return;
                }

                if (result.Chart is null) return;
                if (result.Chart.ComputeFingerprint() == before.ComputeFingerprint()) return;

                ApplyChartToImage(image, result.Chart, "Правка диаграммы");
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[CHART] Диаграмму изменить не удалось");
            }
        }

        /// <summary>
        /// Прямоугольник ячеек таблицы под кареткой для диаграммы: выделенные ячейки или,
        /// без выделения, вся таблица. Ставится полотном: выделение ячеек живёт там.
        /// </summary>
        public Func<(TableBlock Table, int FirstRow, int FirstColumn, int LastRow, int LastColumn)?>? GetTableChartRangeDelegate { get; set; }

        /// <summary>Диаграмма по данным таблицы под кареткой.</summary>
        public void InsertChartFromTable() => _ = InsertChartFromTableAsync();

        private async Task InsertChartFromTableAsync()
        {
            try
            {
                if (IsReadOnly || _document.Sections.Count == 0) return;

                var range = GetTableChartRangeDelegate?.Invoke();
                if (range is not { } found)
                {
                    await ShowChartMessageAsync("Поставьте каретку в таблицу или выделите её ячейки: диаграмма строится по их числам.");
                    return;
                }

                var data = Services.ChartFromTable.Build(found.Table, found.FirstRow, found.FirstColumn, found.LastRow, found.LastColumn);
                if (data is null)
                {
                    await ShowChartMessageAsync("В выделенных ячейках нет чисел — строить диаграмму не по чему.");
                    return;
                }

                var (widthPt, heightPt) = NewChartSize();
                var result = await ChartEditorDialog.ShowAsync(data, widthPt, heightPt, canDelete: false, StoreImageFile, ReadImageFile);
                FocusEditor();
                if (result?.Chart is null || result.Delete || IsReadOnly) return;

                var section = _document.Sections[0];

                // Таблица могла уйти, пока окно было открыто. Вложенная таблица своего
                // места в разделе не имеет — диаграмма встаёт за таблицей верхнего уровня.
                int tableIndex = TopLevelIndexOfTable(section, found.Table);
                if (tableIndex < 0) return;

                var image = new ImageBlock
                {
                    Chart = result.Chart,
                    WidthPt = widthPt,
                    HeightPt = heightPt,
                    LockAspectRatio = false,
                    AltText = result.Chart.DisplayTitle
                };

                BeginEditDelegate?.Invoke("Диаграмма из таблицы");
                MarkNewImageInserted(image);

                // Диаграмма — в пустом абзаце сразу за таблицей, если он есть, иначе в
                // новом. Абзац по центру, как у Word диаграмма под таблицей.
                ParagraphBlock owner;
                bool added = false;
                if (tableIndex + 1 < section.Blocks.Count
                    && section.Blocks[tableIndex + 1] is ParagraphBlock next
                    && next.GetPlainText().Trim().Length == 0)
                {
                    owner = next;
                }
                else
                {
                    owner = new ParagraphBlock();
                    section.Blocks.Insert(tableIndex + 1, owner);
                    added = true;
                }

                owner.Properties.Alignment = Models.Styles.TextAlignment.Center;
                owner.Properties.FirstLineIndent = 0.0;

                section.InlineObjects.Add(image);
                owner.InsertInlineObject(0, image.Id);

                if (image.Inserted is { } inserted)
                {
                    Services.RevisionService.MarkInserted(owner, 0, 1, inserted);
                    image.Inserted = null;
                }

                if (added) RebuildParagraphViewModels();
                CommitEditDelegate?.Invoke();

                InlineObjectsChanged?.Invoke(owner);
                StructureChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[CHART] Диаграмму из таблицы вставить не удалось");
            }
        }

        /// <summary>Место таблицы в блоках раздела; у вложенной — место её внешней таблицы.</summary>
        private static int TopLevelIndexOfTable(SectionModel section, TableBlock table)
        {
            for (int i = 0; i < section.Blocks.Count; i++)
            {
                if (section.Blocks[i] is not TableBlock top) continue;
                if (ReferenceEquals(top, table) || ContainsTable(top, table)) return i;
            }
            return -1;
        }

        private static bool ContainsTable(TableBlock outer, TableBlock inner)
        {
            foreach (var cell in outer.Cells)
            {
                if (cell.NestedTables is not { Count: > 0 } nested) continue;
                foreach (var entry in nested)
                    if (ReferenceEquals(entry.Table, inner) || ContainsTable(entry.Table, inner)) return true;
            }
            return false;
        }

        private static async Task ShowChartMessageAsync(string message)
        {
            var dialogs = Writersword.Core.Services.CoreServices.GetService<Writersword.Core.Interfaces.Services.UI.IDialogService>();
            if (dialogs is null) return;
            await dialogs.ShowMessageAsync(
                "Диаграмма из таблицы", message,
                Writersword.Core.Enums.MessageBoxType.Info,
                Writersword.Core.Enums.MessageBoxButtons.OK);
        }

        /// <summary>
        /// Размер новой диаграммы: как у Word, но не шире текста первого раздела.
        /// </summary>
        private (double WidthPt, double HeightPt) NewChartSize()
        {
            double widthPt = DefaultChartWidthPt;
            double heightPt = DefaultChartHeightPt;

            double textWidthMm = _document.PageSettings?.GetTextWidthMm() ?? 0.0;
            double textWidthPt = textWidthMm * 72.0 / 25.4;

            if (textWidthPt > 36.0 && widthPt > textWidthPt)
            {
                heightPt *= textWidthPt / widthPt;
                widthPt = textWidthPt;
            }

            return (widthPt, heightPt);
        }

        /// <summary>
        /// Байты картинки из хранилища проекта по имени файла: для образца в окне
        /// диаграммы. Null — файла нет или хранилище недоступно.
        /// </summary>
        public byte[]? ReadImageFile(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;

            var ctx = Writersword.Core.Services.CoreServices
                .GetService<Writersword.Core.Interfaces.WorkFlows.ITabCollection>()?.ActiveTab?.Context;
            if (ctx is null) return null;

            try
            {
                return Services.PendingProjectImages.Read(ctx, $"TextEditor/Images/{fileName}");
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[CHART] Не удалось прочитать картинку {File}", fileName);
                return null;
            }
        }
    }
}
