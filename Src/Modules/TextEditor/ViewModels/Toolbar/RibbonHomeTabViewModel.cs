using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using ReactiveUI;
using SkiaSharp;
using IBrush = Avalonia.Media.IBrush;
using SolidColorBrush = Avalonia.Media.SolidColorBrush;
using Color = Avalonia.Media.Color;
using Geometry = Avalonia.Media.Geometry;
using StreamGeometry = Avalonia.Media.StreamGeometry;
using Writersword.Modules.TextEditor.Models.Styles;
using Writersword.Modules.TextEditor.Contracts;
using UnderlineStyle = Writersword.Modules.TextEditor.Models.Inline.UnderlineStyle;
using EmphasisMark = Writersword.Modules.TextEditor.Models.Inline.EmphasisMark;
using TextEffectPreset = Writersword.Modules.TextEditor.Models.Inline.TextEffectPreset;

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

        /// <summary>Вид подчёркивания под кареткой. None — подчёркивания нет.</summary>
        public UnderlineStyle UnderlineStyle { get; set; }

        /// <summary>Цвет линии подчёркивания под кареткой. Null — как у букв.</summary>
        public string? UnderlineColor { get; set; }

        public bool IsStrikethrough { get; set; }

        /// <summary>Двойное зачёркивание под кареткой.</summary>
        public bool IsDoubleStrikethrough { get; set; }

        public bool IsSuperscript { get; set; }
        public bool IsSubscript { get; set; }
        public bool IsAllCaps { get; set; }

        /// <summary>Малые прописные под кареткой.</summary>
        public bool IsSmallCaps { get; set; }

        /// <summary>Скрытый текст под кареткой (w:vanish).</summary>
        public bool IsHidden { get; set; }

        /// <summary>Эффекты букв под кареткой: контур, тень, рельеф, гравировка.</summary>
        public bool IsOutline { get; set; }
        public bool IsShadow { get; set; }
        public bool IsEmboss { get; set; }
        public bool IsImprint { get; set; }

        /// <summary>Знак ударения под кареткой. None — знака нет.</summary>
        public EmphasisMark EmphasisMark { get; set; }

        /// <summary>У текста под кареткой есть рамка вокруг знаков.</summary>
        public bool HasCharBorder { get; set; }

        /// <summary>Свечение и отражение под кареткой (эффекты Word 2010+).</summary>
        public bool IsGlow { get; set; }
        public bool IsReflection { get; set; }

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

        // Вид подчёркивания под кареткой — для отметки в меню подчёркивания.
        private UnderlineStyle _underlineStyle;

        // Цвет линии подчёркивания под кареткой. Null — как у букв.
        private string? _underlineColor;

        // Вид, который ставит сама кнопка подчёркивания (без меню): последний выбранный,
        // как в Word.
        private UnderlineStyle _lastUnderlineStyle = UnderlineStyle.Single;

        private bool _isStrikethrough;
        private bool _isDoubleStrikethrough;

        // Вид, который ставит сама кнопка зачёркивания: последний выбранный в меню.
        private bool _lastStrikeDouble;
        private bool _isSuperscript;
        private bool _isSubscript;
        private bool _isAllCaps;

        // Эффекты из меню «Эффекты текста»: состояние под кареткой.
        private bool _isSmallCaps;
        private bool _isHiddenText;
        private bool _isTextOutline;
        private bool _isTextShadow;
        private bool _isTextEmboss;
        private bool _isTextImprint;
        private EmphasisMark _emphasisMark;
        private bool _isCharBorder;
        private bool _isTextGlow;
        private bool _isTextReflection;

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

        // --- Рамка абзаца ---

        // Перо рамки: им кнопка «Рамка» ставит новые линии и перерисовывает стоящие.
        // Когда каретка стоит в абзаце с рамкой, перо берёт вид его линии — тогда
        // отметки в меню и цвет рядом с кнопкой показывают рамку под кареткой.
        private BorderStylePen _borderPen = BorderStylePen.Default;

        // Какие стороны рамки видны у выделения — для отметок в меню.
        private ParagraphBorderSides _borderSides;

        // Сторона, которую ставит сама кнопка (без меню): последняя выбранная, как в Word.
        private ParagraphBorderSides _lastBorderSides = ParagraphBorderSides.Bottom;

        // --- Адаптивное отображение ---

        private bool _isClipboardGroupExpanded = true;
        private bool _isParagraphGroupExpanded = true;
        private bool _isEditGroupExpanded = true;
        private bool _isStylesGroupExpanded = true;
        private IReadOnlyList<StyleCardViewModel> _visibleStyles = Array.Empty<StyleCardViewModel>();
        private IReadOnlyList<StyleCardViewModel> _availableStyles = Array.Empty<StyleCardViewModel>();
        private StyleCardViewModel? _currentStyle;

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

        /// <summary>
        /// Вид подчёркивания под кареткой ключом для отметки в меню. У текста без
        /// подчёркивания — None, и отмечена плитка «Нет».
        /// </summary>
        public string UnderlineStyleKey => _underlineStyle.ToString();

        /// <summary>
        /// Вид, который ставит кнопка подчёркивания. Им нарисована линия под буквой на
        /// самой кнопке — видно, что ляжет по нажатию.
        /// </summary>
        public UnderlineStyle LastUnderlineStyle => _lastUnderlineStyle;

        /// <summary>Линия подчёркивания под кареткой «авто» — цвета букв.</summary>
        public bool IsUnderlineColorAuto => _underlineColor is null;

        // Цвет в меню подчёркивания. Контекст каретки пишет в поле напрямую (без
        // применения); выбор человеком идёт через сеттер и красит линию выделения.
        // При «авто» показывает цвет букв — им линия и нарисована.
        private string _underlineColorPick = "#1A1A1A";
        public string UnderlineColorPick
        {
            get => _underlineColorPick;
            set
            {
                if (string.Equals(_underlineColorPick, value, StringComparison.OrdinalIgnoreCase)) return;
                this.RaiseAndSetIfChanged(ref _underlineColorPick, value);
                if (!string.IsNullOrWhiteSpace(value)) _target.SetUnderlineColor(value);
            }
        }

        public bool IsStrikethrough
        {
            get => _isStrikethrough;
            set => this.RaiseAndSetIfChanged(ref _isStrikethrough, value);
        }

        /// <summary>Под кареткой есть зачёркивание — одинарное или двойное. Горит кнопка.</summary>
        public bool IsStrikeAny => _isStrikethrough || _isDoubleStrikethrough;

        /// <summary>Вид зачёркивания под кареткой: None, Single или Double — отметка плитки в меню.</summary>
        public string StrikeKindKey => _isDoubleStrikethrough ? "Double" : _isStrikethrough ? "Single" : "None";

        /// <summary>
        /// Вид, который ставит сама кнопка зачёркивания (без меню): последний выбранный,
        /// как у подчёркивания. Нарисован на кнопке.
        /// </summary>
        public bool LastStrikeDouble => _lastStrikeDouble;

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

        /// <summary>Малые прописные под кареткой — отметка в меню «Эффекты текста».</summary>
        public bool IsSmallCaps => _isSmallCaps;

        /// <summary>Скрытый текст под кареткой.</summary>
        public bool IsHiddenText => _isHiddenText;

        /// <summary>Контур букв под кареткой.</summary>
        public bool IsTextOutline => _isTextOutline;

        /// <summary>Тень букв под кареткой.</summary>
        public bool IsTextShadow => _isTextShadow;

        /// <summary>Рельеф букв под кареткой.</summary>
        public bool IsTextEmboss => _isTextEmboss;

        /// <summary>Гравировка букв под кареткой.</summary>
        public bool IsTextImprint => _isTextImprint;

        /// <summary>Рамка вокруг знаков под кареткой.</summary>
        public bool IsCharBorder => _isCharBorder;

        /// <summary>Свечение вокруг букв под кареткой.</summary>
        public bool IsTextGlow => _isTextGlow;

        /// <summary>Отражение букв под кареткой.</summary>
        public bool IsTextReflection => _isTextReflection;

        /// <summary>
        /// Знак ударения под кареткой: None, Dot, Comma, Circle или UnderDot — отметка
        /// плитки в меню.
        /// </summary>
        public string EmphasisMarkKey => _emphasisMark.ToString();

        /// <summary>
        /// Под кареткой есть хоть один эффект из меню «Эффекты текста» — кнопка меню
        /// подсвечена, чтобы эффект было видно и без открытия меню.
        /// </summary>
        public bool IsAnyTextEffect =>
            _isAllCaps || _isSmallCaps || _isHiddenText
            || _isTextOutline || _isTextShadow || _isTextEmboss || _isTextImprint
            || _emphasisMark != EmphasisMark.None || _isCharBorder
            || _isTextGlow || _isTextReflection;

        /// <summary>
        /// Под кареткой есть то, что входит в набор «Мои эффекты»: эффекты букв, рамка
        /// знаков или знак ударения. Регистр и скрытый текст — не эффекты вида, и из
        /// них набор не заводится.
        /// </summary>
        public bool CanSaveCaretTextEffectPreset =>
            _isTextOutline || _isTextShadow || _isTextEmboss || _isTextImprint
            || _isTextGlow || _isTextReflection
            || _emphasisMark != EmphasisMark.None || _isCharBorder;

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
                    EnsureFontListed(value);
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
        /// Имя гарнитуры, показанное в поле ленты.
        /// Меняется при перемещении курсора (UpdateFromCursorContext) и при выборе из списка.
        /// Само по себе SetFontFamily не вызывает — рукопись меняет CurrentFontFamily.
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

        /// <summary>
        /// Внутреннее имя стиля под кареткой. Правится не человеком, а приходом контекста:
        /// выбор в галерее идёт через <see cref="CurrentStyle"/>.
        /// </summary>
        public string CurrentStyleName
        {
            get => _currentStyleName;
            private set => this.RaiseAndSetIfChanged(ref _currentStyleName, value);
        }

        /// <summary>
        /// Карточка, выбранная в галерее. Её выбор и есть применение стиля.
        ///
        /// Абзацу уходит Name, а не то, что написано на карточке. Прежде галерея работала
        /// со строками отображаемых имён и отдавала их прямо в ApplyStyle: абзац получал
        /// «Heading 1», стиля с таким именем в рукописи нет, и он оставался «Обычным» —
        /// а заодно переставал быть заголовком для оглавления, которое ищет стиль по Name.
        /// </summary>
        public StyleCardViewModel? CurrentStyle
        {
            get => _currentStyle;
            set
            {
                if (this.RaiseAndSetIfChanged(ref _currentStyle, value) is null) return;
                if (value is null) return;

                // Символьный стиль ложится на выделение и абзацного имени под кареткой
                // не меняет — сравнивать его с ним нечего, иначе повторное наложение
                // одного и того же стиля на другой кусок текста не сработало бы.
                if (value.IsCharacterStyle)
                {
                    _target.ApplyCharacterStyle(value.Name);
                    return;
                }

                // Приход контекста от каретки тоже ставит это свойство. Отличить его от
                // выбора человеком можно по имени: если стиль под кареткой уже такой,
                // применять нечего — иначе каждый щелчок по тексту писал бы абзацу его
                // же стиль и плодил шаги отмены.
                if (string.Equals(value.Name, _currentStyleName, StringComparison.Ordinal)) return;

                CurrentStyleName = value.Name;
                _target.ApplyStyle(value.Name);
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
        public IReadOnlyList<StyleCardViewModel> VisibleStyles
        {
            get => _visibleStyles;
            private set => this.RaiseAndSetIfChanged(ref _visibleStyles, value);
        }

        // --- Доступные значения ---

        /// <summary>
        /// Гарнитуры для списка выбора: системные, уложенные в проект и та,
        /// что стоит под курсором.
        ///
        /// Последнее обязательно. Список выделяет пункт, совпадающий с гарнитурой
        /// под курсором, и если её среди пунктов нет — выделение уходит на первый
        /// попавшийся. Человек видел бы не своё имя шрифта, а чужое, и стрелки
        /// вели бы не оттуда, откуда он начал.
        /// </summary>
        public IReadOnlyList<string> AvailableFonts
        {
            get => _availableFonts;
            private set => this.RaiseAndSetIfChanged(ref _availableFonts, value);
        }

        private IReadOnlyList<string> _availableFonts =
            Services.ProjectFonts.PickerFamilies();

        /// <summary>
        /// Дописать гарнитуру в список, если её там ещё нет. Зовётся при смене
        /// шрифта под курсором: документ мог прийти с машины, где этот шрифт был.
        /// </summary>
        private void EnsureFontListed(string? family)
        {
            if (string.IsNullOrWhiteSpace(family)) return;
            if (_availableFonts.Contains(family!, StringComparer.OrdinalIgnoreCase)) return;

            AvailableFonts = Services.ProjectFonts.PickerFamilies(family);
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

        /// <summary>
        /// Стили рукописи — все, какие в ней есть, включая написанные человеком.
        ///
        /// Раньше здесь стоял неизменяемый список из десяти имён. Он не знал ни о
        /// пользовательских стилях, ни о стилях оглавления, ни о тех, что приехали с
        /// импортированным документом: показать их было негде, а созданный стиль не
        /// появился бы в галерее вовсе.
        /// </summary>
        public IReadOnlyList<StyleCardViewModel> AvailableStyles
        {
            get => _availableStyles;
            private set => this.RaiseAndSetIfChanged(ref _availableStyles, value);
        }

        /// <summary>
        /// Перечитывает список стилей у рукописи. Зовётся при открытии документа и
        /// всякий раз, когда список стилей изменился.
        /// </summary>
        public void RefreshStyles()
        {
            var document = _target.StyleSourceDocument;
            var styles = _target.DocumentStyles;

            var cards = new List<StyleCardViewModel>(styles.Count);

            foreach (var style in styles)
            {
                if (string.IsNullOrEmpty(style.Name)) continue;

                cards.Add(new StyleCardViewModel(style, document));
            }

            cards.Sort(static (a, b) =>
            {
                int byOrder = a.SortOrderKey.CompareTo(b.SortOrderKey);
                return byOrder != 0
                    ? byOrder
                    : string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCulture);
            });

            AvailableStyles = cards;

            // Список карточек другой — прежняя выбранная принадлежит уже небывшему
            // списку, и ListBox снял бы выделение. Ищем ту же по имени.
            SyncCurrentStyleCard();

            // Сколько карточек влезает — решает ширина риббона, и это уже посчитано.
            // Здесь только пересобираем видимый срез из нового списка: число прежнее,
            // а карточки другие.
            int shown = _visibleStyles.Count > 0 ? _visibleStyles.Count : MaxCards;
            VisibleStyles = Array.Empty<StyleCardViewModel>();
            SetVisibleStyles(shown);
        }

        /// <summary>
        /// Приводит выбранную карточку в соответствие имени стиля под кареткой.
        /// Применения стиля при этом не происходит: карточка ставится напрямую в поле.
        /// </summary>
        private void SyncCurrentStyleCard()
        {
            StyleCardViewModel? found = null;

            foreach (var card in _availableStyles)
            {
                if (!string.Equals(card.Name, _currentStyleName, StringComparison.Ordinal)) continue;
                found = card;
                break;
            }

            if (ReferenceEquals(found, _currentStyle)) return;

            _currentStyle = found;
            this.RaisePropertyChanged(nameof(CurrentStyle));
        }

        // --- Команды: форматирование символов ---

        public ICommand BoldCommand { get; }
        public ICommand ItalicCommand { get; }
        public ICommand UnderlineCommand { get; }

        /// <summary>
        /// Плитка вида в меню подчёркивания. CommandParameter — имя вида
        /// (Single, Dotted, Wave…); None снимает подчёркивание.
        /// </summary>
        public ICommand UnderlineStyleCommand { get; }

        /// <summary>Цвет линии подчёркивания «как у текста».</summary>
        public ICommand UnderlineColorAutoCommand { get; }

        public ICommand StrikethroughCommand { get; }

        /// <summary>Вид зачёркивания из меню кнопки: None, Single или Double.</summary>
        public ICommand StrikeKindCommand { get; }
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

        // --- Команды: эффекты текста (меню «Эффекты текста») ---

        /// <summary>Контур букв (w:outline): включает и снимает.</summary>
        public ICommand TextOutlineCommand { get; }

        /// <summary>Тень букв (w:shadow): включает и снимает.</summary>
        public ICommand TextShadowCommand { get; }

        /// <summary>Рельеф (w:emboss): включает и снимает.</summary>
        public ICommand TextEmbossCommand { get; }

        /// <summary>Гравировка (w:imprint): включает и снимает.</summary>
        public ICommand TextImprintCommand { get; }

        /// <summary>Скрытый текст (w:vanish), Ctrl+Shift+H: включает и снимает.</summary>
        public ICommand HiddenTextCommand { get; }

        /// <summary>Малые прописные, Ctrl+Shift+K: включает и снимает.</summary>
        public ICommand SmallCapsCommand { get; }

        /// <summary>Знак ударения: параметр — None, Dot, Comma, Circle или UnderDot.</summary>
        public ICommand EmphasisMarkCommand { get; }

        /// <summary>Рамка вокруг знаков (w:bdr): включает и снимает.</summary>
        public ICommand CharBorderCommand { get; }

        /// <summary>Свечение вокруг букв (вид Word по умолчанию): включает и снимает.</summary>
        public ICommand TextGlowCommand { get; }

        /// <summary>Отражение букв (вид Word по умолчанию): включает и снимает.</summary>
        public ICommand TextReflectionCommand { get; }

        /// <summary>Открывает окно «Эффекты текста» со всеми настройками.</summary>
        public ICommand OpenTextEffectsCommand { get; }

        /// <summary>
        /// Просьба открыть окно «Эффекты текста». Окно — оверлей модуля, открывает его
        /// вид вкладки (RibbonHomeTab): вьюмодель о видах не знает.
        /// </summary>
        public event Action? TextEffectsDialogRequested;

        // --- Мои эффекты ---

        // Хранилище наборов — редактор (TextEditorViewModel). Подключается после
        // создания ленты: до него раздел «Мои эффекты» пуст.
        private ITextEffectPresetHost? _presetHost;

        /// <summary>Наборы «Мои эффекты» — плитки раздела в меню «A».</summary>
        public ObservableCollection<TextEffectPreset> TextEffectPresets { get; } = new();

        /// <summary>Есть хоть один набор: вместо подсказки показываются плитки.</summary>
        public bool HasTextEffectPresets => TextEffectPresets.Count > 0;

        /// <summary>Ставит набор выделению. Параметр — набор.</summary>
        public ICommand ApplyTextEffectPresetCommand { get; }

        /// <summary>Заводит набор из эффектов текста под кареткой.</summary>
        public ICommand SaveCaretTextEffectPresetCommand { get; }

        /// <summary>
        /// Подключает хранилище наборов и показывает его наборы. Лента узнаёт о каждой
        /// смене списка — и своей, и сделанной в другом редакторе.
        /// </summary>
        public void AttachPresetHost(ITextEffectPresetHost host)
        {
            if (_presetHost is not null)
                _presetHost.TextEffectPresetsChanged -= RebuildTextEffectPresets;

            _presetHost = host;
            _presetHost.TextEffectPresetsChanged += RebuildTextEffectPresets;
            RebuildTextEffectPresets();
        }

        /// <summary>Свободное имя для нового набора.</summary>
        public string SuggestTextEffectPresetName()
            => _presetHost?.SuggestTextEffectPresetName() ?? TextEffectPreset.DefaultName;

        /// <summary>Заводит готовый набор — например, собранный в окне «Эффекты текста».</summary>
        public void SaveTextEffectPreset(TextEffectPreset preset) => _presetHost?.AddTextEffectPreset(preset);

        /// <summary>Переименовывает набор.</summary>
        public void RenameTextEffectPreset(TextEffectPreset preset, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            _presetHost?.ReplaceTextEffectPreset(preset with { Name = name.Trim() });
        }

        /// <summary>
        /// Заменяет вид набора эффектами текста под кареткой; имя и место в списке
        /// остаются. Если под кареткой эффектов нет, набор не трогается.
        /// </summary>
        public void ReplaceTextEffectPresetFromCaret(TextEffectPreset preset)
        {
            var captured = _presetHost?.CaptureCaretTextEffectPreset(preset.Name);
            if (captured is null || captured.IsBlank) return;

            _presetHost!.ReplaceTextEffectPreset(captured with { Id = preset.Id });
        }

        /// <summary>Удаляет набор.</summary>
        public void DeleteTextEffectPreset(TextEffectPreset preset) => _presetHost?.RemoveTextEffectPreset(preset.Id);

        private void RebuildTextEffectPresets()
        {
            TextEffectPresets.Clear();
            if (_presetHost is not null)
                foreach (var preset in _presetHost.TextEffectPresets)
                    TextEffectPresets.Add(preset);

            this.RaisePropertyChanged(nameof(HasTextEffectPresets));
        }

        // --- Рамка абзаца ---

        /// <summary>
        /// Изменяемое состояние пера в ленте. Отдельная запись, а не поля по одному:
        /// перо меняется целиком, когда каретка приходит в абзац с рамкой.
        /// </summary>
        private sealed record BorderStylePen(
            Models.Document.BorderStyle Style,
            double WidthPt,
            double? SpacePt,
            string? Color)
        {
            public static readonly BorderStylePen Default =
                new(Models.Document.BorderStyle.Single, 0.5, null, null);

            public ParagraphBorderPen ToPen() => new()
            {
                Style = Style,
                WidthPt = WidthPt,
                SpacePt = SpacePt,
                Color = Color
            };
        }

        // Точечная рамка-подложка значков: какие стороны сплошные, видно на её фоне.
        private const string BorderIconFrame =
            "M3 3h2v2h-2zM3 7h2v2h-2zM3 11h2v2h-2zM3 15h2v2h-2zM3 19h2v2h-2z"
            + "M7 3h2v2h-2zM7 11h2v2h-2zM7 19h2v2h-2z"
            + "M11 3h2v2h-2zM11 7h2v2h-2zM11 11h2v2h-2zM11 15h2v2h-2zM11 19h2v2h-2z"
            + "M15 3h2v2h-2zM15 11h2v2h-2zM15 19h2v2h-2z"
            + "M19 3h2v2h-2zM19 7h2v2h-2zM19 11h2v2h-2zM19 15h2v2h-2zM19 19h2v2h-2z";

        /// <summary>Значок стороны рамки: точечная подложка и сплошные нужные стороны.</summary>
        private static string BorderIconData(ParagraphBorderSides sides)
        {
            // F1 — заливка «ненулевая»: точки подложки под сплошной линией не
            // выбивают в ней дыр, как было бы при правиле «чёт-нечет».
            var data = new System.Text.StringBuilder("F1 ");
            data.Append(BorderIconFrame);
            if (sides.HasFlag(ParagraphBorderSides.Top)) data.Append("M3 3h18v2H3z");
            if (sides.HasFlag(ParagraphBorderSides.Bottom)) data.Append("M3 19h18v2H3z");
            if (sides.HasFlag(ParagraphBorderSides.Left)) data.Append("M3 3h2v18H3z");
            if (sides.HasFlag(ParagraphBorderSides.Right)) data.Append("M19 3h2v18h-2z");
            return data.ToString();
        }

        /// <summary>Значок кнопки «Рамка» — сторона, которую она поставит.</summary>
        public Geometry LastBorderIcon => StreamGeometry.Parse(BorderIconData(_lastBorderSides));

        /// <summary>Подсказка кнопки «Рамка» — что она поставит.</summary>
        public string LastBorderTip => _lastBorderSides switch
        {
            ParagraphBorderSides.Top => "Рамка абзаца: верхняя граница",
            ParagraphBorderSides.Left => "Рамка абзаца: левая граница",
            ParagraphBorderSides.Right => "Рамка абзаца: правая граница",
            ParagraphBorderSides.Box => "Рамка абзаца: все границы",
            _ => "Рамка абзаца: нижняя граница"
        };

        public bool IsBorderTop => _borderSides.HasFlag(ParagraphBorderSides.Top);
        public bool IsBorderBottom => _borderSides.HasFlag(ParagraphBorderSides.Bottom);
        public bool IsBorderLeft => _borderSides.HasFlag(ParagraphBorderSides.Left);
        public bool IsBorderRight => _borderSides.HasFlag(ParagraphBorderSides.Right);
        public bool IsBorderBox => (_borderSides & ParagraphBorderSides.Box) == ParagraphBorderSides.Box;
        public bool IsBorderNone => _borderSides == ParagraphBorderSides.None;

        /// <summary>Вид линии пера — ключом для отметки в меню.</summary>
        public string BorderPenStyleKey => _borderPen.Style.ToString();

        /// <summary>Толщина пера — ключом для отметки в меню (точка, без культуры).</summary>
        public string BorderPenWidthKey
            => _borderPen.WidthPt.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Зазор пера — ключом для отметки в меню; «auto» — как в Word.</summary>
        public string BorderPenSpaceKey => _borderPen.SpacePt is double space
            ? space.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
            : "auto";

        /// <summary>Цвет пера «авто» — цвет текста листа.</summary>
        public bool IsBorderColorAuto => _borderPen.Color is null;

        // Цвет рядом с кнопкой «Рамка». При «авто» показывает цвет текста по умолчанию.
        private string _borderColorPick = "#1A1A1A";
        public string BorderColorPick
        {
            get => _borderColorPick;
            set
            {
                if (string.Equals(_borderColorPick, value, StringComparison.OrdinalIgnoreCase)) return;
                this.RaiseAndSetIfChanged(ref _borderColorPick, value);
                if (string.IsNullOrWhiteSpace(value)) return;

                SetBorderPen(_borderPen with { Color = value });
            }
        }

        // --- Непечатаемые знаки ---

        private bool _isFormattingMarksVisible;

        /// <summary>Показаны ли непечатаемые знаки — состояние кнопки «¶».</summary>
        public bool IsFormattingMarksVisible
        {
            get => _isFormattingMarksVisible;
            private set => this.RaiseAndSetIfChanged(ref _isFormattingMarksVisible, value);
        }

        /// <summary>Кнопка «¶»: показать или скрыть непечатаемые знаки.</summary>
        public ICommand ToggleFormattingMarksCommand { get; }

        /// <summary>
        /// Состояние кнопки «¶» по редактору. Зовётся после переключения — и с
        /// кнопки, и с клавиатуры, — и при смене контекста курсора.
        /// </summary>
        public void RefreshFormattingMarks()
            => IsFormattingMarksVisible = _target.AreFormattingMarksVisible();

        public ICommand BorderSideCommand { get; }
        public ICommand BorderRepeatCommand { get; }
        public ICommand BorderNoneCommand { get; }
        public ICommand BorderStyleCommand { get; }
        public ICommand BorderWidthCommand { get; }
        public ICommand BorderSpaceCommand { get; }
        public ICommand BorderColorAutoCommand { get; }

        /// <summary>
        /// Кнопка в центре образца абзаца. Пока ни одной стороны нет, на ней плюс, и
        /// она ставит рамку со всех сторон. Как только стоит хоть одна сторона, плюс
        /// поворачивается в крест, и кнопка снимает рамку целиком.
        /// </summary>
        public ICommand BorderCenterCommand { get; }

        /// <summary>Цвет линий в образце абзаца, когда у пера свой цвет, а не «как у текста».</summary>
        public IBrush BorderPenBrush
        {
            get
            {
                string? code = _borderPen.Color;
                if (string.IsNullOrWhiteSpace(code))
                    return new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

                // Градиент линия рамки не рисует — образец показывает тот же сплошной
                // цвет, которым ляжет линия.
                var solid = Writersword.Modules.TextEditor.Rendering.SKTextRenderer
                    .GradientSolidColor(code, SKColors.Black);
                return new SolidColorBrush(Color.FromArgb(solid.Alpha, solid.Red, solid.Green, solid.Blue));
            }
        }

        /// <summary>
        /// Толщина линий в образце абзаца, в точках экрана: растёт с толщиной пера,
        /// но не тоньше полутора точек — иначе тонкую линию в образце не видно — и не
        /// толще пяти, чтобы образец не превращался в брусок.
        /// </summary>
        public double BorderPreviewThickness => Math.Clamp(_borderPen.WidthPt * 1.4, 1.5, 5.0);

        /// <summary>
        /// Новое перо: запоминается для следующих линий и сразу ложится на линии,
        /// которые уже стоят у выделенных абзацев.
        /// </summary>
        private void SetBorderPen(BorderStylePen pen)
        {
            _borderPen = pen;
            RaiseBorderPenChanged();
            _target.RestyleParagraphBorders(pen.ToPen());
        }

        private void RaiseBorderPenChanged()
        {
            this.RaisePropertyChanged(nameof(BorderPenStyleKey));
            this.RaisePropertyChanged(nameof(BorderPenWidthKey));
            this.RaisePropertyChanged(nameof(BorderPenSpaceKey));
            this.RaisePropertyChanged(nameof(IsBorderColorAuto));
            this.RaisePropertyChanged(nameof(BorderPenBrush));
            this.RaisePropertyChanged(nameof(BorderPreviewThickness));
        }

        private void ApplyBorderSides(ParagraphBorderSides sides)
        {
            if (sides == ParagraphBorderSides.None) return;

            _lastBorderSides = sides;
            this.RaisePropertyChanged(nameof(LastBorderIcon));
            this.RaisePropertyChanged(nameof(LastBorderTip));

            _target.ToggleParagraphBorders(sides, _borderPen.ToPen());
            RefreshBorderSides();
        }

        /// <summary>Отметки сторон в меню — по выделению.</summary>
        private void RefreshBorderSides()
        {
            _borderSides = _target.GetSelectedParagraphBorderSides();
            this.RaisePropertyChanged(nameof(IsBorderTop));
            this.RaisePropertyChanged(nameof(IsBorderBottom));
            this.RaisePropertyChanged(nameof(IsBorderLeft));
            this.RaisePropertyChanged(nameof(IsBorderRight));
            this.RaisePropertyChanged(nameof(IsBorderBox));
            this.RaisePropertyChanged(nameof(IsBorderNone));
        }

        /// <summary>
        /// Перо по рамке абзаца под кареткой: вид, толщина, зазор и цвет первой
        /// видимой линии. У абзаца без рамки перо остаётся прежним.
        /// </summary>
        private void SyncBorderPenFromCaret(ParagraphProperties? props)
        {
            var borders = props?.Borders;
            if (borders is null || borders.IsEmpty) return;

            var line = borders.Left is { IsVisible: true } ? borders.Left
                : borders.Bottom is { IsVisible: true } ? borders.Bottom
                : borders.Top is { IsVisible: true } ? borders.Top
                : borders.Right;
            if (line is not { IsVisible: true }) return;

            _borderPen = new BorderStylePen(line.Style, line.WidthPt, line.SpacePt, line.Color);
            _borderColorPick = line.Color ?? "#1A1A1A";
            RaiseBorderPenChanged();
            this.RaisePropertyChanged(nameof(BorderColorPick));
        }

        // --- Команды: форматирование абзаца ---

        public ICommand BulletListCommand { get; }
        public ICommand NumberedListCommand { get; }
        public ICommand MultilevelListCommand { get; }
        public ICommand SelectMultilevelSchemeCommand { get; }
        public ICommand SelectListTypeCommand { get; }
        public ICommand SelectCustomBulletCommand { get; }
        public ICommand IncreaseIndentCommand { get; }
        public ICommand DecreaseIndentCommand { get; }
        public ICommand AlignLeftCommand { get; }
        public ICommand AlignCenterCommand { get; }
        public ICommand AlignRightCommand { get; }
        public ICommand AlignJustifyCommand { get; }

        /// <summary>Растянутое выравнивание: по ширине вместе с последней строкой (Ctrl+Shift+J).</summary>
        public ICommand AlignDistributeCommand { get; }
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
                this.RaisePropertyChanged(nameof(IsBodyTextLevel));
                this.RaisePropertyChanged(nameof(IsOutlineNumbered));
                this.RaisePropertyChanged(nameof(OutlineLevelDigit));
                this.RaisePropertyChanged(nameof(OutlineIconOpacity));
            }
        }

        /// <summary>Подпись текущего уровня структуры для кнопки риббона.</summary>
        public string OutlineLevelLabel => _outlineLevel <= 0 ? "Основной текст" : $"Уровень {_outlineLevel}";

        /// <summary>Цвет текущего уровня структуры: серый для основного текста, далее градиент к серому.</summary>
        public IBrush OutlineLevelBrush => new SolidColorBrush(Color.Parse(OutlineLevelHex(_outlineLevel)));

        /// <summary>Истина, когда абзац на основном тексте (уровень 0) — показываем иконку-линии.</summary>
        public bool IsBodyTextLevel => _outlineLevel <= 0;

        /// <summary>Истина для уровней 1..9 — показываем цифру уровня.</summary>
        public bool IsOutlineNumbered => _outlineLevel > 0;

        /// <summary>Цифра текущего уровня структуры (пусто на основном тексте).</summary>
        public string OutlineLevelDigit => _outlineLevel > 0
            ? _outlineLevel.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;

        /// <summary>Прозрачность иконки-линий: полная на основном тексте, приглушённая под цифрой уровня.</summary>
        public double OutlineIconOpacity => _outlineLevel > 0 ? 0.4 : 1.0;

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

            BoldCommand = ReactiveCommand.Create(() => _target.ToggleBold());
            ItalicCommand = ReactiveCommand.Create(() => _target.ToggleItalic());
            UnderlineCommand = ReactiveCommand.Create(() => _target.ToggleUnderlineStyle(_lastUnderlineStyle));
            UnderlineStyleCommand = ReactiveCommand.Create<string>(param =>
            {
                if (!Enum.TryParse(param, out UnderlineStyle style)) return;

                if (style != UnderlineStyle.None)
                {
                    _lastUnderlineStyle = style;
                    this.RaisePropertyChanged(nameof(LastUnderlineStyle));
                }

                _target.SetUnderlineStyle(style);
            });
            UnderlineColorAutoCommand = ReactiveCommand.Create(() => _target.SetUnderlineColor(null));
            // Кнопка ставит или снимает последний выбранный вид — как подчёркивание.
            StrikethroughCommand = ReactiveCommand.Create(() =>
            {
                if (_lastStrikeDouble) _target.ToggleDoubleStrikethrough();
                else _target.ToggleStrikethrough();
            });
            StrikeKindCommand = ReactiveCommand.Create<string>(kind =>
            {
                bool enabled = kind is "Single" or "Double";
                bool isDouble = kind == "Double";

                // Выбранный вид запоминается, и дальше его ставит сама кнопка.
                if (enabled && _lastStrikeDouble != isDouble)
                {
                    _lastStrikeDouble = isDouble;
                    this.RaisePropertyChanged(nameof(LastStrikeDouble));
                }

                _target.SetStrikethrough(enabled, isDouble);
            });
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

            // Эффекты текста: плитка включает эффект, если его нет под кареткой, и
            // снимает, если есть, — одно значение на всё выделение, как в окне шрифта Word.
            TextOutlineCommand = ReactiveCommand.Create(() => _target.SetTextEffect(TextEffectKind.Outline, !_isTextOutline));
            TextShadowCommand = ReactiveCommand.Create(() => _target.SetTextEffect(TextEffectKind.Shadow, !_isTextShadow));
            TextEmbossCommand = ReactiveCommand.Create(() => _target.SetTextEffect(TextEffectKind.Emboss, !_isTextEmboss));
            TextImprintCommand = ReactiveCommand.Create(() => _target.SetTextEffect(TextEffectKind.Imprint, !_isTextImprint));
            HiddenTextCommand = ReactiveCommand.Create(() => _target.SetHiddenText(!_isHiddenText));
            SmallCapsCommand = ReactiveCommand.Create(() => _target.ToggleSmallCaps());
            EmphasisMarkCommand = ReactiveCommand.Create<string>(param =>
            {
                if (!Enum.TryParse(param, out EmphasisMark mark)) return;
                _target.SetEmphasisMark(mark);
            });
            CharBorderCommand = ReactiveCommand.Create(() => _target.SetCharBorder(!_isCharBorder));
            TextGlowCommand = ReactiveCommand.Create(() => _target.SetTextEffect(TextEffectKind.Glow, !_isTextGlow));
            TextReflectionCommand = ReactiveCommand.Create(() => _target.SetTextEffect(TextEffectKind.Reflection, !_isTextReflection));
            OpenTextEffectsCommand = ReactiveCommand.Create(() => TextEffectsDialogRequested?.Invoke());

            // Мои эффекты: набор ставится одним нажатием; «сохранить» заводит набор из
            // текста под кареткой под свободным именем — переименовать можно в меню плитки.
            ApplyTextEffectPresetCommand = ReactiveCommand.Create<TextEffectPreset>(preset =>
            {
                if (preset is not null) _presetHost?.ApplyTextEffectPreset(preset);
            });
            SaveCaretTextEffectPresetCommand = ReactiveCommand.Create(() =>
            {
                if (_presetHost is null) return;

                var captured = _presetHost.CaptureCaretTextEffectPreset(_presetHost.SuggestTextEffectPresetName());
                if (captured is null || captured.IsBlank) return;

                _presetHost.AddTextEffectPreset(captured);
            });

            BulletListCommand = ReactiveCommand.Create(() => _target.ToggleBulletList());
            NumberedListCommand = ReactiveCommand.Create(() => _target.ToggleNumberedList());
            MultilevelListCommand = ReactiveCommand.Create(() => _target.ApplyMultilevelList());

            // Пресеты многоуровневого списка: параметр — ключ схемы.
            SelectMultilevelSchemeCommand = ReactiveCommand.Create<string>(key =>
            {
                System.Collections.Generic.List<Models.Document.ListMarkerType> scheme = key switch
                {
                    "numeric" => new()
                    {
                        Models.Document.ListMarkerType.Decimal, Models.Document.ListMarkerType.Decimal,
                        Models.Document.ListMarkerType.Decimal, Models.Document.ListMarkerType.Decimal,
                        Models.Document.ListMarkerType.Decimal, Models.Document.ListMarkerType.Decimal,
                        Models.Document.ListMarkerType.Decimal, Models.Document.ListMarkerType.Decimal,
                        Models.Document.ListMarkerType.Decimal
                    },
                    "bullets" => new()
                    {
                        Models.Document.ListMarkerType.Bullet, Models.Document.ListMarkerType.Circle,
                        Models.Document.ListMarkerType.Square, Models.Document.ListMarkerType.Bullet,
                        Models.Document.ListMarkerType.Circle, Models.Document.ListMarkerType.Square,
                        Models.Document.ListMarkerType.Bullet, Models.Document.ListMarkerType.Circle,
                        Models.Document.ListMarkerType.Square
                    },
                    _ => new()
                    {
                        Models.Document.ListMarkerType.Decimal, Models.Document.ListMarkerType.LowerAlpha,
                        Models.Document.ListMarkerType.LowerRoman, Models.Document.ListMarkerType.Decimal,
                        Models.Document.ListMarkerType.LowerAlpha, Models.Document.ListMarkerType.LowerRoman,
                        Models.Document.ListMarkerType.Decimal, Models.Document.ListMarkerType.LowerAlpha,
                        Models.Document.ListMarkerType.LowerRoman
                    }
                };
                _target.ApplyMultilevelScheme(scheme);
            });

            // Галерея типов списка: параметр — имя значения ListMarkerType (Bullet, Decimal, LowerAlpha…).
            SelectListTypeCommand = ReactiveCommand.Create<string>(param =>
            {
                if (Enum.TryParse<Models.Document.ListMarkerType>(param, out var type))
                    _target.ApplyListType(type);
            });

            // Быстрый выбор пользовательского символа маркера из галереи.
            SelectCustomBulletCommand = ReactiveCommand.Create<string>(marker =>
            {
                if (!string.IsNullOrEmpty(marker))
                    _target.ApplyCustomBulletList(marker);
            });
            IncreaseIndentCommand = ReactiveCommand.Create(() => _target.IncreaseIndent());
            DecreaseIndentCommand = ReactiveCommand.Create(() => _target.DecreaseIndent());

            AlignLeftCommand = ReactiveCommand.Create(() => _target.SetAlignment(TextAlignment.Left));
            AlignCenterCommand = ReactiveCommand.Create(() => _target.SetAlignment(TextAlignment.Center));
            AlignRightCommand = ReactiveCommand.Create(() => _target.SetAlignment(TextAlignment.Right));
            AlignJustifyCommand = ReactiveCommand.Create(() => _target.SetAlignment(TextAlignment.Justify));
            AlignDistributeCommand = ReactiveCommand.Create(() => _target.SetAlignment(TextAlignment.Distribute));

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

            // Рамка абзаца. Стороны приходят именем из ParagraphBorderSides, толщина и
            // зазор — числом с точкой: CommandParameter в разметке от культуры не зависит.
            BorderSideCommand = ReactiveCommand.Create<string>(param =>
            {
                if (Enum.TryParse<ParagraphBorderSides>(param, out var sides))
                    ApplyBorderSides(sides);
            });
            BorderRepeatCommand = ReactiveCommand.Create(() => ApplyBorderSides(_lastBorderSides));
            ToggleFormattingMarksCommand = ReactiveCommand.Create(() =>
            {
                _target.ToggleFormattingMarks();
                RefreshFormattingMarks();
            });
            BorderNoneCommand = ReactiveCommand.Create(() =>
            {
                _target.ClearParagraphBorders();
                RefreshBorderSides();
            });
            BorderStyleCommand = ReactiveCommand.Create<string>(param =>
            {
                if (Enum.TryParse<Models.Document.BorderStyle>(param, out var style)
                    && style != Models.Document.BorderStyle.None)
                    SetBorderPen(_borderPen with { Style = style });
            });
            BorderWidthCommand = ReactiveCommand.Create<string>(param =>
            {
                if (double.TryParse(param, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double width)
                    && width > 0)
                    SetBorderPen(_borderPen with { WidthPt = width });
            });
            BorderSpaceCommand = ReactiveCommand.Create<string>(param =>
            {
                if (string.Equals(param, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    SetBorderPen(_borderPen with { SpacePt = null });
                    return;
                }

                if (double.TryParse(param, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double space)
                    && space >= 0)
                    SetBorderPen(_borderPen with { SpacePt = space });
            });
            BorderCenterCommand = ReactiveCommand.Create(() =>
            {
                if (_borderSides == ParagraphBorderSides.None)
                {
                    ApplyBorderSides(ParagraphBorderSides.Box);
                    return;
                }

                _target.ClearParagraphBorders();
                RefreshBorderSides();
            });
            BorderColorAutoCommand = ReactiveCommand.Create(() =>
            {
                _borderColorPick = "#1A1A1A";
                this.RaisePropertyChanged(nameof(BorderColorPick));
                SetBorderPen(_borderPen with { Color = null });
            });

            CutCommand = ReactiveCommand.Create(() => _target.Cut());
            CopyCommand = ReactiveCommand.Create(() => _target.Copy());
            PasteCommand = ReactiveCommand.Create(() => _target.Paste());
            SelectAllCommand = ReactiveCommand.Create(() => _target.SelectAll());
            UndoCommand = ReactiveCommand.Create(() => _target.Undo());
            RedoCommand = ReactiveCommand.Create(() => _target.Redo());
            FindCommand = ReactiveCommand.Create(() => _target.OpenFind());
            FindReplaceCommand = ReactiveCommand.Create(() => _target.OpenFindReplace());

            SaveStyleFromCursorCommand = ReactiveCommand.Create(() => { });
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
            _underlineStyle = ctx.UnderlineStyle;
            _underlineColor = ctx.UnderlineColor;
            _underlineColorPick = ctx.UnderlineColor ?? ctx.TextColor ?? "#1A1A1A";
            _isStrikethrough = ctx.IsStrikethrough;
            _isDoubleStrikethrough = ctx.IsDoubleStrikethrough;
            _isSuperscript = ctx.IsSuperscript;
            _isSubscript = ctx.IsSubscript;
            _isAllCaps = ctx.IsAllCaps;
            _isSmallCaps = ctx.IsSmallCaps;
            _isHiddenText = ctx.IsHidden;
            _isTextOutline = ctx.IsOutline;
            _isTextShadow = ctx.IsShadow;
            _isTextEmboss = ctx.IsEmboss;
            _isTextImprint = ctx.IsImprint;
            _emphasisMark = ctx.EmphasisMark;
            _isCharBorder = ctx.HasCharBorder;
            _isTextGlow = ctx.IsGlow;
            _isTextReflection = ctx.IsReflection;
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
            SyncCurrentStyleCard();

            // Интервалы берём по эффективному значению (с учётом стиля) из CursorContext,
            // уровень структуры — из собственных свойств абзаца.
            _isSpaceBefore = ctx.HasSpaceBefore;
            _isSpaceAfter = ctx.HasSpaceAfter;
            var activeProps = _target.GetActiveParagraphProperties();
            _outlineLevel = activeProps?.OutlineLevel ?? 0;

            // Рамка: отметки сторон по выделению, перо — по рамке абзаца под кареткой.
            SyncBorderPenFromCaret(activeProps);
            RefreshBorderSides();
            RefreshFormattingMarks();

            this.RaisePropertyChanged(nameof(IsBold));
            this.RaisePropertyChanged(nameof(IsItalic));
            this.RaisePropertyChanged(nameof(IsUnderline));
            this.RaisePropertyChanged(nameof(UnderlineStyleKey));
            this.RaisePropertyChanged(nameof(IsUnderlineColorAuto));
            this.RaisePropertyChanged(nameof(UnderlineColorPick));
            this.RaisePropertyChanged(nameof(IsStrikethrough));
            this.RaisePropertyChanged(nameof(IsStrikeAny));
            this.RaisePropertyChanged(nameof(StrikeKindKey));
            this.RaisePropertyChanged(nameof(IsSuperscript));
            this.RaisePropertyChanged(nameof(IsSubscript));
            this.RaisePropertyChanged(nameof(IsAllCaps));
            this.RaisePropertyChanged(nameof(IsSmallCaps));
            this.RaisePropertyChanged(nameof(IsHiddenText));
            this.RaisePropertyChanged(nameof(IsTextOutline));
            this.RaisePropertyChanged(nameof(IsTextShadow));
            this.RaisePropertyChanged(nameof(IsTextEmboss));
            this.RaisePropertyChanged(nameof(IsTextImprint));
            this.RaisePropertyChanged(nameof(IsCharBorder));
            this.RaisePropertyChanged(nameof(IsTextGlow));
            this.RaisePropertyChanged(nameof(IsTextReflection));
            this.RaisePropertyChanged(nameof(EmphasisMarkKey));
            this.RaisePropertyChanged(nameof(IsAnyTextEffect));
            this.RaisePropertyChanged(nameof(CanSaveCaretTextEffectPreset));
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
            this.RaisePropertyChanged(nameof(CurrentStyle));
            this.RaisePropertyChanged(nameof(IsSpaceBefore));
            this.RaisePropertyChanged(nameof(IsSpaceAfter));
            this.RaisePropertyChanged(nameof(OutlineLevel));
            this.RaisePropertyChanged(nameof(OutlineLevelLabel));
            this.RaisePropertyChanged(nameof(OutlineLevelBrush));
            this.RaisePropertyChanged(nameof(IsBodyTextLevel));
            this.RaisePropertyChanged(nameof(IsOutlineNumbered));
            this.RaisePropertyChanged(nameof(OutlineLevelDigit));
            this.RaisePropertyChanged(nameof(OutlineIconOpacity));
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
                VisibleStyles = Array.Empty<StyleCardViewModel>();
                return;
            }

            int clamped = Math.Min(count, AvailableStyles.Count);
            if (VisibleStyles.Count == clamped) return;
            VisibleStyles = AvailableStyles.Take(clamped).ToList();
        }

        /// <summary>
        /// Начать сеанс предпросмотра гарнитуры.
        ///
        /// Повторный вызов при уже начатом сеансе игнорируется. События открытия
        /// и закрытия выпадающего списка приходят не по одному разу: отпускание
        /// указателя над полем догоняет уже закрывшийся список и открывает его
        /// снова. Каждый лишний Begin запоминал исходной гарнитурой ту, что
        /// сейчас показана предпросмотром, а следующий End её же и применял —
        /// документ перекрашивался туда-обратно, и лента с полосой состояния
        /// мигали на каждый такой оборот.
        /// </summary>
        public void BeginFontPreview()
        {
            if (_fontPreviewActive)
                return;

            _fontPreviewActive = true;
            _previewOriginalFont = _fontFamily;
            _target.BeginFontPreview();
        }

        public void PreviewFontFamily(string f)
        {
            // Предпросмотр вне сеанса — тот же лишний оборот: гарнитура
            // применяется к документу, а откатывать её потом нечему.
            if (!_fontPreviewActive)
                return;

            _target.PreviewFontFamily(f);
        }

        /// <summary>
        /// Завершить сеанс. Вне начатого сеанса не делает ничего — иначе
        /// запоздавшее закрытие списка откатывало бы уже применённый выбор.
        /// </summary>
        public void EndFontPreview(bool commit)
        {
            if (!_fontPreviewActive)
                return;

            _fontPreviewActive = false;

            if (!commit)
            {
                _fontFamily = _previewOriginalFont;
                _fontFamilyText = _previewOriginalFont;
                this.RaisePropertyChanged(nameof(CurrentFontFamily));
                this.RaisePropertyChanged(nameof(CurrentFontFamilyText));
            }

            // Гарнитуру применяет закрытие сеанса, и только оно. Раньше рядом стоял
            // ещё и SetFontFamily: рукопись меняли оба, каждый писал свой шаг, и
            // первый Ctrl+Z снимал второй из них — на экране при этом не менялось
            // ничего, потому что первый уже поставил ту же гарнитуру.
            _target.EndFontPreview(commit, commit ? _fontFamily : null);
            _previewOriginalFont = null;
        }
    }
}