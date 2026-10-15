using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Writersword.Modules.TextEditor.Views.Dialogs
{
    /// <summary>
    /// Пароль защиты записи исправлений.
    ///
    /// При установке защиты — пароль и его повтор; пустой пароль ставит защиту без
    /// пароля, как у Word. При снятии — одно поле. Возвращает введённый пароль
    /// (пустая строка — без пароля) или null, если окно закрыли отменой.
    /// </summary>
    public partial class RevisionLockDialog : Window
    {
        private readonly bool _protect;

        public RevisionLockDialog()
        {
            InitializeComponent();
        }

        /// <param name="protect">True — установка защиты, false — снятие.</param>
        public RevisionLockDialog(bool protect) : this()
        {
            _protect = protect;

            var title = this.FindControl<TextBlock>("TitleText")!;
            var prompt = this.FindControl<TextBlock>("PromptText")!;
            var repeatPanel = this.FindControl<StackPanel>("RepeatPanel")!;
            var okBtn = this.FindControl<Button>("OkBtn")!;
            var cancelBtn = this.FindControl<Button>("CancelBtn")!;
            var passwordBox = this.FindControl<TextBox>("PasswordBox")!;
            var repeatBox = this.FindControl<TextBox>("RepeatBox")!;

            if (protect)
            {
                Title = "Защитить исправления";
                title.Text = "Защитить исправления";
                prompt.Text = "Пока защита стоит, любая правка записывается исправлением: выключить запись, принять " +
                              "или отклонить правки нельзя. Отдайте документ так — и каждая правка вернётся видимой.\n\n" +
                              "Пароль необязателен. Без пароля защиту снимет любой — это защита от «забыл включить " +
                              "запись», а не от взлома. С паролем защиту снимет только знающий его, в Word тоже.";
                okBtn.Content = "Защитить";
            }
            else
            {
                Title = "Снять защиту";
                title.Text = "Снять защиту исправлений";
                prompt.Text = "Документ защищён паролем. Введите его, чтобы снова принимать и отклонять правки.";
                okBtn.Content = "Снять защиту";
                repeatPanel.IsVisible = false;
            }

            okBtn.Click += OkBtn_Click;
            cancelBtn.Click += CancelBtn_Click;
            passwordBox.KeyDown += Box_KeyDown;
            repeatBox.KeyDown += Box_KeyDown;
        }

        private void OkBtn_Click(object? sender, RoutedEventArgs e) => Confirm();

        private void CancelBtn_Click(object? sender, RoutedEventArgs e) => Close(null);

        private void Box_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return || e.Key == Key.Enter)
            {
                Confirm();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Close(null);
                e.Handled = true;
            }
        }

        private void Confirm()
        {
            string password = this.FindControl<TextBox>("PasswordBox")!.Text ?? string.Empty;

            if (_protect)
            {
                string repeat = this.FindControl<TextBox>("RepeatBox")!.Text ?? string.Empty;
                if (!string.Equals(password, repeat, StringComparison.Ordinal))
                {
                    var error = this.FindControl<TextBlock>("ErrorText")!;
                    error.Text = "Пароли не совпадают";
                    error.IsVisible = true;
                    this.FindControl<TextBox>("RepeatBox")!.Focus();
                    return;
                }
            }

            Close(password);
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            this.FindControl<TextBox>("PasswordBox")?.Focus();
        }
    }
}
