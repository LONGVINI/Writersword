using Avalonia.Controls;
using Avalonia.Media;
using Writersword.Resources.Localization;

namespace Writersword.Views
{
    public enum MessageBoxType
    {
        Info,
        Warning,
        Error,
        Question
    }

    public enum MessageBoxButtons
    {
        OK,
        OKCancel,
        YesNo,
        YesNoCancel
    }

    public enum MessageBoxResult
    {
        None,
        OK,
        Cancel,
        Yes,
        No
    }

    public partial class MessageBoxView : Window
    {
        public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

        public MessageBoxView()
        {
            InitializeComponent();
        }

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
            }

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
            }
        }

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