using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using System;
using System.Collections.Generic;

namespace Writersword.Modules.TextEditor.Views.Dialogs
{
    /// <summary>Строка справки: что это, как записать и пример, который рисуется рядом.</summary>
    /// <param name="What">Что получается.</param>
    /// <param name="Write">Как это записать.</param>
    /// <param name="Example">Пример записью LaTeX — рисуется картинкой и вставляется по нажатию.</param>
    public sealed record FormulaHelpEntry(string What, string Write, string Example);

    /// <summary>Раздел справки по записи формул.</summary>
    public sealed record FormulaHelpSection(string Title, string? Note, IReadOnlyList<FormulaHelpEntry> Entries);

    /// <summary>
    /// Короткая справка по записи формул: как набрать степень, дробь, корень, сумму,
    /// скобки, матрицу и знаки. Одна на окно формулы и на вкладку «Формулы» ленты.
    /// </summary>
    public static class FormulaHelp
    {
        private static FormulaHelpEntry E(string what, string write, string example) => new(what, write, example);

        public static readonly IReadOnlyList<FormulaHelpSection> Sections = new[]
        {
            new FormulaHelpSection("Основное",
                "Формула пишется командами LaTeX. Команда начинается с обратной косой черты: \\frac, \\sqrt, \\alpha. "
                + "Фигурные скобки { } собирают несколько знаков в одно целое. Пустые скобки {} — пустое место, "
                + "на образце это пунктирный квадратик. Пробелы в записи ничего не значат: отступы ставятся командами.",
                new[]
                {
                    E("Степень, верхний индекс", "x^2   x^{10}", "x^{2}+x^{10}"),
                    E("Нижний индекс", "a_1   a_{ij}", "a_{1}+a_{ij}"),
                    E("Оба индекса", "x_1^2", "x_{1}^{2}"),
                    E("Дробь", "\\frac{a}{b}", "\\frac{a}{b}"),
                    E("Корень", "\\sqrt{x}   \\sqrt[3]{x}", "\\sqrt{x}+\\sqrt[3]{x}"),
                    E("Обычный текст в формуле", "\\text{если } x>0", "\\text{если }x>0"),
                    E("Отступы", "a\\,b   a\\quad b   a\\qquad b", "a\\,b\\quad c\\qquad d"),
                    E("Знаки { } % # как есть", "\\{  \\}  \\%  \\#", "\\{x\\}\\;50\\%")
                }),

            new FormulaHelpSection("Скобки",
                "\\left и \\right растят скобки по высоте содержимого. Точка вместо скобки — скобки нет.",
                new[]
                {
                    E("Растущие круглые", "\\left( \\frac{a}{b} \\right)", "\\left(\\frac{a}{b}\\right)"),
                    E("Квадратные, фигурные", "\\left[ x \\right]   \\left\\{ x \\right\\}", "\\left[x\\right]\\left\\{x\\right\\}"),
                    E("Модуль, норма", "\\left| x \\right|   \\left\\| v \\right\\|", "\\left|x\\right|\\;\\left\\|v\\right\\|"),
                    E("Угловые", "\\left\\langle a \\right\\rangle", "\\left\\langle a\\right\\rangle"),
                    E("Одна скобка", "\\left. \\frac{dy}{dx} \\right|_{x=0}", "\\left.\\frac{dy}{dx}\\right|_{x=0}"),
                    E("Черта внутри скобок", "\\left\\{ x \\middle| x>0 \\right\\}", "\\left\\{x\\middle|x>0\\right\\}"),
                    E("Биномиальный коэффициент", "\\binom{n}{k}", "\\binom{n}{k}")
                }),

            new FormulaHelpSection("Суммы, интегралы, пределы",
                "Пределы пишутся индексами: _ — нижний, ^ — верхний.",
                new[]
                {
                    E("Сумма", "\\sum_{i=1}^{n} i", "\\sum_{i=1}^{n}i"),
                    E("Произведение", "\\prod_{k=1}^{n} a_k", "\\prod_{k=1}^{n}a_{k}"),
                    E("Интеграл", "\\int_a^b f(x)\\,dx", "\\int_{a}^{b}f\\left(x\\right)\\,dx"),
                    E("Двойной, по контуру", "\\iint_D   \\oint_C", "\\iint_{D}\\oint_{C}"),
                    E("Предел", "\\lim_{x \\to 0} f(x)", "\\lim_{x\\to 0}f\\left(x\\right)"),
                    E("Объединение, пересечение", "\\bigcup_{i} A_i   \\bigcap_{i} A_i", "\\bigcup_{i}A_{i}\\bigcap_{i}A_{i}")
                }),

            new FormulaHelpSection("Функции",
                "Имя функции с косой чертой пишется прямо, а не курсивом, как и положено.",
                new[]
                {
                    E("Тригонометрия", "\\sin x   \\cos 2x   \\tan\\alpha", "\\sin x+\\cos 2x+\\tan\\alpha"),
                    E("Логарифмы", "\\log_2 x   \\ln x   \\lg x", "\\log_{2}x+\\ln x+\\lg x"),
                    E("Максимум, минимум", "\\max_{x} f   \\min_{x} f", "\\max_{x}f"),
                    E("Своя функция", "\\operatorname{sgn} x", "\\operatorname{sgn}x")
                }),

            new FormulaHelpSection("Над и под буквой",
                null,
                new[]
                {
                    E("Крышка, черта, вектор, точка", "\\hat{x}  \\bar{x}  \\vec{v}  \\dot{x}  \\ddot{x}", "\\hat{x}\\;\\bar{x}\\;\\vec{v}\\;\\dot{x}\\;\\ddot{x}"),
                    E("Черта над выражением", "\\overline{AB}   \\underline{x}", "\\overline{AB}\\;\\underline{x}"),
                    E("Фигурная скобка с подписью", "\\overbrace{a+b}^{n}   \\underbrace{a+b}_{n}", "\\overbrace{a+b}^{n}"),
                    E("Знак с подписью", "\\overset{def}{=}   \\underset{x\\to 0}{\\sim}", "\\overset{def}{=}\\;\\underset{x\\to 0}{\\sim}"),
                    E("Рамка", "\\boxed{E=mc^2}", "\\boxed{E=mc^{2}}")
                }),

            new FormulaHelpSection("Матрицы и системы",
                "& разделяет столбцы, \\\\ — строки.",
                new[]
                {
                    E("Матрица в скобках", "\\begin{pmatrix} a & b \\\\ c & d \\end{pmatrix}", "\\begin{pmatrix} a & b \\\\ c & d \\end{pmatrix}"),
                    E("В квадратных, определитель", "bmatrix   vmatrix", "\\begin{vmatrix} a & b \\\\ c & d \\end{vmatrix}"),
                    E("Система", "\\begin{cases} x, & x>0 \\\\ -x, & x\\le 0 \\end{cases}", "\\begin{cases} x, & x>0 \\\\ -x, & x\\le 0 \\end{cases}"),
                    E("Строки одна под другой", "a=b \\\\ c=d", "a=b \\\\ c=d"),
                    E("Многоточия", "\\cdots   \\ldots   \\vdots   \\ddots", "\\cdots\\;\\ldots\\;\\vdots\\;\\ddots")
                }),

            new FormulaHelpSection("Буквы и знаки",
                "Знаки можно вставлять и прямо, готовыми символами: α, ≤, →. Кнопки «Символы» делают это за вас.",
                new[]
                {
                    E("Греческие", "\\alpha \\beta \\pi \\theta \\Omega", "\\alpha\\beta\\pi\\theta\\Omega"),
                    E("Сравнения", "\\le  \\ge  \\ne  \\approx  \\equiv", "\\le\\ge\\ne\\approx\\equiv"),
                    E("Действия", "\\pm  \\times  \\cdot  \\div", "\\pm\\times\\cdot\\div"),
                    E("Стрелки", "\\to  \\Rightarrow  \\Leftrightarrow  \\mapsto", "\\to\\Rightarrow\\Leftrightarrow\\mapsto"),
                    E("Множества", "\\in  \\notin  \\subset  \\cup  \\cap  \\emptyset", "\\in\\notin\\subset\\cup\\cap\\emptyset"),
                    E("Прочее", "\\infty  \\partial  \\nabla  \\forall  \\exists", "\\infty\\partial\\nabla\\forall\\exists"),
                    E("Наборы букв", "\\mathbb{R}  \\mathcal{L}  \\mathbf{v}  \\mathfrak{g}", "\\mathbb{R}\\mathcal{L}\\mathbf{v}\\mathfrak{g}"),
                    E("Штрих, градус", "f'(x)   90^\\circ", "f'\\left(x\\right)\\;90^{\\circ}")
                }),

            new FormulaHelpSection("Клавиши",
                null,
                new[]
                {
                    E("Новая формула в документе", "Alt+=", ""),
                    E("К следующему пустому месту", "Tab (назад — Shift+Tab)", ""),
                    E("Вставить формулу", "Enter", ""),
                    E("Закрыть без изменений", "Esc", ""),
                    E("Поправить формулу", "двойной щелчок по формуле", "")
                })
        };
    }

    /// <summary>
    /// Справка по записи формул: разделы с таблицей «что — как записать — как выглядит».
    /// Нажатие на строку с примером вставляет пример, если панели дали, куда вставлять.
    /// </summary>
    public sealed class FormulaHelpPanel : Border
    {
        private readonly Action<string>? _insert;
        private bool _built;

        public FormulaHelpPanel(Action<string>? insert = null)
        {
            _insert = insert;
            Padding = new Thickness(4);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (_built) return;
            _built = true;
            Build();
        }

        private void Build()
        {
            var color = FormulaTiles.ThemeTextColor(this);
            var stack = new StackPanel { Spacing = 10 };

            if (_insert is not null)
            {
                var tip = new TextBlock
                {
                    Text = "Нажатие на строку вставляет пример в запись формулы.",
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap
                };
                FormulaTiles.Bind(this, tip, TextBlock.ForegroundProperty, "TextMutedBrush");
                stack.Children.Add(tip);
            }

            foreach (var section in FormulaHelp.Sections)
            {
                var title = new TextBlock { Text = section.Title, FontSize = 13, FontWeight = FontWeight.SemiBold };
                FormulaTiles.Bind(this, title, TextBlock.ForegroundProperty, "TextPrimaryBrush");
                stack.Children.Add(title);

                if (section.Note is not null)
                {
                    var note = new TextBlock { Text = section.Note, FontSize = 11, TextWrapping = TextWrapping.Wrap };
                    FormulaTiles.Bind(this, note, TextBlock.ForegroundProperty, "TextSecondaryBrush");
                    stack.Children.Add(note);
                }

                var table = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*,Auto") };
                int row = 0;

                foreach (var entry in section.Entries)
                {
                    table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

                    var what = new TextBlock
                    {
                        Text = entry.What,
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 3, 10, 3)
                    };
                    FormulaTiles.Bind(this, what, TextBlock.ForegroundProperty, "TextSecondaryBrush");

                    var write = new SelectableTextBlock
                    {
                        Text = entry.Write,
                        FontFamily = new FontFamily("Consolas, Menlo, monospace"),
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 3, 10, 3)
                    };
                    FormulaTiles.Bind(this, write, SelectableTextBlock.ForegroundProperty, "TextPrimaryBrush");

                    Control view = new Border();
                    if (entry.Example.Length > 0)
                    {
                        string example = entry.Example;
                        var image = FormulaTiles.Image(example, 13f, color, maxHeight: 60);
                        if (image is not null)
                        {
                            if (_insert is not null)
                            {
                                var button = new Button
                                {
                                    Content = image,
                                    Padding = new Thickness(6, 2),
                                    Margin = new Thickness(0, 2),
                                    CornerRadius = new CornerRadius(4),
                                    BorderThickness = new Thickness(1),
                                    HorizontalAlignment = HorizontalAlignment.Left,
                                    Cursor = new Cursor(StandardCursorType.Hand),
                                    Focusable = false
                                };
                                ToolTip.SetTip(button, "Вставить пример");
                                FormulaTiles.Bind(this, button, Button.BackgroundProperty, "BgSurfaceBrush");
                                FormulaTiles.Bind(this, button, Button.BorderBrushProperty, "BorderSubtleBrush");
                                button.Click += (_, _) => _insert(example);
                                view = button;
                            }
                            else
                            {
                                image.HorizontalAlignment = HorizontalAlignment.Left;
                                image.Margin = new Thickness(6, 2);
                                view = image;
                            }
                        }
                    }

                    Grid.SetRow(what, row);
                    Grid.SetColumn(what, 0);
                    Grid.SetRow(write, row);
                    Grid.SetColumn(write, 1);
                    Grid.SetRow(view, row);
                    Grid.SetColumn(view, 2);
                    table.Children.Add(what);
                    table.Children.Add(write);
                    table.Children.Add(view);
                    row++;
                }

                stack.Children.Add(table);
            }

            Child = stack;
        }
    }

    /// <summary>Окно справки по записи формул — с вкладки «Формулы» ленты.</summary>
    public sealed class FormulaHelpWindow : Window
    {
        private static FormulaHelpWindow? _open;

        public FormulaHelpWindow()
        {
            Title = "Как записывать формулы";
            Width = 860;
            Height = 680;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            CanResize = true;

            var close = new Button
            {
                Content = "Закрыть",
                FontSize = 12,
                Padding = new Thickness(20, 6),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Right,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            FormulaTiles.Bind(this, close, Button.BackgroundProperty, "BgSurfaceBrush");
            FormulaTiles.Bind(this, close, Button.BorderBrushProperty, "BorderDefaultBrush");
            FormulaTiles.Bind(this, close, Button.ForegroundProperty, "TextPrimaryBrush");
            close.Click += (_, _) => Close();

            var title = new TextBlock { Text = "Как записывать формулы", FontSize = 15, FontWeight = FontWeight.SemiBold };
            FormulaTiles.Bind(this, title, TextBlock.ForegroundProperty, "TextPrimaryBrush");

            var dock = new DockPanel { Margin = new Thickness(20, 16) };
            DockPanel.SetDock(title, Avalonia.Controls.Dock.Top);
            DockPanel.SetDock(close, Avalonia.Controls.Dock.Bottom);
            title.Margin = new Thickness(0, 0, 0, 10);
            close.Margin = new Thickness(0, 10, 0, 0);
            dock.Children.Add(title);
            dock.Children.Add(close);
            dock.Children.Add(new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = new FormulaHelpPanel()
            });

            Content = dock;
            FormulaTiles.Bind(this, this, BackgroundProperty, "BgAppBrush");

            KeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape) return;
                Close();
                e.Handled = true;
            };
        }

        /// <summary>Показывает справку; уже открытая — выходит вперёд, вторая не открывается.</summary>
        public static void ShowHelp()
        {
            if (_open is not null)
            {
                _open.Activate();
                return;
            }

            var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            var owner = lifetime?.MainWindow;

            var window = new FormulaHelpWindow();
            window.Closed += (_, _) => _open = null;
            _open = window;

            if (owner is not null) window.Show(owner);
            else window.Show();
        }
    }
}
