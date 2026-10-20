using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace Writersword.Modules.TextEditor.Views.Dialogs
{
    /// <summary>Что сделать с обсуждением примечания.</summary>
    public enum CommentThreadAction
    {
        /// <summary>Добавить ответ.</summary>
        Reply = 0,

        /// <summary>Закрыть обсуждение или открыть снова.</summary>
        ToggleDone = 1,

        /// <summary>Удалить примечание вместе с ответами.</summary>
        Delete = 2
    }

    /// <summary>Итог окна обсуждения.</summary>
    /// <param name="Action">Что сделать.</param>
    /// <param name="Text">Текст ответа (для ответа).</param>
    public sealed record CommentThreadResult(CommentThreadAction Action, string Text);

    /// <summary>Одна запись обсуждения для показа.</summary>
    /// <param name="Author">Автор.</param>
    /// <param name="Date">Когда оставлена (UTC) или null.</param>
    /// <param name="Text">Текст.</param>
    public sealed record CommentThreadEntry(string Author, DateTime? Date, string Text);

    /// <summary>
    /// Окно обсуждения примечания: само примечание и ответы на него, поле ответа,
    /// кнопки «Ответить», «Решено» («Открыть снова» у закрытого) и «Удалить». Вид тот же,
    /// что у окна ввода (<see cref="InputDialog"/>): без рамки системы, на подложке
    /// со скруглёнными углами, цветами оформления приложения. Ctrl+Enter — ответить,
    /// Esc — закрыть.
    /// </summary>
    public sealed class CommentThreadDialog : Window
    {
        private readonly TextBox _reply;

        public CommentThreadDialog(IReadOnlyList<CommentThreadEntry> entries, bool done, string? quote)
        {
            Title = "Комментарий";
            Width = 480;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            CanResize = false;
            WindowDecorations = WindowDecorations.None;
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            Background = Brushes.Transparent;

            var content = new StackPanel { Spacing = 12 };

            var titleText = new TextBlock
            {
                Text = done ? "Комментарий (обсуждение закрыто)" : "Комментарий",
                FontSize = 15,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap
            };
            BindResource(titleText, TextBlock.ForegroundProperty, "TextPrimaryBrush");
            content.Children.Add(titleText);

            if (!string.IsNullOrWhiteSpace(quote))
            {
                var quoteText = new TextBlock
                {
                    Text = "«" + quote!.Trim() + "»",
                    FontSize = 12,
                    FontStyle = FontStyle.Italic,
                    TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 60
                };
                BindResource(quoteText, TextBlock.ForegroundProperty, "TextSecondaryBrush");
                content.Children.Add(quoteText);
            }

            var thread = new StackPanel { Spacing = 10 };
            foreach (var entry in entries)
            {
                var header = new TextBlock
                {
                    Text = entry.Date is DateTime date
                        ? entry.Author + " · " + date.ToLocalTime().ToString("d MMMM yyyy, HH:mm", CultureInfo.GetCultureInfo("ru-RU"))
                        : entry.Author,
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                };
                BindResource(header, TextBlock.ForegroundProperty, "TextSecondaryBrush");

                var body = new SelectableTextBlock
                {
                    Text = entry.Text,
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap
                };
                BindResource(body, SelectableTextBlock.ForegroundProperty, "TextPrimaryBrush");

                var item = new StackPanel { Spacing = 2 };
                item.Children.Add(header);
                item.Children.Add(body);
                thread.Children.Add(item);
            }

            var threadScroll = new ScrollViewer
            {
                MaxHeight = 280,
                Content = thread,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            };
            content.Children.Add(threadScroll);

            _reply = new TextBox
            {
                FontSize = 13,
                Padding = new Thickness(8, 6),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 64,
                MaxHeight = 200,
                PlaceholderText = "Ответ…"
            };
            BindResource(_reply, TextBox.BackgroundProperty, "BgSurfaceBrush");
            BindResource(_reply, TextBox.BorderBrushProperty, "BorderDefaultBrush");
            BindResource(_reply, TextBox.ForegroundProperty, "TextPrimaryBrush");
            BindResource(_reply, TextBox.CaretBrushProperty, "TextPrimaryBrush");
            content.Children.Add(_reply);

            var deleteButton = BuildButton("Удалить", accent: false);
            deleteButton.HorizontalAlignment = HorizontalAlignment.Left;
            deleteButton.Click += (_, _) => Close(new CommentThreadResult(CommentThreadAction.Delete, string.Empty));

            var doneButton = BuildButton(done ? "Открыть снова" : "Решено", accent: false);
            doneButton.Click += (_, _) => Close(new CommentThreadResult(CommentThreadAction.ToggleDone, string.Empty));

            var closeButton = BuildButton("Закрыть", accent: false);
            closeButton.Click += (_, _) => Close(null);

            var replyButton = BuildButton("Ответить", accent: true);
            replyButton.Click += (_, _) => CloseWithReply();

            var rightButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 10
            };
            rightButtons.Children.Add(doneButton);
            rightButtons.Children.Add(closeButton);
            rightButtons.Children.Add(replyButton);

            var buttons = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            buttons.Children.Add(deleteButton);
            buttons.Children.Add(rightButtons);
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

            // Клавиши окна ловятся на спуске события, раньше поля ответа: поле в
            // несколько строк забирает Enter себе.
            AddHandler(KeyDownEvent, OnDialogKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        }

        /// <summary>
        /// Открывает окно поверх главного окна приложения. null — окно закрыто без
        /// действия или показать его не на чем.
        /// </summary>
        public static async Task<CommentThreadResult?> ShowAsync(
            IReadOnlyList<CommentThreadEntry> entries, bool done, string? quote)
        {
            var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            var owner = lifetime?.MainWindow;
            if (owner is null) return null;

            var dialog = new CommentThreadDialog(entries, done, quote);
            return await dialog.ShowDialog<CommentThreadResult?>(owner);
        }

        private void CloseWithReply()
        {
            string text = _reply.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                _reply.Focus();
                return;
            }

            Close(new CommentThreadResult(CommentThreadAction.Reply, text));
        }

        private void BindResource(AvaloniaObject target, AvaloniaProperty property, string key)
            => target.Bind(property, this.GetResourceObservable(key));

        private Button BuildButton(string text, bool accent)
        {
            var button = new Button
            {
                Content = text,
                FontSize = 12,
                Padding = new Thickness(16, 8),
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
                CloseWithReply();
                e.Handled = true;
            }
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            _reply.Focus();
        }
    }
}
