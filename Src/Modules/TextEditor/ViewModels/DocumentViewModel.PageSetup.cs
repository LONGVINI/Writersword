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

        /// <summary>
        /// Размер листа раздела под кареткой, мм, — ширина и высота листа так, как он
        /// стоит. Совпавший с известным форматом размер получает имя формата, иначе —
        /// «свой». Ориентация остаётся прежней: у альбомного листа ширина и высота
        /// формата меняются местами.
        /// </summary>
        public void ApplySectionPageSizeMm(double widthMm, double heightMm)
        {
            widthMm = Math.Clamp(widthMm, 10.0, 1000.0);
            heightMm = Math.Clamp(heightMm, 10.0, 1000.0);

            ApplyToSections("Размер листа", newPage: true, page =>
            {
                bool landscape = page.Orientation == PageOrientation.Landscape;
                double portraitWidth = landscape ? heightMm : widthMm;
                double portraitHeight = landscape ? widthMm : heightMm;

                page.PaperSize = KnownPaperSize(portraitWidth, portraitHeight);
                page.WidthMm = portraitWidth;
                page.HeightMm = portraitHeight;
            }, null);
        }

        /// <summary>Формат бумаги с таким размером книжного листа; иначе — свой.</summary>
        private static PaperSize KnownPaperSize(double widthMm, double heightMm)
        {
            foreach (var size in new[] { PaperSize.A3, PaperSize.A4, PaperSize.A5, PaperSize.Letter, PaperSize.Legal, PaperSize.B5 })
            {
                var probe = new TextEditorPageSettings();
                probe.ApplyPaperSize(size);
                if (Math.Abs(probe.WidthMm - widthMm) < 0.5 && Math.Abs(probe.HeightMm - heightMm) < 0.5)
                    return size;
            }

            return PaperSize.Custom;
        }

        /// <summary>
        /// Колонки раздела целиком: число, промежуток, черта и ширина каждой колонки,
        /// если они разные, — окно «Другие колонки». Колонки меняются на том же листе:
        /// разрывы режима «только выделенное» — непрерывные.
        /// </summary>
        public void ApplySectionColumnLayout(SectionColumns columns)
        {
            var wanted = NormalizedColumns(columns);

            ApplyToSections("Колонки", newPage: false, null, null,
                layout => layout.Columns = wanted.Clone(),
                () =>
                {
                    var settings = (_document.Sections.Count > 0 ? _document.Sections[0].ColumnSettings : null)
                        ?? _document.ColumnSettings;
                    settings.ColumnCount = wanted.Count;
                    settings.GapMm = wanted.GapMm;
                    settings.ShowSeparator = wanted.Count > 1 && wanted.Separator;

                    // Колонки разной ширины последнего раздела живут в его параметрах;
                    // равные — в колонках документа.
                    if (wanted.Columns is { Count: > 1 })
                    {
                        var final = _document.FinalSection ??= new SectionLayout();
                        final.Columns = wanted.Clone();
                    }
                    else if (_document.FinalSection is { Columns: not null } final)
                    {
                        final.Columns = null;
                    }
                });
        }

        /// <summary>
        /// Колонки в том виде, в каком их хранит раздел: одна колонка — без черты и без
        /// списка ширин; список ширин — только у колонок разной ширины, и его длина
        /// задаёт их число.
        /// </summary>
        private static SectionColumns NormalizedColumns(SectionColumns columns)
        {
            if (columns.Columns is { Count: > 1 } own)
            {
                var list = new List<SectionColumn>(own.Count);
                for (int i = 0; i < own.Count; i++)
                {
                    list.Add(new SectionColumn
                    {
                        WidthMm = Math.Max(own[i].WidthMm, 1.0),
                        SpaceAfterMm = i < own.Count - 1 ? Math.Max(own[i].SpaceAfterMm, 0.0) : 0.0
                    });
                }

                return new SectionColumns
                {
                    Count = list.Count,
                    GapMm = list[0].SpaceAfterMm,
                    Separator = columns.Separator,
                    Columns = list
                };
            }

            int count = Math.Clamp(columns.Count, 1, 12);
            return new SectionColumns
            {
                Count = count,
                GapMm = Math.Max(columns.GapMm, 0.0),
                Separator = count > 1 && columns.Separator
            };
        }

        /// <summary>Выравнивание текста раздела по высоте листа.</summary>
        public void ApplySectionVerticalAlignment(SectionVerticalAlignment alignment)
            => ApplyToSections("Вертикальное выравнивание", newPage: true, null, null,
                layout => layout.VerticalAlignment = alignment, null);

        /// <summary>Номера строк раздела; null — строки не нумеруются.</summary>
        public void ApplySectionLineNumbering(SectionLineNumbering? numbering)
        {
            var wanted = numbering is null
                ? null
                : new SectionLineNumbering
                {
                    CountBy = Math.Clamp(numbering.CountBy, 1, 100),
                    Start = Math.Clamp(numbering.Start, 1, 32767),
                    DistanceMm = Math.Clamp(numbering.DistanceMm, 0.0, 100.0),
                    Restart = numbering.Restart
                };

            ApplyToSections("Номера строк", newPage: true, null, null,
                layout => layout.LineNumbering = wanted?.Clone(), null);
        }

        /// <summary>Рамка страницы раздела; null — рамки нет.</summary>
        public void ApplySectionPageBorders(SectionPageBorders? borders)
            => ApplyToSections("Рамка страницы", newPage: true, null, null,
                layout => layout.PageBorders = borders?.Clone(), null);

        /// <summary>
        /// Все параметры окна «Параметры раздела» одним шагом отмены: размер листа,
        /// выравнивание по высоте, номера строк, рамка и начало раздела.
        /// </summary>
        public void ApplySectionSetup(SectionSetup setup)
        {
            if (IsReadOnly) return;

            double widthMm = Math.Clamp(setup.Page.GetPhysicalWidthMm(), 10.0, 1000.0);
            double heightMm = Math.Clamp(setup.Page.GetPhysicalHeightMm(), 10.0, 1000.0);
            var borders = setup.PageBorders?.Clone();
            var numbering = setup.LineNumbering is null
                ? null
                : new SectionLineNumbering
                {
                    CountBy = Math.Clamp(setup.LineNumbering.CountBy, 1, 100),
                    Start = Math.Clamp(setup.LineNumbering.Start, 1, 32767),
                    DistanceMm = Math.Clamp(setup.LineNumbering.DistanceMm, 0.0, 100.0),
                    Restart = setup.LineNumbering.Restart
                };

            BeginUndoStep("Параметры раздела");
            try
            {
                ApplyToSections("Параметры раздела", newPage: true, page =>
                {
                    // Размер меняется, только если его поменяли: иначе лист известного
                    // формата без нужды становился бы «своим».
                    if (Math.Abs(page.GetPhysicalWidthMm() - widthMm) < 0.05
                        && Math.Abs(page.GetPhysicalHeightMm() - heightMm) < 0.05)
                        return;

                    bool landscape = page.Orientation == PageOrientation.Landscape;
                    double portraitWidth = landscape ? heightMm : widthMm;
                    double portraitHeight = landscape ? widthMm : heightMm;
                    page.PaperSize = KnownPaperSize(portraitWidth, portraitHeight);
                    page.WidthMm = portraitWidth;
                    page.HeightMm = portraitHeight;
                }, null, layout =>
                {
                    layout.VerticalAlignment = setup.VerticalAlignment;
                    layout.LineNumbering = numbering?.Clone();
                    layout.PageBorders = borders?.Clone();
                }, null);

                if (setup.StartBreak is { } start)
                    SetCaretSectionStart(start);
            }
            finally
            {
                CommitUndoStep();
            }
        }

        /// <summary>
        /// Как начинается раздел под кареткой: вид разрыва раздела перед ним. У первого
        /// раздела документа разрыва перед ним нет, и команда ничего не меняет.
        /// </summary>
        public void ApplySectionStart(BreakType start)
        {
            if (IsReadOnly) return;

            BeginUndoStep("Начало раздела");
            try
            {
                SetCaretSectionStart(start);
                SyncParagraphViewModels();
            }
            finally
            {
                CommitUndoStep();
            }

            this.RaisePropertyChanged(nameof(PageSettings));
        }

        private void SetCaretSectionStart(BreakType start)
        {
            if (start is not (BreakType.SectionNextPage or BreakType.SectionContinuous
                or BreakType.SectionEvenPage or BreakType.SectionOddPage or BreakType.SectionNextColumn))
                return;

            var blocks = PageSetupBlocks();
            if (blocks is null || PageSetupRange(blocks) is not { } range) return;

            int sectionStart = SectionStartOf(blocks, range.First);
            if (sectionStart > 0 && blocks[sectionStart - 1] is BreakBlock { IsSectionBreak: true } before)
                before.BreakType = start;
        }

        /// <summary>
        /// Параметры раздела под кареткой — копия для окон вкладки «Макет»: лист,
        /// колонки, выравнивание по высоте, рамка, номера строк и начало раздела.
        /// </summary>
        public SectionSetup GetSectionSetup()
        {
            var blocks = PageSetupBlocks();
            int end = -1;
            int sectionStart = 0;
            if (blocks is not null && PageSetupRange(blocks) is { } range)
            {
                end = SectionEndAtOrAfter(blocks, range.First);
                sectionStart = SectionStartOf(blocks, range.First);
            }

            SectionLayout? layout = end >= 0
                ? ((BreakBlock)blocks![end]).Section
                : _document.FinalSection;

            var columns = end >= 0
                ? ColumnFrames.ColumnsOf(_document, (BreakBlock)blocks![end])
                : ColumnFrames.FinalColumns(_document);

            BreakType? startBreak = blocks is not null && sectionStart > 0
                                    && blocks[sectionStart - 1] is BreakBlock { IsSectionBreak: true } before
                ? before.BreakType
                : null;

            return new SectionSetup
            {
                Page = CaretSectionPage().Clone(),
                Columns = columns.Clone(),
                VerticalAlignment = layout?.VerticalAlignment ?? SectionVerticalAlignment.Top,
                PageBorders = layout?.PageBorders?.Clone(),
                LineNumbering = layout?.LineNumbering?.Clone(),
                StartBreak = startBreak
            };
        }

        /// <summary>
        /// Разрыв колонки у каретки: текст за ним идёт в следующую колонку, в разделе из
        /// одной колонки — на следующую страницу, как у Word.
        /// </summary>
        public void InsertColumnBreak()
        {
            if (IsReadOnly) return;

            var breakBlock = new BreakBlock { BreakType = BreakType.Page, FromColumnBreak = true };
            InsertBlockAtCaret(breakBlock);
            NormalizeBreakAnchors();

            if (_document.Sections.Count == 0) return;
            var blocks = _document.Sections[0].Blocks;
            int breakIdx = blocks.IndexOf(breakBlock);
            if (breakIdx >= 0 && breakIdx + 1 < blocks.Count && blocks[breakIdx + 1] is ParagraphBlock anchorBlock)
                OnPageBreakInserted?.Invoke(anchorBlock);
        }

        /// <summary>
        /// Разрыв раздела у каретки. Как у Word, обе части раздела сохраняют его
        /// параметры: новый разрыв получает копию параметров раздела, в котором стоит
        /// каретка, — без неё первая часть жила бы по листу и колонкам документа. Новый
        /// счёт страниц остаётся у первой части: она начинает прежний раздел.
        /// </summary>
        private void InsertSectionBreakAtCaret(BreakType type)
        {
            if (IsReadOnly) return;
            if (_document.Sections.Count == 0) return;

            var blocks = _document.Sections[0].Blocks;
            var target = GetCaretTargetDelegate?.Invoke();
            int at = target?.Para is { } caretPara ? blocks.IndexOf(caretPara) : -1;
            if (at < 0 && _activeParagraph is not null) at = blocks.IndexOf(_activeParagraph.Model);

            int end = at >= 0 ? SectionEndAtOrAfter(blocks, at) : -1;
            var copy = SectionCopy(blocks, end);

            var breakBlock = new BreakBlock { BreakType = type, Section = copy };

            BeginUndoStep("Разрыв раздела");
            try
            {
                ContinuePageNumbering(blocks, end);
                InsertBlockAtCaret(breakBlock);
                NormalizeBreakAnchors();
            }
            finally
            {
                CommitUndoStep();
            }

            int breakIdx = blocks.IndexOf(breakBlock);
            if (breakIdx >= 0 && breakIdx + 1 < blocks.Count && blocks[breakIdx + 1] is ParagraphBlock anchorBlock)
                OnPageBreakInserted?.Invoke(anchorBlock);

            this.RaisePropertyChanged(nameof(PageSettings));
        }

        /// <summary>
        /// Копия параметров раздела, который кончается разрывом end (-1 — последнего), с
        /// листом и колонками: в отличие от <see cref="CopyOfSection"/>, сам раздел не
        /// трогает. Смена параметров правкой к копии не переходит: новый раздел ещё
        /// никто не менял.
        /// </summary>
        private SectionLayout SectionCopy(List<BlockModel> blocks, int end)
        {
            SectionLayout copy;
            if (end >= 0 && blocks[end] is BreakBlock { Section: { } own })
            {
                copy = own.Clone();
                copy.Page ??= _document.PageSettings.Clone();
                copy.Columns ??= ColumnFrames.FinalColumns(_document).Clone();
            }
            else if (end >= 0)
            {
                copy = new SectionLayout
                {
                    Page = _document.PageSettings.Clone(),
                    Columns = ColumnFrames.FinalColumns(_document).Clone()
                };
            }
            else
            {
                copy = _document.FinalSection?.Clone() ?? new SectionLayout();
                copy.Page = _document.PageSettings.Clone();
                copy.Columns = ColumnFrames.FinalColumns(_document).Clone();
            }

            copy.FormatChange = null;
            return copy;
        }

        /// <summary>Зеркальные поля документа.</summary>
        public bool GetMirrorMargins() => _document.PageSettings.MirrorMargins;

        /// <summary>
        /// Зеркальные поля — настройка документа, как у Word: левое поле становится
        /// внутренним, правое — внешним, и на чётных листах они меняются местами вместе с
        /// переплётом. Листы разделов отдельной настройки не имеют.
        /// </summary>
        public void SetMirrorMargins(bool mirror)
        {
            if (IsReadOnly || _document.PageSettings.MirrorMargins == mirror) return;

            BeginUndoStep("Зеркальные поля");
            try
            {
                _document.PageSettings.MirrorMargins = mirror;
            }
            finally
            {
                CommitUndoStep();
            }

            this.RaisePropertyChanged(nameof(PageSettings));
        }

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
        /// <param name="changeSection">
        /// Правка остальных параметров раздела: выравнивание по высоте, рамка, номера
        /// строк, колонки целиком. У последнего раздела — его параметры
        /// (<see cref="DocumentModel.FinalSection"/>), если не задан changeFinal.
        /// </param>
        /// <param name="changeFinal">Своя правка последнего раздела вместо changeSection.</param>
        private void ApplyToSections(
            string description, bool newPage, Action<TextEditorPageSettings>? changePage, int? columnCount,
            Action<SectionLayout>? changeSection = null, Action? changeFinal = null)
        {
            if (IsReadOnly) return;

            var blocks = PageSetupBlocks();
            var range = blocks is null ? null : PageSetupRange(blocks);

            BeginUndoStep(description);
            try
            {
                if (blocks is null || range is null)
                {
                    ApplyToFinalSection(changePage, columnCount, changeSection, changeFinal);
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
                            ApplyToFinalSection(changePage, columnCount, changeSection, changeFinal);
                            break;
                        }

                        var layout = OwnSectionLayout((BreakBlock)blocks[end]);
                        changePage?.Invoke(layout.Page!);
                        changeSection?.Invoke(layout);
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
        private void ApplyToFinalSection(
            Action<TextEditorPageSettings>? changePage, int? columnCount,
            Action<SectionLayout>? changeSection, Action? changeFinal)
        {
            changePage?.Invoke(_document.PageSettings);

            if (changeFinal is not null) changeFinal();
            else if (changeSection is not null) changeSection(_document.FinalSection ??= new SectionLayout());

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
