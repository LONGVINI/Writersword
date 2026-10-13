using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Writersword.Modules.TextEditor.Rendering.Formulas;

namespace Writersword.Modules.TextEditor.Views.Dialogs
{
    /// <summary>Итог окна формулы.</summary>
    /// <param name="Omml">XML формулы Word.</param>
    /// <param name="Display">Формула отдельной строкой.</param>
    /// <param name="Delete">Нажата кнопка удаления формулы.</param>
    public sealed record FormulaEditResult(string Omml, bool Display, bool Delete);

    /// <summary>
    /// Редактор формулы: лента с конструкциями, знаками и готовыми формулами, поле
    /// записи и живой образец того, как формула встанет в документ.
    ///
    /// Лента — как вкладка «Уравнение» у Word: кнопка конструкции открывает галерею
    /// заготовок, нарисованных тем же набором, что лист; нажатие на заготовку вставляет
    /// её в место каретки, а выделенный текст становится её первым содержимым.
    ///
    /// Формула пишется командами LaTeX — так их пишут во всех редакторах формул, и
    /// любая подсказка в сети подходит как есть: x^2, \frac{a}{b}, \sqrt{x}. Пустые
    /// скобки {} — пустое место конструкции: на образце это пунктирный квадратик, как
    /// у Word, каретка встаёт в первое такое место, Tab ведёт к следующему, Shift+Tab —
    /// к предыдущему.
    ///
    /// Образец рисуется тем же набором формул, что и лист. Ошибка в записи видна под
    /// образцом сразу, с указанием места; с ошибкой формулу не вставить.
    /// </summary>
    public sealed class FormulaEditorDialog : Window
    {
        /// <summary>Кегль образца, пт.</summary>
        private const float PreviewSizePt = 22f;

        /// <summary>Кегль картинок на кнопках конструкций, пт.</summary>
        private const float IconSizePt = 12f;

        /// <summary>Кегль заготовок в галереях, пт.</summary>
        private const float TileSizePt = 15f;

        /// <summary>Высота тела ленты: у всех вкладок одна, лента не прыгает.</summary>
        private const double RibbonBodyHeight = 88;

        private readonly TextBox _input;
        private readonly Image _preview;
        private readonly TextBlock _error;
        private readonly CheckBox _display;
        private readonly Button _okButton;
        private readonly DispatcherTimer _previewTimer;
        private readonly Border _ribbonBody;
        private readonly List<(ToggleButton Header, Func<Control> Build)> _ribbonTabs = new();
        private readonly Dictionary<int, Control> _builtTabs = new();

        private Color _themeText = Color.FromRgb(0xE6, 0xE6, 0xE6);
        private int _symbolSet;
        private WrapPanel? _symbolPanel;
        private string? _omml;

        /// <summary>
        /// Быстрые конструкции — ряд кнопок над записью: одно нажатие, без галерей.
        /// Подпись кнопки, запись ({} — пустое место) и подсказка.
        /// </summary>
        private static readonly (string Label, string Snippet, string Tip)[] QuickStructures =
        {
            ("a/b", "\\frac{}{}", "Дробь"),
            ("xⁿ", "^{}", "Верхний индекс, степень"),
            ("xₙ", "_{}", "Нижний индекс"),
            ("√x", "\\sqrt{}", "Квадратный корень"),
            ("ⁿ√x", "\\sqrt[]{}", "Корень степени n"),
            ("∑", "\\sum_{}^{}", "Сумма с пределами"),
            ("∏", "\\prod_{}^{}", "Произведение с пределами"),
            ("∫", "\\int_{}^{}", "Интеграл с пределами"),
            ("∮", "\\oint_{}", "Интеграл по контуру"),
            ("lim", "\\lim_{{} \\to {}}", "Предел"),
            ("( )", "\\left( {} \\right)", "Круглые скобки по высоте содержимого"),
            ("[ ]", "\\left[ {} \\right]", "Квадратные скобки по высоте содержимого"),
            ("{ }", "\\left\\{ {} \\right\\}", "Фигурные скобки по высоте содержимого"),
            ("|x|", "\\left| {} \\right|", "Модуль"),
            ("x̂", "\\hat{}", "Крышка над буквой"),
            ("x̄", "\\overline{}", "Черта сверху"),
            ("x⃗", "\\vec{}", "Вектор"),
            ("ẋ", "\\dot{}", "Точка над буквой"),
            ("⏞", "\\overbrace{}^{}", "Фигурная скобка сверху с подписью"),
            ("⏟", "\\underbrace{}_{}", "Фигурная скобка снизу с подписью"),
            ("(ⁿₖ)", "\\binom{}{}", "Биномиальный коэффициент"),
            ("sin", "\\sin{}", "Синус"),
            ("cos", "\\cos{}", "Косинус"),
            ("log", "\\log_{}{}", "Логарифм по основанию"),
            ("ln", "\\ln{}", "Натуральный логарифм"),
            ("▦", "\\begin{pmatrix} {} & {} \\\\ {} & {} \\end{pmatrix}", "Матрица 2×2"),
            ("{≡", "\\begin{cases} {} & {} \\\\ {} & {} \\end{cases}", "Система"),
            ("☐", "\\boxed{}", "Рамка"),
            ("Аа", "\\text{}", "Обычный текст внутри формулы")
        };

        private static readonly string[] QuickGreek =
        {
            "α", "β", "γ", "δ", "ε", "ζ", "η", "θ", "ι", "κ", "λ", "μ", "ν", "ξ", "π", "ρ",
            "σ", "τ", "υ", "φ", "χ", "ψ", "ω", "Γ", "Δ", "Θ", "Λ", "Ξ", "Π", "Σ", "Φ", "Ψ", "Ω"
        };

        private static readonly string[] QuickSymbols =
        {
            "±", "∓", "×", "÷", "·", "≤", "≥", "≠", "≈", "≡", "∼", "∝", "∞", "∂", "∇", "∈",
            "∉", "⊂", "⊆", "∪", "∩", "∅", "∀", "∃", "→", "←", "⇒", "⇔", "°", "′", "…", "⋯"
        };

        /// <summary>Ширина окна со справкой и без неё.</summary>
        private const double WindowWidth = 980;
        private const double HelpWidth = 440;

        private Border? _helpColumn;
        private ScrollViewer? _helpScroll;
        private StackPanel? _mainContent;
        private ToggleButton? _helpToggle;

        // Пустое место записи: пустые фигурные скобки или пустые квадратные (показатель корня).
        private static readonly Regex SlotPattern = new(@"\{\}|\[\]", RegexOptions.Compiled);

        // Команда, которой кончается текст перед кареткой: \alpha, \sum.
        private static readonly Regex TrailingCommand = new(@"\\[A-Za-z]+$", RegexOptions.Compiled);

        public FormulaEditorDialog(string latex, bool display, bool canDelete)
        {
            Title = "Редактор формул";
            Width = WindowWidth;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            CanResize = false;
            WindowDecorations = WindowDecorations.None;
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            Background = Brushes.Transparent;

            var titleText = new TextBlock
            {
                Text = canDelete ? "Формула" : "Новая формула",
                FontSize = 15,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            BindResource(titleText, TextBlock.ForegroundProperty, "TextPrimaryBrush");

            _ribbonBody = new Border
            {
                Height = RibbonBodyHeight,
                Padding = new Thickness(8, 6),
                ClipToBounds = true
            };

            var ribbon = BuildRibbon();

            _input = new TextBox
            {
                Text = latex,
                FontFamily = new FontFamily("Consolas, Menlo, monospace"),
                FontSize = 14,
                Padding = new Thickness(8, 6),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                AcceptsReturn = false,
                AcceptsTab = false,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 36
            };
            BindResource(_input, TextBox.BackgroundProperty, "BgSurfaceBrush");
            BindResource(_input, TextBox.BorderBrushProperty, "BorderDefaultBrush");
            BindResource(_input, TextBox.ForegroundProperty, "TextPrimaryBrush");
            BindResource(_input, TextBox.CaretBrushProperty, "TextPrimaryBrush");
            _input.TextChanged += (_, _) => SchedulePreview();

            var inputHint = new TextBlock
            {
                Text = "Запись формулы командами LaTeX: x^2, \\frac{a}{b}, \\sqrt{x}. "
                     + "{} — пустое место (на образце — пунктирный квадратик). Tab — к следующему пустому месту, "
                     + "Shift+Tab — к предыдущему, Enter — готово.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            };
            BindResource(inputHint, TextBlock.ForegroundProperty, "TextMutedBrush");

            // Картинка нарисована вдвое плотнее экрана для резкости и показывается в
            // половину своих точек. Stretch.None рисовал бы её в полный размер и обрезал
            // по рамке элемента — от формулы оставалась бы середина.
            _preview = new Image
            {
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Образец — на белом листе, как формула в документе, при любой теме окна.
            var previewFrame = new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                MinHeight = 120,
                MaxHeight = 340,
                Padding = new Thickness(12),
                ClipToBounds = true,
                Child = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Content = _preview
                }
            };
            BindResource(previewFrame, Border.BorderBrushProperty, "BorderDefaultBrush");

            _error = new TextBlock
            {
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0x30, 0x30)),
                TextWrapping = TextWrapping.Wrap,
                IsVisible = false
            };

            _display = new CheckBox
            {
                Content = "Отдельной строкой, по центру",
                IsChecked = display,
                FontSize = 12
            };
            BindResource(_display, CheckBox.ForegroundProperty, "TextPrimaryBrush");
            _display.IsCheckedChanged += (_, _) => SchedulePreview();

            var cancelButton = BuildButton("Отмена", accent: false);
            cancelButton.Click += (_, _) => Close(null);

            _okButton = BuildButton(canDelete ? "Готово" : "Вставить", accent: true);
            _okButton.Click += (_, _) => Accept();

            var rightButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 10
            };
            rightButtons.Children.Add(cancelButton);
            rightButtons.Children.Add(_okButton);

            var buttons = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            buttons.Children.Add(rightButtons);

            if (canDelete)
            {
                var deleteButton = BuildButton("Удалить формулу", accent: false);
                deleteButton.HorizontalAlignment = HorizontalAlignment.Left;
                deleteButton.Click += (_, _) => Close(new FormulaEditResult(string.Empty, false, true));
                buttons.Children.Add(deleteButton);
            }

            _helpToggle = new ToggleButton
            {
                Content = "Как вводить формулы ▸",
                FontSize = 12,
                Padding = new Thickness(12, 4),
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Right,
                Cursor = new Cursor(StandardCursorType.Hand),
                Focusable = false
            };
            BindResource(_helpToggle, ToggleButton.BackgroundProperty, "BgSurfaceBrush");
            BindResource(_helpToggle, ToggleButton.BorderBrushProperty, "BorderDefaultBrush");
            BindResource(_helpToggle, ToggleButton.ForegroundProperty, "TextPrimaryBrush");
            _helpToggle.IsCheckedChanged += (_, _) => ShowHelp(_helpToggle.IsChecked == true);

            var titleRow = new Grid();
            titleRow.Children.Add(titleText);
            titleRow.Children.Add(_helpToggle);

            var content = new StackPanel { Spacing = 10 };
            _mainContent = content;
            content.Children.Add(titleRow);
            content.Children.Add(BuildQuickPalette("Конструкции", QuickStructures));
            content.Children.Add(BuildQuickPalette("Греческие буквы", QuickGreek));
            content.Children.Add(BuildQuickPalette("Знаки", QuickSymbols));
            content.Children.Add(ribbon);
            content.Children.Add(_input);
            content.Children.Add(inputHint);
            content.Children.Add(previewFrame);
            content.Children.Add(_error);
            content.Children.Add(_display);
            content.Children.Add(buttons);

            var shadow = new Border { CornerRadius = new CornerRadius(20), Margin = new Thickness(-2) };
            BindResource(shadow, Border.BackgroundProperty, "BgBaseBrush");

            // Справка — колонкой справа: окно расширяется, а запись и образец остаются
            // на месте и видны вместе со справкой.
            _helpScroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            _helpColumn = new Border
            {
                Width = HelpWidth - 20,
                Margin = new Thickness(20, 0, 0, 0),
                Padding = new Thickness(14, 0, 0, 0),
                BorderThickness = new Thickness(1, 0, 0, 0),
                IsVisible = false,
                Child = _helpScroll
            };
            BindResource(_helpColumn, Border.BorderBrushProperty, "BorderSubtleBrush");

            var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(content, 0);
            Grid.SetColumn(_helpColumn, 1);
            columns.Children.Add(content);
            columns.Children.Add(_helpColumn);

            var frame = new Border
            {
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(22, 18),
                BorderThickness = new Thickness(1),
                ClipToBounds = true,
                Margin = new Thickness(2),
                Child = columns
            };
            BindResource(frame, Border.BackgroundProperty, "BgAppBrush");
            BindResource(frame, Border.BorderBrushProperty, "BorderDefaultBrush");

            var root = new Grid();
            root.Children.Add(shadow);
            root.Children.Add(frame);
            Content = root;

            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _previewTimer.Tick += (_, _) =>
            {
                _previewTimer.Stop();
                UpdatePreview();
            };

            // Клавиши окна ловятся на спуске события, раньше поля ввода.
            AddHandler(KeyDownEvent, OnDialogKeyDown, RoutingStrategies.Tunnel);

            UpdatePreview();
        }

        /// <summary>
        /// Открывает окно поверх главного окна приложения. null — окно закрыто без
        /// формулы (Отмена, Esc) или показать его не на чем.
        /// </summary>
        public static async Task<FormulaEditResult?> ShowAsync(string latex, bool display, bool canDelete)
        {
            var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            var owner = lifetime?.MainWindow;
            if (owner is null) return null;

            var dialog = new FormulaEditorDialog(latex, display, canDelete);
            return await dialog.ShowDialog<FormulaEditResult?>(owner);
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);

            // Цвет картинок ленты — цвет текста темы; он известен, когда окно в дереве.
            _themeText = FormulaTiles.ThemeTextColor(this);
            SelectRibbonTab(0);

            _input.Focus();

            // Каретка — в первое пустое место заготовки, иначе в конец записи.
            string text = _input.Text ?? string.Empty;
            var slot = SlotPattern.Match(text);
            int caret = slot.Success ? slot.Index + 1 : text.Length;
            SetCaret(caret);
        }

        protected override void OnClosed(EventArgs e)
        {
            _previewTimer.Stop();

            if (_preview.Source is IDisposable bitmap)
            {
                _preview.Source = null;
                bitmap.Dispose();
            }

            base.OnClosed(e);
        }

        private void OnDialogKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close(null);
                e.Handled = true;
                return;
            }

            if (e.Key is Key.Return or Key.Enter)
            {
                Accept();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Tab && _input.IsFocused)
            {
                MoveToSlot(forward: (e.KeyModifiers & KeyModifiers.Shift) == 0);
                e.Handled = true;
            }
        }

        private void Accept()
        {
            UpdatePreview();
            if (_omml is null) return;

            Close(new FormulaEditResult(_omml, _display.IsChecked == true, false));
        }

        private void SchedulePreview()
        {
            _previewTimer.Stop();
            _previewTimer.Start();
        }

        /// <summary>Разбирает запись и перерисовывает образец; при ошибке показывает её.</summary>
        private void UpdatePreview()
        {
            string text = _input.Text ?? string.Empty;

            try
            {
                if (FormulaText.IsEmpty(text))
                {
                    _omml = null;
                    SetPreview(null);
                    ShowError(null);
                    _okButton.IsEnabled = false;
                    return;
                }

                _omml = FormulaText.ToOmml(text);
                SetPreview(RenderPreview(_omml, _display.IsChecked == true));
                ShowError(null);
                _okButton.IsEnabled = true;
            }
            catch (FormulaSyntaxException ex)
            {
                _omml = null;
                ShowError(ex.Message + " Место: знак " + (ex.Position + 1) + ".");
                _okButton.IsEnabled = false;
            }
        }

        private void ShowError(string? message)
        {
            _error.Text = message ?? string.Empty;
            _error.IsVisible = message is not null;
        }

        private void SetPreview(Bitmap? bitmap)
        {
            var previous = _preview.Source as IDisposable;
            _preview.Source = bitmap;

            if (bitmap is not null)
            {
                _preview.Width = bitmap.PixelSize.Width / FormulaTiles.Oversample;
                _preview.Height = bitmap.PixelSize.Height / FormulaTiles.Oversample;
            }

            previous?.Dispose();
        }

        /// <summary>
        /// Образец формулы: тот же набор, что на листе, чёрным по белому, с пустыми
        /// местами конструкций. Картинка обрезается по всему нарисованному — высокий
        /// индекс или знак над габаритом формулы не срезаются.
        /// </summary>
        private static Bitmap? RenderPreview(string omml, bool display)
        {
            var image = FormulaRenderer.RenderImage(
                omml, display, PreviewSizePt, SKColors.Black, placeholders: true, FormulaTiles.Oversample, padPt: 3f);
            if (image is null) return null;

            using var stream = new MemoryStream(image.Png);
            return new Bitmap(stream);
        }

        // ── Лента ────────────────────────────────────────────────────────

        private Control BuildRibbon()
        {
            var headers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(6, 4, 6, 0) };

            AddRibbonTab(headers, "Конструкции", BuildStructuresTab);
            AddRibbonTab(headers, "Символы", BuildSymbolsTab);
            AddRibbonTab(headers, "Готовые формулы", BuildReadyTab);

            var separator = new Border { Height = 1, Margin = new Thickness(0, 2, 0, 0) };
            BindResource(separator, Border.BackgroundProperty, "BorderSubtleBrush");

            var stack = new StackPanel();
            stack.Children.Add(headers);
            stack.Children.Add(separator);
            stack.Children.Add(_ribbonBody);

            var band = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Child = stack
            };
            BindResource(band, Border.BackgroundProperty, "BgSurfaceBrush");
            BindResource(band, Border.BorderBrushProperty, "BorderDefaultBrush");
            return band;
        }

        private void AddRibbonTab(StackPanel headers, string title, Func<Control> build)
        {
            int index = _ribbonTabs.Count;
            var header = new ToggleButton
            {
                Content = title,
                FontSize = 12,
                Padding = new Thickness(14, 5),
                CornerRadius = new CornerRadius(5, 5, 0, 0),
                BorderThickness = new Thickness(0),
                Cursor = new Cursor(StandardCursorType.Hand),
                Focusable = false
            };
            BindResource(header, ToggleButton.ForegroundProperty, "TextPrimaryBrush");
            header.Click += (_, _) => SelectRibbonTab(index);

            headers.Children.Add(header);
            _ribbonTabs.Add((header, build));
        }

        private void SelectRibbonTab(int index)
        {
            for (int i = 0; i < _ribbonTabs.Count; i++)
            {
                var header = _ribbonTabs[i].Header;
                header.IsChecked = i == index;
                header.FontWeight = i == index ? FontWeight.SemiBold : FontWeight.Normal;
                if (i == index) BindResource(header, ToggleButton.BackgroundProperty, "BgHoverBrush");
                else header.Background = Brushes.Transparent;
            }

            if (!_builtTabs.TryGetValue(index, out var body))
            {
                body = _ribbonTabs[index].Build();
                _builtTabs[index] = body;
            }

            _ribbonBody.Child = body;
        }

        /// <summary>Конструкции: большие кнопки с образцом, каждая открывает свою галерею.</summary>
        private Control BuildStructuresTab()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };

            foreach (var structure in FormulaGallery.Structures)
            {
                row.Children.Add(FormulaTiles.StructureButton(
                    this, structure, _themeText, InsertSnippet,
                    IconSizePt, iconHeight: 40, captionSize: 10, minWidth: 64));
            }

            return new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = row
            };
        }

        /// <summary>Ряд быстрых кнопок с подписью: конструкции, буквы или знаки.</summary>
        private Control BuildQuickPalette(string title, (string Label, string Snippet, string Tip)[] items)
        {
            var panel = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var (label, snippet, tip) in items)
                panel.Children.Add(BuildQuickButton(label, snippet, tip));
            return WrapQuickPalette(title, panel);
        }

        private Control BuildQuickPalette(string title, string[] symbols)
        {
            var panel = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var symbol in symbols)
                panel.Children.Add(BuildQuickButton(symbol, symbol, symbol));
            return WrapQuickPalette(title, panel);
        }

        private Control WrapQuickPalette(string title, WrapPanel panel)
        {
            var caption = new TextBlock { Text = title, FontSize = 11, Margin = new Thickness(0, 0, 0, 2) };
            BindResource(caption, TextBlock.ForegroundProperty, "TextSecondaryBrush");

            var stack = new StackPanel { Spacing = 2 };
            stack.Children.Add(caption);
            stack.Children.Add(panel);
            return stack;
        }

        private Button BuildQuickButton(string label, string snippet, string tip)
        {
            var button = new Button
            {
                Content = label,
                FontSize = 13,
                MinWidth = 30,
                Padding = new Thickness(6, 3),
                Margin = new Thickness(0, 0, 3, 3),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Cursor = new Cursor(StandardCursorType.Hand),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Focusable = false
            };
            ToolTip.SetTip(button, tip == snippet ? tip : tip + "   " + snippet);
            BindResource(button, Button.BackgroundProperty, "BgSurfaceBrush");
            BindResource(button, Button.BorderBrushProperty, "BorderDefaultBrush");
            BindResource(button, Button.ForegroundProperty, "TextPrimaryBrush");
            button.Click += (_, _) => InsertSnippet(snippet);
            return button;
        }

        /// <summary>Показывает или прячет справку колонкой справа от записи.</summary>
        private void ShowHelp(bool show)
        {
            if (_helpColumn is null || _helpScroll is null || _helpToggle is null) return;

            if (show && _helpScroll.Content is null)
                _helpScroll.Content = new FormulaHelpPanel(InsertSnippet);

            // Справка не выше самого окна: иначе высота по содержимому растянула бы его
            // на весь список.
            if (show && _mainContent is not null)
                _helpScroll.Height = Math.Max(300, _mainContent.Bounds.Height);

            _helpColumn.IsVisible = show;
            _helpToggle.Content = show ? "Скрыть справку ◂" : "Как вводить формулы ▸";
            Width = show ? WindowWidth + HelpWidth : WindowWidth;
        }

        /// <summary>Знаки: набор выбирается слева, знаки набора — плиткой справа.</summary>
        private Control BuildSymbolsTab()
        {
            var sets = new ComboBox
            {
                Width = 210,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Top
            };
            foreach (var set in FormulaGallery.SymbolSets) sets.Items.Add(set.Title);
            sets.SelectedIndex = _symbolSet;
            sets.SelectionChanged += (_, _) =>
            {
                if (sets.SelectedIndex < 0) return;
                _symbolSet = sets.SelectedIndex;
                FillSymbols();
            };

            _symbolPanel = new WrapPanel { Orientation = Orientation.Horizontal };
            FillSymbols();

            var symbols = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _symbolPanel
            };

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,10,*") };
            Grid.SetColumn(sets, 0);
            Grid.SetColumn(symbols, 2);
            grid.Children.Add(sets);
            grid.Children.Add(symbols);
            return grid;
        }

        private void FillSymbols()
        {
            if (_symbolPanel is null) return;
            _symbolPanel.Children.Clear();

            var set = FormulaGallery.SymbolSets[Math.Clamp(_symbolSet, 0, FormulaGallery.SymbolSets.Count - 1)];
            foreach (var (symbol, insert, tip) in set.Symbols)
                _symbolPanel.Children.Add(FormulaTiles.SymbolButton(this, symbol, insert, tip, InsertSnippet));
        }

        /// <summary>Готовые формулы: плитки с подписью; нажатие вставляет формулу целиком.</summary>
        private Control BuildReadyTab()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };

            foreach (var formula in FormulaGallery.ReadyFormulas)
            {
                string latex = formula.Latex;
                row.Children.Add(FormulaTiles.Tile(
                    this, latex, formula.Tip, IconSizePt, _themeText,
                    () => InsertSnippet(latex),
                    minWidth: 90, minHeight: 72, maxImageHeight: 46, caption: formula.Tip));
            }

            return new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = row
            };
        }

        // ── Запись ───────────────────────────────────────────────────────

        /// <summary>
        /// Вставляет заготовку в место каретки. Выделенный текст встаёт в первое пустое
        /// место {} заготовки; каретка — в первое оставшееся пустое место, а без них —
        /// за вставкой. Команда перед кареткой не сливается с буквами вставки: между
        /// ними ставится пробел.
        /// </summary>
        private void InsertSnippet(string snippet)
        {
            string text = _input.Text ?? string.Empty;
            int selectionStart = Math.Min(_input.SelectionStart, _input.SelectionEnd);
            int selectionEnd = Math.Max(_input.SelectionStart, _input.SelectionEnd);
            selectionStart = Math.Clamp(selectionStart, 0, text.Length);
            selectionEnd = Math.Clamp(selectionEnd, 0, text.Length);

            string selected = text.Substring(selectionStart, selectionEnd - selectionStart);
            string before = text.Substring(0, selectionStart);
            string after = text.Substring(selectionEnd);

            string inserted = snippet;
            if (selected.Length > 0)
            {
                int slot = inserted.IndexOf("{}", StringComparison.Ordinal);
                inserted = slot >= 0
                    ? inserted.Substring(0, slot + 1) + selected + inserted.Substring(slot + 1)
                    : inserted + selected;
            }

            if (inserted.Length > 0 && IsAsciiLetter(inserted[0]) && TrailingCommand.IsMatch(before))
                inserted = " " + inserted;

            if (after.Length > 0 && IsAsciiLetter(after[0]) && TrailingCommand.IsMatch(inserted))
                inserted += " ";

            _input.Text = before + inserted + after;

            var nextSlot = SlotPattern.Match(inserted);
            int caret = nextSlot.Success
                ? selectionStart + nextSlot.Index + 1
                : selectionStart + inserted.Length;

            _input.Focus();
            SetCaret(caret);
            SchedulePreview();
        }

        /// <summary>Каретка к следующему (или предыдущему) пустому месту записи.</summary>
        private void MoveToSlot(bool forward)
        {
            string text = _input.Text ?? string.Empty;
            int caret = Math.Clamp(_input.CaretIndex, 0, text.Length);

            int target = -1;
            if (forward)
            {
                // Каретка внутри пустого места — ищется следующее за ним.
                for (var match = SlotPattern.Match(text); match.Success; match = match.NextMatch())
                {
                    if (match.Index + 1 <= caret) continue;
                    target = match.Index + 1;
                    break;
                }

                if (target < 0)
                {
                    var first = SlotPattern.Match(text);
                    if (first.Success && first.Index + 1 != caret) target = first.Index + 1;
                }
            }
            else
            {
                Match? previous = null;
                Match? last = null;
                for (var match = SlotPattern.Match(text); match.Success; match = match.NextMatch())
                {
                    if (match.Index + 1 < caret) previous = match;
                    last = match;
                }

                if (previous is not null) target = previous.Index + 1;
                else if (last is not null && last.Index + 1 != caret) target = last.Index + 1;
            }

            if (target < 0) target = forward ? text.Length : 0;
            SetCaret(target);
        }

        private void SetCaret(int caret)
        {
            int length = _input.Text?.Length ?? 0;
            caret = Math.Clamp(caret, 0, length);
            _input.SelectionStart = caret;
            _input.SelectionEnd = caret;
            _input.CaretIndex = caret;
        }

        private static bool IsAsciiLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

        /// <summary>Свойство берёт кисть оформления приложения и следует за сменой темы.</summary>
        private void BindResource(AvaloniaObject target, AvaloniaProperty property, string key)
            => target.Bind(property, this.GetResourceObservable(key));

        private Button BuildButton(string text, bool accent)
        {
            var button = new Button
            {
                Content = text,
                FontSize = 12,
                Padding = new Thickness(20, 8),
                CornerRadius = new CornerRadius(6),
                Cursor = new Cursor(StandardCursorType.Hand)
            };

            if (accent)
            {
                button.FontWeight = FontWeight.SemiBold;
                button.BorderThickness = new Thickness(0);
                button.Foreground = Brushes.White;
                BindResource(button, Button.BackgroundProperty, "AccentDefaultBrush");
            }
            else
            {
                button.BorderThickness = new Thickness(1);
                BindResource(button, Button.BackgroundProperty, "BgSurfaceBrush");
                BindResource(button, Button.BorderBrushProperty, "BorderDefaultBrush");
                BindResource(button, Button.ForegroundProperty, "TextPrimaryBrush");
            }

            return button;
        }
    }
}
