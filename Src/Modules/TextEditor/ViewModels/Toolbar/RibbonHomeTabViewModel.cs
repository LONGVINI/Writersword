using System;
using System.Collections.Generic;
using System.Windows.Input;
using ReactiveUI;
using Writersword.Modules.TextEditor.Models.Styles;

namespace Writersword.Modules.TextEditor.ViewModels.Toolbar
{
    /// <summary>
    /// Контекст курсора — снимок форматирования в позиции каретки.
    /// Передаётся из DocumentViewModel в RibbonHomeTabViewModel для синхронизации кнопок.
    /// </summary>
    public sealed class CursorContext
    {
        public bool    IsBold          { get; set; }
        public bool    IsItalic        { get; set; }
        public bool    IsUnderline     { get; set; }
        public bool    IsStrikethrough { get; set; }
        public bool    IsSuperscript   { get; set; }
        public bool    IsSubscript     { get; set; }
        public bool    IsAllCaps       { get; set; }
        public bool    IsBulletList    { get; set; }
        public bool    IsNumberedList  { get; set; }
        public string? FontFamily      { get; set; }
        public double  FontSize        { get; set; } = 14;
        public string? TextColor       { get; set; }
        public string? HighlightColor  { get; set; }
        public TextAlignment Alignment { get; set; } = TextAlignment.Left;
        public string  StyleName       { get; set; } = "Normal";
        public string? Language        { get; set; }
    }

    /// <summary>
    /// ViewModel вкладки "Главная" Ribbon.
    /// Хранит текущее состояние форматирования и команды для кнопок.
    /// </summary>
    public sealed class RibbonHomeTabViewModel : ReactiveObject
    {
        private readonly ITextEditorCommandTarget _target;

        // --- Состояние форматирования символов ---

        private bool    _isBold;
        private bool    _isItalic;
        private bool    _isUnderline;
        private bool    _isStrikethrough;
        private bool    _isSuperscript;
        private bool    _isSubscript;
        private bool    _isAllCaps;
        private bool    _isBulletList;
        private bool    _isNumberedList;
        private string? _fontFamily;
        private double  _currentFontSize = 14;
        private string? _currentTextColor;
        private string? _currentHighlightColor;

        // --- Состояние форматирования абзаца ---

        private TextAlignment _currentAlignment = TextAlignment.Left;
        private string        _currentStyleName = "Normal";

        // --- Свойства: символы ---

        public bool IsBold
        {
            get => _isBold;
            set => this.RaiseAndSetIfChanged(ref _isBold, value);
        }

        public bool IsItalic
        {
            get => _isItalic;
            set => this.RaiseAndSetIfChanged(ref _isItalic, value);
        }

        public bool IsUnderline
        {
            get => _isUnderline;
            set => this.RaiseAndSetIfChanged(ref _isUnderline, value);
        }

        public bool IsStrikethrough
        {
            get => _isStrikethrough;
            set => this.RaiseAndSetIfChanged(ref _isStrikethrough, value);
        }

        public bool IsSuperscript
        {
            get => _isSuperscript;
            set => this.RaiseAndSetIfChanged(ref _isSuperscript, value);
        }

        public bool IsSubscript
        {
            get => _isSubscript;
            set => this.RaiseAndSetIfChanged(ref _isSubscript, value);
        }

        public bool IsAllCaps
        {
            get => _isAllCaps;
            set => this.RaiseAndSetIfChanged(ref _isAllCaps, value);
        }

        public bool IsBulletList
        {
            get => _isBulletList;
            set => this.RaiseAndSetIfChanged(ref _isBulletList, value);
        }

        public bool IsNumberedList
        {
            get => _isNumberedList;
            set => this.RaiseAndSetIfChanged(ref _isNumberedList, value);
        }

        public string? CurrentFontFamily
        {
            get => _fontFamily;
            set
            {
                if (this.RaiseAndSetIfChanged(ref _fontFamily, value) is { } && value is not null)
                    _target.SetFontFamily(value);
            }
        }

        public double CurrentFontSize
        {
            get => _currentFontSize;
            set
            {
                if (this.RaiseAndSetIfChanged(ref _currentFontSize, value) is { } && value > 0)
                    _target.SetFontSize(value);
            }
        }

        public string? CurrentTextColor
        {
            get => _currentTextColor;
            set => this.RaiseAndSetIfChanged(ref _currentTextColor, value);
        }

        public string? CurrentHighlightColor
        {
            get => _currentHighlightColor;
            set => this.RaiseAndSetIfChanged(ref _currentHighlightColor, value);
        }

        // --- Свойства: абзац ---

        public TextAlignment CurrentAlignment
        {
            get => _currentAlignment;
            set => this.RaiseAndSetIfChanged(ref _currentAlignment, value);
        }

        public string CurrentStyleName
        {
            get => _currentStyleName;
            set
            {
                if (this.RaiseAndSetIfChanged(ref _currentStyleName, value) is { } && value is not null)
                    _target.ApplyStyle(value);
            }
        }

        // --- Доступные значения для комбобоксов ---

        public IReadOnlyList<string> AvailableFonts { get; } = new[]
        {
            "Arial",
            "Times New Roman",
            "Calibri",
            "Georgia",
            "Verdana",
            "Tahoma",
            "Trebuchet MS",
            "Consolas",
            "Courier New"
        };

        public IReadOnlyList<string> AvailableStyles { get; } = new[]
        {
            "Normal",
            "Heading 1",
            "Heading 2",
            "Heading 3",
            "Heading 4",
            "Heading 5",
            "Heading 6",
            "Quote",
            "Code",
            "No Spacing"
        };

        // --- Команды ---

        public ICommand BoldCommand           { get; }
        public ICommand ItalicCommand         { get; }
        public ICommand UnderlineCommand      { get; }
        public ICommand StrikethroughCommand  { get; }
        public ICommand SuperscriptCommand    { get; }
        public ICommand SubscriptCommand      { get; }
        public ICommand AllCapsCommand        { get; }
        public ICommand ClearFormattingCommand{ get; }
        public ICommand TextColorCommand      { get; }
        public ICommand HighlightColorCommand { get; }
        public ICommand IncreaseFontSizeCommand { get; }
        public ICommand DecreaseFontSizeCommand { get; }

        public ICommand BulletListCommand     { get; }
        public ICommand NumberedListCommand   { get; }
        public ICommand IncreaseIndentCommand { get; }
        public ICommand DecreaseIndentCommand { get; }
        public ICommand AlignLeftCommand      { get; }
        public ICommand AlignCenterCommand    { get; }
        public ICommand AlignRightCommand     { get; }
        public ICommand AlignJustifyCommand   { get; }
        public ICommand SetLineSpacingCommand { get; }
        public ICommand SpaceBeforeCommand    { get; }
        public ICommand SpaceAfterCommand     { get; }

        public ICommand CutCommand            { get; }
        public ICommand CopyCommand           { get; }
        public ICommand PasteCommand          { get; }
        public ICommand SelectAllCommand      { get; }
        public ICommand UndoCommand           { get; }
        public ICommand RedoCommand           { get; }
        public ICommand FindCommand           { get; }
        public ICommand FindReplaceCommand    { get; }

        public RibbonHomeTabViewModel(ITextEditorCommandTarget target)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));

            BoldCommand            = ReactiveCommand.Create(() => _target.ToggleBold());
            ItalicCommand          = ReactiveCommand.Create(() => _target.ToggleItalic());
            UnderlineCommand       = ReactiveCommand.Create(() => _target.ToggleUnderline());
            StrikethroughCommand   = ReactiveCommand.Create(() => _target.ToggleStrikethrough());
            SuperscriptCommand     = ReactiveCommand.Create(() => _target.ToggleSuperscript());
            SubscriptCommand       = ReactiveCommand.Create(() => _target.ToggleSubscript());
            AllCapsCommand         = ReactiveCommand.Create(() => _target.ToggleAllCaps());
            ClearFormattingCommand = ReactiveCommand.Create(() => _target.ClearFormatting());

            // Цвет: при нажатии применяем текущий выбранный цвет.
            TextColorCommand      = ReactiveCommand.Create(() =>
                _target.SetTextColor(_currentTextColor ?? "#1A1A1A"));
            HighlightColorCommand = ReactiveCommand.Create(() =>
                _target.SetHighlightColor(_currentHighlightColor));

            IncreaseFontSizeCommand = ReactiveCommand.Create(() => _target.IncreaseFontSize());
            DecreaseFontSizeCommand = ReactiveCommand.Create(() => _target.DecreaseFontSize());

            BulletListCommand     = ReactiveCommand.Create(() => _target.ToggleBulletList());
            NumberedListCommand   = ReactiveCommand.Create(() => _target.ToggleNumberedList());
            IncreaseIndentCommand = ReactiveCommand.Create(() => _target.IncreaseIndent());
            DecreaseIndentCommand = ReactiveCommand.Create(() => _target.DecreaseIndent());

            AlignLeftCommand    = ReactiveCommand.Create(() => _target.SetAlignment(TextAlignment.Left));
            AlignCenterCommand  = ReactiveCommand.Create(() => _target.SetAlignment(TextAlignment.Center));
            AlignRightCommand   = ReactiveCommand.Create(() => _target.SetAlignment(TextAlignment.Right));
            AlignJustifyCommand = ReactiveCommand.Create(() => _target.SetAlignment(TextAlignment.Justify));

            // CommandParameter передаётся строкой из AXAML ("1.0", "1.5" и т.д.).
            SetLineSpacingCommand = ReactiveCommand.Create<string>(param =>
            {
                if (double.TryParse(param, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double v))
                    _target.SetLineSpacing(v);
            });

            SpaceBeforeCommand = ReactiveCommand.Create(() => _target.SetSpaceBefore(6));
            SpaceAfterCommand  = ReactiveCommand.Create(() => _target.SetSpaceAfter(6));

            CutCommand         = ReactiveCommand.Create(() => _target.Cut());
            CopyCommand        = ReactiveCommand.Create(() => _target.Copy());
            PasteCommand       = ReactiveCommand.Create(() => _target.Paste());
            SelectAllCommand   = ReactiveCommand.Create(() => _target.SelectAll());
            UndoCommand        = ReactiveCommand.Create(() => _target.Undo());
            RedoCommand        = ReactiveCommand.Create(() => _target.Redo());
            FindCommand        = ReactiveCommand.Create(() => _target.OpenFind());
            FindReplaceCommand = ReactiveCommand.Create(() => _target.OpenFindReplace());
        }

        /// <summary>
        /// Синхронизирует состояние кнопок Ribbon с контекстом курсора.
        /// Вызывается TextEditorViewModel при каждом изменении позиции каретки.
        /// При установке полей напрямую (через backing field) не вызывает команды.
        /// </summary>
        public void UpdateFromCursorContext(CursorContext ctx)
        {
            // Устанавливаем напрямую в backing fields, чтобы не триггерить команды.
            _isBold          = ctx.IsBold;
            _isItalic        = ctx.IsItalic;
            _isUnderline     = ctx.IsUnderline;
            _isStrikethrough = ctx.IsStrikethrough;
            _isSuperscript   = ctx.IsSuperscript;
            _isSubscript     = ctx.IsSubscript;
            _isAllCaps       = ctx.IsAllCaps;
            _isBulletList    = ctx.IsBulletList;
            _isNumberedList  = ctx.IsNumberedList;
            _fontFamily      = ctx.FontFamily;
            _currentFontSize = ctx.FontSize;
            _currentTextColor       = ctx.TextColor;
            _currentHighlightColor  = ctx.HighlightColor;
            _currentAlignment = ctx.Alignment;
            _currentStyleName = ctx.StyleName;

            // Поднимаем изменения разом.
            this.RaisePropertyChanged(nameof(IsBold));
            this.RaisePropertyChanged(nameof(IsItalic));
            this.RaisePropertyChanged(nameof(IsUnderline));
            this.RaisePropertyChanged(nameof(IsStrikethrough));
            this.RaisePropertyChanged(nameof(IsSuperscript));
            this.RaisePropertyChanged(nameof(IsSubscript));
            this.RaisePropertyChanged(nameof(IsAllCaps));
            this.RaisePropertyChanged(nameof(IsBulletList));
            this.RaisePropertyChanged(nameof(IsNumberedList));
            this.RaisePropertyChanged(nameof(CurrentFontFamily));
            this.RaisePropertyChanged(nameof(CurrentFontSize));
            this.RaisePropertyChanged(nameof(CurrentTextColor));
            this.RaisePropertyChanged(nameof(CurrentHighlightColor));
            this.RaisePropertyChanged(nameof(CurrentAlignment));
            this.RaisePropertyChanged(nameof(CurrentStyleName));
        }
    }
}
