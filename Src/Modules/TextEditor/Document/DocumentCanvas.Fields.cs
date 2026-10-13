using System;
using System.Collections.Generic;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Services;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Живые поля на листах: дата и время — на момент показа, номер страницы — того
    /// листа, где поле встало, число страниц — сколько их в документе, как у Word.
    ///
    /// Номер страницы известен только после раскладки, а ширина номера влияет на саму
    /// раскладку. Поэтому раскладка верстает поле последним известным значением,
    /// затем сверяет его с листом, на котором поле оказалось, и при расхождении
    /// пересобирается ещё раз; пересборок подряд не больше трёх — на случай поля,
    /// которое своим значением перебрасывает себя на другой лист.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        // Наибольшее число пересборок подряд ради уточнения номеров страниц.
        private const int FieldRelayoutLimit = 3;

        // Живые поля документа: само поле, его абзац, знак в абзаце и язык текста.
        private Dictionary<Guid, (FieldBlock Field, ParagraphBlock? Para, int Char, string? Language)> _liveFields = new();

        // Значения номеров страниц и числа страниц, найденные по последней раскладке.
        private readonly Dictionary<Guid, string> _fieldPageValues = new();

        // Текст, которым поле сверстано последним: им же оно и рисуется.
        private readonly Dictionary<Guid, string> _fieldTexts = new();
        private readonly object _fieldTextsLock = new();

        private int _fieldRelayoutStreak;

        /// <summary>
        /// Освежает список живых полей перед раскладкой: поля могли появиться (вставка,
        /// отмена) или уйти из текста.
        /// </summary>
        private void RefreshLiveFields()
        {
            var document = DocVm?.Document;
            var fields = new Dictionary<Guid, (FieldBlock, ParagraphBlock?, int, string?)>();

            if (document is not null)
            {
                foreach (var section in document.Sections)
                    foreach (var block in section.InlineObjects)
                        if (block is FieldBlock field)
                            fields[field.Id] = (field, null, -1, null);

                if (fields.Count > 0)
                {
                    void Visit(ParagraphBlock paragraph)
                    {
                        int position = 0;
                        foreach (var chunk in paragraph.Chunks)
                        {
                            foreach (var run in chunk.Runs)
                            {
                                if (run.InlineImageId is Guid id && fields.TryGetValue(id, out var entry))
                                    fields[id] = (entry.Item1, paragraph, position, run.Properties?.Language);

                                position += run.InlineImageId.HasValue ? 1 : (run.Text?.Length ?? 0);
                            }
                        }
                    }

                    foreach (var section in document.Sections)
                    {
                        foreach (var block in section.Blocks)
                        {
                            switch (block)
                            {
                                case ParagraphBlock paragraph:
                                    Visit(paragraph);
                                    break;

                                case TableBlock table:
                                    foreach (var cell in table.Cells)
                                        foreach (var cellParagraph in cell.ParagraphsDeep())
                                            Visit(cellParagraph);
                                    break;

                                case FloatingTextBlock floatingText:
                                    foreach (var textParagraph in floatingText.Paragraphs)
                                        Visit(textParagraph);
                                    break;
                            }
                        }
                    }
                }
            }

            _liveFields = fields;
        }

        /// <summary>Текст живого поля для вёрстки строки. null — объект с этим Id не поле.</summary>
        private string? GetInlineFieldText(Guid id)
        {
            var fields = _liveFields;
            FieldBlock? field = fields.TryGetValue(id, out var entry) ? entry.Field : null;
            string? language = entry.Language;

            // Поле, появившееся после последней раскладки (вставка из буфера).
            if (field is null)
            {
                var document = DocVm?.Document;
                if (document is null) return null;

                foreach (var section in document.Sections)
                {
                    foreach (var block in section.InlineObjects)
                    {
                        if (block is FieldBlock candidate && candidate.Id == id)
                        {
                            field = candidate;
                            break;
                        }
                    }

                    if (field is not null) break;
                }

                if (field is null) return null;
            }

            string text = FieldCodes.KindOf(field.Instruction) switch
            {
                LiveFieldKind.Date or LiveFieldKind.Time
                    => FieldCodes.FormatDateTime(field.Instruction, DateTime.Now, language),
                LiveFieldKind.Page or LiveFieldKind.NumPages
                    => _fieldPageValues.TryGetValue(id, out var value)
                        ? value
                        : (string.IsNullOrEmpty(field.Result) ? "1" : field.Result),
                _ => field.Result
            };

            lock (_fieldTextsLock) _fieldTexts[id] = text;
            return text;
        }

        /// <summary>Текст, которым поле сверстано: им же оно рисуется.</summary>
        private string? LaidOutFieldText(Guid id)
        {
            lock (_fieldTextsLock)
                return _fieldTexts.TryGetValue(id, out var text) ? text : null;
        }

        /// <summary>
        /// Сверяет номера страниц в полях с листами, на которых поля встали. При
        /// расхождении сбрасывает раскладки их абзацев и пересобирает лист ещё раз.
        /// </summary>
        private void UpdatePageFieldValues()
        {
            var fields = _liveFields;
            if (fields.Count == 0 || DocVm is null) return;

            // Листы чтения — не листы документа: их больше, они меньше и нумеруются
            // по-своему. Номер страницы в поле остаётся номером листа документа, как в
            // режиме чтения у Word: последним, что дала раскладка листов, а до неё —
            // сохранённым в файле.
            if (ReadingActive || SpreadMode)
            {
                _fieldRelayoutStreak = 0;
                return;
            }

            List<ParaLayout> layouts;
            List<PageRect> pages;
            lock (_renderLock)
            {
                layouts = _layouts;
                pages = _pages;
            }

            if (pages.Count == 0) return;

            PageDecoration[]? decorations = null;
            int PageNumber(int pageIndex)
            {
                decorations ??= GetDecorations(layouts, pages.Count);
                return pageIndex >= 0 && pageIndex < decorations.Length && decorations[pageIndex].Number > 0
                    ? decorations[pageIndex].Number
                    : pageIndex + 1;
            }

            // Куски раскладки по абзацам.
            Dictionary<ParagraphBlock, List<ParaLayout>>? slices = null;

            var changedOwners = new HashSet<ParagraphBlock>();
            bool changed = false;

            foreach (var (id, (field, para, charIndex, _)) in fields)
            {
                var kind = FieldCodes.KindOf(field.Instruction);
                string? value = null;

                if (kind == LiveFieldKind.NumPages)
                {
                    value = FieldCodes.FormatNumber(field.Instruction, pages.Count);
                }
                else if (kind == LiveFieldKind.Page && para is not null && charIndex >= 0)
                {
                    if (slices is null)
                    {
                        slices = new Dictionary<ParagraphBlock, List<ParaLayout>>();
                        foreach (var pl in layouts)
                        {
                            var model = pl.Cell?.ParaBlock ?? pl.Vm?.Model;
                            if (model is null || pl.Layout is null) continue;
                            if (!slices.TryGetValue(model, out var list))
                                slices[model] = list = new List<ParaLayout>();
                            list.Add(pl);
                        }
                    }

                    if (slices.TryGetValue(para, out var paraSlices))
                    {
                        foreach (var pl in paraSlices)
                        {
                            var layout = pl.Layout!;
                            if (layout.Lines.Count == 0)
                            {
                                value = FieldCodes.FormatNumber(field.Instruction, PageNumber(pl.PageIndex));
                                break;
                            }

                            int lineIndex = layout.GetLineIndexForChar(charIndex);
                            if (lineIndex < pl.LineFrom || lineIndex >= pl.LineTo) continue;

                            value = FieldCodes.FormatNumber(field.Instruction, PageNumber(pl.PageIndex));
                            break;
                        }
                    }
                }

                if (value is null) continue;
                if (_fieldPageValues.TryGetValue(id, out var previous) && previous == value) continue;

                _fieldPageValues[id] = value;

                // Сверстано было другим текстом — абзац верстается заново.
                if (LaidOutFieldText(id) != value)
                {
                    changed = true;
                    if (para is not null) changedOwners.Add(para);
                }
            }

            if (!changed)
            {
                _fieldRelayoutStreak = 0;
                return;
            }

            bool cellOwners = false;
            var drop = new List<ViewModels.Blocks.ParagraphViewModel>();
            foreach (var key in _layoutCache.Keys)
                if (key.Model is not null && changedOwners.Contains(key.Model))
                    drop.Add(key);
            foreach (var key in drop)
                _layoutCache.Remove(key);

            foreach (var owner in changedOwners)
            {
                bool inFlow = false;
                foreach (var pvm in DocVm.Paragraphs)
                    if (ReferenceEquals(pvm.Model, owner)) { inFlow = true; break; }
                if (!inFlow) cellOwners = true;
            }

            if (cellOwners) InvalidateCellLayoutCaches();

            if (_fieldRelayoutStreak >= FieldRelayoutLimit) return;
            _fieldRelayoutStreak++;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                RebuildLayouts();
                InvalidateMeasure();
                InvalidateFull();
            }, Avalonia.Threading.DispatcherPriority.Background);
        }
    }
}
