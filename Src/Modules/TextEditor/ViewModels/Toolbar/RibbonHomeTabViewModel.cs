using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using ReactiveUI;
using SkiaSharp;
using IBrush = Avalonia.Media.IBrush;
using SolidColorBrush = Avalonia.Media.SolidColorBrush;
using Color = Avalonia.Media.Color;
using Writersword.Modules.TextEditor.Models.Styles;
using Writersword.Modules.TextEditor.Contracts;

namespace Writersword.Modules.TextEditor.ViewModels.Toolbar
{
    /// <summary>
    /// Контекст курсора — снимок форматирования в позиции каретки.
    /// Передаётся из DocumentViewModel в RibbonHomeTabViewModel для синхронизации кнопок.
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
        public bool IsBulletList { get; set; }
        public bool IsNumberedList { get; set; }
        public string? FontFamily { get; set; }
        public double FontSize { get; set; } = 14;
        public string? TextColor { get; set; }
        public string? HighlightColor { get; set; }
        public TextAlignment Alignment { get; set; } = TextAlignment.Left;
        public string StyleName { get; set; } = "Normal";
        public string? Language { get; set; }

        /// <summary>Левый отступ активного абзаца в pt. Используется линейкой.</summary>
        public double LeftIndentPt { get; set; }

        /// <summary>Отступ первой строки активного абзаца в pt. Используется линейкой.</summary>
        public double FirstLineIndentPt { get; set; }

        /// <summary>Правый отступ активного абзаца в pt. Используется линейкой.</summary>
        public double RightIndentPt { get; set; }

        /// <summary>Есть ли у абзаца эффективный интервал перед (с учётом стиля).</summary>
        public bool HasSpaceBefore { get; set; }

        /// <summary>Есть ли у абзаца эффективный интервал после (с учётом стиля).</summary>
        public bool HasSpaceAfter { get; set; }
    }

    /// <summary>
    /// ViewModel вкладки "Главная" Ribbon.
    /// Хранит текущее состояние форматирования, команды для кнопок
    /// и свойства адаптивного отображения групп.
    /// </summary>
    public sealed class RibbonHomeTabViewModel : ReactiveObject
    {
        private readonly ITextEditorCommandTarget _target;

        // --- Состояние форматирования символов ---

        private bool _isBold;
        private bool _isItalic;
        private bool _isUnderline;
        private bool _isStrikethrough;
        private bool _isSuperscript;
        private bool _isSubscript;
        private bool _isAllCaps;
        private bool _isBulletList;
        private bool _isNumberedList;
        private string? _fontFamily;
        private string? _fontFamilyText;
        private double _currentFontSize = 14;
        private string _currentFontSizeText = "14";
        private string? _currentTextColor;
        private string? _currentHighlightColor;

        // --- Флаг подавления рекурсии при синхронизации размера шрифта ---
        private bool _isSyncingFontSize;

        // --- Флаг активного font-preview (дропдаун открыт, пользователь листает) ---
        // Пока true — CurrentFontFamily.set НЕ вызывает SetFontFamily,
        // чтобы навигация стрелками не порождала записи в Undo и RebuildLayouts.
        private bool _fontPreviewActive;
        private string? _previewOriginalFont;

        // --- Состояние форматирования абзаца ---

        private TextAlignment _currentAlignment = TextAlignment.Left;
        private string _currentStyleName = "Normal";
        private bool _isSpaceBefore;
        private bool _isSpaceAfter;
        private int _outlineLevel;

        // --- Адаптивное отображение ---

        private bool _isClipboardGroupExpanded = true;
        private bool _isParagraphGroupExpanded = true;
        private bool _isEditGroupExpanded = true;
        private bool _isStylesGroupExpanded = true;
        private IReadOnlyList<string> _visibleStyles;

        // --- Константы геометрии риббона ---

        private const double CardWidth = 66;
        private const double WidthFont = 300;
        private const double WidthClipboardFull = 135;
        private const double WidthClipboardSmall = 66;
        private const double WidthParagraphFull = 295;
        private const double WidthParagraphSmall = 66;
        private const double WidthEditFull = 172;
        private const double WidthEditSmall = 66;
        private const double WidthStylesSmall = 66;
        private const double StylesGroupOverhead = 20;
        private const int MaxCards = 10;
        private const int MinCards = 3;

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
                {
                    _fontFamilyText = value;
                    this.RaisePropertyChanged(nameof(CurrentFontFamilyText));
                    // Во время preview навигация стрелками не должна писать в Undo-стек
                    // и вызывать RebuildLayouts. Реальное применение — в EndFontPreview.
                    if (!_fontPreviewActive)
                        _target.SetFontFamily(value);
                }
            }
        }

        /// <summary>
        /// Текст отображаемый в AutoCompleteBox шрифта.
        /// Изменяется при перемещении курсора (UpdateFromCursorContext) и при выборе из списка.
        /// Не вызывает SetFontFamily — это делает CurrentFontFamily через SelectedItem.
        /// </summary>
        public string? CurrentFontFamilyText
        {
            get => _fontFamilyText;
            set => this.RaiseAndSetIfChanged(ref _fontFamilyText, value);
        }

        public double CurrentFontSize
        {
            get => _currentFontSize;
            set
            {
                if (this.RaiseAndSetIfChanged(ref _currentFontSize, value) is { } && value > 0)
                {
                    if (!_isSyncingFontSize)
                    {
                        _isSyncingFontSize = true;
                        CurrentFontSizeText = ((int)value).ToString();
                        _isSyncingFontSize = false;
                    }
                    _target.SetFontSize(value);
                }
            }
        }

        /// <summary>
        /// Строковое представление размера шрифта для редактируемого поля.
        /// При изменении парсится и применяется к документу.
        /// Синхронизируется с CurrentFontSize в обе стороны.
        /// </summary>
        public string CurrentFontSizeText
        {
            get => _currentFontSizeText;
            set
            {
                if (this.RaiseAndSetIfChanged(ref _currentFontSizeText, value) is { } && !_isSyncingFontSize)
                {
                    if (double.TryParse(value,
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double parsed) && parsed > 0 && parsed <= 144)
                    {
                        _isSyncingFontSize = true;
                        _currentFontSize = parsed;
                        this.RaisePropertyChanged(nameof(CurrentFontSize));
                        _target.SetFontSize(parsed);
                        _isSyncingFontSize = false;
                    }
                }
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

        // Цвет для пикеров в Ribbon (двусторонняя привязка к ColorPickerButton). Контекст каретки
        // пишет в поле напрямую (без применения, см. UpdateFromCursorContext), а выбор цвета
        // пользователем идёт через сеттер и применяет цвет к выделению/тексту.
        private string _textColorPick = "#1A1A1A";
        public string TextColorPick
        {
            get => _textColorPick;
            set
            {
                if (string.Equals(_textColorPick, value, StringComparison.OrdinalIgnoreCase)) return;
                this.RaiseAndSetIfChanged(ref _textColorPick, value);
                if (!string.IsNullOrWhiteSpace(value)) _target.SetTextColor(value);
            }
        }

        private string _highlightColorPick = "#FFF176";
        public string HighlightColorPick
        {
            get => _highlightColorPick;
            set
            {
                if (string.Equals(_highlightColorPick, value, StringComparison.OrdinalIgnoreCase)) return;
                this.RaiseAndSetIfChanged(ref _highlightColorPick, value);
                if (!string.IsNullOrWhiteSpace(value)) _target.SetHighlightColor(value);
            }
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

        // --- Свойства: адаптивное отображение ---

        /// <summary>
        /// True — группа Буфер обмена показывается полностью.
        /// False — свёрнута в кнопку с Flyout.
        /// </summary>
        public bool IsClipboardGroupExpanded
        {
            get => _isClipboardGroupExpanded;
            set => this.RaiseAndSetIfChanged(ref _isClipboardGroupExpanded, value);
        }

        /// <summary>
        /// True — группа Абзац показывается полностью.
        /// False — свёрнута в кнопку с Flyout.
        /// </summary>
        public bool IsParagraphGroupExpanded
        {
            get => _isParagraphGroupExpanded;
            set => this.RaiseAndSetIfChanged(ref _isParagraphGroupExpanded, value);
        }

        /// <summary>
        /// True — группа Правка показывается полностью.
        /// False — свёрнута в кнопку с Flyout.
        /// </summary>
        public bool IsEditGroupExpanded
        {
            get => _isEditGroupExpanded;
            set => this.RaiseAndSetIfChanged(ref _isEditGroupExpanded, value);
        }

        /// <summary>
        /// True — группа Стили показывается полностью.
        /// False — свёрнута в кнопку с Flyout.
        /// </summary>
        public bool IsStylesGroupExpanded
        {
            get => _isStylesGroupExpanded;
            set => this.RaiseAndSetIfChanged(ref _isStylesGroupExpanded, value);
        }

        /// <summary>
        /// Подмножество AvailableStyles для отображения в галерее.
        /// Количество карточек уменьшается по одной при сужении риббона.
        /// </summary>
        public IReadOnlyList<string> VisibleStyles
        {
            get => _visibleStyles;
            private set => this.RaiseAndSetIfChanged(ref _visibleStyles, value);
        }

        // --- Доступные значения ---

        public IReadOnlyList<string> AvailableFonts { get; } = LoadSystemFonts();

        private static IReadOnlyList<string> LoadSystemFonts()
        {
            try
            {
                var families = SKFontManager.Default.FontFamilies;
                return families
                    .Where(f => !string.IsNullOrWhiteSpace(f))
                    .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch
            {
                return new[] { "Arial", "Times New Roman", "Calibri", "Georgia", "Verdana" };
            }
        }

        /// <summary>
        /// Стандартный набор размеров шрифта как в Word.
        /// Отображается в выпадающем списке поля размера.
        /// </summary>
        public IReadOnlyList<string> StandardFontSizes { get; } = new[]
        {
            "8", "9", "10", "11", "12", "14", "16", "18",
            "20", "22", "24", "28", "32", "36", "48", "72"
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

        // --- Команды: форматирование символов ---

        public ICommand BoldCommand { get; }
        public ICommand ItalicCommand { get; }
        public ICommand UnderlineCommand { get; }
        public ICommand StrikethroughCommand { get; }
        public ICommand SuperscriptCommand { get; }
        public ICommand SubscriptCommand { get; }
        public ICommand AllCapsCommand { get; }
        public ICommand ClearFormattingCommand { get; }
        public ICommand TextColorCommand { get; }
        public ICommand HighlightColorCommand { get; }
        public ICommand IncreaseFontSizeCommand { get; }
        public ICommand DecreaseFontSizeCommand { get; }

        /// <summary>
        /// Применяет выбранный размер из выпадающего списка.
        /// CommandParameter — строка с числом.
        /// </summary>
        public ICommand SelectFontSizeCommand { get; }

        // --- Команды: смена регистра ---

        /// <summary>Как в предложениях — первая буква предложения заглавная.</summary>
        public ICommand CaseSentenceCommand { get; }

        /// <summary>все строчные.</summary>
        public ICommand CaseLowerCommand { get; }

        /// <summary>ВСЕ ПРОПИСНЫЕ.</summary>
        public ICommand CaseUpperCommand { get; }

        /// <summary>Начинать С Прописных — каждое слово с заглавной.</summary>
        public ICommand CaseTitleCommand { get; }

        /// <summary>иЗМЕНИТЬ РЕГИСТР — инвертирует регистр каждого символа.</summary>
        public ICommand CaseToggleCommand { get; }

        // --- Команды: эффекты текста (заготовки) ---

        /// <summary>Контур текста — заготовка для будущей реализации.</summary>
        public ICommand TextOutlineCommand { get; }

        /// <summary>Тень текста — заготовка для будущей реализации.</summary>
        public ICommand TextShadowCommand { get; }

        /// <summary>Отражение текста — заготовка для будущей реализации.</summary>
        public ICommand TextReflectionCommand { get; }

        /// <summary>Свечение текста — заготовка для будущей реализации.</summary>
        public ICommand TextGlowCommand { get; }

        // --- Команды: форматирование абзаца ---

        public ICommand BulletListCommand { get; }
        public ICommand NumberedListCommand { get; }
        public ICommand IncreaseIndentCommand { get; }
        public ICommand DecreaseIndentCommand { get; }
        public ICommand AlignLeftCommand { get; }
        public ICommand AlignCenterCommand { get; }
        public ICommand AlignRightCommand { get; }
        public ICommand AlignJustifyCommand { get; }
        public ICommand SetLineSpacingCommand { get; }
        public ICommand SpaceBeforeCommand { get; }
        public ICommand SpaceAfterCommand { get; }
        public ICommand SetOutlineLevelCommand { get; }

        // --- Состояние: интервалы до/после и уровень структуры ---

        public bool IsSpaceBefore
        {
            get => _isSpaceBefore;
            set => this.RaiseAndSetIfChanged(ref _isSpaceBefore, value);
        }

        public bool IsSpaceAfter
        {
            get => _isSpaceAfter;
            set => this.RaiseAndSetIfChanged(ref _isSpaceAfter, value);
        }

        public int OutlineLevel
        {
            get => _outlineLevel;
            private set
            {
                this.RaiseAndSetIfChanged(ref _outlineLevel, value);
                this.RaisePropertyChanged(nameof(OutlineLevelLabel));
                this.RaisePropertyChanged(nameof(OutlineLevelBrush));
            }
        }

        /// <summary>Подпись текущего уровня структуры для кнопки риббона.</summary>
        public string OutlineLevelLabel => _outlineLevel <= 0 ? "Основной текст" : $"Уровень {_outlineLevel}";

        /// <summary>Цвет текущего уровня структуры: серый для основного текста, далее градиент к серому.</summary>
        public IBrush OutlineLevelBrush => new SolidColorBrush(Color.Parse(OutlineLevelHex(_outlineLevel)));

        private static string OutlineLevelHex(int lvl) => lvl switch
        {
            <= 0 => "#8A8A8A",
            1 => "#E07B39",
            2 => "#D67D43",
            3 => "#CB7F4E",
            4 => "#C18158",
            5 => "#B68463",
            6 => "#AC866D",
            7 => "#A18877",
            8 => "#978A82",
            _ => "#8C8C8C"
        };

        // --- Команды: буфер, правка ---

        public ICommand CutCommand { get; }
        public ICommand CopyCommand { get; }
        public ICommand PasteCommand { get; }
        public ICommand SelectAllCommand { get; }
        public ICommand UndoCommand { get; }
        public ICommand RedoCommand { get; }
        public ICommand FindCommand { get; }
        public ICommand FindReplaceCommand { get; }

        // --- Команды: стили ---

        /// <summary>Сохраняет форматирование курсора как стиль.</summary>
        public ICommand SaveStyleFromCursorCommand { get; }

        /// <summary>Открывает окно редактора стилей.</summary>
        public ICommand EditStylesCommand { get; }

        /// <summary>Сбрасывает стили к стандартным.</summary>
        public ICommand ResetStylesToDefaultsCommand { get; }

        public RibbonHomeTabViewModel(ITextEditorCommandTarget target)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
            _visibleStyles = AvailableStyles;

            BoldCommand = ReactiveCommand.Create(() => _target.ToggleBold());
            ItalicCommand = ReactiveCommand.Create(() => _target.ToggleItalic());
            UnderlineCommand = ReactiveCommand.Create(() => _target.ToggleUnderline());
            StrikethroughCommand = ReactiveCommand.Create(() => _target.ToggleStrikethrough());
            SuperscriptCommand = ReactiveCommand.Create(() => _target.ToggleSuperscript());
            SubscriptCommand = ReactiveCommand.Create(() => _target.ToggleSubscript());
            AllCapsCommand = ReactiveCommand.Create(() => _target.ToggleAllCaps());
            ClearFormattingCommand = ReactiveCommand.Create(() => _target.ClearFormatting());

            TextColorCommand = ReactiveCommand.Create(() =>
                _target.SetTextColor(_currentTextColor ?? "#1A1A1A"));
            HighlightColorCommand = ReactiveCommand.Create(() =>
                _target.SetHighlightColor(_currentHighlightColor));

            IncreaseFontSizeCommand = ReactiveCommand.Create(() => _target.IncreaseFontSize());
            DecreaseFontSizeCommand = ReactiveCommand.Create(() => _target.DecreaseFontSize());

            // Применяет размер шрифта выбранный из выпадающего списка.
            SelectFontSizeCommand = ReactiveCommand.Create<string>(param =>
            {
                if (double.TryParse(param, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double v) && v > 0)
                {
                    _isSyncingFontSize = true;
                    _currentFontSize = v;
                    _currentFontSizeText = ((int)v).ToString();
                    this.RaisePropertyChanged(nameof(CurrentFontSize));
                    this.RaisePropertyChanged(nameof(CurrentFontSizeText));
                    _isSyncingFontSize = false;
                    _target.SetFontSize(v);
                }
            });

            // Смена регистра — делегируем в ToggleAllCaps или реализуем через target позже.
            CaseSentenceCommand = ReactiveCommand.Create(() => _target.ChangeCase(TextCaseMode.Sentence));
            CaseLowerCommand = ReactiveCommand.Create(() => _target.ChangeCase(TextCaseMode.Lower));
            CaseUpperCommand = ReactiveCommand.Create(() => _target.ChangeCase(TextCaseMode.Upper));
            CaseTitleCommand = ReactiveCommand.Create(() => _target.ChangeCase(TextCaseMode.Title));
            CaseToggleCommand = ReactiveCommand.Create(() => _target.ChangeCase(TextCaseMode.Toggle));

            // Эффекты текста — заготовки.
            TextOutlineCommand = ReactiveCommand.Create(() => { });
            TextShadowCommand = ReactiveCommand.Create(() => { });
            TextReflectionCommand = ReactiveCommand.Create(() => { });
            TextGlowCommand = ReactiveCommand.Create(() => { });

            BulletListCommand = ReactiveCommand.Create(() => _target.ToggleBulletList());
            NumberedListCommand = ReactiveCommand.Create(() => _target.ToggleNumberedList());
            IncreaseIndentCommand = ReactiveCommand.Create(() => _target.IncreaseIndent());
            DecreaseIndentCommand = ReactiveCommand.Create(() => _target.DecreaseIndent());

            AlignLeftCommand = ReactiveCommand.Create(() => _target.SetAlignment(TextAlignment.Left));
            AlignCenterCommand = ReactiveCommand.Create(() => _target.SetAlignment(TextAlignment.Center));
            AlignRightCommand = ReactiveCommand.Create(() => _target.SetAlignment(TextAlignment.Right));
            AlignJustifyCommand = ReactiveCommand.Create(() => _target.SetAlignment(TextAlignment.Justify));

            // CommandParameter передаётся строкой из AXAML ("1.0", "1.5" и т.д.).
            SetLineSpacingCommand = ReactiveCommand.Create<string>(param =>
            {
                if (double.TryParse(param, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double v))
                    _target.SetLineSpacing(v);
            });

            SpaceBeforeCommand = ReactiveCommand.Create(() => _target.SetSpaceBefore(IsSpaceBefore ? 8 : 0));
            SpaceAfterCommand = ReactiveCommand.Create(() => _target.SetSpaceAfter(IsSpaceAfter ? 8 : 0));
            SetOutlineLevelCommand = ReactiveCommand.Create<string>(param =>
            {
                if (int.TryParse(param, out int lvl))
                    _target.SetOutlineLevel(lvl);
            });

            CutCommand = ReactiveCommand.Create(() => _target.Cut());
            CopyCommand = ReactiveCommand.Create(() => _target.Copy());
            PasteCommand = ReactiveCommand.Create(() => _target.Paste());
            SelectAllCommand = ReactiveCommand.Create(() => _target.SelectAll());
            UndoCommand = ReactiveCommand.Create(() => _target.Undo());
            RedoCommand = ReactiveCommand.Create(() => _target.Redo());
            FindCommand = ReactiveCommand.Create(() => _target.OpenFind());
            FindReplaceCommand = ReactiveCommand.Create(() => _target.OpenFindReplace());

            SaveStyleFromCursorCommand = ReactiveCommand.Create(() => _target.ApplyStyle(_currentStyleName));
            EditStylesCommand = ReactiveCommand.Create(() => { });
            ResetStylesToDefaultsCommand = ReactiveCommand.Create(() => { });
        }

        /// <summary>
        /// Синхронизирует состояние кнопок Ribbon с контекстом курсора.
        /// При установке полей напрямую (через backing field) не вызывает команды.
        /// </summary>
        public void UpdateFromCursorContext(CursorContext ctx)
        {
            _isBold = ctx.IsBold;
            _isItalic = ctx.IsItalic;
            _isUnderline = ctx.IsUnderline;
            _isStrikethrough = ctx.IsStrikethrough;
            _isSuperscript = ctx.IsSuperscript;
            _isSubscript = ctx.IsSubscript;
            _isAllCaps = ctx.IsAllCaps;
            _isBulletList = ctx.IsBulletList;
            _isNumberedList = ctx.IsNumberedList;
            _fontFamily = ctx.FontFamily;
            _fontFamilyText = ctx.FontFamily;
            _currentFontSize = ctx.FontSize;
            _currentFontSizeText = ((int)ctx.FontSize).ToString();
            _currentTextColor = ctx.TextColor;
            _currentHighlightColor = ctx.HighlightColor;
            _textColorPick = ctx.TextColor ?? "#1A1A1A";
            _highlightColorPick = ctx.HighlightColor ?? "#FFF176";
            _currentAlignment = ctx.Alignment;
            _currentStyleName = ctx.StyleName;

            // Интервалы берём по эффективному значению (с учётом стиля) из CursorContext,
            // уровень структуры — из собственных свойств абзаца.
            _isSpaceBefore = ctx.HasSpaceBefore;
            _isSpaceAfter = ctx.HasSpaceAfter;
            _outlineLevel = _target.GetActiveParagraphProperties()?.OutlineLevel ?? 0;

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
            this.RaisePropertyChanged(nameof(CurrentFontFamilyText));
            this.RaisePropertyChanged(nameof(CurrentFontSize));
            this.RaisePropertyChanged(nameof(CurrentFontSizeText));
            this.RaisePropertyChanged(nameof(CurrentTextColor));
            this.RaisePropertyChanged(nameof(CurrentHighlightColor));
            this.RaisePropertyChanged(nameof(TextColorPick));
            this.RaisePropertyChanged(nameof(HighlightColorPick));
            this.RaisePropertyChanged(nameof(CurrentAlignment));
            this.RaisePropertyChanged(nameof(CurrentStyleName));
            this.RaisePropertyChanged(nameof(IsSpaceBefore));
            this.RaisePropertyChanged(nameof(IsSpaceAfter));
            this.RaisePropertyChanged(nameof(OutlineLevel));
            this.RaisePropertyChanged(nameof(OutlineLevelLabel));
            this.RaisePropertyChanged(nameof(OutlineLevelBrush));
        }

        /// <summary>
        /// Обновляет адаптивное отображение риббона по доступной ширине.
        /// Порядок сворачивания:
        ///   1. Стили теряют карточки по одной (MaxCards → MinCards).
        ///   2. Стили схлопываются в кнопку.
        ///   3. Правка схлопывается.
        ///   4. Абзац схлопывается.
        ///   5. Буфер обмена схлопывается.
        ///   6. Только после этого появляются стрелки (управляется code-behind).
        /// </summary>
        public void UpdateLayout(double availableWidth)
        {
            double baseWidth = WidthFont
                + WidthClipboardFull
                + WidthParagraphFull
                + WidthEditFull
                + StylesGroupOverhead;

            double stylesSpace = availableWidth - baseWidth;

            int cards = stylesSpace > 0
                ? Math.Clamp((int)(stylesSpace / CardWidth), 0, MaxCards)
                : 0;

            if (cards >= MinCards)
            {
                IsClipboardGroupExpanded = true;
                IsParagraphGroupExpanded = true;
                IsEditGroupExpanded = true;
                IsStylesGroupExpanded = true;
                SetVisibleStyles(cards);
                return;
            }

            IsStylesGroupExpanded = false;
            SetVisibleStyles(0);

            double w3 = WidthFont + WidthClipboardFull + WidthParagraphFull
                      + WidthEditSmall + WidthStylesSmall;

            if (availableWidth >= w3)
            {
                IsClipboardGroupExpanded = true;
                IsParagraphGroupExpanded = true;
                IsEditGroupExpanded = false;
                return;
            }

            IsEditGroupExpanded = false;

            double w4 = WidthFont + WidthClipboardFull + WidthParagraphSmall
                      + WidthEditSmall + WidthStylesSmall;

            if (availableWidth >= w4)
            {
                IsClipboardGroupExpanded = true;
                IsParagraphGroupExpanded = false;
                return;
            }

            IsParagraphGroupExpanded = false;

            IsClipboardGroupExpanded = availableWidth >= 645;
        }

        /// <summary>
        /// Устанавливает подмножество стилей для отображения в галерее.
        /// При count == 0 устанавливает пустой список.
        /// </summary>
        private void SetVisibleStyles(int count)
        {
            if (count <= 0)
            {
                VisibleStyles = Array.Empty<string>();
                return;
            }

            int clamped = Math.Min(count, AvailableStyles.Count);
            if (VisibleStyles.Count == clamped) return;
            VisibleStyles = AvailableStyles.Take(clamped).ToList();
        }

        public void BeginFontPreview()
        {
            _fontPreviewActive = true;
            _previewOriginalFont = _fontFamily;
            _target.BeginFontPreview();
        }

        public void PreviewFontFamily(string f) => _target.PreviewFontFamily(f);

        public void EndFontPreview(bool commit)
        {
            _fontPreviewActive = false;
            if (commit && _fontFamily is not null)
            {
                _target.SetFontFamily(_fontFamily);
            }
            else if (!commit)
            {
                _fontFamily = _previewOriginalFont;
                _fontFamilyText = _previewOriginalFont;
                this.RaisePropertyChanged(nameof(CurrentFontFamily));
                this.RaisePropertyChanged(nameof(CurrentFontFamilyText));
            }
            _target.EndFontPreview(commit);
            _previewOriginalFont = null;
        }
    }
}