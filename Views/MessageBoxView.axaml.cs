using Avalonia.Controls;
using Avalonia.Media;
using System;
using Writersword.Core.Enums;
using Writersword.Resources.Localization;

namespace Writersword.Views
{
    public enum MessageBoxType
    {
        Info,
        Warning,
        Error,
        Question,
        Recovery // Новый тип для диалога восстановления
    }

    public enum MessageBoxButtons
    {
        OK,
        OKCancel,
        YesNo,
        YesNoCancel,
        Recovery // Новый набор кнопок для восстановления
    }

    public enum MessageBoxResult
    {
        None,
        OK,
        Cancel,
        Yes,
        No,
        // Новые результаты для Recovery диалога
        Restore,
        OpenSaved,
        Compare
    }

    public partial class MessageBoxView : Window
    {
        public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

        public MessageBoxView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Стандартный конструктор MessageBox
        /// </summary>
        public MessageBoxView(
            string title,
            string message,
            MessageBoxType type = MessageBoxType.Info,
            MessageBoxButtons buttons = MessageBoxButtons.OK) : this()
        {
            System.Console.WriteLine($"[MessageBoxView] Creating with message: '{message}'");
            System.Console.WriteLine($"[MessageBoxView] Message length: {message.Length}");

            this.FindControl<TextBlock>("TitleText")!.Text = title;

            var messageTextBlock = this.FindControl<TextBlock>("MessageText")!;
            messageTextBlock.Text = message;

            System.Console.WriteLine($"[MessageBoxView] TextBlock.Text: '{messageTextBlock.Text}'");
            System.Console.WriteLine($"[MessageBoxView] TextBlock.MaxWidth: {messageTextBlock.MaxWidth}");
            System.Console.WriteLine($"[MessageBoxView] TextBlock.MaxHeight: {messageTextBlock.MaxHeight}");

            Title = title;

            // Скрываем панель дат (для обычных MessageBox)
            var datesPanel = this.FindControl<StackPanel>("DatesPanel");
            if (datesPanel != null)
                datesPanel.IsVisible = false;

            SetupIcon(type);
            SetupButtons(buttons);
        }

        /// <summary>
        /// Конструктор для Recovery диалога с датами
        /// </summary>
        public MessageBoxView(
            string title,
            string message,
            DateTime cacheDate,
            DateTime saveDate) : this()
        {
            System.Console.WriteLine($"[MessageBoxView] Creating Recovery dialog");

            this.FindControl<TextBlock>("TitleText")!.Text = title;
            this.FindControl<TextBlock>("MessageText")!.Text = message;

            Title = title;

            // Показываем панель дат
            var datesPanel = this.FindControl<StackPanel>("DatesPanel");
            if (datesPanel != null)
            {
                datesPanel.IsVisible = true;

                // Устанавливаем даты
                var cacheDateText = this.FindControl<TextBlock>("CacheDateText");
                var saveDateText = this.FindControl<TextBlock>("SaveDateText");

                if (cacheDateText != null)
                    cacheDateText.Text = $"{Strings.MessageBox_Recovery_CacheDate} {cacheDate:HH:mm:ss}";

                if (saveDateText != null)
                    saveDateText.Text = $"{Strings.MessageBox_Recovery_SaveDate} {saveDate:HH:mm:ss}";
            }

            SetupIcon(MessageBoxType.Recovery);
            SetupButtons(MessageBoxButtons.Recovery);
        }

        /// <summary>Настроить иконку</summary>
        private void SetupIcon(MessageBoxType type)
        {
            var iconText = this.FindControl<TextBlock>("IconText")!;
            switch (type)
            {
                case MessageBoxType.Info:
                    iconText.Text = "ℹ";
                    iconText.Foreground = new SolidColorBrush(Color.Parse("#007ACC"));
                    break;
                case MessageBoxType.Warning:
                    iconText.Text = "⚠";
                    iconText.Foreground = new SolidColorBrush(Color.Parse("#FFA500"));
                    break;
                case MessageBoxType.Error:
                    iconText.Text = "❌";
                    iconText.Foreground = new SolidColorBrush(Color.Parse("#DC3545"));
                    break;
                case MessageBoxType.Question:
                    iconText.Text = "❓";
                    iconText.Foreground = new SolidColorBrush(Color.Parse("#17A2B8"));
                    break;
                case MessageBoxType.Recovery:
                    iconText.Text = "⚠";
                    iconText.Foreground = new SolidColorBrush(Color.Parse("#FFA500"));
                    break;
            }
        }

        /// <summary>Настроить кнопки</summary>
        private void SetupButtons(MessageBoxButtons buttons)
        {
            var buttonsPanel = this.FindControl<StackPanel>("ButtonsPanel")!;

            switch (buttons)
            {
                case MessageBoxButtons.OK:
                    buttonsPanel.Children.Add(CreateButton(Strings.MessageBox_OK, MessageBoxResult.OK, true));
                    break;
                case MessageBoxButtons.OKCancel:
                    buttonsPanel.Children.Add(CreateButton(Strings.MessageBox_OK, MessageBoxResult.OK, true));
                    buttonsPanel.Children.Add(CreateButton(Strings.MessageBox_Cancel, MessageBoxResult.Cancel, false));
                    break;
                case MessageBoxButtons.YesNo:
                    buttonsPanel.Children.Add(CreateButton(Strings.MessageBox_Yes, MessageBoxResult.Yes, true));
                    buttonsPanel.Children.Add(CreateButton(Strings.MessageBox_No, MessageBoxResult.No, false));
                    break;
                case MessageBoxButtons.YesNoCancel:
                    buttonsPanel.Children.Add(CreateButton(Strings.MessageBox_Yes, MessageBoxResult.Yes, true));
                    buttonsPanel.Children.Add(CreateButton(Strings.MessageBox_No, MessageBoxResult.No, false));
                    buttonsPanel.Children.Add(CreateButton(Strings.MessageBox_Cancel, MessageBoxResult.Cancel, false));
                    break;
                case MessageBoxButtons.Recovery:
                    buttonsPanel.Children.Add(CreateButton(Strings.MessageBox_Restore, MessageBoxResult.Restore, true));
                    buttonsPanel.Children.Add(CreateButton(Strings.MessageBox_OpenSaved, MessageBoxResult.OpenSaved, false));
                    buttonsPanel.Children.Add(CreateButton(Strings.MessageBox_Compare, MessageBoxResult.Compare, false));
                    buttonsPanel.Children.Add(CreateButton(Strings.MessageBox_Cancel, MessageBoxResult.Cancel, false));
                    break;
            }
        }

        /// <summary>Создать кнопку</summary>
        private Button CreateButton(string content, MessageBoxResult result, bool isPrimary)
        {
            var button = new Button
            {
                Content = content,
                Padding = new Avalonia.Thickness(30, 10),
                FontSize = 14,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
            };

            if (isPrimary)
            {
                button.Background = new SolidColorBrush(Color.Parse("#007ACC"));
                button.Foreground = Brushes.White;
            }
            else
            {
                button.Background = new SolidColorBrush(Color.Parse("#3E3E42"));
                button.Foreground = new SolidColorBrush(Color.Parse("#CCC"));
            }

            button.Click += (s, e) =>
            {
                Result = result;
                Close();
            };

            return button;
        }
    }
}