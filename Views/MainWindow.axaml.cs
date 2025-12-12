using Avalonia.Controls;
using Avalonia.Input;
using Writersword.ViewModels;
using System.ComponentModel;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Writersword.Services.Interfaces;

namespace Writersword.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            // Обработчик попытки закрытия окна
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

        /// <summary>Обработчик попытки закрытия главного окна</summary>
        private async void OnClosing(object? sender, CancelEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                System.Console.WriteLine($"[MainWindow.OnClosing] Tabs count: {vm.OpenTabs.Count}");

                // НОВОЕ: Сохраняем список открытых проектов перед закрытием
                if (vm.OpenTabs.Count > 0)
                {
                    var settingsService = App.Services.GetRequiredService<ISettingsService>();
                    var openPaths = vm.OpenTabs
                        .Select(t => t.GetModel().FilePath)
                        .Where(p => !string.IsNullOrEmpty(p))
                        .Distinct()
                        .ToList();

                    settingsService.SaveOpenProjects(openPaths!);
                    System.Console.WriteLine($"[MainWindow.OnClosing] Saved {openPaths.Count} open projects");
                }

                // Если нет открытых вкладок - отменяем закрытие и показываем Welcome
                if (vm.OpenTabs.Count == 0)
                {
                    System.Console.WriteLine("[MainWindow.OnClosing] No tabs, cancelling close and showing welcome");
                    e.Cancel = true;
                    await Writersword.App.ShowWelcomeScreen(this);
                }
            }
        }
    }
}