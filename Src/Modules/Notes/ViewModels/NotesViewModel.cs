using Avalonia.Media;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using Writersword.Core.Interfaces.Modules;
using Writersword.Modules.Common;
using Writersword.Modules.Notes.Models;
using Writersword.Modules.Notes.Resources;
using Writersword.Modules.Notes.Services;

namespace Writersword.Modules.Notes.ViewModels
{
    /// <summary>
    /// Строка и место каретки в ней. Возвращается операциями над строками,
    /// чтобы представление знало, куда перевести ввод после правки модели.
    /// </summary>
    /// <param name="Block">Строка, в которую переходит ввод.</param>
    /// <param name="Caret">Место каретки в тексте строки.</param>
    public readonly record struct NoteCaret(NoteBlockViewModel Block, int Caret);

    /// <summary>
    /// Модуль заметок: страницы слева, строки выбранной страницы справа.
    ///
    /// Вся правка содержимого проходит через методы этой модели: представление
    /// только переводит нажатия в вызовы и возвращает каретку туда, куда
    /// указывает результат. Прежняя версия держала половину правил в
    /// code-behind поверх текстового документа, вторую половину — здесь, и
    /// половины расходились.
    /// </summary>
    public sealed class NotesViewModel : ReactiveObject
    {
        /// <summary>Ниже этой ширины панель страниц прячется в выпадающий список.</summary>
        public const double CompactWidth = 560;

        /// <summary>Ширина панели страниц по умолчанию.</summary>
        public const double DefaultPagePanelWidth = 220;

        /// <summary>Пределы ширины панели страниц при перетаскивании разделителя.</summary>
        public const double MinPagePanelWidth = 150;
        public const double MaxPagePanelWidth = 420;

        private readonly UndoRedoStack _undo = new(50);

        private NotePageViewModel? _selectedPage;
        private NoteBlockViewModel? _selectedBlock;
        private string _searchText = string.Empty;
        private string? _undoMessage;
        private double _pagePanelWidth = DefaultPagePanelWidth;
        private bool _isCompact;
        private bool _isPagePanelOpen = true;
        private bool _isReadOnly;
        private bool _isPristine = true;

        public NotesViewModel()
        {
            Pages = new ObservableCollection<NotePageViewModel>();
            FilteredPages = new ObservableCollection<NotePageViewModel>();
            Pages.CollectionChanged += OnPagesChanged;
            _undo.StateChanged += () => HistoryChanged?.Invoke();
            LoadData(null);
        }

        // ── Отслеживание правок ───────────────────────────────────────────

        /// <summary>Номер состояния истории отмены (см. UndoRedoStack.StateId).</summary>
        public long HistoryState => _undo.StateId;

        /// <summary>История отмены сдвинулась: удаление или его отмена.</summary>
        public event Action? HistoryChanged;

        /// <summary>
        /// Пользователь изменил содержимое заметок. Поднимается при каждой правке, а не
        /// только при первой: модуль по нему сообщает вкладке о несохранённых правках.
        /// </summary>
        public event Action? DataEdited;

        // ── Данные ────────────────────────────────────────────────────────

        /// <summary>Все страницы по порядку.</summary>
        public ObservableCollection<NotePageViewModel> Pages { get; }

        /// <summary>
        /// Страницы, попавшие под строку поиска.
        /// Открытая страница остаётся в списке всегда, даже если не подходит
        /// под поиск: иначе набор первой же буквы выкидывал бы из списка ту
        /// самую страницу, которую человек сейчас правит, и список выбирал бы
        /// вместо неё другую.
        /// </summary>
        public ObservableCollection<NotePageViewModel> FilteredPages { get; }

        /// <summary>Открытая страница.</summary>
        public NotePageViewModel? SelectedPage
        {
            get => _selectedPage;
            set
            {
                // Список страниц отдаёт null, когда его содержимое
                // перестраивается фильтром. Открытую страницу это закрывать
                // не должно.
                if (value == null && _selectedPage != null && Pages.Contains(_selectedPage))
                    return;
                if (ReferenceEquals(_selectedPage, value))
                    return;

                EndEdit();
                this.RaiseAndSetIfChanged(ref _selectedPage, value);
                this.RaisePropertyChanged(nameof(HasSelectedPage));
                SelectBlock(value?.Blocks.FirstOrDefault());
                RefreshFilter();
            }
        }

        /// <summary>Строка, на которой стоит каретка.</summary>
        public NoteBlockViewModel? SelectedBlock
        {
            get => _selectedBlock;
            private set
            {
                if (ReferenceEquals(_selectedBlock, value))
                    return;
                this.RaiseAndSetIfChanged(ref _selectedBlock, value);
                RaiseSelectionProperties();
            }
        }

        /// <summary>Открыта хоть одна страница.</summary>
        public bool HasSelectedPage => _selectedPage != null;

        /// <summary>Строка поиска по заголовкам и тексту страниц.</summary>
        public string SearchText
        {
            get => _searchText;
            set
            {
                var next = value ?? string.Empty;
                if (_searchText == next)
                    return;
                this.RaiseAndSetIfChanged(ref _searchText, next);
                this.RaisePropertyChanged(nameof(HasSearch));
                RefreshFilter();
            }
        }

        /// <summary>Поиск задан.</summary>
        public bool HasSearch => _searchText.Trim().Length > 0;

        /// <summary>Подпись под списком: сколько страниц показано.</summary>
        public string PageCountText => HasSearch
            ? string.Format(NotesStrings.Pages_FoundCount, FilteredPages.Count, Pages.Count)
            : string.Format(NotesStrings.Pages_TotalCount, Pages.Count);

        // ── Состояние интерфейса ──────────────────────────────────────────

        /// <summary>Модуль ужат по ширине — панель страниц заменяется выпадающим списком.</summary>
        public bool IsCompact
        {
            get => _isCompact;
            set
            {
                if (_isCompact == value)
                    return;
                this.RaiseAndSetIfChanged(ref _isCompact, value);
                RaisePanelProperties();
            }
        }

        /// <summary>Панель страниц развёрнута.</summary>
        public bool IsPagePanelOpen
        {
            get => _isPagePanelOpen;
            set
            {
                if (_isPagePanelOpen == value)
                    return;
                this.RaiseAndSetIfChanged(ref _isPagePanelOpen, value);
                RaisePanelProperties();
            }
        }

        /// <summary>Ширина панели страниц в точках.</summary>
        public double PagePanelWidth
        {
            get => _pagePanelWidth;
            set
            {
                var next = double.IsFinite(value)
                    ? Math.Clamp(value, MinPagePanelWidth, MaxPagePanelWidth)
                    : DefaultPagePanelWidth;
                this.RaiseAndSetIfChanged(ref _pagePanelWidth, next);
            }
        }

        /// <summary>Широкая панель страниц видна.</summary>
        public bool IsWidePagePanelVisible => !_isCompact && _isPagePanelOpen;

        /// <summary>Вместо панели показывается выпадающий список страниц.</summary>
        public bool IsCompactHeaderVisible => !IsWidePagePanelVisible;

        /// <summary>
        /// Правка запрещена — модуль открыт для сравнения версий.
        /// Признак спускается в страницы и строки: разметке не нужно ходить
        /// вверх по дереву за корневой моделью.
        /// </summary>
        public bool IsReadOnly
        {
            get => _isReadOnly;
            set
            {
                if (_isReadOnly == value)
                    return;
                this.RaiseAndSetIfChanged(ref _isReadOnly, value);
                this.RaisePropertyChanged(nameof(IsEditable));
                RaiseSelectionProperties();
                foreach (var page in Pages)
                    page.IsReadOnly = value;
                if (value)
                    EndEdit();
            }
        }

        /// <summary>Модуль можно править.</summary>
        public bool IsEditable => !_isReadOnly;

        /// <summary>Есть выбранная строка и её разрешено править.</summary>
        public bool CanEditBlock => !_isReadOnly && _selectedBlock != null;

        /// <summary>
        /// Пользователь ничего не вводил: в модуле лежит одна пустая страница
        /// в том виде, в каком её создал сам модуль. Такую страницу не за чем
        /// класть в файл проекта.
        /// </summary>
        public bool IsPristine => _isPristine;

        // ── Признаки выбранной строки для ленты ───────────────────────────

        public bool IsParagraphSelected => _selectedBlock?.Type == NoteBlockType.Paragraph;
        public bool IsHeading1Selected => _selectedBlock?.Type == NoteBlockType.Heading1;
        public bool IsHeading2Selected => _selectedBlock?.Type == NoteBlockType.Heading2;
        public bool IsHeading3Selected => _selectedBlock?.Type == NoteBlockType.Heading3;
        public bool IsBulletSelected => _selectedBlock?.Type == NoteBlockType.Bullet;
        public bool IsChecklistSelected => _selectedBlock?.Type == NoteBlockType.Checklist;
        public bool IsNumberedSelected => _selectedBlock?.Type == NoteBlockType.Numbered;
        public bool IsQuoteSelected => _selectedBlock?.Type == NoteBlockType.Quote;
        public bool IsCodeSelected => _selectedBlock?.Type == NoteBlockType.Code;
        public bool IsDividerSelected => _selectedBlock?.Type == NoteBlockType.Divider;
        public bool IsStrikeSelected => _selectedBlock?.IsStruckThrough == true;
        public bool IsHighlightSelected => _selectedBlock?.IsHighlighted == true;

        /// <summary>
        /// Виды строки для галереи стилей ленты — в том порядке, в каком они
        /// стоят в галерее.
        /// </summary>
        public IReadOnlyList<NoteStyleViewModel> BlockStyles { get; } = new[]
        {
            new NoteStyleViewModel(NoteBlockType.Paragraph, NotesStrings.Block_Paragraph,
                12, FontWeight.Normal, FontStyle.Normal),
            new NoteStyleViewModel(NoteBlockType.Heading1, NotesStrings.Block_Heading1,
                18, FontWeight.SemiBold, FontStyle.Normal),
            new NoteStyleViewModel(NoteBlockType.Heading2, NotesStrings.Block_Heading2,
                15, FontWeight.SemiBold, FontStyle.Normal),
            new NoteStyleViewModel(NoteBlockType.Heading3, NotesStrings.Block_Heading3,
                13, FontWeight.SemiBold, FontStyle.Normal),
            new NoteStyleViewModel(NoteBlockType.Quote, NotesStrings.Block_Quote,
                12, FontWeight.Normal, FontStyle.Italic),
            new NoteStyleViewModel(NoteBlockType.Code, NotesStrings.Block_Code,
                11, FontWeight.Normal, FontStyle.Normal,
                new FontFamily("Consolas, Courier New, monospace"))
        };

        /// <summary>
        /// Карточка галереи, отвечающая виду выбранной строки.
        ///
        /// Выбор в галерее ставит вид сразу, без переключения туда-обратно:
        /// человек выбирает «Заголовок 1» и ждёт заголовок, а не отмену
        /// заголовка от повторного щелчка.
        /// </summary>
        public NoteStyleViewModel? SelectedStyle
        {
            get => BlockStyles.FirstOrDefault(style => style.Type == _selectedBlock?.Type);
            set
            {
                if (value == null || _selectedBlock == null)
                    return;
                ApplyStyle(value.Type);
            }
        }

        /// <summary>Поставить выбранной строке вид без переключения.</summary>
        public void ApplyStyle(NoteBlockType type)
        {
            if (_isReadOnly || _selectedBlock is not { } block || !Enum.IsDefined(type) ||
                _selectedPage?.Blocks.Contains(block) != true || block.Type == type)
                return;

            block.Type = type;
            if (type != NoteBlockType.Checklist)
                block.IsChecked = false;
            RaiseSelectionProperties();
            MarkDirty();
        }

        // ── Отмена ────────────────────────────────────────────────────────

        /// <summary>Сообщение о последнем отменяемом действии; null — сообщения нет.</summary>
        public string? UndoMessage
        {
            get => _undoMessage;
            private set
            {
                if (_undoMessage == value)
                    return;
                this.RaiseAndSetIfChanged(ref _undoMessage, value);
                this.RaisePropertyChanged(nameof(HasUndoMessage));
            }
        }

        /// <summary>Показывать плашку отмены.</summary>
        public bool HasUndoMessage => !string.IsNullOrEmpty(_undoMessage);

        /// <summary>Есть что отменять.</summary>
        public bool CanUndo => _undo.CanUndo;

        /// <summary>Отменить последнее удаление.</summary>
        public void Undo()
        {
            if (!_undo.CanUndo || _isReadOnly)
                return;
            _undo.Undo();
            UndoMessage = null;
            this.RaisePropertyChanged(nameof(CanUndo));
        }

        /// <summary>Убрать плашку отмены, оставив действие в истории.</summary>
        public void DismissUndoMessage() => UndoMessage = null;

        // ── Страницы ──────────────────────────────────────────────────────

        /// <summary>
        /// Создать страницу и открыть её.
        /// В режиме сравнения версий возвращает null: отказ выражается
        /// значением, а не исключением — вызов идёт прямо из нажатия кнопки.
        /// </summary>
        public NotePageViewModel? AddPage()
        {
            if (_isReadOnly)
                return null;

            var page = new NotePageViewModel(
                NotesService.CreatePage(string.Format(NotesStrings.Page_NewTitle, Pages.Count + 1)));
            AttachPage(page);
            Pages.Add(page);
            SelectedPage = page;
            MarkDirty();
            return page;
        }

        /// <summary>Скопировать открытую страницу вместе со строками.</summary>
        public NotePageViewModel? DuplicateSelectedPage()
        {
            if (_isReadOnly || _selectedPage == null)
                return null;

            var model = _selectedPage.ToModel();
            model.Id = Guid.NewGuid();
            model.Title = string.Format(NotesStrings.Page_CopyTitle, _selectedPage.DisplayTitle);
            model.CreatedAtUtc = DateTime.UtcNow;
            model.UpdatedAtUtc = model.CreatedAtUtc;
            foreach (var block in model.Blocks)
                block.Id = Guid.NewGuid();

            var page = new NotePageViewModel(model);
            AttachPage(page);
            Pages.Insert(Pages.IndexOf(_selectedPage) + 1, page);
            SelectedPage = page;
            MarkDirty();
            return page;
        }

        /// <summary>
        /// Убрать открытую страницу.
        /// Действие ложится в историю: удаление страницы уносит весь её текст,
        /// и без отмены одно неверное нажатие стоило бы работы за день.
        /// </summary>
        public void RemoveSelectedPage()
        {
            if (_isReadOnly || _selectedPage == null || Pages.Count <= 1)
                return;

            var index = Pages.IndexOf(_selectedPage);
            var model = _selectedPage.ToModel();
            var command = new RemovePageCommand(this, index, model);
            command.Execute();
            _undo.Push(command);
            UndoMessage = string.Format(NotesStrings.Undo_PageRemoved, model.Title.Length > 0
                ? model.Title
                : NotesStrings.Page_Untitled);
            this.RaisePropertyChanged(nameof(CanUndo));
            MarkDirty();
        }

        /// <summary>Передвинуть открытую страницу в списке.</summary>
        public void MoveSelectedPage(int delta)
        {
            if (_isReadOnly || _selectedPage == null || delta == 0)
                return;

            var index = Pages.IndexOf(_selectedPage);
            var target = index + delta;
            if (index < 0 || target < 0 || target >= Pages.Count)
                return;

            Pages.Move(index, target);
            RefreshFilter();
            MarkDirty();
        }

        /// <summary>Можно ли передвинуть открытую страницу вверх.</summary>
        public bool CanMovePageUp => _selectedPage != null && Pages.IndexOf(_selectedPage) > 0;

        /// <summary>Можно ли передвинуть открытую страницу вниз.</summary>
        public bool CanMovePageDown =>
            _selectedPage != null && Pages.IndexOf(_selectedPage) >= 0 &&
            Pages.IndexOf(_selectedPage) < Pages.Count - 1;

        // ── Строки ────────────────────────────────────────────────────────

        /// <summary>Пометить строку выбранной.</summary>
        public void SelectBlock(NoteBlockViewModel? block)
        {
            if (block != null && _selectedPage?.Blocks.Contains(block) != true)
                return;

            if (_selectedBlock != null)
                _selectedBlock.IsSelected = false;
            SelectedBlock = block;
            if (_selectedBlock != null)
                _selectedBlock.IsSelected = true;
        }

        /// <summary>
        /// Снять признак правки со всех строк открытой страницы.
        /// Нужен при уходе со страницы и при переходе в режим сравнения:
        /// поле ввода в этот момент ввод уже не теряет, а признак остаётся.
        /// </summary>
        public void EndEdit()
        {
            if (_selectedPage == null)
                return;

            foreach (var block in _selectedPage.Blocks)
                block.IsEditing = false;
        }

        /// <summary>
        /// Разорвать строку в месте каретки.
        /// Пустая строка списка или задачи по Enter не разрывается, а
        /// превращается в обычную: так список заканчивают, не снимая руки
        /// с клавиатуры.
        /// </summary>
        public NoteCaret SplitBlock(NoteBlockViewModel block, int caret)
        {
            if (_isReadOnly || _selectedPage == null || !_selectedPage.Blocks.Contains(block))
                return new NoteCaret(block, caret);

            if (block.Text.Length == 0 && block.Type is NoteBlockType.Bullet
                or NoteBlockType.Numbered or NoteBlockType.Checklist
                or NoteBlockType.Quote or NoteBlockType.Code)
            {
                block.Type = NoteBlockType.Paragraph;
                block.IsChecked = false;
                RaiseSelectionProperties();
                return new NoteCaret(block, 0);
            }

            // Сокращение, набранное целиком и не завершённое пробелом,
            // применяется в момент разрыва: «# Глава» + Enter даёт заголовок.
            var position = Math.Clamp(caret, 0, block.Text.Length);
            if (block.Type == NoteBlockType.Paragraph && position == block.Text.Length &&
                NotesService.TryParseShortcut(block.Text, out var parsed, out var rest, out var parsedChecked))
            {
                block.Type = parsed;
                block.IsChecked = parsed == NoteBlockType.Checklist && parsedChecked;
                block.Text = parsed == NoteBlockType.Divider ? string.Empty : rest;
                position = block.Text.Length;
            }

            var tail = block.Text.Substring(position);
            block.Text = block.Text.Substring(0, position);

            // Список, нумерация, задача, цитата и код продолжаются сами
            // собой; заголовок — нет: за заголовком идёт текст, а не ещё
            // один заголовок.
            var nextType = block.Type is NoteBlockType.Bullet or NoteBlockType.Numbered
                or NoteBlockType.Checklist or NoteBlockType.Quote or NoteBlockType.Code
                ? block.Type
                : NoteBlockType.Paragraph;

            var index = _selectedPage.Blocks.IndexOf(block);
            var next = _selectedPage.InsertBlock(index + 1, new NoteBlock { Type = nextType, Text = tail });
            RaiseSelectionProperties();
            MarkDirty();
            return new NoteCaret(next, 0);
        }

        /// <summary>
        /// Обработать Backspace в начале строки: сперва снимается вид строки,
        /// и только у обычной строки происходит склейка с предыдущей.
        /// </summary>
        public NoteCaret? MergeWithPrevious(NoteBlockViewModel block)
        {
            if (_isReadOnly || _selectedPage == null || !_selectedPage.Blocks.Contains(block))
                return null;

            if (block.Type != NoteBlockType.Paragraph)
            {
                block.Type = NoteBlockType.Paragraph;
                block.IsChecked = false;
                RaiseSelectionProperties();
                MarkDirty();
                return new NoteCaret(block, 0);
            }

            var index = _selectedPage.Blocks.IndexOf(block);
            if (index <= 0)
                return null;

            var previous = _selectedPage.Blocks[index - 1];

            // Разделитель не с чем склеивать — он просто убирается.
            if (previous.IsDivider)
            {
                _selectedPage.RemoveBlock(previous);
                MarkDirty();
                return new NoteCaret(block, 0);
            }

            var caret = previous.Text.Length;
            previous.Text += block.Text;
            _selectedPage.RemoveBlock(block);
            SelectBlock(previous);
            MarkDirty();
            return new NoteCaret(previous, caret);
        }

        /// <summary>Обработать Delete в конце строки: подтянуть следующую строку.</summary>
        public NoteCaret? MergeWithNext(NoteBlockViewModel block)
        {
            if (_isReadOnly || _selectedPage == null || !_selectedPage.Blocks.Contains(block))
                return null;

            var index = _selectedPage.Blocks.IndexOf(block);
            if (index < 0 || index >= _selectedPage.Blocks.Count - 1)
                return null;

            var next = _selectedPage.Blocks[index + 1];
            var caret = block.Text.Length;

            if (next.IsDivider)
            {
                _selectedPage.RemoveBlock(next);
                MarkDirty();
                return new NoteCaret(block, caret);
            }

            block.Text += next.Text;
            _selectedPage.RemoveBlock(next);
            MarkDirty();
            return new NoteCaret(block, caret);
        }

        /// <summary>
        /// Применить сокращение, набранное перед кареткой, в момент пробела.
        /// Возвращает false, если перед кареткой не сокращение — тогда пробел
        /// вводится обычным порядком.
        /// </summary>
        public bool TryApplyShortcut(NoteBlockViewModel block, int caret, out NoteCaret result)
        {
            result = new NoteCaret(block, caret);
            if (_isReadOnly || _selectedPage == null || !_selectedPage.Blocks.Contains(block))
                return false;
            if (block.Type != NoteBlockType.Paragraph)
                return false;
            if (!NotesService.TryParseShortcutBeforeCaret(
                    block.Text, caret, out var type, out var isChecked, out var consumed))
                return false;

            block.Text = block.Text.Substring(consumed);
            block.Type = type;
            block.IsChecked = type == NoteBlockType.Checklist && isChecked;
            if (type == NoteBlockType.Divider)
            {
                // Разделитель текста не несёт: остаток строки уезжает в
                // следующую строку, а не пропадает.
                var tail = block.Text;
                block.Text = string.Empty;
                var index = _selectedPage.Blocks.IndexOf(block);
                var next = _selectedPage.InsertBlock(index + 1, new NoteBlock { Text = tail });
                RaiseSelectionProperties();
                MarkDirty();
                result = new NoteCaret(next, 0);
                return true;
            }

            RaiseSelectionProperties();
            MarkDirty();
            result = new NoteCaret(block, 0);
            return true;
        }

        /// <summary>
        /// Обернуть выделенный кусок знаками оформления — «**», «*», «~~»,
        /// «`», «==». Повторное нажатие на уже обёрнутом куске знаки снимает,
        /// поэтому кнопка работает переключателем.
        /// Без выделения знаки ставятся парой у каретки, и набор продолжается
        /// между ними.
        /// </summary>
        public NoteCaret? WrapSelection(NoteBlockViewModel? block, int start, int end, string marker)
        {
            if (_isReadOnly || block == null || string.IsNullOrEmpty(marker) ||
                _selectedPage?.Blocks.Contains(block) != true || block.IsDivider)
                return null;

            var text = block.Text;
            var from = Math.Clamp(Math.Min(start, end), 0, text.Length);
            var to = Math.Clamp(Math.Max(start, end), 0, text.Length);

            // Кусок уже обёрнут — снимаем знаки вместо того, чтобы городить
            // вторые поверх первых.
            if (from >= marker.Length && to + marker.Length <= text.Length &&
                string.CompareOrdinal(text, from - marker.Length, marker, 0, marker.Length) == 0 &&
                string.CompareOrdinal(text, to, marker, 0, marker.Length) == 0)
            {
                block.Text = text.Remove(to, marker.Length).Remove(from - marker.Length, marker.Length);
                MarkDirty();
                return new NoteCaret(block, to - marker.Length);
            }

            block.Text = text.Insert(to, marker).Insert(from, marker);
            MarkDirty();
            return new NoteCaret(block, to + marker.Length);
        }

        /// <summary>Сменить вид строки.</summary>
        public void SetBlockType(NoteBlockViewModel? block, NoteBlockType type)
        {
            if (_isReadOnly || block == null || !Enum.IsDefined(type) ||
                _selectedPage is not { } page || !page.Blocks.Contains(block))
                return;

            // Разделитель не хранит текст. Вместо того чтобы стереть строку,
            // разделитель встаёт следом за ней: набранное не пропадает.
            if (type == NoteBlockType.Divider && block.Text.Length > 0)
            {
                var index = page.Blocks.IndexOf(block);
                var divider = page.InsertBlock(index + 1, new NoteBlock { Type = NoteBlockType.Divider });
                SelectBlock(divider);
                RaiseSelectionProperties();
                MarkDirty();
                return;
            }

            // Повторное нажатие того же вида возвращает обычную строку — так
            // кнопка ленты работает переключателем, а не защёлкой.
            block.Type = block.Type == type ? NoteBlockType.Paragraph : type;
            if (block.Type != NoteBlockType.Checklist)
                block.IsChecked = false;
            RaiseSelectionProperties();
            MarkDirty();
        }

        /// <summary>Сменить вид выбранной строки.</summary>
        public void SetSelectedBlockType(NoteBlockType type) => SetBlockType(_selectedBlock, type);

        /// <summary>Подсветить или снять подсветку выбранной строки.</summary>
        public void ToggleSelectedHighlight()
        {
            if (_isReadOnly || _selectedBlock == null)
                return;
            _selectedBlock.IsHighlighted = !_selectedBlock.IsHighlighted;
            RaiseSelectionProperties();
            MarkDirty();
        }

        /// <summary>Зачеркнуть или снять зачёркивание выбранной строки.</summary>
        public void ToggleSelectedStrikeThrough()
        {
            if (_isReadOnly || _selectedBlock == null)
                return;
            _selectedBlock.IsStruckThrough = !_selectedBlock.IsStruckThrough;
            RaiseSelectionProperties();
            MarkDirty();
        }

        /// <summary>Передвинуть строку вверх или вниз по странице.</summary>
        public void MoveBlock(NoteBlockViewModel? block, int delta)
        {
            if (_isReadOnly || block == null || delta == 0 ||
                _selectedPage is not { } page || !page.Blocks.Contains(block))
                return;

            var index = page.Blocks.IndexOf(block);
            var target = index + delta;
            if (target < 0 || target >= page.Blocks.Count)
                return;

            page.Blocks.Move(index, target);
            MarkDirty();
        }

        /// <summary>
        /// Убрать строку. Последняя строка страницы не убирается, а очищается:
        /// странице нужна хотя бы одна строка, чтобы принимать ввод.
        /// </summary>
        public NoteCaret? RemoveBlock(NoteBlockViewModel? block)
        {
            if (_isReadOnly || block == null ||
                _selectedPage is not { } page || !page.Blocks.Contains(block))
                return null;

            if (page.Blocks.Count == 1)
            {
                block.Apply(new NoteBlock());
                RaiseSelectionProperties();
                MarkDirty();
                return new NoteCaret(block, 0);
            }

            var index = page.Blocks.IndexOf(block);
            var command = new RemoveBlockCommand(this, page.Id, index, block.ToModel());
            command.Execute();
            _undo.Push(command);
            UndoMessage = NotesStrings.Undo_BlockRemoved;
            this.RaisePropertyChanged(nameof(CanUndo));
            MarkDirty();

            var target = page.Blocks[Math.Clamp(index - 1, 0, page.Blocks.Count - 1)];
            SelectBlock(target);
            return new NoteCaret(target, target.Text.Length);
        }

        /// <summary>
        /// Завести строку в конце страницы.
        /// Нужно полотну: щелчок под последней строкой должен дать место для
        /// ввода даже тогда, когда последняя строка — черта.
        /// </summary>
        public NoteCaret? AppendParagraph()
        {
            if (_isReadOnly || _selectedPage is not { } page)
                return null;

            var block = page.InsertBlock(page.Blocks.Count, new NoteBlock());
            SelectBlock(block);
            MarkDirty();
            return new NoteCaret(block, 0);
        }

        /// <summary>
        /// Перейти на соседнюю строку. Разделители пропускаются: текста они не
        /// содержат и каретке там делать нечего.
        /// </summary>
        public NoteCaret? StepBlock(NoteBlockViewModel block, int delta, int caret)
        {
            if (_selectedPage == null || delta == 0 || !_selectedPage.Blocks.Contains(block))
                return null;

            var index = _selectedPage.Blocks.IndexOf(block) + delta;
            while (index >= 0 && index < _selectedPage.Blocks.Count && _selectedPage.Blocks[index].IsDivider)
                index += delta;
            if (index < 0 || index >= _selectedPage.Blocks.Count)
                return null;

            var target = _selectedPage.Blocks[index];
            return new NoteCaret(target, Math.Clamp(caret, 0, target.Text.Length));
        }

        /// <summary>
        /// Вставить многострочный текст в место каретки.
        /// Каждая строка разбирается отдельно, поэтому вставленный markdown
        /// становится настоящими списками и заголовками.
        /// </summary>
        public NoteCaret? PasteText(NoteBlockViewModel block, int caret, string? text)
        {
            if (_isReadOnly || _selectedPage == null || string.IsNullOrEmpty(text) ||
                !_selectedPage.Blocks.Contains(block))
                return null;

            var parsed = NotesService.ParseText(text);
            if (parsed.Count == 0)
                return null;

            var position = Math.Clamp(caret, 0, block.Text.Length);
            var head = block.Text.Substring(0, position);
            var tail = block.Text.Substring(position);

            // Одна строка — обычная вставка внутрь текста, без ломки строки.
            if (parsed.Count == 1 && parsed[0].Type == NoteBlockType.Paragraph)
            {
                block.Text = head + parsed[0].Text + tail;
                MarkDirty();
                return new NoteCaret(block, position + parsed[0].Text.Length);
            }

            block.Text = head + (parsed[0].Type == NoteBlockType.Divider ? string.Empty : parsed[0].Text);
            if (block.Type == NoteBlockType.Paragraph && parsed[0].Type != NoteBlockType.Paragraph)
            {
                block.Type = parsed[0].Type;
                block.IsChecked = parsed[0].IsChecked;
            }

            var index = _selectedPage.Blocks.IndexOf(block);
            NoteBlockViewModel last = block;
            for (var i = 1; i < parsed.Count; i++)
                last = _selectedPage.InsertBlock(index + i, parsed[i]);

            var caretInLast = last.Text.Length;
            last.Text += tail;
            SelectBlock(last);
            RaiseSelectionProperties();
            MarkDirty();
            return new NoteCaret(last, caretInLast);
        }

        /// <summary>
        /// Открытая страница обычным текстом с сокращениями разметки.
        /// Тот же текст, вставленный обратно, даёт те же строки — разбор
        /// вставки и запись в буфер обмена работают по одним правилам.
        /// </summary>
        public string SelectedPageAsText() =>
            _selectedPage == null ? string.Empty : NotesService.ToPlainText(_selectedPage.ToModel());

        // ── Сохранение и загрузка ─────────────────────────────────────────

        /// <summary>Снять данные модуля для файла проекта.</summary>
        public NotesData CreateSnapshot() => new()
        {
            FormatVersion = NotesData.CurrentFormatVersion,
            Pages = Pages.Select(page => page.ToModel()).ToList()
        };

        /// <summary>Снять рабочие данные сессии.</summary>
        public NotesSessionData CreateSessionSnapshot() => new()
        {
            SelectedPageId = _selectedPage?.Id,
            SelectedBlockId = _selectedBlock?.Id,
            IsPagePanelOpen = _isPagePanelOpen,
            PagePanelWidth = _pagePanelWidth
        };

        /// <summary>
        /// Загрузить данные модуля.
        /// Вся новая модель строится до того, как тронута текущая: ошибка в
        /// любой странице или строке не должна оставить модуль с половиной
        /// прежних данных и половиной новых.
        /// </summary>
        public void LoadData(NotesData? data)
        {
            if (data != null && (data.FormatVersion != NotesData.CurrentFormatVersion || data.Pages == null))
                throw new ArgumentException("Unsupported or invalid Notes data", nameof(data));

            var loaded = (data?.Pages ?? new List<NotePage>())
                .Select(page => new NotePageViewModel(page))
                .ToList();

            var isFresh = loaded.Count == 0;
            if (isFresh)
                loaded.Add(new NotePageViewModel(NotesService.CreatePage(NotesStrings.Page_DefaultTitle)));

            foreach (var page in Pages)
                DetachPage(page);

            _selectedPage = null;
            SelectedBlock = null;
            _undo.Clear();
            UndoMessage = null;

            Pages.Clear();
            foreach (var page in loaded)
            {
                page.IsReadOnly = _isReadOnly;
                AttachPage(page);
                Pages.Add(page);
            }

            SelectedPage = Pages[0];
            _isPristine = isFresh;
            this.RaisePropertyChanged(nameof(IsPristine));
            this.RaisePropertyChanged(nameof(CanUndo));
        }

        /// <summary>Восстановить рабочее состояние сессии.</summary>
        public void RestoreSession(NotesSessionData? session)
        {
            if (session == null)
                return;

            IsPagePanelOpen = session.IsPagePanelOpen;
            if (session.PagePanelWidth > 0)
                PagePanelWidth = session.PagePanelWidth;

            SelectedPage = Pages.FirstOrDefault(page => page.Id == session.SelectedPageId)
                ?? Pages.FirstOrDefault();

            if (session.SelectedBlockId is { } blockId)
                SelectBlock(_selectedPage?.Blocks.FirstOrDefault(block => block.Id == blockId));
        }

        // ── Внутреннее ────────────────────────────────────────────────────

        private void AttachPage(NotePageViewModel page) => page.ContentChanged += OnPageContentChanged;

        private void DetachPage(NotePageViewModel page) => page.ContentChanged -= OnPageContentChanged;

        private void OnPageContentChanged(object? sender, EventArgs e) => MarkDirty();

        /// <summary>
        /// Отметить, что в модуле появилось пользовательское содержимое.
        /// Пока модуль остаётся нетронутым, он не кладёт в файл проекта пустую
        /// страницу и не делает версию отличной от предыдущей на пустом месте.
        /// </summary>
        private void MarkDirty()
        {
            DataEdited?.Invoke();

            if (!_isPristine)
                return;
            _isPristine = false;
            this.RaisePropertyChanged(nameof(IsPristine));
        }

        private void OnPagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            RefreshFilter();
            this.RaisePropertyChanged(nameof(CanMovePageUp));
            this.RaisePropertyChanged(nameof(CanMovePageDown));
        }

        /// <summary>Пересобрать список страниц под строку поиска.</summary>
        private void RefreshFilter()
        {
            var query = _searchText.Trim();
            FilteredPages.Clear();
            foreach (var page in Pages)
            {
                if (query.Length == 0 || ReferenceEquals(page, _selectedPage) || Matches(page, query))
                    FilteredPages.Add(page);
            }

            this.RaisePropertyChanged(nameof(PageCountText));
            this.RaisePropertyChanged(nameof(CanMovePageUp));
            this.RaisePropertyChanged(nameof(CanMovePageDown));

            // Список страниц при пересборке содержимого сбрасывает свой выбор.
            // Значение выталкивается в него заново, иначе открытая страница
            // остаётся открытой, но в списке ничем не отмечена.
            this.RaisePropertyChanged(nameof(SelectedPage));
        }

        private static bool Matches(NotePageViewModel page, string query)
        {
            if (page.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                return true;
            foreach (var block in page.Blocks)
            {
                if (block.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                    return true;
            }

            return false;
        }

        private void RaisePanelProperties()
        {
            this.RaisePropertyChanged(nameof(IsWidePagePanelVisible));
            this.RaisePropertyChanged(nameof(IsCompactHeaderVisible));
        }

        private void RaiseSelectionProperties()
        {
            this.RaisePropertyChanged(nameof(CanEditBlock));
            this.RaisePropertyChanged(nameof(IsParagraphSelected));
            this.RaisePropertyChanged(nameof(IsHeading1Selected));
            this.RaisePropertyChanged(nameof(IsHeading2Selected));
            this.RaisePropertyChanged(nameof(IsHeading3Selected));
            this.RaisePropertyChanged(nameof(IsBulletSelected));
            this.RaisePropertyChanged(nameof(IsChecklistSelected));
            this.RaisePropertyChanged(nameof(IsNumberedSelected));
            this.RaisePropertyChanged(nameof(IsQuoteSelected));
            this.RaisePropertyChanged(nameof(IsCodeSelected));
            this.RaisePropertyChanged(nameof(IsDividerSelected));
            this.RaisePropertyChanged(nameof(IsStrikeSelected));
            this.RaisePropertyChanged(nameof(IsHighlightSelected));
            this.RaisePropertyChanged(nameof(SelectedStyle));
        }

        /// <summary>Вернуть страницу по идентификатору — нужно командам отмены.</summary>
        private NotePageViewModel? FindPage(Guid id) => Pages.FirstOrDefault(page => page.Id == id);

        /// <summary>Удаление строки: отменяется возвратом строки на своё место.</summary>
        private sealed class RemoveBlockCommand : IUndoableCommand
        {
            private readonly NotesViewModel _owner;
            private readonly Guid _pageId;
            private readonly int _index;
            private readonly NoteBlock _model;

            public RemoveBlockCommand(NotesViewModel owner, Guid pageId, int index, NoteBlock model)
            {
                _owner = owner;
                _pageId = pageId;
                _index = index;
                _model = model;
            }

            public string Description => NotesStrings.Undo_BlockRemoved;

            public void Execute()
            {
                var page = _owner.FindPage(_pageId);
                if (page == null || _index < 0 || _index >= page.Blocks.Count)
                    return;
                page.RemoveBlock(page.Blocks[_index]);
            }

            public void Undo()
            {
                var page = _owner.FindPage(_pageId);
                if (page == null)
                    return;
                var block = page.InsertBlock(_index, _model.Clone());
                _owner.SelectedPage = page;
                _owner.SelectBlock(block);
            }
        }

        /// <summary>Удаление страницы: отменяется возвратом страницы на своё место.</summary>
        private sealed class RemovePageCommand : IUndoableCommand
        {
            private readonly NotesViewModel _owner;
            private readonly int _index;
            private readonly NotePage _model;

            public RemovePageCommand(NotesViewModel owner, int index, NotePage model)
            {
                _owner = owner;
                _index = index;
                _model = model;
            }

            public string Description => NotesStrings.Undo_PageRemovedShort;

            public void Execute()
            {
                var page = _owner.FindPage(_model.Id);
                if (page == null)
                    return;

                var index = _owner.Pages.IndexOf(page);
                _owner.DetachPage(page);
                _owner.Pages.Remove(page);

                if (ReferenceEquals(_owner._selectedPage, page))
                {
                    _owner._selectedPage = null;
                    _owner.SelectedPage = _owner.Pages.Count > 0
                        ? _owner.Pages[Math.Clamp(index - 1, 0, _owner.Pages.Count - 1)]
                        : null;
                }
            }

            public void Undo()
            {
                var page = new NotePageViewModel(_model.Clone()) { IsReadOnly = _owner._isReadOnly };
                _owner.AttachPage(page);
                _owner.Pages.Insert(Math.Clamp(_index, 0, _owner.Pages.Count), page);
                _owner.SelectedPage = page;
            }
        }
    }
}
