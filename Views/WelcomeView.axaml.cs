using Avalonia.Controls;
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
    }
}