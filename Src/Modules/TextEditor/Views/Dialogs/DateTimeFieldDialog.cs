using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Writersword.Modules.TextEditor.Views.Dialogs
{
    /// <summary>
    /// Выбор вида даты и времени для вставки, как «Вставка → Дата и время» у Word:
    /// список образцов на сегодня. Вставляется живое поле — дата обновляется сама при
    /// каждом открытии документа. Вид окна — как у окна ввода (<see cref="InputDialog"/>).
    /// Двойной щелчок по образцу или Enter — вставить, Esc — закрыть.
    /// </summary>
    public sealed class DateTimeFieldDialog : Window
    {
        private readonly ListBox _list;
        private readonly IReadOnlyList<(string Instruction, string Sample)> _choices;

        public DateTimeFieldDialog(IReadOnlyList<(string Instruction, string Sample)> choices)
        {
            _choices = choices;

            Title = "Дата и время";
            Width = 380;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            CanResize = false;
            WindowDecorations = WindowDecorations.None;
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            Background = Brushes.Transparent;

            var titleText = new TextBlock
            {
                Text = "Дата и время",
                FontSize = 15,
                FontWeight = FontWeight.SemiBold
            };
            BindResource(titleText, TextBlock.ForegroundProperty, "TextPrimaryBrush");

            var promptText = new TextBlock
            {
                Text = "Дата встанет живым полем: при каждом открытии документа она станет сегодняшней.",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            };
            BindResource(promptText, TextBlock.ForegroundProperty, "TextSecondaryBrush");

            var items = new List<string>(choices.Count);
            foreach (var (_, sample) in choices) items.Add(sample);

            _list = new ListBox
            {
                ItemsSource = items,
                SelectedIndex = 0,
                MaxHeight = 300,
                FontSize = 13
            };
            BindResource(_list, ListBox.BackgroundProperty, "BgSurfaceBrush");
            BindResource(_list, ListBox.ForegroundProperty, "TextPrimaryBrush");
            _list.DoubleTapped += (_, _) => CloseWithChoice();

            var cancelButton = BuildButton("Отмена", accent: false);
            cancelButton.Click += (_, _) => Close(null);

            var okButton = BuildButton("Вставить", accent: true);
            okButton.Click += (_, _) => CloseWithChoice();

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 10,
                Margin = new Thickness(0, 4, 0, 0)
            };
            buttons.Children.Add(cancelButton);
            buttons.Children.Add(okButton);

            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(titleText);
            content.Children.Add(promptText);
            content.Children.Add(_list);
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

            AddHandler(KeyDownEvent, OnDialogKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        }

        /// <summary>
        /// Открывает окно поверх главного окна. Возвращает код выбранного поля или null.
        /// </summary>
        public static async Task<string?> ShowAsync(IReadOnlyList<(string Instruction, string Sample)> choices)
        {
            var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            var owner = lifetime?.MainWindow;
            if (owner is null || choices.Count == 0) return null;

            var dialog = new DateTimeFieldDialog(choices);
            return await dialog.ShowDialog<string?>(owner);
        }

        private void CloseWithChoice()
        {
            int index = _list.SelectedIndex;
            if (index < 0 || index >= _choices.Count) return;
            Close(_choices[index].Instruction);
        }

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
            }
            else if (e.Key == Key.Return || e.Key == Key.Enter)
            {
                CloseWithChoice();
                e.Handled = true;
            }
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            _list.Focus();
        }
    }
}
