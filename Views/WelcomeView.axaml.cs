using Avalonia.Controls;
using Avalonia.Interactivity;
using Writersword.ViewModels;

namespace Writersword.Views
{
    public partial class WelcomeView : Window
    {
        public WelcomeView()
        {
            InitializeComponent();

            // Подписываемся на событие выбора проекта
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object? sender, System.EventArgs e)
        {
            if (DataContext is WelcomeViewModel viewModel)
            {
                // Когда проект выбран - закрываем окно
                viewModel.ProjectSelected += () => Close();
            }
        }

        /// <summary>Обработчик кнопки закрытия окна</summary>
        private void CloseButton_Click(object? sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}