using System;
using System.Collections.Generic;
using ReactiveUI;
using Writersword.Core.Models.Print;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Page;
using Writersword.Modules.TextEditor.Rendering;

namespace Writersword.Modules.TextEditor.ViewModels
{
    /// <summary>
    /// Параметры страницы с вкладки «Макет»: размер листа, ориентация, поля, переплёт и
    /// колонки. Как у Word, они принадлежат разделу, а не документу: команда меняет
    /// раздел, в котором стоит каретка (или все разделы, которые задевает выделение).
    ///
    /// Режим «только выделенное» — то, что в Word делает «Применить: к выделенному
    /// тексту». Раздел режется разрывами до и после выделенных абзацев, и новые
    /// параметры получает только средний кусок: так один лист или несколько абзацев
    /// становятся альбомными, а остальной раздел остаётся как был. Без выделения
    /// кусок — абзац под кареткой. Повторная команда по тому же куску новых разрывов
    /// не ставит: он уже отдельный раздел.
    ///
    /// Вся команда — один шаг отмены вместе со вставленными разрывами.
    /// </summary>
    public sealed partial class DocumentViewModel
    {
        private bool _pageSetupToSelection;

        /// <summary>
        /// Параметры страницы ложатся только на выделенное: раздел режется разрывами.
        /// Иначе — на весь раздел под кареткой.
        /// </summary>
        public bool PageSetupToSelection
        {
            get => _pageSetupToSelection;
            set => this.RaiseAndSetIfChanged(ref _pageSetupToSelection, value);
        }

        // ── Команды вкладки «Макет» ───────────────────────────────────────

        /// <summary>Формат бумаги раздела под кареткой.</summary>
        public void ApplySectionPaperSize(PaperSize size)
            => ApplyToSections("Размер листа", newPage: true, page => page.ApplyPaperSize(size), null);

        /// <summary>Ориентация листа раздела под кареткой.</summary>
        public void ApplySectionOrientation(PageOrientation orientation)
            => ApplyToSections("Ориентация листа", newPage: true, page => page.Orientation = orientation, null);

        /// <summary>Поля раздела под кареткой, мм; левое — без переплёта.</summary>
        public void ApplySectionMargins(double top, double bottom, double left, double right)
            => ApplyToSections("Поля страницы", newPage: true, page =>
            {
                page.MarginTopMm = top;
                page.MarginBottomMm = bottom;
                page.MarginLeftMm = left;
                page.MarginRightMm = right;
            }, null);

        /// <summary>Переплёт раздела под кареткой, мм.</summary>
        public void ApplySectionGutter(double gutterMm)
            => ApplyToSections("Переплёт", newPage: true, page => page.MarginGutterMm = Math.Max(0.0, gutterMm), null);

        /// <summary>
        /// Число колонок раздела под кареткой. Колонки меняются на том же листе: разрывы
        /// для них — непрерывные, как ставит Word.
        /// </summary>
        public void ApplySectionColumns(int count)
            => ApplyToSections("Колонки", newPage: false, null, Math.Clamp(count, 1, 12));

        /// <summary>Поля и переплёт раздела под кареткой — для полей ввода вкладки «Макет».</summary>
        public (double TopMm, double BottomMm, double LeftMm, double RightMm, double GutterMm) GetSectionPageMargins()
        {
            var page = CaretSectionPage();
            return (page.MarginTopMm, page.MarginBottomMm, page.MarginLeftMm, page.MarginRightMm, page.MarginGutterMm);
        }

        /// <summary>Число колонок раздела под кареткой.</summary>
        public int GetSectionColumnCount()
        {
            var blocks = PageSetupBlocks();
            if (blocks is null) return Math.Max(1, ColumnFrames.FinalColumns(_document).Count);

            int end = -1;
            if (PageSetupRange(blocks) is { } range)
                end = SectionEndAtOrAfter(blocks, range.First);

            var columns = end >= 0
                ? ColumnFrames.ColumnsOf(_document, (BreakBlock)blocks[end])
                : ColumnFrames.FinalColumns(_document);

            return columns.Columns is { Count: > 1 } own ? own.Count : Math.Max(1, columns.Count);
        }

        // ── Разделы ───────────────────────────────────────────────────────

        private List<BlockModel>? PageSetupBlocks()
            => _document.Sections.Count > 0 ? _document.Sections[0].Blocks : null;

        /// <summary>Лист раздела под кареткой; у последнего раздела — лист документа.</summary>
        private TextEditorPageSettings CaretSectionPage()
        {
            var blocks = PageSetupBlocks();
            if (blocks is null || PageSetupRange(blocks) is not { } range) return _document.PageSettings;

            int end = SectionEndAtOrAfter(blocks, range.First);
            return end >= 0 && blocks[end] is BreakBlock { Section.Page: { } own } ? own : _document.PageSettings;
        }

        /// <summary>
        /// Блоки, к которым идёт команда: выделенные абзацы, таблица с кареткой или
        /// абзац под кареткой. Null — каретки в разделе нет.
        /// </summary>
        private (int First, int Last)? PageSetupRange(List<BlockModel> blocks)
        {
            int first = int.MaxValue;
            int last = -1;

            void Take(BlockModel? block)
            {
                if (block is null) return;
                int index = blocks.IndexOf(block);
                if (index < 0) return;
                first = Math.Min(first, index);
                last = Math.Max(last, index);
            }

            if (TableActiveCellParagraph is not null && ActiveTable is not null)
            {
                Take(ActiveTable);
            }
            else if (SelectionParagraphs.Count > 0)
            {
                foreach (var pvm in SelectionParagraphs) Take(pvm.Model);
            }
            else
            {
                Take(_activeParagraph?.Model);
            }

            return last < 0 ? null : (first, last);
        }

        private static bool IsSectionEnd(BlockModel block) => block is BreakBlock { IsSectionBreak: true };

        /// <summary>Разрыв, которым кончается раздел блока с этим индексом; -1 — последний раздел.</summary>
        private static int SectionEndAtOrAfter(List<BlockModel> blocks, int index)
        {
            for (int i = Math.Max(0, index); i < blocks.Count; i++)
                if (IsSectionEnd(blocks[i])) return i;
            return -1;
        }

        /// <summary>Первый блок раздела, в который входит блок с этим индексом.</summary>
        private static int SectionStartOf(List<BlockModel> blocks, int index)
        {
            for (int i = Math.Min(index, blocks.Count) - 1; i >= 0; i--)
                if (IsSectionEnd(blocks[i])) return i + 1;
            return 0;
        }

        /// <summary>
        /// Параметры раздела, который кончается этим разрывом, — свои, а не отсылка к
        /// документу: у разрыва, вставленного в редакторе, их нет, и раздел жил по листу
        /// документа. Править его отдельно можно, только когда лист и колонки у него свои.
        /// </summary>
        private SectionLayout OwnSectionLayout(BreakBlock sectionEnd)
        {
            var layout = sectionEnd.Section ??= new SectionLayout();
            layout.Page ??= _document.PageSettings.Clone();
            layout.Columns ??= ColumnFrames.FinalColumns(_document).Clone();
            return layout;
        }

        /// <summary>Копия параметров раздела, который кончается разрывом end (-1 — последнего).</summary>
        private SectionLayout CopyOfSection(List<BlockModel> blocks, int end)
        {
            if (end >= 0 && blocks[end] is BreakBlock sectionEnd)
                return OwnSectionLayout(sectionEnd).Clone();

            var layout = _document.FinalSection?.Clone() ?? new SectionLayout();
            layout.Page = _document.PageSettings.Clone();
            layout.Columns = ColumnFrames.FinalColumns(_document).Clone();
            return layout;
        }

        /// <summary>
        /// Счёт страниц раздела больше не начинается заново: раздел отрезан от своего
        /// начала, и новый номер остался у куска, который начинает прежний раздел.
        /// </summary>
        private void ContinuePageNumbering(List<BlockModel> blocks, int end)
        {
            if (end >= 0)
            {
                if (blocks[end] is BreakBlock { Section: { } layout }) layout.PageNumberStart = null;
                return;
            }

            if (_document.FinalSection is { } final) final.PageNumberStart = null;
        }

        /// <summary>
        /// Режет разделы так, чтобы блоки [first, last] стали отдельным разделом: разрыв
        /// после последнего блока и разрыв перед первым, если кусок не начинается и не
        /// кончается вместе с разделом. Новым разрывам — копии параметров раздела.
        /// </summary>
        private void SplitSectionsAround(List<BlockModel> blocks, ref int first, ref int last, bool newPage)
        {
            var type = newPage ? BreakType.SectionNextPage : BreakType.SectionContinuous;

            // Разрыв после куска: остаток раздела начинается за ним.
            if (!IsSectionEnd(blocks[last]))
            {
                int end = SectionEndAtOrAfter(blocks, last);
                bool endsWithSection = end >= 0 ? last + 1 == end : last == blocks.Count - 1;

                if (!endsWithSection)
                {
                    var after = new BreakBlock { BreakType = type, Section = CopyOfSection(blocks, end) };
                    blocks.Insert(last + 1, after);
                    ContinuePageNumbering(blocks, end >= 0 ? end + 1 : -1);
                }
            }

            // Разрыв перед куском: кусок начинает новый раздел.
            int start = SectionStartOf(blocks, first);
            if (first > start)
            {
                int end = SectionEndAtOrAfter(blocks, first);
                var before = new BreakBlock { BreakType = type, Section = CopyOfSection(blocks, end) };
                blocks.Insert(first, before);
                first++;
                last++;
                ContinuePageNumbering(blocks, end >= 0 ? end + 1 : -1);
            }
        }

        /// <summary>
        /// Применяет правку листа и/или колонок к разделам под кареткой или выделением.
        /// </summary>
        /// <param name="newPage">
        /// Правка меняет лист: разрывы режима «только выделенное» — со следующей страницы.
        /// Иначе (колонки) — непрерывные.
        /// </param>
        private void ApplyToSections(
            string description, bool newPage, Action<TextEditorPageSettings>? changePage, int? columnCount)
        {
            if (IsReadOnly) return;

            var blocks = PageSetupBlocks();
            var range = blocks is null ? null : PageSetupRange(blocks);

            BeginUndoStep(description);
            try
            {
                if (blocks is null || range is null)
                {
                    ApplyToFinalSection(changePage, columnCount);
                }
                else
                {
                    int first = range.Value.First;
                    int last = range.Value.Last;

                    if (PageSetupToSelection)
                        SplitSectionsAround(blocks, ref first, ref last, newPage);

                    // Все разделы, которые задевает кусок.
                    int from = first;
                    while (true)
                    {
                        int end = SectionEndAtOrAfter(blocks, from);
                        if (end < 0)
                        {
                            ApplyToFinalSection(changePage, columnCount);
                            break;
                        }

                        var layout = OwnSectionLayout((BreakBlock)blocks[end]);
                        changePage?.Invoke(layout.Page!);
                        if (columnCount is int count)
                            layout.Columns = new SectionColumns
                            {
                                Count = count,
                                GapMm = layout.Columns?.GapMm ?? 12.5,
                                Separator = count > 1 && layout.Columns?.Separator == true
                            };

                        if (end >= last) break;
                        from = end + 1;
                    }
                }

                SyncParagraphViewModels();
            }
            finally
            {
                CommitUndoStep();
            }

            this.RaisePropertyChanged(nameof(PageSettings));
        }

        /// <summary>Последний раздел: его лист — параметры страницы документа.</summary>
        private void ApplyToFinalSection(Action<TextEditorPageSettings>? changePage, int? columnCount)
        {
            changePage?.Invoke(_document.PageSettings);

            if (columnCount is not int count) return;

            var settings = (_document.Sections.Count > 0 ? _document.Sections[0].ColumnSettings : null)
                ?? _document.ColumnSettings;
            settings.ColumnCount = count;
            if (count <= 1) settings.ShowSeparator = false;

            // Колонки разной ширины последнего раздела перекрывали бы новое число.
            if (_document.FinalSection is { Columns: not null } final)
                final.Columns = null;
        }
    }
}
