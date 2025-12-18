using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using System;
using Writersword.Core.Models.Project;
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
            // Получаем MainWindowViewModel для проверки количества открытых вкладок
            var mainViewModel = App.Services.GetRequiredService<MainWindowViewModel>();

            if (mainViewModel.OpenTabs.Count > 0)
            {
                // Есть открытые вкладки - просто закрываем Welcome окно
                Console.WriteLine("[CloseButton_Click] Has open tabs, closing welcome window");
                Close();
            }
            else
            {
                // Нет открытых вкладок - закрываем всю программу
                Console.WriteLine("[CloseButton_Click] No open tabs, closing application");

                // Закрываем главное окно, что приведёт к закрытию приложения
                if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.Shutdown();
                }
            }
        }

        /// <summary>Обработчик клика по недавнему проекту</summary>
        private void RecentProject_Click(object? sender, PointerPressedEventArgs e)
        {
            if (sender is Border border && border.DataContext is RecentProject recentProject)
            {
                if (DataContext is WelcomeViewModel viewModel)
                {
                    Console.WriteLine($"[RecentProject_Click] Clicked on: {recentProject.Name}");
                    viewModel.OpenRecentProjectDirect(recentProject);
                }
            }
        }
    }
}