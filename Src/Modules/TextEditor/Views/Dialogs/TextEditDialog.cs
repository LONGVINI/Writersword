using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using System;
using System.Threading.Tasks;

namespace Writersword.Modules.TextEditor.Views.Dialogs
{
    /// <summary>Итог окна правки текста.</summary>
    /// <param name="Text">Текст, оставленный в окне.</param>
    /// <param name="Delete">Нажата кнопка удаления: текст не нужен, объект убирается.</param>
    public sealed record TextEditResult(string Text, bool Delete);

    /// <summary>
    /// Окно правки текста в несколько строк: текст сноски, текст примечания.
    ///
    /// Вид тот же, что у окна ввода одной строки (<see cref="InputDialog"/>): без
    /// рамки системы, на подложке со скруглёнными углами, цветами оформления
    /// приложения. Enter переводит строку, Ctrl+Enter закрывает окно с текстом,
    /// Esc — без него. Кнопка удаления есть только у окна, открытого для правки
    /// уже существующего объекта.
    /// </summary>
    public sealed class TextEditDialog : Window
    {
        private readonly TextBox _input;

        public TextEditDialog(string title, string? prompt, string currentValue, string? deleteLabel)
        {
            Title = title;
            Width = 480;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            CanResize = false;
            WindowDecorations = WindowDecorations.None;
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            Background = Brushes.Transparent;

            var titleText = new TextBlock
            {
                Text = title,
                FontSize = 15,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap
            };
            BindResource(titleText, TextBlock.ForegroundProperty, "TextPrimaryBrush");

            var promptText = new TextBlock
            {
                Text = prompt ?? string.Empty,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                IsVisible = !string.IsNullOrEmpty(prompt)
            };
            BindResource(promptText, TextBlock.ForegroundProperty, "TextSecondaryBrush");

            _input = new TextBox
            {
                Text = currentValue,
                FontSize = 13,
                Padding = new Thickness(8, 6),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 96,
                MaxHeight = 320
            };
            BindResource(_input, TextBox.BackgroundProperty, "BgSurfaceBrush");
            BindResource(_input, TextBox.BorderBrushProperty, "BorderDefaultBrush");
            BindResource(_input, TextBox.ForegroundProperty, "TextPrimaryBrush");
            BindResource(_input, TextBox.CaretBrushProperty, "TextPrimaryBrush");

            // Клавиши окна ловятся на спуске события, раньше поля ввода: поле в
            // несколько строк забирает Enter себе и до всплытия его не отдаёт.
            AddHandler(KeyDownEvent, OnDialogKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

            var cancelButton = BuildButton("Отмена", accent: false);
            cancelButton.Click += (_, _) => Close(null);

            var okButton = BuildButton("OK", accent: true);
            okButton.Click += (_, _) => Close(new TextEditResult(_input.Text ?? string.Empty, false));

            var rightButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 10
            };
            rightButtons.Children.Add(cancelButton);
            rightButtons.Children.Add(okButton);

            var buttons = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            buttons.Children.Add(rightButtons);

            if (!string.IsNullOrEmpty(deleteLabel))
            {
                var deleteButton = BuildButton(deleteLabel!, accent: false);
                deleteButton.HorizontalAlignment = HorizontalAlignment.Left;
                deleteButton.Click += (_, _) => Close(new TextEditResult(string.Empty, true));
                buttons.Children.Add(deleteButton);
            }

            var content = new StackPanel { Spacing = 14 };
            content.Children.Add(titleText);
            content.Children.Add(promptText);
            content.Children.Add(_input);
            content.Children.Add(buttons);

            var shadow = new Border
            {
                CornerRadius = new CornerRadius(20),
                Margin = new Thickness(-2)
            };
            BindResource(shadow, Border.BackgroundProperty, "BgBaseBrush");

            var frame = new Border
            {
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(24, 20),
                BorderThickness = new Thickness(1),
                ClipToBounds = true,
                Margin = new Thickness(2),
                Child = content
            };
            BindResource(frame, Border.BackgroundProperty, "BgAppBrush");
            BindResource(frame, Border.BorderBrushProperty, "BorderDefaultBrush");

            var root = new Grid();
            root.Children.Add(shadow);
            root.Children.Add(frame);

            Content = root;
        }

        /// <summary>
        /// Открывает окно поверх главного окна приложения. null — окно закрыто без
        /// текста (Отмена, Esc) или показать его не на чем.
        /// </summary>
        public static async Task<TextEditResult?> ShowAsync(
            string title, string? prompt, string currentValue, string? deleteLabel = null)
        {
            var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            var owner = lifetime?.MainWindow;
            if (owner is null) return null;

            var dialog = new TextEditDialog(title, prompt, currentValue, deleteLabel);
            return await dialog.ShowDialog<TextEditResult?>(owner);
        }

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

        private void OnDialogKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close(null);
                e.Handled = true;
                return;
            }

            bool enter = e.Key == Key.Return || e.Key == Key.Enter;
            if (enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                Close(new TextEditResult(_input.Text ?? string.Empty, false));
                e.Handled = true;
            }
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);

            _input.Focus();
            _input.CaretIndex = _input.Text?.Length ?? 0;
        }
    }
}
