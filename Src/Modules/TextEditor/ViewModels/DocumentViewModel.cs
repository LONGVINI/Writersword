using Avalonia.Input.Platform;
using Avalonia.Threading;
using ReactiveUI;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using Writersword.Core.Models.Print;
using Writersword.Core.Services;
using Writersword.Core.Interfaces.WorkFlows;
using Writersword.Modules.TextEditor.Contracts;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Models.Page;
using Writersword.Modules.TextEditor.Models.Styles;
using Writersword.Modules.TextEditor.Services;
using Writersword.Modules.TextEditor.ViewModels.Blocks;
using Writersword.Modules.TextEditor.ViewModels.Toolbar;

namespace Writersword.Modules.TextEditor.ViewModels
{
    public sealed class DocumentViewModel : ReactiveObject, ITextEditorCommandTarget
    {
        private static readonly ILogger _log = Log.ForContext<DocumentViewModel>();

        private readonly DocumentModel _document;
        private readonly ChunkManager _chunkManager;
        private readonly AutoReplaceService _autoReplace;
        private readonly SpellCheckService _spellCheck;

        private EditorViewMode _viewMode;
        private double _zoom = 1.0;
        private bool _isFocusMode;
        private bool _isFullscreen;
        private bool _isReadOnly;

        private ParagraphViewModel? _activeParagraph;

        /// <summary>
        /// Активный параграф внутри ячейки таблицы.
        /// Устанавливается в FireTableCellCursorContext, сбрасывается в SetActiveParagraph.
        /// ApplyParaProperty применяет форматирование к нему когда каретка в таблице.
        /// </summary>
        public ParagraphBlock? TableActiveCellParagraph { get; private set; }

        // Текущее выделение внутри активного параграфа.
        // Устанавливается из DocumentCanvas перед каждым применением форматирования.
        private int _selectionStart;
        private int _selectionEnd;

        /// <summary>
        /// Устанавливает диапазон выделения для активного параграфа.
        /// Вызывается из DocumentCanvas перед применением форматирования.
        /// </summary>
        public void SetSelection(int start, int end)
        {
            _selectionStart = start;
            _selectionEnd = end;
        }

        /// <summary>
        /// Абзацы попавшие в текущее выделение (может быть несколько).
        /// </summary>
        public List<ParagraphViewModel> SelectionParagraphs { get; } = new();

        // ── Делегаты таблицы (устанавливаются DocumentCanvas) ─────────────
        public Action<bool>? TableAddRowDelegate { get; set; }
        public Action<bool>? TableAddColDelegate { get; set; }
        public Action? TableDeleteRowDelegate { get; set; }
        public Action? TableDeleteColDelegate { get; set; }
        public Action? TableDeleteDelegate { get; set; }

        /// <summary>
        /// Вызывается после вставки разрыва страницы с блоком-якорём новой страницы.
        /// DocumentCanvas подписывается и откладывает переход каретки до конца rebuild.
        /// </summary>
        public Action<ParagraphBlock>? OnPageBreakInserted { get; set; }

        // ── Делегат сдвига левого края таблицы ───────────────────────────
        /// <summary>
        /// Устанавливается DocumentCanvas при входе каретки в таблицу.
        /// Параметр — новый отступ таблицы в pt от начала текстовой области.
        /// </summary>
        public Action<double>? TableSetLeftEdgeDelegate { get; set; }

        // Устанавливается DocumentCanvas — пробрасывает вызов в UndoStack.
        public Action? UndoDelegate { get; set; }
        public Action? RedoDelegate { get; set; }

        // Делегаты для создания undo-снапшота форматирования.
        // Устанавливаются DocumentCanvas при подключении.
        public Action<string>? BeginEditDelegate { get; set; }
        public Action? CommitEditDelegate { get; set; }

        // Гранулярный коммит свойств рана (жирность/цвет/размер) через лёгкий TextUndoStack.
        // Канвас строит SetRunPropertyCommand на каждый диапазон и пушит одной командой.
        // Возвращает true, если обработал; иначе ApplyCharProperty идёт снапшотным путём.
        public Func<System.Collections.Generic.IReadOnlyList<(System.Guid ParaId, int From, int To)>,
            Action<RunProperties>, string, bool>? CommitRunPropertyGranularDelegate
        { get; set; }

        // Диапазоны всех выделенных абзацев текущей ячейки таблицы (Id, From, To). Позиция
        // выделения живёт в канвасе. Используется форматированием символов в ячейке, чтобы
        // применять к ВСЕМ выделенным абзацам ячейки, а не к одному активному.
        public Func<System.Collections.Generic.IReadOnlyList<(System.Guid ParaId, int From, int To)>>?
            GetCellSelectionRangesDelegate
        { get; set; }

        // Гранулярный коммит изменений текста с сохранением форматирования (смена регистра).
        // Канвас строит ChangeCaseCommand на каждый диапазон и пушит одной командой в TextUndoStack.
        // Возвращает true, если обработал; иначе ChangeCase идёт снапшотным запасным путём.
        public Func<System.Collections.Generic.IReadOnlyList<(System.Guid ParaId, int From, string OldText, string NewText)>,
            string, bool>? CommitTextEditsDelegate
        { get; set; }

        // Диапазон слова под кареткой: параграф и границы [From, To) в его тексте.
        // Позиция каретки живёт в канвасе, DocVm её не знает. Используется сменой
        // регистра без выделения — команда применяется к текущему слову (как в Word).
        public Func<(ParagraphViewModel Pvm, int From, int To)?>? GetCaretWordRangeDelegate
        { get; set; }

        // Гранулярный коммит свойств абзаца (выравнивание/отступы/интервалы) через TextUndoStack.
        // Канвас строит SetParagraphPropertyCommand на каждый абзац и пушит одной командой.
        // Возвращает true, если обработал; иначе ApplyParaProperty идёт снапшотным путём.
        public Func<System.Collections.Generic.IReadOnlyList<(System.Guid ParaId,
            Action<ParagraphProperties> Apply, Action<ParagraphProperties> Revert)>,
            string, bool>? CommitParagraphPropertyGranularDelegate
        { get; set; }
        public Action? CutDelegate { get; set; }
        public Action? CopyDelegate { get; set; }
        public Action? PasteDelegate { get; set; }

        /// <summary>
        /// Активная таблица — та в которой стоит каретка.
        /// Устанавливается из DocumentCanvas при входе каретки в таблицу.
        /// Используется линейкой для применения изменений ширины и отступа
        /// к правильной таблице (а не к первой найденной через FindTable).
        /// </summary>
        public TableBlock? ActiveTable { get; set; }

        // ── Делегаты: оформление / структура ─────────────────────────────
        public Action? TableMergeCellsDelegate { get; set; }
        public Action? TableSplitCellDelegate { get; set; }
        public Action<Writersword.Modules.TextEditor.Models.Styles.TextAlignment>? TableSetCellHAlignDelegate { get; set; }

        /// <summary>
        /// Пытается применить выравнивание к выделенной блок-картинке.
        /// Возвращает true, если картинка выделена и выравнивание применено —
        /// тогда абзац не трогается.
        /// </summary>
        public Func<Writersword.Modules.TextEditor.Models.Styles.TextAlignment, bool>? TrySetImageAlignmentDelegate { get; set; }

        /// <summary>
        /// Возвращает выравнивание выделенной блок-картинки, либо null если картинка не выделена.
        /// Позволяет риббону показывать выравнивание картинки, а не активного абзаца.
        /// </summary>
        public Func<Writersword.Modules.TextEditor.Models.Styles.TextAlignment?>? GetSelectedImageAlignmentDelegate { get; set; }

        // Делегаты команд контекстной вкладки «Формат» (работают с выделенной картинкой).
        public Action<WrapMode>? SetImageWrapModeDelegate { get; set; }
        public Action<bool>? SetImageLockAspectDelegate { get; set; }
        public Action? DeleteSelectedImageDelegate { get; set; }
        public Func<(WrapMode Wrap, bool LockAspect, Writersword.Modules.TextEditor.Models.Styles.TextAlignment Align)?>? GetSelectedImageInfoDelegate { get; set; }
        public Action<int>? TableSetCellVAlignDelegate { get; set; }
        public Action<string?>? TableSetCellBackgroundDelegate { get; set; }
        public Action<string, BorderStyle, double, string?>? TableSetCellBorderDelegate { get; set; }
        public Action<double>? TableSetColumnWidthDelegate { get; set; }
        public Action<double>? TableSetRowHeightDelegate { get; set; }
        public Action? TableAutoFitDelegate { get; set; }
        public Action? TableDistributeColsDelegate { get; set; }
        public Action? TableDistributeRowsDelegate { get; set; }
        public Action<int, bool>? TableSortDelegate { get; set; }

        public DocumentModel Document => _document;

        public ObservableCollection<ParagraphViewModel> Paragraphs { get; } = new();
        public ObservableCollection<string> AvailableStyleNames { get; } = new();

        public event Action<CursorContext>? CursorContextChanged;

        /// <summary>
        /// Поднимается когда изменилось форматирование параграфа.
        /// DocumentCanvas подписывается чтобы сбросить кеш лейаутов.
        /// </summary>
        public event Action? ParagraphFormatChanged;

        /// <summary>
        /// Структурное изменение документа (вставка/удаление блока-картинки и т.п.),
        /// при котором текст абзацев не менялся. Холст пересобирает раскладку БЕЗ очистки
        /// кэша абзацев — это быстро, в отличие от ParagraphFormatChanged.
        /// </summary>
        public event Action? StructureChanged;

        // Абзацы, затронутые последним char-форматированием. Канвас забирает этот список
        // в обработчике ParagraphFormatChanged, чтобы инвалидировать кэш раскладки ТОЛЬКО
        // у них, а не сбрасывать весь кэш и пересобирать весь документ (на больших
        // документах это давало секундный фриз на каждый коммит). null => затронутые
        // неизвестны (напр. форматирование ячейки) => канвас делает полный сброс.
        private IReadOnlyList<ParagraphViewModel>? _lastFormatAffected;

        /// <summary>
        /// Возвращает и сбрасывает список абзацев, затронутых последним форматированием.
        /// Сброс гарантирует, что следующее событие без явного списка приведёт к полному
        /// пересчёту, а не к использованию устаревшего списка.
        /// </summary>
        public IReadOnlyList<ParagraphViewModel>? TakeLastFormatAffected()
        {
            var v = _lastFormatAffected;
            _lastFormatAffected = null;
            return v;
        }

        // Во время drag маркеров отступа на линейке ApplyParaProperty вызывается на каждый
        // шаг мыши. Без батча это давало бы по снапшоту всего документа на каждый шаг (фриз).
        // BeginParagraphFormatBatch делает один снапшот на весь drag, ApplyParaProperty
        // внутри батча снапшот не повторяет, EndParagraphFormatBatch коммитит один раз.
        private bool _suppressFormatSnapshot;

        public void BeginParagraphFormatBatch()
        {
            if (_suppressFormatSnapshot) return;
            _suppressFormatSnapshot = true;
            BeginEditDelegate?.Invoke("Format paragraph");
        }

        public void EndParagraphFormatBatch()
        {
            if (!_suppressFormatSnapshot) return;
            _suppressFormatSnapshot = false;
            CommitEditDelegate?.Invoke();
        }

        // Идёт массовая перестройка всех VM-абзацев (загрузка/undo/структурные операции).
        // Канвас в это время пропускает поабзацную инкрементальную раскладку — она
        // бессмысленна (следом идёт общий пересбор) и даёт O(n^2) на больших документах.
        public bool IsBulkRebuilding { get; private set; }

        /// <summary>
        /// Устанавливается DocumentCanvas. Вызывается при изменении preview-шрифта.
        /// null = preview снят. Никаких изменений модели — canvas сам строит временный лейаут.
        /// </summary>
        // Делегаты live-preview шрифта. Канвас сам вычисляет затронутые абзацы и
        // ячейки по своему состоянию выделения, поэтому сюда передаются только команды
        // начала сессии, имя шрифта при наведении и завершение (коммит/отмена).
        public Action? BeginFontPreviewDelegate { get; set; }
        public Action<string>? PreviewFontFamilyDelegate { get; set; }
        public Action<bool>? EndFontPreviewDelegate { get; set; }

        // Возврат клавиатурного фокуса редактору (канвасу) после работы с лентой.
        public Action? FocusEditorDelegate { get; set; }

        public EditorViewMode ViewMode
        {
            get => _viewMode;
            set => this.RaiseAndSetIfChanged(ref _viewMode, value);
        }

        public double Zoom
        {
            get => _zoom;
            set
            {
                double clamped = Math.Max(0.25, Math.Min(5.0, value));
                this.RaiseAndSetIfChanged(ref _zoom, clamped);
                _document.Zoom = clamped;
            }
        }

        public bool IsFocusMode
        {
            get => _isFocusMode;
            set => this.RaiseAndSetIfChanged(ref _isFocusMode, value);
        }

        public bool IsFullscreen
        {
            get => _isFullscreen;
            set => this.RaiseAndSetIfChanged(ref _isFullscreen, value);
        }

        public bool IsReadOnly
        {
            get => _isReadOnly;
            set => this.RaiseAndSetIfChanged(ref _isReadOnly, value);
        }

        public CanvasSettings CanvasSettings => _document.CanvasSettings;
        public TextEditorPageSettings PageSettings => _document.PageSettings;

        public DocumentViewModel(
            DocumentModel document,
            ChunkManager chunkManager,
            AutoReplaceService autoReplace,
            SpellCheckService spellCheck)
        {
            _document = document ?? throw new ArgumentNullException(nameof(document));
            _chunkManager = chunkManager ?? throw new ArgumentNullException(nameof(chunkManager));
            _autoReplace = autoReplace ?? throw new ArgumentNullException(nameof(autoReplace));
            _spellCheck = spellCheck ?? throw new ArgumentNullException(nameof(spellCheck));

            _viewMode = document.ViewMode;
            _zoom = document.Zoom;

            RebuildStyleNames();
            RebuildParagraphViewModels();
        }

        // ── Активный параграф ─────────────────────────────────────────────

        public void SetActiveParagraph(ParagraphViewModel vm)
        {
            // Выходим из режима таблицы при клике на обычный параграф.
            TableActiveCellParagraph = null;
            _activeParagraph = vm;
            FireCursorContextChanged();
        }

        public void FireCursorContextChanged()
        {
            if (_activeParagraph is null) return;
            CursorContextChanged?.Invoke(BuildCursorContext(_activeParagraph));
        }

        /// <summary>
        /// Обновляет контекст линейки/риббона для параграфа внутри ячейки таблицы.
        /// Вызывается из DocumentCanvas при каждом изменении позиции каретки в таблице.
        /// </summary>
        public void FireTableCellCursorContext(ParagraphBlock cellPara, int selStart = 0, int selEnd = 0)
        {
            TableActiveCellParagraph = cellPara;
            var tempVm = new ParagraphViewModel(cellPara)
            {
                // Реальная позиция каретки/выделения в ячейке — иначе BuildCursorContext
                // всегда читал бы первый ран, и риббон показывал бы один шрифт независимо
                // от того, где стоит каретка и что выделено.
                SelectionStart = selStart,
                SelectionEnd = selEnd
            };
            CursorContextChanged?.Invoke(BuildCursorContext(tempVm));
        }

        private CursorContext BuildCursorContext(ParagraphViewModel pvm)
        {
            var ctx = new CursorContext();
            var block = pvm.Model;

            RunProperties? rp = null;

            if (pvm.SelectionEnd > pvm.SelectionStart)
            {
                int offset = 0;
                foreach (var chunk in block.Chunks)
                    foreach (var run in chunk.Runs)
                    {
                        if (offset + run.Text.Length > pvm.SelectionStart)
                        { rp = run.Properties; goto foundRun; }
                        offset += run.Text.Length;
                    }
                foundRun:;
            }
            else
            {
                // Каретка без выделения: свойства символа слева от каретки (как в Word).
                // SelectionStart == SelectionEnd — позиция каретки, переданная канвасом.
                int caretPos = Math.Max(0, pvm.SelectionStart - 1);
                rp = GetRunPropsAtOffset(block, caretPos);
            }

            if (rp is not null)
            {
                ctx.IsBold = rp.IsBold;
                ctx.IsItalic = rp.IsItalic;
                ctx.IsUnderline = rp.IsUnderline;
                ctx.IsStrikethrough = rp.IsStrikethrough;
                ctx.IsSuperscript = rp.IsSuperscript;
                ctx.IsSubscript = rp.IsSubscript;
                ctx.IsAllCaps = rp.IsAllCaps;
                ctx.TextColor = rp.TextColor ?? "#1A1A1A";
                ctx.HighlightColor = rp.HighlightColor;
                ctx.FontFamily = rp.FontFamily ?? ResolveStyleFontFamily(block.Properties.StyleName);
                ctx.FontSize = rp.FontSize ?? ResolveStyleFontSize(block.Properties.StyleName);
            }
            else
            {
                ctx.FontFamily = ResolveStyleFontFamily(block.Properties.StyleName);
                ctx.FontSize = ResolveStyleFontSize(block.Properties.StyleName);
                ctx.TextColor = "#1A1A1A";
            }

            // Если выделена блок-картинка — риббон показывает её выравнивание, а не абзаца.
            var imageAlign = GetSelectedImageAlignmentDelegate?.Invoke();
            ctx.Alignment = imageAlign ?? block.Properties.Alignment ?? TextAlignment.Left;
            ctx.StyleName = block.Properties.StyleName ?? "Normal";
            ctx.LeftIndentPt = block.Properties.LeftIndent ?? 0;
            ctx.FirstLineIndentPt = block.Properties.FirstLineIndent ?? 0;
            ctx.RightIndentPt = block.Properties.RightIndent ?? 0;
            ctx.HasSpaceBefore = ResolveEffectiveSpaceBefore(block) > 0;
            ctx.HasSpaceAfter = ResolveEffectiveSpaceAfter(block) > 0;
            return ctx;
        }

        private string ResolveStyleFontFamily(string? styleName)
        {
            var style = _document.FindStyle(styleName ?? "Normal");
            return style?.RunProperties?.FontFamily ?? "Times New Roman";
        }

        private double ResolveStyleFontSize(string? styleName)
        {
            var style = _document.FindStyle(styleName ?? "Normal");
            return style?.RunProperties?.FontSize ?? 14.0;
        }

        // Эффективный интервал перед абзацем (pt): собственное значение, иначе из цепочки стилей BasedOn.
        private double ResolveEffectiveSpaceBefore(ParagraphBlock block)
        {
            if (block.Properties.SpaceBefore.HasValue)
                return block.Properties.SpaceBefore.Value;

            string? name = block.Properties.StyleName ?? "Normal";
            for (int guard = 0; name is not null && guard < 16; guard++)
            {
                var style = _document.FindStyle(name);
                if (style is null) break;
                if (style.ParagraphProperties?.SpaceBefore.HasValue == true)
                    return style.ParagraphProperties.SpaceBefore.Value;
                name = style.BasedOn;
            }
            return 0.0;
        }

        // Эффективный интервал после абзаца (pt): собственное значение, иначе из цепочки стилей BasedOn.
        private double ResolveEffectiveSpaceAfter(ParagraphBlock block)
        {
            if (block.Properties.SpaceAfter.HasValue)
                return block.Properties.SpaceAfter.Value;

            string? name = block.Properties.StyleName ?? "Normal";
            for (int guard = 0; name is not null && guard < 16; guard++)
            {
                var style = _document.FindStyle(name);
                if (style is null) break;
                if (style.ParagraphProperties?.SpaceAfter.HasValue == true)
                    return style.ParagraphProperties.SpaceAfter.Value;
                name = style.BasedOn;
            }
            return 8.0;
        }

        // ── Управление параграфами ────────────────────────────────────────

        public ParagraphViewModel AddParagraphAfter(ParagraphViewModel after)
        {
            var section = _document.Sections[0];
            var newBlock = new ParagraphBlock();

            // Новый абзац наследует форматирование текущего: выравнивание, отступы
            // (левый/правый/первой строки), интервалы, межстрочный и стиль. Иначе при
            // Enter настройки абзаца сбрасывались на дефолтные.
            newBlock.Properties = after.Model.Properties.Clone();
            // Разрыв страницы перед абзацем не наследуем — иначе каждый Enter добавлял бы
            // новый разрыв. Это совпадает с поведением Word.
            newBlock.Properties.PageBreakBefore = false;

            int modelIndex = section.Blocks.IndexOf(after.Model);
            if (modelIndex < 0) section.Blocks.Add(newBlock);
            else section.Blocks.Insert(modelIndex + 1, newBlock);

            int vmIndex = Paragraphs.IndexOf(after);
            var newVm = CreateParagraphViewModel(newBlock);
            Paragraphs.Insert(vmIndex + 1, newVm);

            return newVm;
        }

        public ParagraphViewModel? DeleteParagraph(ParagraphViewModel target)
        {
            if (Paragraphs.Count <= 1) return null;

            int vmIndex = Paragraphs.IndexOf(target);
            if (vmIndex < 0) return null;

            _document.Sections[0].Blocks.Remove(target.Model);
            Paragraphs.RemoveAt(vmIndex);

            int focusIndex = Math.Max(0, vmIndex - 1);
            var focusVm = Paragraphs[focusIndex];
            focusVm.RequestFocus();
            return focusVm;
        }

        public void MergeParagraphWithPrevious(ParagraphViewModel target, string textToMerge)
        {
            int vmIndex = Paragraphs.IndexOf(target);
            if (vmIndex <= 0) return;

            var previous = Paragraphs[vmIndex - 1];
            int caretPosition = previous.PlainText?.Length ?? 0;

            // Дописываем текст следующего параграфа сохраняя форматирование обоих.
            int prevLen = previous.PlainText?.Length ?? 0;
            previous.Model.SpliceText(prevLen, prevLen, textToMerge);
            previous.RefreshPlainTextFromModel();

            _document.Sections[0].Blocks.Remove(target.Model);
            Paragraphs.RemoveAt(vmIndex);

            previous.RequestFocusAtPosition?.Invoke(caretPosition);
        }

        public void SelectAll() { foreach (var p in Paragraphs) p.IsSelected = true; }
        public void ClearSelection() { foreach (var p in Paragraphs) p.IsSelected = false; }

        public string? GetDocumentSelectedText()
        {
            var selected = Paragraphs.Where(p => p.IsSelected).ToList();
            if (selected.Count == 0) return null;
            return string.Join(Environment.NewLine, selected.Select(p => p.PlainText));
        }

        // ── ITextEditorCommandTarget: символы ─────────────────────────────

        public void ToggleBold() => ApplyCharProperty(p => p.IsBold = !p.IsBold);
        public void ToggleItalic() => ApplyCharProperty(p => p.IsItalic = !p.IsItalic);
        public void ToggleUnderline() => ApplyCharProperty(p => p.IsUnderline = !p.IsUnderline);
        public void ToggleStrikethrough() => ApplyCharProperty(p => p.IsStrikethrough = !p.IsStrikethrough);

        public void ToggleSuperscript()
            => ApplyCharProperty(p => { p.IsSuperscript = !p.IsSuperscript; if (p.IsSuperscript) p.IsSubscript = false; });

        public void ToggleSubscript()
            => ApplyCharProperty(p => { p.IsSubscript = !p.IsSubscript; if (p.IsSubscript) p.IsSuperscript = false; });

        public void ToggleAllCaps() => ApplyCharProperty(p => p.IsAllCaps = !p.IsAllCaps);

        /// <summary>
        /// Меняет регистр текста (сами буквы, не форматирование), сохраняя форматирование
        /// ранов. С выделением работает по его границам; без выделения применяется к слову
        /// под кареткой (как в Word), диапазон которого запрашивается у канваса.
        /// </summary>
        public void ChangeCase(TextCaseMode mode)
        {
            if (IsReadOnly) return;
            // Целевые диапазоны: (параграф, from, to) по выделению либо слово под кареткой.
            var targets = new System.Collections.Generic.List<(ParagraphViewModel Pvm, int From, int To)>();
            if (SelectionParagraphs.Count > 0)
            {
                for (int i = 0; i < SelectionParagraphs.Count; i++)
                {
                    var pvm = SelectionParagraphs[i];
                    int len = pvm.Model.GetPlainText().Length;
                    int from = (SelectionParagraphs.Count == 1 || i == 0) ? pvm.SelectionStart : 0;
                    int to = (SelectionParagraphs.Count == 1 || i == SelectionParagraphs.Count - 1) ? pvm.SelectionEnd : len;
                    from = Math.Clamp(from, 0, len);
                    to = Math.Clamp(to, from, len);
                    if (to > from) targets.Add((pvm, from, to));
                }
            }
            else
            {
                var word = GetCaretWordRangeDelegate?.Invoke();
                if (word is null) return;
                targets.Add(word.Value);
            }
            if (targets.Count == 0) return;

            // Собираем правки (paraId, from, старый текст, новый текст) по диапазонам.
            var edits = new System.Collections.Generic.List<(System.Guid ParaId, int From, string OldText, string NewText)>();
            foreach (var (pvm, from, to) in targets)
            {
                string full = pvm.Model.GetPlainText();
                int len = full.Length;
                int f = Math.Clamp(from, 0, len);
                int t = Math.Clamp(to, f, len);
                if (t <= f) continue;

                char[] chars = full.ToCharArray();
                ApplyCaseToRange(chars, f, t, mode);
                string newText = new string(chars, f, t - f);
                string oldText = full.Substring(f, t - f);
                if (oldText != newText)
                    edits.Add((pvm.Model.Id, f, oldText, newText));
            }
            if (edits.Count == 0) return;

            // Операционный путь: гранулярная команда в общий TextUndoStack (как и весь остальной
            // ввод/форматирование). Отмена идёт в общем хронологическом порядке, без снапшота.
            if (CommitTextEditsDelegate is not null && CommitTextEditsDelegate(edits, "Change case"))
            {
                FireCursorContextChanged();
                return;
            }

            // Запасной путь (канвас не подключён) — снапшот.
            BeginEditDelegate?.Invoke("Change case");
            foreach (var (pvm, from, to) in targets)
            {
                TransformParagraphRange(pvm.Model, from, to, mode);
                pvm.RefreshPlainTextFromModel();
            }
            CommitEditDelegate?.Invoke();
            _lastFormatAffected = targets.ConvertAll(t => t.Pvm);
            FireCursorContextChanged();
            ParagraphFormatChanged?.Invoke();
        }

        // Меняет регистр символов абзаца в диапазоне [from, to), записывая их обратно в раны.
        // Длина текста не меняется, поэтому структура ранов и форматирование сохраняются.
        private static void TransformParagraphRange(ParagraphBlock block, int from, int to, TextCaseMode mode)
        {
            string full = block.GetPlainText();
            int len = full.Length;
            from = Math.Clamp(from, 0, len);
            to = Math.Clamp(to, from, len);
            if (to <= from) return;

            char[] chars = full.ToCharArray();
            ApplyCaseToRange(chars, from, to, mode);

            int offset = 0;
            foreach (var chunk in block.Chunks)
                foreach (var run in chunk.Runs)
                {
                    int rl = run.Text.Length;
                    if (rl == 0) continue;
                    int runStart = offset;
                    int s = Math.Max(from, runStart);
                    int e = Math.Min(to, runStart + rl);
                    if (e > s)
                    {
                        var arr = run.Text.ToCharArray();
                        for (int g = s; g < e; g++)
                            arr[g - runStart] = chars[g];
                        run.Text = new string(arr);
                    }
                    offset += rl;
                }
            block.InvalidateAllChunks();
        }

        // Применяет режим регистра к диапазону массива символов с учётом контекста слева
        // (для Title — границы слов, для Sentence — конец предложения).
        private static void ApplyCaseToRange(char[] text, int from, int to, TextCaseMode mode)
        {
            switch (mode)
            {
                case TextCaseMode.Upper:
                    for (int i = from; i < to; i++) text[i] = char.ToUpper(text[i]);
                    break;

                case TextCaseMode.Lower:
                    for (int i = from; i < to; i++) text[i] = char.ToLower(text[i]);
                    break;

                case TextCaseMode.Toggle:
                    for (int i = from; i < to; i++)
                        text[i] = char.IsUpper(text[i]) ? char.ToLower(text[i]) : char.ToUpper(text[i]);
                    break;

                case TextCaseMode.Title:
                    {
                        bool prevSep = from == 0 || !char.IsLetterOrDigit(text[from - 1]);
                        for (int i = from; i < to; i++)
                        {
                            char c = text[i];
                            if (char.IsLetter(c))
                            {
                                text[i] = prevSep ? char.ToUpper(c) : char.ToLower(c);
                                prevSep = false;
                            }
                            else prevSep = !char.IsLetterOrDigit(c);
                        }
                        break;
                    }

                case TextCaseMode.Sentence:
                    {
                        // Определяем начало предложения по контексту слева от диапазона.
                        bool startSentence = true;
                        for (int j = from - 1; j >= 0; j--)
                        {
                            char pc = text[j];
                            if (pc == ' ' || pc == '\t') continue;
                            startSentence = pc == '.' || pc == '!' || pc == '?';
                            break;
                        }
                        for (int i = from; i < to; i++)
                        {
                            char c = text[i];
                            if (char.IsLetter(c))
                            {
                                text[i] = startSentence ? char.ToUpper(c) : char.ToLower(c);
                                startSentence = false;
                            }
                            else if (c == '.' || c == '!' || c == '?')
                                startSentence = true;
                        }
                        break;
                    }
            }
        }
        public void ToggleSmallCaps() => ApplyCharProperty(p => p.IsSmallCaps = !p.IsSmallCaps);
        public void ClearFormatting() => ApplyCharProperty(_ => { }, clearAll: true);

        public void SetTextColor(string color) => ApplyCharProperty(p => p.TextColor = color);
        public void SetHighlightColor(string? color) => ApplyCharProperty(p => p.HighlightColor = color);
        public void SetFontFamily(string font) => ApplyCharProperty(p => p.FontFamily = font);

        // Live-preview шрифта полностью реализован в DocumentCanvas: он знает полную
        // картину выделения (обычные абзацы + ячейки таблицы). Здесь — только проброс.
        public void BeginFontPreview() => BeginFontPreviewDelegate?.Invoke();

        public void PreviewFontFamily(string font) => PreviewFontFamilyDelegate?.Invoke(font);

        public void EndFontPreview(bool commit) => EndFontPreviewDelegate?.Invoke(commit);

        public void FocusEditor() => FocusEditorDelegate?.Invoke();

        /// <summary>
        /// Применяет шрифт к набору абзацев и диапазонов одним undo-снапшотом.
        /// Вызывается DocumentCanvas при коммите live-preview для всего выделения,
        /// включая абзацы и ячейки таблицы. Диапазон end &lt;= start трактуется как весь абзац.
        /// </summary>
        public void ApplyFontToBlocks(
            IReadOnlyList<(ParagraphBlock block, int start, int end)> targets, string font)
        {
            if (targets is null || targets.Count == 0) return;

            BeginEditDelegate?.Invoke("Format text");

            foreach (var (block, start, end) in targets)
            {
                if (block is null) continue;
                if (end > start)
                    ApplyCharPropertyToRange(block, start, end, p => p.FontFamily = font, false);
                else
                    ApplyCharPropertyToBlock(block, 0, 0, p => p.FontFamily = font, false);
            }

            CommitEditDelegate?.Invoke();
            FireCursorContextChanged();
            ParagraphFormatChanged?.Invoke();
        }

        public void SetFontSize(double size)
            => ApplyCharProperty(p => p.FontSize = size > 0 ? size : (double?)null);

        public void IncreaseFontSize()
        {
            double current = ResolveCurrentFontSize();
            ApplyCharProperty(p => p.FontSize = current + 2);
        }

        public void DecreaseFontSize()
        {
            double current = ResolveCurrentFontSize();
            ApplyCharProperty(p => p.FontSize = Math.Max(1, current - 2));
        }

        private double ResolveCurrentFontSize()
        {
            // Берём размер по позиции выделения (первый выделенный абзац, его SelectionStart),
            // как это делает BuildCursorContext. Раньше всегда читался первый ран абзаца —
            // из-за этого increase/decrease на выделении не на первом ране «застревали»:
            // читался старый размер первого рана, и каждое нажатие давало один и тот же шаг.
            ParagraphViewModel? pvm = SelectionParagraphs.Count > 0 ? SelectionParagraphs[0] : _activeParagraph;
            if (pvm is null) return 14;
            var block = pvm.Model;
            int pos = pvm.SelectionEnd > pvm.SelectionStart ? pvm.SelectionStart : 0;
            var rp = GetRunPropsAtOffset(block, pos);
            return rp?.FontSize ?? ResolveStyleFontSize(block.Properties.StyleName);
        }

        // Возвращает свойства рана, покрывающего символ в позиции charOffset.
        // Если позиция в конце текста — последний непустой ран.
        private static RunProperties? GetRunPropsAtOffset(ParagraphBlock block, int charOffset)
        {
            int offset = 0;
            foreach (var chunk in block.Chunks)
                foreach (var run in chunk.Runs)
                {
                    if (offset + run.Text.Length > charOffset)
                        return run.Properties;
                    offset += run.Text.Length;
                }
            for (int ci = block.Chunks.Count - 1; ci >= 0; ci--)
                if (block.Chunks[ci].Runs.Count > 0)
                    return block.Chunks[ci].Runs[^1].Properties;
            return null;
        }

        // ── ITextEditorCommandTarget: абзац ───────────────────────────────

        public void SetAlignment(TextAlignment a)
        {
            // Если выделена блок-картинка — выравниваем её в колонке, а не абзац.
            if (TrySetImageAlignmentDelegate?.Invoke(a) == true) return;
            ApplyParaProperty(p => p.Alignment = a);
        }

        // ── Команды выделенной картинки (контекстная вкладка «Формат») ─────
        public void SetImageWrapMode(WrapMode mode) => SetImageWrapModeDelegate?.Invoke(mode);
        public void SetImageLockAspect(bool locked) => SetImageLockAspectDelegate?.Invoke(locked);
        public void DeleteSelectedImage() => DeleteSelectedImageDelegate?.Invoke();
        public (WrapMode Wrap, bool LockAspect, Writersword.Modules.TextEditor.Models.Styles.TextAlignment Align)? GetSelectedImageInfo()
            => GetSelectedImageInfoDelegate?.Invoke();

        public void IncreaseIndent()
            => ApplyParaProperty(p => p.LeftIndent = (p.LeftIndent ?? 0) + 18);

        public void DecreaseIndent()
            => ApplyParaProperty(p => p.LeftIndent = Math.Max(0, (p.LeftIndent ?? 0) - 18));

        public void SetLineSpacing(double v)
            => ApplyParaProperty(p => { p.LineSpacingRule = LineSpacingRule.Auto; p.LineSpacingValue = v; });

        public void SetSpaceBefore(double pt) => ApplyParaProperty(p => p.SpaceBefore = pt);
        public void SetSpaceAfter(double pt) => ApplyParaProperty(p => p.SpaceAfter = pt);
        public void ApplyStyle(string name) => ApplyParaProperty(p => p.StyleName = name);

        public void SetLeftIndentPt(double pt) => ApplyParaProperty(p => p.LeftIndent = pt);
        public void SetFirstLineIndentPt(double pt) => ApplyParaProperty(p => p.FirstLineIndent = pt);
        public void SetRightIndentPt(double pt) => ApplyParaProperty(p => p.RightIndent = pt);

        /// <summary>
        /// Снимок свойств текущего абзаца (активного или абзаца активной ячейки) для пред-заполнения
        /// окна «Абзац». Null — нет активного абзаца.
        /// </summary>
        public ParagraphProperties? GetActiveParagraphProperties()
        {
            if (TableActiveCellParagraph is not null)
                return TableActiveCellParagraph.Properties.Clone();
            return _activeParagraph?.Model.Properties.Clone();
        }

        /// <summary>
        /// Применяет к выделенным абзацам поля окна «Абзац» (выравнивание, уровень, отступы,
        /// интервалы, междустрочный) одной командой отмены. Прочие поля (стиль, флаги страницы)
        /// не трогает.
        /// </summary>
        public void ApplyParagraphSettings(ParagraphProperties s) => ApplyParaProperty(p =>
        {
            p.Alignment = s.Alignment;
            p.OutlineLevel = s.OutlineLevel;
            p.LeftIndent = s.LeftIndent;
            p.RightIndent = s.RightIndent;
            p.FirstLineIndent = s.FirstLineIndent;
            p.SpaceBefore = s.SpaceBefore;
            p.SpaceAfter = s.SpaceAfter;
            p.LineSpacingRule = s.LineSpacingRule;
            p.LineSpacingValue = s.LineSpacingValue;
        });

        /// <summary>Ставит выделенным абзацам структурный уровень (0 — основной текст, 1…9).</summary>
        public void SetOutlineLevel(int level) => ApplyParaProperty(p => p.OutlineLevel = level);

        // ── ITextEditorCommandTarget: списки ──────────────────────────────

        public void ToggleBulletList()
        {
            if (_activeParagraph is null) return;
            var block = _activeParagraph.Model;
            block.ListProperties = block.ListProperties?.MarkerType == ListMarkerType.Bullet
                ? null
                : new ListProperties { ListId = Guid.NewGuid(), Level = 0, MarkerType = ListMarkerType.Bullet };
            FireCursorContextChanged();
        }

        public void ToggleNumberedList()
        {
            if (_activeParagraph is null) return;
            var block = _activeParagraph.Model;
            block.ListProperties = block.ListProperties?.MarkerType == ListMarkerType.Decimal
                ? null
                : new ListProperties { ListId = Guid.NewGuid(), Level = 0, MarkerType = ListMarkerType.Decimal };
            FireCursorContextChanged();
        }

        public void ToggleMultilevelList()
        {
            if (_activeParagraph is null) return;
            var block = _activeParagraph.Model;
            if (block.ListProperties is null)
                block.ListProperties = new ListProperties
                { ListId = Guid.NewGuid(), Level = 0, MarkerType = ListMarkerType.Decimal };
            else
                block.ListProperties.Level = (block.ListProperties.Level + 1) % 9;
            FireCursorContextChanged();
        }

        // ── ITextEditorCommandTarget: буфер обмена ────────────────────────

        public void Cut()
        {
            if (CutDelegate != null) CutDelegate.Invoke();
            else _activeParagraph?.RequestFocus();
        }

        public void Copy()
        {
            if (CopyDelegate != null) { CopyDelegate.Invoke(); return; }
            string? docText = GetDocumentSelectedText();
            if (docText is not null) { CopyToClipboardAsync(docText); return; }
            _activeParagraph?.RequestFocus();
        }

        public void Paste()
        {
            if (PasteDelegate != null) PasteDelegate.Invoke();
            else _activeParagraph?.RequestFocus();
        }

        void ITextEditorCommandTarget.SelectAll() => SelectAll();
        public void Undo() => UndoDelegate?.Invoke();
        public void Redo() => RedoDelegate?.Invoke();

        // ── ITextEditorCommandTarget: вставка ─────────────────────────────

        public void InsertTable(int rows, int columns) => InsertBlock(BuildEmptyTable(rows, columns));

        public void InsertTableBlock(TableBlock table) => InsertBlock(table);

        /// <summary>
        /// Вставляет TableBlock сразу после заданного якорного параграфа.
        /// В отличие от InsertBlock, не зависит от _activeParagraph — позволяет
        /// точно контролировать позицию при последовательной вставке нескольких блоков.
        /// Возвращает post-anchor ParagraphBlock (пустой параграф после таблицы),
        /// созданный NormalizeTableAnchors внутри RebuildParagraphViewModels.
        /// </summary>
        public ParagraphBlock? InsertTableBlockAfterParagraph(TableBlock table, ParagraphBlock anchor)
        {
            if (_document.Sections.Count == 0) return null;
            var section = _document.Sections[0];

            int idx = section.Blocks.IndexOf(anchor);
            if (idx >= 0)
                section.Blocks.Insert(idx + 1, table);
            else
                section.Blocks.Add(table);

            RebuildParagraphViewModels();

            int tblIdx = section.Blocks.IndexOf(table);
            if (tblIdx >= 0 && tblIdx + 1 < section.Blocks.Count
                && section.Blocks[tblIdx + 1] is ParagraphBlock postAnchor)
                return postAnchor;

            return null;
        }
        public void InsertImage(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath)) return;

            byte[] data;
            try { data = System.IO.File.ReadAllBytes(filePath); }
            catch { return; }

            InsertImageBytes(data, System.IO.Path.GetExtension(filePath));
        }

        /// <summary>
        /// Вставляет картинку из готовых байтов (файл или буфер обмена). Файл кладётся в проект,
        /// в документ добавляется ImageBlock под кареткой. Операция попадает в Undo.
        /// </summary>
        public void InsertImageBytes(byte[] data, string ext)
        {
            if (data is null || data.Length == 0) return;

            // Файлы картинок хранятся внутри проекта (ZIP), доступ — через контекст активной вкладки.
            var ctx = CoreServices.GetService<ITabCollection>()?.ActiveTab?.Context;
            if (ctx is null) return;

            if (string.IsNullOrWhiteSpace(ext)) ext = ".png";
            if (!ext.StartsWith(".")) ext = "." + ext;
            string fileName = $"img_{System.Guid.NewGuid():N}{ext}";
            ctx.WriteFile($"TextEditor/Images/{fileName}", data);

            // Размер по умолчанию берём из самого изображения (пиксели при 96 dpi -> пункты),
            // ширину разумно ограничиваем, сохраняя пропорции.
            double widthPt = 200, heightPt = 150;
            try
            {
                using var bmp = SkiaSharp.SKBitmap.Decode(data);
                if (bmp is { Width: > 0, Height: > 0 })
                {
                    widthPt = bmp.Width * 72.0 / 96.0;
                    heightPt = bmp.Height * 72.0 / 96.0;
                    const double maxWidthPt = 400.0;
                    if (widthPt > maxWidthPt)
                    {
                        double k = maxWidthPt / widthPt;
                        widthPt = maxWidthPt;
                        heightPt *= k;
                    }
                }
            }
            catch { }

            var image = new ImageBlock
            {
                ImageFileName = fileName,
                WidthPt = widthPt,
                HeightPt = heightPt
            };

            // Снимок до/после вставки — для Ctrl+Z.
            BeginEditDelegate?.Invoke("Вставка изображения");
            var section = _document.Sections[0];
            int idx = _activeParagraph is not null ? section.Blocks.IndexOf(_activeParagraph.Model) : -1;
            if (idx >= 0)
                section.Blocks.Insert(idx + 1, image);
            else
                section.Blocks.Add(image);
            CommitEditDelegate?.Invoke();

            StructureChanged?.Invoke();
        }
        public void RemoveImage(ImageBlock image)
        {
            if (image is null) return;
            foreach (var section in _document.Sections)
            {
                if (section.Blocks.Contains(image) || section.FloatingObjects.Contains(image))
                {
                    // Снимок до/после удаления — для Ctrl+Z.
                    BeginEditDelegate?.Invoke("Удаление изображения");
                    section.Blocks.Remove(image);
                    section.FloatingObjects.Remove(image);
                    CommitEditDelegate?.Invoke();
                    StructureChanged?.Invoke();
                    return;
                }
            }
        }

        public void InsertShape(ShapeType st) { }
        public void InsertFloatingTextBox() { }
        public void InsertPageBreak()
        {
            InsertBlock(new BreakBlock { BreakType = BreakType.Page });
            NormalizeBreakAnchors();

            // Уведомляем DocumentCanvas о том какой якорь нужно сфокусировать.
            // Canvas применит переход ПОСЛЕ перестройки _layouts, поэтому
            // прямой вызов RequestFocusAtPosition здесь не подходит — _layouts ещё старые.
            if (_document.Sections.Count == 0) return;
            var blocks = _document.Sections[0].Blocks;
            for (int i = 0; i < blocks.Count - 1; i++)
            {
                if (blocks[i] is not BreakBlock { BreakType: BreakType.Page }) continue;
                if (blocks[i + 1] is ParagraphBlock anchorBlock)
                {
                    OnPageBreakInserted?.Invoke(anchorBlock);
                    break;
                }
            }
        }
        public void InsertSectionBreak(BreakType t) => InsertBlock(new BreakBlock { BreakType = t });
        public void InsertFootnote() => AddAnnotation(InlineAnnotationType.Footnote);
        public void InsertEndnote() => AddAnnotation(InlineAnnotationType.Endnote);
        public void InsertBookmark(string name) => AddAnnotation(InlineAnnotationType.Bookmark, bookmarkName: name);
        public void InsertHyperlink(string url, string? text) => AddAnnotation(InlineAnnotationType.Hyperlink, url: url);
        public void InsertTOC() { }
        public void InsertComment(string text) => AddAnnotation(InlineAnnotationType.Comment, content: text);

        // ── Таблица ───────────────────────────────────────────────────────
        public void TableAddRow(bool above) => TableAddRowDelegate?.Invoke(above);
        public void TableAddColumn(bool left) => TableAddColDelegate?.Invoke(left);
        public void TableDeleteRow() => TableDeleteRowDelegate?.Invoke();
        public void TableDeleteColumn() => TableDeleteColDelegate?.Invoke();
        public void TableDelete() => TableDeleteDelegate?.Invoke();

        public void TableMergeCells() => TableMergeCellsDelegate?.Invoke();
        public void TableSplitCell() => TableSplitCellDelegate?.Invoke();
        public void TableSetCellHAlign(Writersword.Modules.TextEditor.Models.Styles.TextAlignment align)
            => TableSetCellHAlignDelegate?.Invoke(align);
        public void TableSetCellVAlign(int vAlign) => TableSetCellVAlignDelegate?.Invoke(vAlign);
        public void TableSetCellBackground(string? color) => TableSetCellBackgroundDelegate?.Invoke(color);
        public void TableSetCellBorder(string side, BorderStyle style, double thicknessPt, string? color)
            => TableSetCellBorderDelegate?.Invoke(side, style, thicknessPt, color);
        public void TableSetColumnWidth(double widthMm) => TableSetColumnWidthDelegate?.Invoke(widthMm);
        public void TableSetRowHeight(double heightPt) => TableSetRowHeightDelegate?.Invoke(heightPt);
        public void TableAutoFit() => TableAutoFitDelegate?.Invoke();
        public void TableDistributeColumns() => TableDistributeColsDelegate?.Invoke();
        public void TableDistributeRows() => TableDistributeRowsDelegate?.Invoke();
        public void TableSort(int columnIndex, bool ascending) => TableSortDelegate?.Invoke(columnIndex, ascending);

        public void TableToggleRepeatHeader()
        {
            var table = ActiveTable;
            if (table is null) return;
            table.RepeatHeader = !table.RepeatHeader;
            FireParagraphFormatChanged();
        }

        public bool TableGetRepeatHeader() => ActiveTable?.RepeatHeader ?? false;

        public void TableToggleSplitMode()
        {
            var table = ActiveTable;
            if (table is null) return;
            table.SplitMode = table.SplitMode == Models.Document.TableSplitMode.ByRow
                ? Models.Document.TableSplitMode.ByCell
                : Models.Document.TableSplitMode.ByRow;
            FireParagraphFormatChanged();
        }
        public bool TableGetSplitModeByCell() =>
            ActiveTable?.SplitMode == Models.Document.TableSplitMode.ByCell;

        public void TableSetBreakLabel(string? text)
        {
            var table = ActiveTable; if (table is null) return;
            table.BreakLabel = string.IsNullOrWhiteSpace(text) ? null : text;
            FireParagraphFormatChanged();
        }
        public void TableSetContinuationLabel(string? text)
        {
            var table = ActiveTable; if (table is null) return;
            table.ContinuationLabel = string.IsNullOrWhiteSpace(text) ? null : text;
            FireParagraphFormatChanged();
        }
        public string? TableGetBreakLabel() => ActiveTable?.BreakLabel;
        public string? TableGetContinuationLabel() => ActiveTable?.ContinuationLabel;

        public void RebuildParagraphViewModelsPublic() => RebuildParagraphViewModels();
        public void FireParagraphFormatChanged() => ParagraphFormatChanged?.Invoke();

        // ── Операции с таблицами (модель) ─────────────────────────────────

        public void TableAddRowBelow(TableBlock table, int afterRow)
        {
            int insertRow = afterRow + 1;
            foreach (var cell in table.Cells)
                if (cell.Row >= insertRow) cell.Row++;
            for (int c = 0; c < table.ColumnCount; c++)
                table.Cells.Add(new TableCell { Row = insertRow, Column = c });
            table.RowCount++;
        }

        public void TableAddRowAbove(TableBlock table, int beforeRow)
        {
            foreach (var cell in table.Cells)
                if (cell.Row >= beforeRow) cell.Row++;
            for (int c = 0; c < table.ColumnCount; c++)
                table.Cells.Add(new TableCell { Row = beforeRow, Column = c });
            table.RowCount++;
        }

        public void TableDeleteRow(TableBlock table, int row)
        {
            if (table.RowCount <= 1)
            {
                _document.Sections[0].Blocks.Remove(table);
                RebuildParagraphViewModels();
                return;
            }
            table.Cells.RemoveAll(c => c.Row == row);
            foreach (var cell in table.Cells)
                if (cell.Row > row) cell.Row--;
            table.RowCount--;
        }

        public void TableAddColumnRight(TableBlock table, int afterCol)
        {
            int insertCol = afterCol + 1;
            foreach (var cell in table.Cells)
                if (cell.Column >= insertCol) cell.Column++;
            for (int r = 0; r < table.RowCount; r++)
                table.Cells.Add(new TableCell { Row = r, Column = insertCol });
            table.Columns.Insert(insertCol,
                new TableColumnDefinition { WidthType = TableColumnWidthType.Auto });
            table.ColumnCount++;
        }

        public void TableAddColumnLeft(TableBlock table, int beforeCol)
        {
            foreach (var cell in table.Cells)
                if (cell.Column >= beforeCol) cell.Column++;
            for (int r = 0; r < table.RowCount; r++)
                table.Cells.Add(new TableCell { Row = r, Column = beforeCol });
            table.Columns.Insert(beforeCol,
                new TableColumnDefinition { WidthType = TableColumnWidthType.Auto });
            table.ColumnCount++;
        }

        public void TableDeleteColumn(TableBlock table, int col)
        {
            if (table.ColumnCount <= 1)
            {
                _document.Sections[0].Blocks.Remove(table);
                RebuildParagraphViewModels();
                return;
            }
            table.Cells.RemoveAll(c => c.Column == col);
            foreach (var cell in table.Cells)
                if (cell.Column > col) cell.Column--;
            if (col < table.Columns.Count)
                table.Columns.RemoveAt(col);
            table.ColumnCount--;
        }

        public void TableMergeCells(TableBlock table,
            int startRow, int startCol, int endRow, int endCol)
        {
            var mainCell = table.GetCell(startRow, startCol);
            if (mainCell is null) return;

            for (int r = startRow; r <= endRow; r++)
            {
                for (int c = startCol; c <= endCol; c++)
                {
                    if (r == startRow && c == startCol) continue;
                    var cell = table.GetCell(r, c);
                    if (cell is null) continue;
                    bool isEmpty = cell.Paragraphs.Count == 1
                        && string.IsNullOrEmpty(GetCellPlainText(cell));
                    if (!isEmpty)
                        foreach (var para in cell.Paragraphs)
                            mainCell.Paragraphs.Add(para);
                    table.Cells.Remove(cell);
                }
            }
            mainCell.RowSpan = endRow - startRow + 1;
            mainCell.ColSpan = endCol - startCol + 1;
        }

        public void TableSplitCell(TableBlock table, int row, int col)
        {
            var mainCell = table.GetCell(row, col);
            if (mainCell is null || (mainCell.RowSpan == 1 && mainCell.ColSpan == 1)) return;

            int rowSpan = mainCell.RowSpan;
            int colSpan = mainCell.ColSpan;
            mainCell.RowSpan = 1;
            mainCell.ColSpan = 1;

            for (int r = row; r < row + rowSpan; r++)
                for (int c = col; c < col + colSpan; c++)
                {
                    if (r == row && c == col) continue;
                    table.Cells.Add(new TableCell { Row = r, Column = c });
                }
        }

        public void TableSetColumnWidth(TableBlock table, int colIndex, double widthMm)
        {
            if (colIndex < 0 || colIndex >= table.Columns.Count) return;
            table.Columns[colIndex].WidthType = TableColumnWidthType.Fixed;
            table.Columns[colIndex].WidthValue = Math.Max(5.0, widthMm);
        }

        public TableBlock? FindTable(Func<TableBlock, bool> predicate)
        {
            foreach (var section in _document.Sections)
                foreach (var block in section.Blocks)
                    if (block is TableBlock t && predicate(t))
                        return t;
            return null;
        }

        // ── ITextEditorCommandTarget: макет ───────────────────────────────

        public void SetPageSize(PaperSize size)
        {
            _document.PageSettings.ApplyPaperSize(size);
            this.RaisePropertyChanged(nameof(PageSettings));
        }

        public void SetPageOrientation(PageOrientation o)
        {
            _document.PageSettings.Orientation = o;
            this.RaisePropertyChanged(nameof(PageSettings));
        }

        public void SetPageMargins(double top, double bottom, double left, double right)
        {
            _document.PageSettings.MarginTopMm = top;
            _document.PageSettings.MarginBottomMm = bottom;
            _document.PageSettings.MarginLeftMm = left;
            _document.PageSettings.MarginRightMm = right;
            this.RaisePropertyChanged(nameof(PageSettings));
        }

        public void SetColumns(int count) => _document.ColumnSettings.ColumnCount = count;

        // ── ITextEditorCommandTarget: вид ─────────────────────────────────

        public void SetZoom(double zoom) => Zoom = zoom;

        public void SetViewMode(EditorViewMode mode)
        {
            ViewMode = mode;
            _document.ViewMode = mode;
        }

        public void ToggleFullscreen() => IsFullscreen = !IsFullscreen;
        public void ToggleFocusMode() => IsFocusMode = !IsFocusMode;

        public void SetCanvasTheme(CanvasThemePreset preset)
        {
            _document.CanvasSettings.ApplyPreset(preset);
            this.RaisePropertyChanged(nameof(CanvasSettings));
        }

        public void SetCanvasColors(string pageBackground, string textColor)
        {
            _document.CanvasSettings.Preset = CanvasThemePreset.Custom;
            _document.CanvasSettings.PageBackgroundColor = pageBackground;
            _document.CanvasSettings.DefaultTextColor = textColor;
            this.RaisePropertyChanged(nameof(CanvasSettings));
        }

        public void ZoomIn()
        {
            double[] steps = { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 2.5, 3.0, 4.0, 5.0 };
            foreach (double step in steps)
                if (step > Zoom + 0.01) { Zoom = step; return; }
        }

        public void ZoomOut()
        {
            double[] steps = { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 2.5, 3.0, 4.0, 5.0 };
            for (int i = steps.Length - 1; i >= 0; i--)
                if (steps[i] < Zoom - 0.01) { Zoom = steps[i]; return; }
        }

        public void ZoomReset() => Zoom = 1.0;

        // ── ITextEditorCommandTarget: инструменты ─────────────────────────

        public void OpenFind() { }
        public void OpenFindReplace() { }
        public void RunSpellCheck() { }
        public void ShowWordCount() { }
        public void Print() { }
        public void ExportToPdf() { }
        public void ExportToDocx() { }
        public void ExportToTxt() { }
        public void ExportToMarkdown() { }

        // ── Внутренние методы ─────────────────────────────────────────────

        private void ApplyCharProperty(Action<RunProperties> mutate, bool clearAll = false)
        {
            if (IsReadOnly) return;
            _log.Information("[FONT] ApplyCharProperty: tableCell={TC} clearAll={CA} granularDelegate={GD} active={AP} selStart={SS} selEnd={SE}",
                TableActiveCellParagraph is not null, clearAll,
                CommitRunPropertyGranularDelegate is not null, _activeParagraph is not null,
                _selectionStart, _selectionEnd);
            // Ячейка таблицы: применяем операционно ко ВСЕМ выделенным абзацам ячейки (диапазоны
            // отдаёт канвас), а не к одному активному — иначе формат «через строку» не проходил.
            // Без выделения / при очистке — старый снапшотный путь (умеет ставить свойство пустому
            // рану для «ожидающего» форматирования).
            if (TableActiveCellParagraph is not null && !clearAll
                && CommitRunPropertyGranularDelegate is not null)
            {
                var cellRanges = GetCellSelectionRangesDelegate?.Invoke();
                if (cellRanges is { Count: > 0 }
                    && CommitRunPropertyGranularDelegate(cellRanges, mutate, "Format text"))
                {
                    FireCursorContextChanged();
                    return;
                }
            }

            // Ячейка таблицы, очистка форматирования или нет лёгкого пути — снапшот (как было).
            if (TableActiveCellParagraph is not null || clearAll
                || CommitRunPropertyGranularDelegate is null || _activeParagraph is null)
            {
                ApplyCharPropertySnapshot(mutate, clearAll);
                return;
            }

            // Собираем диапазоны (paraId, from, to) — та же логика, что и в снапшотном пути.
            var ranges = new System.Collections.Generic.List<(System.Guid, int, int)>();
            if (SelectionParagraphs.Count > 1)
            {
                for (int idx = 0; idx < SelectionParagraphs.Count; idx++)
                {
                    var pvm = SelectionParagraphs[idx];
                    bool isFirst = idx == 0;
                    bool isLast = idx == SelectionParagraphs.Count - 1;
                    int len = pvm.Model.GetPlainText().Length;
                    int s, e;
                    if (!isFirst && !isLast) { s = 0; e = len; }
                    else { s = isFirst ? pvm.SelectionStart : 0; e = isLast ? pvm.SelectionEnd : len; }
                    s = Math.Clamp(s, 0, len);
                    e = Math.Clamp(e, 0, len);
                    if (e > s) ranges.Add((pvm.Model.Id, s, e));
                }
            }
            else
            {
                int len = _activeParagraph.Model.GetPlainText().Length;
                int s, e;
                if (_selectionEnd > _selectionStart) { s = _selectionStart; e = _selectionEnd; }
                else { s = 0; e = len; }
                s = Math.Clamp(s, 0, len);
                e = Math.Clamp(e, 0, len);
                if (e > s) ranges.Add((_activeParagraph.Model.Id, s, e));
            }

            // Нечего форматировать гранулярно (пустой абзац, пустое выделение) — снапшот:
            // он умеет проставить свойство пустому рану для «ожидающего» форматирования.
            if (ranges.Count == 0)
            {
                ApplyCharPropertySnapshot(mutate, clearAll);
                return;
            }

            bool handled = CommitRunPropertyGranularDelegate(ranges, mutate, "Format text");
            if (!handled)
            {
                ApplyCharPropertySnapshot(mutate, clearAll);
                return;
            }

            // Модель и раскладку обновил канвас (команда + точечный пересбор). Здесь только
            // обновляем состояние тулбара под кареткой.
            FireCursorContextChanged();
        }

        // Прежний снапшотный путь форматирования (полная сериализация документа для отмены).
        // Используется для ячеек таблицы, очистки форматирования и пустых абзацев.
        private void ApplyCharPropertySnapshot(Action<RunProperties> mutate, bool clearAll = false)
        {
            // Режим ячейки таблицы — применяем только к активной ячейке.
            if (TableActiveCellParagraph is not null)
            {
                _log.Information("[FONT] Snapshot cell apply: para len={L} selStart={SS} selEnd={SE}",
                    TableActiveCellParagraph.GetPlainText().Length, _selectionStart, _selectionEnd);
                BeginEditDelegate?.Invoke("Format text");
                ApplyCharPropertyToBlock(TableActiveCellParagraph, _selectionStart, _selectionEnd, mutate, clearAll);
                CommitEditDelegate?.Invoke();
                FireCursorContextChanged();
                ParagraphFormatChanged?.Invoke();
                return;
            }

            if (_activeParagraph is null) return;

            BeginEditDelegate?.Invoke("Format text");

            if (SelectionParagraphs.Count > 1)
            {
                for (int idx = 0; idx < SelectionParagraphs.Count; idx++)
                {
                    var pvm = SelectionParagraphs[idx];
                    bool isFirst = idx == 0;
                    bool isLast = idx == SelectionParagraphs.Count - 1;

                    if (!isFirst && !isLast)
                    {
                        // Средний параграф — целиком (selEnd <= selStart = "нет выделения" = все раны).
                        ApplyCharPropertyToBlock(pvm.Model, 0, 0, mutate, clearAll);
                    }
                    else
                    {
                        // Первый: от SelectionStart до конца (int.MaxValue clamp-ится внутри).
                        // Последний: от 0 до SelectionEnd.
                        int s = isFirst ? pvm.SelectionStart : 0;
                        int e = isLast ? pvm.SelectionEnd : int.MaxValue;
                        if (e > s)
                            ApplyCharPropertyToRange(pvm.Model, s, e, mutate, clearAll);
                    }
                }
            }
            else
            {
                ApplyCharPropertyToBlock(_activeParagraph.Model, _selectionStart, _selectionEnd, mutate, clearAll);
            }

            CommitEditDelegate?.Invoke();
            FireCursorContextChanged();
            // Затронуты только эти абзацы — канвас инвалидирует кэш раскладки точечно,
            // а не сбрасывает весь документ.
            _lastFormatAffected = SelectionParagraphs.Count > 1
                ? SelectionParagraphs.ToList()
                : new[] { _activeParagraph };
            ParagraphFormatChanged?.Invoke();
        }

        private static void ApplyCharPropertyToBlock(
            ParagraphBlock block, int selStart, int selEnd,
            Action<RunProperties> mutate, bool clearAll)
        {
            if (selEnd > selStart)
            {
                ApplyCharPropertyToRange(block, selStart, selEnd, mutate, clearAll);
                return;
            }
            // Нет выделения — применяем ко всем ранам параграфа.
            foreach (var chunk in block.Chunks)
            {
                foreach (var run in chunk.Runs)
                {
                    if (clearAll) run.Properties = null;
                    else
                    {
                        run.Properties ??= new RunProperties();
                        mutate(run.Properties);
                        if (run.Properties.IsDefault()) run.Properties = null;
                    }
                }
                chunk.InvalidateLength();
            }
        }

        private void ApplyParaProperty(Action<ParagraphProperties> mutate)
        {
            if (IsReadOnly) return;
            // Режим таблицы: применяем к параграфу активной ячейки (снапшотный путь как был).
            if (TableActiveCellParagraph is not null)
            {
                if (!_suppressFormatSnapshot)
                    BeginEditDelegate?.Invoke("Format paragraph");
                mutate(TableActiveCellParagraph.Properties);
                if (!_suppressFormatSnapshot)
                    CommitEditDelegate?.Invoke();
                var tempVm = new ParagraphViewModel(TableActiveCellParagraph);
                CursorContextChanged?.Invoke(BuildCursorContext(tempVm));
                ParagraphFormatChanged?.Invoke();
                return;
            }

            // Список затронутых абзацев.
            var targets = SelectionParagraphs.Count > 0
                ? SelectionParagraphs.ToList()
                : (_activeParagraph is not null
                    ? new System.Collections.Generic.List<ParagraphViewModel> { _activeParagraph }
                    : null);
            if (targets is null || targets.Count == 0) return;

            // Операционный путь (одиночное форматирование, не во время drag отступов на линейке):
            // гранулярная команда в общий TextUndoStack. Ctrl+Z мгновенный, без тяжёлого снапшота
            // всего документа — на больших документах это и убирало фриз при отмене.
            if (!_suppressFormatSnapshot && CommitParagraphPropertyGranularDelegate is not null)
            {
                var edits = new System.Collections.Generic.List<(System.Guid,
                    Action<ParagraphProperties>, Action<ParagraphProperties>)>();
                foreach (var pvm in targets)
                {
                    var old = pvm.Model.Properties.Clone();
                    edits.Add((pvm.Model.Id, mutate, p => p.CopyFrom(old)));
                }
                if (CommitParagraphPropertyGranularDelegate(edits, "Format paragraph"))
                {
                    FireCursorContextChanged();
                    return;
                }
            }

            // Снапшотный путь: батч-drag отступов (один снапшот на весь drag) или нет делегата.
            if (!_suppressFormatSnapshot)
                BeginEditDelegate?.Invoke("Format paragraph");
            foreach (var pvm in targets)
                mutate(pvm.Model.Properties);
            if (!_suppressFormatSnapshot)
                CommitEditDelegate?.Invoke();
            FireCursorContextChanged();
            // Затронуты только эти абзацы — канвас инвалидирует кэш раскладки точечно,
            // а не сбрасывает весь документ (на больших документах это убирает фриз).
            _lastFormatAffected = targets;
            ParagraphFormatChanged?.Invoke();
        }

        private void InsertBlock(BlockModel block)
        {
            if (IsReadOnly) return;
            if (_document.Sections.Count == 0) return;
            var section = _document.Sections[0];

            if (_activeParagraph is not null)
            {
                int idx = section.Blocks.IndexOf(_activeParagraph.Model);
                if (idx >= 0)
                {
                    section.Blocks.Insert(idx + 1, block);
                    RebuildParagraphViewModels();
                    return;
                }
            }

            section.Blocks.Add(block);
            RebuildParagraphViewModels();
        }

        private static void ApplyCharPropertyToRange(
    ParagraphBlock block, int selStart, int selEnd,
    Action<RunProperties> mutate, bool clearAll)
        {
            var chars = new List<(char ch, RunProperties? props)>();
            foreach (var chunk in block.Chunks)
                foreach (var run in chunk.Runs)
                    foreach (var ch in run.Text)
                        chars.Add((ch, run.Properties?.Clone()));

            int len = chars.Count;
            selStart = Math.Max(0, Math.Min(selStart, len));
            selEnd = Math.Max(selStart, Math.Min(selEnd, len));

            for (int i = selStart; i < selEnd; i++)
            {
                var (ch, props) = chars[i];
                if (clearAll)
                {
                    chars[i] = (ch, null);
                }
                else
                {
                    var newProps = props?.Clone() ?? new RunProperties();
                    mutate(newProps);
                    chars[i] = (ch, newProps.IsDefault() ? null : newProps);
                }
            }

            block.Chunks.Clear();
            var newChunk = new TextChunk();
            block.Chunks.Add(newChunk);

            if (chars.Count == 0)
            {
                newChunk.Runs.Add(new RunModel { Text = string.Empty });
                block.InvalidateAllChunks();
                return;
            }

            var sb = new System.Text.StringBuilder();
            var currentProps = chars[0].props;

            foreach (var (ch, props) in chars)
            {
                bool sameProps = RunPropertiesEqualValue(props, currentProps);
                if (!sameProps)
                {
                    newChunk.Runs.Add(new RunModel { Text = sb.ToString(), Properties = currentProps });
                    sb.Clear();
                    currentProps = props;
                }
                sb.Append(ch);
            }

            newChunk.Runs.Add(new RunModel { Text = sb.ToString(), Properties = currentProps });
            block.InvalidateAllChunks();
        }

        private static bool RunPropertiesEqualValue(RunProperties? a, RunProperties? b)
        {
            bool aDefault = a is null || a.IsDefault();
            bool bDefault = b is null || b.IsDefault();
            if (aDefault && bDefault) return true;
            if (aDefault || bDefault) return false;
            return a!.FontFamily == b!.FontFamily
                && a.FontSize == b.FontSize
                && a.IsBold == b.IsBold
                && a.IsItalic == b.IsItalic
                && a.IsUnderline == b.IsUnderline
                && a.IsStrikethrough == b.IsStrikethrough
                && a.IsSuperscript == b.IsSuperscript
                && a.IsSubscript == b.IsSubscript
                && a.IsAllCaps == b.IsAllCaps
                && a.IsSmallCaps == b.IsSmallCaps
                && a.TextColor == b.TextColor
                && a.HighlightColor == b.HighlightColor
                && a.Language == b.Language;
        }

        /// <summary>
        /// Гарантирует наличие пустого ParagraphBlock до и после каждой TableBlock.
        /// Якоря невидимы визуально (нулевая высота в layout) но нужны для:
        /// — позиционирования каретки у края таблицы по клику
        /// — вставки параграфов выше/ниже таблицы через Enter
        /// Якорь после таблицы защищён от удаления в DocumentCanvas.
        /// </summary>
        private void NormalizeTableAnchors()
        {
            if (_document.Sections.Count == 0) return;
            var blocks = _document.Sections[0].Blocks;

            for (int i = blocks.Count - 1; i >= 0; i--)
            {
                if (blocks[i] is not TableBlock) continue;

                // Якорь после таблицы: пустой ParagraphBlock (текст пустой).
                // Проверяем через GetPlainText() — Chunks.Count всегда >= 1 даже у нового блока.
                bool hasAfter = i + 1 < blocks.Count
                    && blocks[i + 1] is ParagraphBlock afterPb
                    && string.IsNullOrEmpty(afterPb.GetPlainText());
                if (!hasAfter)
                    blocks.Insert(i + 1, new ParagraphBlock());

                // Якорь перед таблицей: пустой ParagraphBlock.
                // Обычный параграф с текстом не считается якорем.
                bool hasBefore = i > 0
                    && blocks[i - 1] is ParagraphBlock beforePb
                    && string.IsNullOrEmpty(beforePb.GetPlainText());
                if (!hasBefore)
                    blocks.Insert(i, new ParagraphBlock());
            }
        }

        private void NormalizeBreakAnchors()
        {
            if (_document.Sections.Count == 0) return;
            var blocks = _document.Sections[0].Blocks;

            // Проходим с конца чтобы Insert не сдвигал ещё не обработанные индексы.
            for (int i = blocks.Count - 1; i >= 0; i--)
            {
                if (blocks[i] is not BreakBlock { BreakType: BreakType.Page }) continue;

                // Якорь нужен только если после разрыва вообще нет параграфа
                // (разрыв в конце документа или за ним стоит не-параграф блок).
                // Если параграф уже есть — он и будет якорём: Backspace в его начале
                // вызовет DeleteBreakWithAnchor через IsBreakAnchor.
                bool hasFollowingParagraph = i + 1 < blocks.Count
                    && blocks[i + 1] is ParagraphBlock;
                if (!hasFollowingParagraph)
                    blocks.Insert(i + 1, new ParagraphBlock());
            }
        }

        /// <summary>
        /// Удаляет разрыв страницы вместе с его параграфом-якорем.
        /// Вызывается из DocumentCanvas при Backspace в начале якоря
        /// или при Delete в конце параграфа непосредственно перед разрывом.
        /// </summary>
        public void DeleteBreakWithAnchor(ParagraphViewModel anchor)
        {
            var blocks = _document.Sections[0].Blocks;
            int anchorIdx = blocks.IndexOf(anchor.Model);
            if (anchorIdx <= 0 || blocks[anchorIdx - 1] is not BreakBlock) return;

            // Удаляем сначала якорь (больший индекс), потом разрыв (меньший).
            blocks.RemoveAt(anchorIdx);
            blocks.RemoveAt(anchorIdx - 1);

            int vmIdx = Paragraphs.IndexOf(anchor);
            if (vmIdx >= 0) Paragraphs.RemoveAt(vmIdx);

            // Перемещаем каретку в конец предыдущего параграфа.
            int focusIdx = Math.Max(0, vmIdx - 1);
            if (focusIdx < Paragraphs.Count)
                Paragraphs[focusIdx].RequestFocusAtPosition?.Invoke(
                    Paragraphs[focusIdx].PlainText?.Length ?? 0);
        }

        private void AddAnnotation(
            InlineAnnotationType type,
            string? bookmarkName = null,
            string? content = null,
            string? url = null)
        {
            _document.Annotations.Add(new InlineAnnotation
            {
                Type = type,
                BookmarkName = bookmarkName,
                Content = content,
                Url = url
            });
        }

        private static TableBlock BuildEmptyTable(int rows, int columns)
        {
            var table = new TableBlock { RowCount = rows, ColumnCount = columns };
            for (int c = 0; c < columns; c++)
                table.Columns.Add(new TableColumnDefinition { WidthType = TableColumnWidthType.Auto });
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < columns; c++)
                    table.Cells.Add(new TableCell { Row = r, Column = c });
            return table;
        }

        private static string GetCellPlainText(TableCell cell)
        {
            var sb = new StringBuilder();
            foreach (var para in cell.Paragraphs)
                foreach (var chunk in para.Chunks)
                    foreach (var run in chunk.Runs)
                        sb.Append(run.Text);
            return sb.ToString();
        }

        private void RebuildStyleNames()
        {
            AvailableStyleNames.Clear();
            foreach (var style in _document.Styles)
                AvailableStyleNames.Add(
                    style.DisplayName.Length > 0 ? style.DisplayName : style.Name);
        }

        private void RebuildParagraphViewModels()
        {
            NormalizeTableAnchors();
            NormalizeBreakAnchors();
            IsBulkRebuilding = true;
            try
            {
                Paragraphs.Clear();
                if (_document.Sections.Count == 0) return;
                foreach (var block in _document.Sections[0].Blocks)
                    if (block is ParagraphBlock para)
                        Paragraphs.Add(CreateParagraphViewModel(para));
            }
            finally
            {
                IsBulkRebuilding = false;
            }
        }

        public void DeleteSelectedParagraphs()
        {
            var toDelete = Paragraphs.Where(p => p.IsSelected).ToList();
            if (toDelete.Count == 0) return;

            int firstIdx = Paragraphs.IndexOf(toDelete[0]);
            int focusIdx = Math.Max(0, firstIdx - 1);

            var blocks = _document.Sections[0].Blocks;
            foreach (var pvm in toDelete)
            {
                // Если удаляемый параграф — якорь разрыва страницы, удаляем и сам BreakBlock.
                int blockIdx = blocks.IndexOf(pvm.Model);
                if (blockIdx > 0 && blocks[blockIdx - 1] is BreakBlock { BreakType: BreakType.Page })
                    blocks.RemoveAt(blockIdx - 1);  // BreakBlock удалён; якорь сместился на -1

                blocks.Remove(pvm.Model);
                Paragraphs.Remove(pvm);
            }

            if (Paragraphs.Count == 0)
            {
                var empty = new ParagraphBlock();
                blocks.Add(empty);
                Paragraphs.Add(CreateParagraphViewModel(empty));
            }

            Paragraphs[Math.Min(focusIdx, Paragraphs.Count - 1)].RequestFocus();
        }

        private ParagraphViewModel CreateParagraphViewModel(ParagraphBlock block)
        {
            var vm = new ParagraphViewModel(block);
            vm.RequestAddAfter = AddParagraphAfter;
            vm.RequestDelete = pvm => DeleteParagraph(pvm);
            vm.RequestMergeWithPrevious = MergeParagraphWithPrevious;
            vm.RequestSelectAll = SelectAll;
            vm.RequestClearSelection = ClearSelection;
            vm.RequestGetDocumentSelectedText = GetDocumentSelectedText;
            vm.OnActivated = SetActiveParagraph;
            vm.RequestDeleteSelected = DeleteSelectedParagraphs;
            vm.OnSelectionChanged = _ => FireCursorContextChanged();
            return vm;
        }

        private static async void CopyToClipboardAsync(string text)
        {
            try
            {
                var lifetime = Avalonia.Application.Current?.ApplicationLifetime
                    as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
                var clipboard = lifetime?.MainWindow?.Clipboard;
                if (clipboard is not null)
                    await clipboard.SetTextAsync(text);
            }
            catch { }
        }

        // ── Расширенные методы модели таблицы ────────────────────────────

        public void TableCellSetBackground(TableCell cell, string? color)
        {
            cell.BackgroundColor = color;
            ParagraphFormatChanged?.Invoke();
        }

        public static void TableCellSetBorder(TableCell cell, string side,
            BorderStyle style, double thicknessPt, string? color)
        {
            var b = cell.Borders;
            if (color is not null) b.Color = color;
            b.ThicknessPt = thicknessPt > 0 ? thicknessPt : b.ThicknessPt;
            switch (side)
            {
                case "top": b.Top = style; break;
                case "bottom": b.Bottom = style; break;
                case "left": b.Left = style; break;
                case "right": b.Right = style; break;
                case "all":
                case "outer": b.Top = b.Bottom = b.Left = b.Right = style; break;
                case "inner": b.Top = b.Bottom = b.Left = b.Right = style; break;
            }
        }

        public void TableCellSetHAlign(TableCell cell,
            Writersword.Modules.TextEditor.Models.Styles.TextAlignment align)
        {
            foreach (var para in cell.Paragraphs)
                para.Properties.Alignment = align;
            ParagraphFormatChanged?.Invoke();
        }

        public void TableCellSetVAlign(TableCell cell, int vAlign)
        {
            cell.VerticalAlignment = (VerticalAlignment)vAlign;
            ParagraphFormatChanged?.Invoke();
        }

        public void TableAutoFitColumns(TableBlock table)
        {
            for (int i = 0; i < table.Columns.Count; i++)
            {
                table.Columns[i].WidthType = TableColumnWidthType.Auto;
                table.Columns[i].WidthValue = 0;
            }
            ParagraphFormatChanged?.Invoke();
        }

        public void TableDistributeColumnsEvenly(TableBlock table)
        {
            int cols = table.ColumnCount;
            if (cols == 0) return;
            double each = 100.0 / cols;
            for (int i = 0; i < table.Columns.Count; i++)
            {
                table.Columns[i].WidthType = TableColumnWidthType.Percent;
                table.Columns[i].WidthValue = each;
            }
            ParagraphFormatChanged?.Invoke();
        }

        public void TableSortByColumn(TableBlock table, int col, bool ascending)
        {
            if (col < 0 || col >= table.ColumnCount) return;

            var rows = new List<(int RowIdx, string SortKey, List<TableCell> Cells)>();
            for (int r = 0; r < table.RowCount; r++)
            {
                var cells = table.Cells.Where(c => c.Row == r).ToList();
                var sortCell = table.GetCell(r, col);
                string key = sortCell is not null ? GetCellPlainText(sortCell) : "";
                rows.Add((r, key, cells));
            }

            var sorted = ascending
                ? rows.OrderBy(x => double.TryParse(x.SortKey, out var d) ? d : double.MaxValue)
                      .ThenBy(x => x.SortKey, StringComparer.CurrentCulture).ToList()
                : rows.OrderByDescending(x => double.TryParse(x.SortKey, out var d) ? d : double.MinValue)
                      .ThenByDescending(x => x.SortKey, StringComparer.CurrentCulture).ToList();

            for (int newRow = 0; newRow < sorted.Count; newRow++)
                foreach (var cell in sorted[newRow].Cells)
                    cell.Row = newRow;

            ParagraphFormatChanged?.Invoke();
        }

        public void PasteTextAtCursor(string text)
        {
            if (_activeParagraph is null) return;

            string[] lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            int caretPos = _activeParagraph.SelectionStart;
            string before = _activeParagraph.PlainText?[..caretPos] ?? "";
            string after = _activeParagraph.PlainText?[caretPos..] ?? "";

            if (lines.Length == 1)
            {
                _activeParagraph.Model.SpliceText(caretPos, caretPos, lines[0]);
                _activeParagraph.RefreshPlainTextFromModel();
                int newPos = caretPos + lines[0].Length;
                _activeParagraph.SelectionStart = newPos;
                _activeParagraph.SelectionEnd = newPos;
                _activeParagraph.RequestFocusAtPosition?.Invoke(newPos);
                return;
            }

            _activeParagraph.Model.SpliceText(caretPos, (before + after).Length, lines[0]);
            _activeParagraph.RefreshPlainTextFromModel();
            ParagraphViewModel prev = _activeParagraph;

            for (int i = 1; i < lines.Length - 1; i++)
            {
                var newVm = AddParagraphAfter(prev);
                newVm.PlainText = lines[i];
                prev = newVm;
            }

            var last = AddParagraphAfter(prev);
            last.Model.SpliceText(0, 0, lines[^1] + after);
            last.RefreshPlainTextFromModel();
            last.RequestFocusAtPosition?.Invoke(lines[^1].Length);
        }
    }
}