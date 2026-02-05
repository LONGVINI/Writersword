using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using Writersword.Core.Models.Project;
using Writersword.ViewModels;
using Writersword.Src.Core.Interfaces.WorkFlows;

namespace Writersword.Views
{
    /// <summary>
    /// Окно приветствия (Welcome screen)
    /// Показывается при первом запуске или когда нет открытых проектов
    /// </summary>
    public partial class WelcomeView : Window
    {
        private readonly ILogger<WelcomeView> _logger;

        public WelcomeView()
        {
            _logger = App.Services.GetService<ILogger<WelcomeView>>()!;

            InitializeComponent();

            _logger.LogDebug("WelcomeView created");

            // Подписываемся на событие выбора проекта
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object? sender, System.EventArgs e)
        {
            if (DataContext is WelcomeViewModel viewModel)
            {
                // Когда проект выбран - закрываем окно
                viewModel.ProjectSelected += () =>
                {
                    _logger.LogDebug("Project selected, closing WelcomeView");
                    Close();
                };
            }
        }

        /// <summary>Обработчик кнопки закрытия окна</summary>
        private void CloseButton_Click(object? sender, RoutedEventArgs e)
        {
            // Получаем ITabCollection для проверки количества открытых вкладок
            var tabCollection = App.Services.GetRequiredService<ITabCollection>();

            if (tabCollection.Tabs.Count > 0)
            {
                // Есть открытые вкладки - просто закрываем Welcome окно
                _logger.LogDebug("CloseButton clicked - has open tabs, closing welcome window");
                Close();
            }
            else
            {
                // Нет открытых вкладок - закрываем всю программу
                _logger.LogInformation("CloseButton clicked - no open tabs, closing application");

                // Закрываем главное окно, что приведёт к закрытию приложения
                if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.Shutdown();
                }
            }
        }

        /// <summary>Обработчик клика по недавнему проекту</summary>
        private async void RecentProject_Click(object? sender, PointerPressedEventArgs e)
        {
            if (sender is Border border && border.DataContext is RecentProject recentProject)
            {
                if (DataContext is WelcomeViewModel viewModel)
                {
                    _logger.LogDebug("RecentProject clicked: {ProjectName}", recentProject.Name);
                    await viewModel.OpenRecentProjectDirect(recentProject);
                }
            }
        }
    }
}