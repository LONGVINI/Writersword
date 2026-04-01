using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System;

namespace Writersword.Modules.TextEditor.Views.Dialogs
{
    /// <summary>
    /// Диалог ввода одной строки текста.
    /// Возвращает введённый текст (string) или null если пользователь нажал Cancel.
    /// Используется для задания текста меток разрыва и продолжения таблицы.
    /// </summary>
    public partial class InputDialog : Window
    {
        public InputDialog()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Создаёт диалог с заголовком, подсказкой и начальным значением.
        /// </summary>
        /// <param name="title">Заголовок окна и заголовок в содержимом.</param>
        /// <param name="prompt">Описание под заголовком. Null или пустой — не показывается.</param>
        /// <param name="currentValue">Начальное значение TextBox.</param>
        public InputDialog(string title, string? prompt, string currentValue) : this()
        {
            Title = title;

            var titleBlock = this.FindControl<TextBlock>("TitleText")!;
            titleBlock.Text = title;

            var promptBlock = this.FindControl<TextBlock>("PromptText")!;
            if (!string.IsNullOrEmpty(prompt))
            {
                promptBlock.Text = prompt;
                promptBlock.IsVisible = true;
            }

            var inputBox = this.FindControl<TextBox>("InputBox")!;
            inputBox.Text = currentValue;
            inputBox.SelectAll();

            var okBtn = this.FindControl<Button>("OkBtn")!;
            var cancelBtn = this.FindControl<Button>("CancelBtn")!;

            okBtn.Click += OkBtn_Click;
            cancelBtn.Click += CancelBtn_Click;

            inputBox.KeyDown += InputBox_KeyDown;
        }

        private void OkBtn_Click(object? sender, RoutedEventArgs e)
        {
            var inputBox = this.FindControl<TextBox>("InputBox")!;
            Close(inputBox.Text);
        }

        private void CancelBtn_Click(object? sender, RoutedEventArgs e)
        {
            Close(null);
        }

        private void InputBox_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return || e.Key == Key.Enter)
            {
                var inputBox = this.FindControl<TextBox>("InputBox")!;
                Close(inputBox.Text);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Close(null);
                e.Handled = true;
            }
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            var inputBox = this.FindControl<TextBox>("InputBox");
            inputBox?.Focus();
        }
    }
}