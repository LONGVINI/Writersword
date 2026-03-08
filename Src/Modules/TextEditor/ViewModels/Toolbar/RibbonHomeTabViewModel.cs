using System;
using System.Collections.Generic;
using System.Windows.Input;
using ReactiveUI;
using Writersword.Modules.TextEditor.Models.Styles;

namespace Writersword.Modules.TextEditor.ViewModels.Toolbar
{
    /// <summary>
    /// ViewModel вкладки "Главная" Ribbon.
    /// Содержит команды форматирования символов и абзацев.
    /// Все команды делегируются через <see cref="ITextEditorCommandTarget"/>.
    /// </summary>
    public sealed class RibbonHomeTabViewModel : ReactiveObject
    {
        private readonly ITextEditorCommandTarget _target;

        // --- Состояние активных переключателей (отражают текущую позицию курсора) ---

        private bool _isBold;
        private bool _isItalic;
        private bool _isUnderline;
        private bool _isStrikethrough;
        private bool _isSuperscript;
        private bool _isSubscript;
        private bool _isAllCaps;

        private TextAlignment _currentAlignment = TextAlignment.Left;
        private string _currentStyleName = "Normal";
        private string _currentFontFamily = "Times New Roman";
        private double _currentFontSize = 14;
        private string _currentTextColor = "#1A1A1A";
        private string? _currentHighlightColor;

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

        public TextAlignment CurrentAlignment
        {
            get => _currentAlignment;
            set => this.RaiseAndSetIfChanged(ref _currentAlignment, value);
        }

        public string CurrentStyleName
        {
            get => _currentStyleName;
            set => this.RaiseAndSetIfChanged(ref _currentStyleName, value);
        }

        public string CurrentFontFamily
        {
            get => _currentFontFamily;
            set => this.RaiseAndSetIfChanged(ref _currentFontFamily, value);
        }

        public double CurrentFontSize
        {
            get => _currentFontSize;
            set => this.RaiseAndSetIfChanged(ref _currentFontSize, value);
        }

        public string CurrentTextColor
        {
            get => _currentTextColor;
            set => this.RaiseAndSetIfChanged(ref _currentTextColor, value);
        }

        public string? CurrentHighlightColor
        {
            get => _currentHighlightColor;
            set => this.RaiseAndSetIfChanged(ref _currentHighlightColor, value);
        }

        // --- Команды форматирования символов ---

        public ICommand ToggleBoldCommand { get; }
        public ICommand ToggleItalicCommand { get; }
        public ICommand ToggleUnderlineCommand { get; }
        public ICommand ToggleStrikethroughCommand { get; }
        public ICommand ToggleSuperscriptCommand { get; }
        public ICommand ToggleSubscriptCommand { get; }
        public ICommand ToggleAllCapsCommand { get; }
        public ICommand ClearFormattingCommand { get; }
        public ICommand SetTextColorCommand { get; }
        public ICommand SetHighlightColorCommand { get; }
        public ICommand SetFontFamilyCommand { get; }
        public ICommand SetFontSizeCommand { get; }
        public ICommand IncreaseFontSizeCommand { get; }
        public ICommand DecreaseFontSizeCommand { get; }

        // --- Команды форматирования абзаца ---

        public ICommand AlignLeftCommand { get; }
        public ICommand AlignCenterCommand { get; }
        public ICommand AlignRightCommand { get; }
        public ICommand AlignJustifyCommand { get; }
        public ICommand IncreaseIndentCommand { get; }
        public ICommand DecreaseIndentCommand { get; }
        public ICommand SetLineSpacingCommand { get; }
        public ICommand SetStyleCommand { get; }

        // --- Команды списков ---

        public ICommand ToggleBulletListCommand { get; }
        public ICommand ToggleNumberedListCommand { get; }
        public ICommand ToggleMultilevelListCommand { get; }

        // --- Команды буфера обмена (делегируются через HotKeyService) ---

        public ICommand CutCommand { get; }
        public ICommand CopyCommand { get; }
        public ICommand PasteCommand { get; }
        public ICommand SelectAllCommand { get; }
        public ICommand UndoCommand { get; }
        public ICommand RedoCommand { get; }

        // --- Команды поиска ---

        public ICommand FindCommand { get; }
        public ICommand FindReplaceCommand { get; }

        public RibbonHomeTabViewModel(ITextEditorCommandTarget target)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));

            ToggleBoldCommand         = ReactiveCommand.Create(() => _target.ToggleBold());
            ToggleItalicCommand       = ReactiveCommand.Create(() => _target.ToggleItalic());
            ToggleUnderlineCommand    = ReactiveCommand.Create(() => _target.ToggleUnderline());
            ToggleStrikethroughCommand = ReactiveCommand.Create(() => _target.ToggleStrikethrough());
            ToggleSuperscriptCommand  = ReactiveCommand.Create(() => _target.ToggleSuperscript());
            ToggleSubscriptCommand    = ReactiveCommand.Create(() => _target.ToggleSubscript());
            ToggleAllCapsCommand      = ReactiveCommand.Create(() => _target.ToggleAllCaps());
            ClearFormattingCommand    = ReactiveCommand.Create(() => _target.ClearFormatting());
            SetTextColorCommand       = ReactiveCommand.Create<string>(color => _target.SetTextColor(color));
            SetHighlightColorCommand  = ReactiveCommand.Create<string?>(color => _target.SetHighlightColor(color));
            SetFontFamilyCommand      = ReactiveCommand.Create<string>(font => _target.SetFontFamily(font));
            SetFontSizeCommand        = ReactiveCommand.Create<double>(size => _target.SetFontSize(size));
            IncreaseFontSizeCommand   = ReactiveCommand.Create(() => _target.IncreaseFontSize());
            DecreaseFontSizeCommand   = ReactiveCommand.Create(() => _target.DecreaseFontSize());

            AlignLeftCommand    = ReactiveCommand.Create(() => _target.SetAlignment(TextAlignment.Left));
            AlignCenterCommand  = ReactiveCommand.Create(() => _target.SetAlignment(TextAlignment.Center));
            AlignRightCommand   = ReactiveCommand.Create(() => _target.SetAlignment(TextAlignment.Right));
            AlignJustifyCommand = ReactiveCommand.Create(() => _target.SetAlignment(TextAlignment.Justify));
            IncreaseIndentCommand = ReactiveCommand.Create(() => _target.IncreaseIndent());
            DecreaseIndentCommand = ReactiveCommand.Create(() => _target.DecreaseIndent());
            SetLineSpacingCommand = ReactiveCommand.Create<double>(v => _target.SetLineSpacing(v));
            SetStyleCommand       = ReactiveCommand.Create<string>(name => _target.ApplyStyle(name));

            ToggleBulletListCommand    = ReactiveCommand.Create(() => _target.ToggleBulletList());
            ToggleNumberedListCommand  = ReactiveCommand.Create(() => _target.ToggleNumberedList());
            ToggleMultilevelListCommand = ReactiveCommand.Create(() => _target.ToggleMultilevelList());

            CutCommand       = ReactiveCommand.Create(() => _target.Cut());
            CopyCommand      = ReactiveCommand.Create(() => _target.Copy());
            PasteCommand     = ReactiveCommand.Create(() => _target.Paste());
            SelectAllCommand = ReactiveCommand.Create(() => _target.SelectAll());
            UndoCommand      = ReactiveCommand.Create(() => _target.Undo());
            RedoCommand      = ReactiveCommand.Create(() => _target.Redo());

            FindCommand        = ReactiveCommand.Create(() => _target.OpenFind());
            FindReplaceCommand = ReactiveCommand.Create(() => _target.OpenFindReplace());
        }

        /// <summary>
        /// Обновляет состояние переключателей по текущей позиции курсора.
        /// Вызывается из DocumentViewModel при смене выделения.
        /// </summary>
        public void UpdateFromCursorContext(CursorContext ctx)
        {
            IsBold        = ctx.IsBold;
            IsItalic      = ctx.IsItalic;
            IsUnderline   = ctx.IsUnderline;
            IsStrikethrough = ctx.IsStrikethrough;
            IsSuperscript = ctx.IsSuperscript;
            IsSubscript   = ctx.IsSubscript;
            IsAllCaps     = ctx.IsAllCaps;

            CurrentAlignment  = ctx.Alignment;
            CurrentStyleName  = ctx.StyleName ?? "Normal";
            CurrentFontFamily = ctx.FontFamily ?? "Times New Roman";
            CurrentFontSize   = ctx.FontSize > 0 ? ctx.FontSize : 14;
            CurrentTextColor  = ctx.TextColor ?? "#1A1A1A";
            CurrentHighlightColor = ctx.HighlightColor;
        }
    }

    /// <summary>
    /// Контекст форматирования в позиции курсора.
    /// Заполняется DocumentViewModel при смене позиции курсора.
    /// </summary>
    public sealed class CursorContext
    {
        public bool IsBold { get; set; }
        public bool IsItalic { get; set; }
        public bool IsUnderline { get; set; }
        public bool IsStrikethrough { get; set; }
        public bool IsSuperscript { get; set; }
        public bool IsSubscript { get; set; }
        public bool IsAllCaps { get; set; }

        public TextAlignment Alignment { get; set; }
        public string? StyleName { get; set; }
        public string? FontFamily { get; set; }
        public double FontSize { get; set; }
        public string? TextColor { get; set; }
        public string? HighlightColor { get; set; }
    }
}
