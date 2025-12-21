using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Writersword.ViewModels;
using System.ComponentModel;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Writersword.Services.Interfaces;
using Writersword.Src.Infrastructure.Dock;

namespace Writersword.Views
{
    /// <summary>
    /// Главное окно приложения
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            // Один обработчик для всей логики закрытия
            Closing += OnClosing;
        }

        /// <summary>Обработчик клика по вкладке</summary>
        private void Tab_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is Border border && border.DataContext is DocumentTabViewModel tab)
            {
                if (DataContext is MainWindowViewModel vm)
                {
                    vm.ActivateTab(tab);
                }
            }
        }

        /// <summary>Обработчик клика по кнопке WorkMode</summary>
        private void WorkModeButton_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is Writersword.Core.Models.WorkModes.WorkMode workMode)
            {
                if (DataContext is MainWindowViewModel vm)
                {
                    vm.SwitchWorkModeCommand.Execute(workMode);
                }
            }
        }

        /// <summary>
        /// Обработчик попытки закрытия главного окна.
        /// Сохраняет открытые вкладки и закрывает все Float окна.
        /// </summary>
        private async void OnClosing(object? sender, CancelEventArgs e)
        {
            System.Console.WriteLine("[MainWindow] OnClosing started");

            if (DataContext is MainWindowViewModel vm)
            {
                System.Console.WriteLine($"[MainWindow] Open tabs count: {vm.OpenTabs.Count}");

                // Сохраняем список открытых проектов перед закрытием
                if (vm.OpenTabs.Count > 0)
                {
                    var settingsService = App.Services.GetRequiredService<ISettingsService>();
                    var openPaths = vm.OpenTabs
                        .Select(t => t.GetModel().FilePath)
                        .Where(p => !string.IsNullOrEmpty(p))
                        .Distinct()
                        .ToList();

                    settingsService.SaveOpenProjects(openPaths!);
                    System.Console.WriteLine($"[MainWindow] Saved {openPaths.Count} open projects");
                }

                // Если нет открытых вкладок - отменяем закрытие и показываем Welcome
                if (vm.OpenTabs.Count == 0)
                {
                    System.Console.WriteLine("[MainWindow] No tabs, cancelling close and showing welcome");
                    e.Cancel = true;
                    await Writersword.App.ShowWelcomeScreen(this);
                    return; // Выходим, не закрываем Float окна
                }
            }

            // ВАЖНО: Закрываем все Float окна только если действительно закрываем приложение
            if (!e.Cancel)
            {
                System.Console.WriteLine("[MainWindow] Closing all Float windows");
                HostWindow.CloseAllWindows();
            }

            System.Console.WriteLine("[MainWindow] OnClosing finished");
        }
    }
}