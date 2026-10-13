using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Input;
using Writersword.Modules.TextEditor.Rendering.Formulas;

namespace Writersword.Modules.TextEditor.Views.Dialogs
{
    /// <summary>
    /// Картинки формул для ленты формулы, её галерей и меню готовых формул:
    /// формула рисуется тем же набором, что на листе, пустые места — пунктирными
    /// квадратиками. Картинки запоминаются: одна заготовка рисуется один раз.
    /// </summary>
    public static class FormulaTiles
    {
        /// <summary>Во сколько раз картинка плотнее экранных точек — для резкости.</summary>
        public const float Oversample = 2f;

        private static readonly Dictionary<(string Latex, uint Color, int SizeKey, bool Display), Bitmap?> Cache = new();
        private static readonly object Gate = new();

        /// <summary>
        /// Картинка записи LaTeX заданным кеглем и цветом. null — запись с ошибкой
        /// или рисовать нечего. Размер картинки в экранных точках — её пиксели,
        /// делённые на <see cref="Oversample"/>.
        /// </summary>
        public static Bitmap? Render(string latex, float sizePt, Color color, bool display = false)
        {
            var key = (latex, color.ToUInt32(), (int)Math.Round(sizePt * 100f), display);

            lock (Gate)
            {
                if (Cache.TryGetValue(key, out var known)) return known;
            }

            Bitmap? bitmap = null;
            try
            {
                string omml = FormulaText.ToOmml(latex);
                var image = FormulaRenderer.RenderImage(
                    omml, display, sizePt, new SKColor(color.R, color.G, color.B, color.A),
                    placeholders: true, Oversample);

                if (image is not null)
                {
                    using var stream = new MemoryStream(image.Png);
                    bitmap = new Bitmap(stream);
                }
            }
            catch (Exception)
            {
                bitmap = null;
            }

            lock (Gate)
            {
                // Кешу незачем расти без конца: заготовок несколько сотен, тем две.
                if (Cache.Count > 2000)
                {
                    foreach (var old in Cache.Values) old?.Dispose();
                    Cache.Clear();
                }

                Cache[key] = bitmap;
            }

            return bitmap;
        }

        /// <summary>Картинка как элемент окна: в своих экранных размерах, не растянутая.</summary>
        public static Control? Image(string latex, float sizePt, Color color, double maxHeight = double.PositiveInfinity, bool display = false)
        {
            var bitmap = Render(latex, sizePt, color, display);
            if (bitmap is null) return null;

            double width = bitmap.PixelSize.Width / Oversample;
            double height = bitmap.PixelSize.Height / Oversample;

            // Высокая заготовка (матрица, система) уменьшается до высоты кнопки целиком.
            if (height > maxHeight && maxHeight > 0)
            {
                double k = maxHeight / height;
                width *= k;
                height = maxHeight;
            }

            return new Image
            {
                Source = bitmap,
                Width = width,
                Height = height,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        /// <summary>Цвет текста темы приложения: им рисуются формулы на кнопках ленты.</summary>
        public static Color ThemeTextColor(Control? anchor = null)
        {
            object? value = null;

            if (anchor is not null && anchor.TryFindResource("TextPrimaryBrush", anchor.ActualThemeVariant, out var own))
                value = own;
            else if (Application.Current is { } app
                     && app.TryGetResource("TextPrimaryBrush", app.ActualThemeVariant, out var shared))
                value = shared;

            return value switch
            {
                ISolidColorBrush brush => brush.Color,
                Color color => color,
                _ => Color.FromRgb(0xE6, 0xE6, 0xE6)
            };
        }

        /// <summary>Кнопка-плитка галереи: картинка заготовки, подсказка, нажатие.</summary>
        public static Button Tile(
            AvaloniaObject themeOwner, string latex, string tip, float sizePt, Color color,
            Action onClick, double minWidth = 40, double minHeight = 36, double maxImageHeight = 64,
            string? caption = null, bool display = false)
        {
            var image = Image(latex, sizePt, color, maxImageHeight, display);

            Control content;
            if (caption is null)
            {
                content = image ?? new TextBlock { Text = latex, FontSize = 11 };
            }
            else
            {
                var stack = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
                if (image is not null) stack.Children.Add(image);
                var text = new TextBlock
                {
                    Text = caption,
                    FontSize = 10,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 220
                };
                Bind(themeOwner, text, TextBlock.ForegroundProperty, "TextSecondaryBrush");
                stack.Children.Add(text);
                content = stack;
            }

            var button = new Button
            {
                Content = content,
                MinWidth = minWidth,
                MinHeight = minHeight,
                Padding = new Thickness(6, 4),
                Margin = new Thickness(0, 0, 4, 4),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Cursor = new Cursor(StandardCursorType.Hand),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Focusable = false
            };
            ToolTip.SetTip(button, tip);
            Bind(themeOwner, button, Button.BackgroundProperty, "BgSurfaceBrush");
            Bind(themeOwner, button, Button.BorderBrushProperty, "BorderSubtleBrush");
            Bind(themeOwner, button, Button.ForegroundProperty, "TextPrimaryBrush");
            button.Click += (_, _) => onClick();
            return button;
        }

        /// <summary>
        /// Кнопка конструкции: картинка-образец, подпись и стрелка; открывает галерею
        /// заготовок конструкции. Нажатие на заготовку закрывает галерею и отдаёт её
        /// запись в insert.
        /// </summary>
        public static Button StructureButton(
            AvaloniaObject themeOwner, FormulaStructure structure, Color color, Action<string> insert,
            float iconSizePt, double iconHeight, double captionSize, double minWidth)
        {
            var icon = Image(structure.IconLatex, iconSizePt, color, maxHeight: iconHeight);

            var caption = new TextBlock
            {
                Text = structure.Title + " ▾",
                FontSize = captionSize,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 80,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var face = new StackPanel { Spacing = 1, HorizontalAlignment = HorizontalAlignment.Center };
            face.Children.Add(new Border { Height = iconHeight, Child = icon });
            face.Children.Add(caption);

            var button = new Button
            {
                Content = face,
                MinWidth = minWidth,
                Padding = new Thickness(4, 2),
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Top,
                Cursor = new Cursor(StandardCursorType.Hand),
                Focusable = false
            };
            ToolTip.SetTip(button, structure.Title);
            Bind(themeOwner, button, Button.BackgroundProperty, "BgSurfaceBrush");
            Bind(themeOwner, button, Button.BorderBrushProperty, "BorderSubtleBrush");
            Bind(themeOwner, button, Button.ForegroundProperty, "TextPrimaryBrush");

            var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft, ShowMode = FlyoutShowMode.Standard };
            flyout.Opening += (_, _) =>
            {
                if (flyout.Content is null)
                    flyout.Content = StructureGallery(themeOwner, structure, color, insert, flyout.Hide);
            };
            button.Flyout = flyout;
            return button;
        }

        /// <summary>Галерея заготовок одной конструкции: разделы с подзаголовками.</summary>
        public static Control StructureGallery(
            AvaloniaObject themeOwner, FormulaStructure structure, Color color, Action<string> insert, Action close)
        {
            var stack = new StackPanel { Spacing = 6, Margin = new Thickness(4), Width = 460 };

            foreach (var section in structure.Sections)
            {
                var caption = new TextBlock { Text = section.Title, FontSize = 11, FontWeight = FontWeight.SemiBold };
                Bind(themeOwner, caption, TextBlock.ForegroundProperty, "TextSecondaryBrush");
                stack.Children.Add(caption);

                var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
                foreach (var template in section.Items)
                {
                    string latex = template.Latex;
                    wrap.Children.Add(Tile(
                        themeOwner, latex, template.Tip, 15f, color,
                        () =>
                        {
                            close();
                            insert(latex);
                        },
                        minWidth: 48, minHeight: 44, maxImageHeight: 72));
                }
                stack.Children.Add(wrap);
            }

            return new ScrollViewer
            {
                MaxHeight = 480,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = stack
            };
        }

        /// <summary>Кнопка знака: сам знак шрифтом формул, подсказка, нажатие отдаёт запись.</summary>
        public static Button SymbolButton(
            AvaloniaObject themeOwner, string symbol, string insert, string tip, Action<string> onInsert,
            double width = 32, double height = 28)
        {
            var button = new Button
            {
                Content = symbol,
                FontFamily = new FontFamily("Cambria Math, Segoe UI Symbol, Segoe UI"),
                FontSize = 15,
                Width = width,
                Height = height,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 2, 2),
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
                Focusable = false
            };
            ToolTip.SetTip(button, insert == symbol ? tip : tip + "  " + insert);
            Bind(themeOwner, button, Button.BackgroundProperty, "BgSurfaceBrush");
            Bind(themeOwner, button, Button.BorderBrushProperty, "BorderSubtleBrush");
            Bind(themeOwner, button, Button.ForegroundProperty, "TextPrimaryBrush");
            button.Click += (_, _) => onInsert(insert);
            return button;
        }

        /// <summary>
        /// Кнопка набора знаков: три знака набора и название; открывает все знаки набора.
        /// </summary>
        public static Button SymbolSetButton(AvaloniaObject themeOwner, FormulaSymbolSet set, Action<string> insert)
        {
            var sample = new System.Text.StringBuilder();
            for (int i = 0; i < set.Symbols.Count && i < 3; i++) sample.Append(set.Symbols[i].Symbol);

            var face = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            face.Children.Add(new TextBlock
            {
                Text = sample.ToString(),
                FontFamily = new FontFamily("Cambria Math, Segoe UI Symbol, Segoe UI"),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            });
            face.Children.Add(new TextBlock { Text = set.Title + " ▾", FontSize = 10, VerticalAlignment = VerticalAlignment.Center });

            var button = new Button
            {
                Content = face,
                Height = 26,
                Padding = new Thickness(6, 0),
                Margin = new Thickness(0, 0, 4, 4),
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
                Focusable = false
            };
            ToolTip.SetTip(button, set.Title);
            Bind(themeOwner, button, Button.BackgroundProperty, "BgSurfaceBrush");
            Bind(themeOwner, button, Button.BorderBrushProperty, "BorderSubtleBrush");
            Bind(themeOwner, button, Button.ForegroundProperty, "TextPrimaryBrush");

            var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft, ShowMode = FlyoutShowMode.Standard };
            flyout.Opening += (_, _) =>
            {
                if (flyout.Content is not null) return;

                var wrap = new WrapPanel { Orientation = Orientation.Horizontal, Width = 380, Margin = new Thickness(4) };
                foreach (var (symbol, text, tip) in set.Symbols)
                {
                    wrap.Children.Add(SymbolButton(themeOwner, symbol, text, tip, chosen =>
                    {
                        flyout.Hide();
                        insert(chosen);
                    }));
                }
                flyout.Content = wrap;
            };
            button.Flyout = flyout;
            return button;
        }

        /// <summary>Свойство берёт кисть темы приложения и следует за её сменой.</summary>
        public static void Bind(AvaloniaObject themeOwner, AvaloniaObject target, AvaloniaProperty property, string key)
        {
            if (themeOwner is IResourceHost host)
                target.Bind(property, host.GetResourceObservable(key));
            else if (Application.Current is { } app)
                target.Bind(property, app.GetResourceObservable(key));
        }
    }

    /// <summary>
    /// Конструкции для ленты документа: кнопки с образцами, каждая открывает свою
    /// галерею заготовок. Нажатие на заготовку отдаёт её запись команде.
    /// </summary>
    public sealed class FormulaStructuresStrip : Border
    {
        public static readonly StyledProperty<ICommand?> CommandProperty =
            AvaloniaProperty.Register<FormulaStructuresStrip, ICommand?>(nameof(Command));

        /// <summary>Команда вставки заготовки; параметр — запись LaTeX.</summary>
        public ICommand? Command
        {
            get => GetValue(CommandProperty);
            set => SetValue(CommandProperty, value);
        }

        private bool _built;

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (_built) return;
            _built = true;

            var color = FormulaTiles.ThemeTextColor(this);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
            foreach (var structure in FormulaGallery.Structures)
            {
                row.Children.Add(FormulaTiles.StructureButton(
                    this, structure, color, Insert, iconSizePt: 10f, iconHeight: 28, captionSize: 9, minWidth: 54));
            }
            Child = row;
        }

        private void Insert(string latex)
        {
            if (Command?.CanExecute(latex) == true) Command.Execute(latex);
        }
    }

    /// <summary>Наборы знаков для ленты документа: кнопка на набор, знаки — во всплывающей панели.</summary>
    public sealed class FormulaSymbolsStrip : Border
    {
        public static readonly StyledProperty<ICommand?> CommandProperty =
            AvaloniaProperty.Register<FormulaSymbolsStrip, ICommand?>(nameof(Command));

        /// <summary>Команда вставки знака; параметр — запись знака.</summary>
        public ICommand? Command
        {
            get => GetValue(CommandProperty);
            set => SetValue(CommandProperty, value);
        }

        private bool _built;

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (_built) return;
            _built = true;

            // Два ряда по высоте группы ленты.
            var grid = new WrapPanel { Orientation = Orientation.Vertical, MaxHeight = 60 };
            foreach (var set in FormulaGallery.SymbolSets)
                grid.Children.Add(FormulaTiles.SymbolSetButton(this, set, Insert));
            Child = grid;
        }

        private void Insert(string text)
        {
            if (Command?.CanExecute(text) == true) Command.Execute(text);
        }
    }

    /// <summary>
    /// Галерея готовых формул для меню кнопки «Формула» на ленте: плитки с
    /// картинками формул и подписями. Нажатие отдаёт запись формулы команде.
    /// </summary>
    public sealed class FormulaReadyPanel : Border
    {
        public static readonly StyledProperty<ICommand?> CommandProperty =
            AvaloniaProperty.Register<FormulaReadyPanel, ICommand?>(nameof(Command));

        public static readonly StyledProperty<ICommand?> NewFormulaCommandProperty =
            AvaloniaProperty.Register<FormulaReadyPanel, ICommand?>(nameof(NewFormulaCommand));

        /// <summary>Команда вставки готовой формулы; параметр — запись LaTeX.</summary>
        public ICommand? Command
        {
            get => GetValue(CommandProperty);
            set => SetValue(CommandProperty, value);
        }

        /// <summary>Команда «Новая формула» — пустое окно формулы.</summary>
        public ICommand? NewFormulaCommand
        {
            get => GetValue(NewFormulaCommandProperty);
            set => SetValue(NewFormulaCommandProperty, value);
        }

        private bool _built;

        public FormulaReadyPanel()
        {
            Padding = new Thickness(10);
            MaxWidth = 720;
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

            var stack = new StackPanel { Spacing = 8 };

            var newButton = new Button
            {
                Content = "Новая формула  (Alt+=)",
                FontSize = 12,
                Padding = new Thickness(12, 6),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            FormulaTiles.Bind(this, newButton, Button.BackgroundProperty, "BgSurfaceBrush");
            FormulaTiles.Bind(this, newButton, Button.BorderBrushProperty, "BorderSubtleBrush");
            FormulaTiles.Bind(this, newButton, Button.ForegroundProperty, "TextPrimaryBrush");
            newButton.Click += (_, _) =>
            {
                ClosePopup();
                if (NewFormulaCommand?.CanExecute(null) == true) NewFormulaCommand.Execute(null);
            };
            stack.Children.Add(newButton);

            var caption = new TextBlock { Text = "Готовые формулы", FontSize = 11 };
            FormulaTiles.Bind(this, caption, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            stack.Children.Add(caption);

            var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var formula in FormulaGallery.ReadyFormulas)
            {
                string latex = formula.Latex;
                wrap.Children.Add(FormulaTiles.Tile(
                    this, latex, formula.Tip, 13f, color,
                    () =>
                    {
                        ClosePopup();
                        if (Command?.CanExecute(latex) == true) Command.Execute(latex);
                    },
                    minWidth: 120, minHeight: 56, maxImageHeight: 56, caption: formula.Tip));
            }

            stack.Children.Add(new ScrollViewer
            {
                MaxHeight = 460,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = wrap
            });

            Child = stack;
        }

        /// <summary>Закрывает всплывающую панель, в которой стоит галерея.</summary>
        private void ClosePopup()
        {
            Control? current = this;
            while (current is not null)
            {
                if (current is Avalonia.Controls.Primitives.Popup popup)
                {
                    popup.IsOpen = false;
                    return;
                }
                current = current.Parent as Control;
            }
        }
    }
}
