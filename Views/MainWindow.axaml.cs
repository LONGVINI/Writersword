using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Writersword.ViewModels;
using System.ComponentModel;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Writersword.Src.Infrastructure.Dock;
using Writersword.Src.Core.Interfaces.Services.Storage;
using Writersword.Src.Core.Interfaces.Services.Input;
using Writersword.Src.Core.Interfaces.WorkFlows;

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

            KeyDown += OnKeyDown;
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
                var tabCollection = App.Services.GetRequiredService<ITabCollection>();

                System.Console.WriteLine($"[MainWindow] Open tabs count: {tabCollection.Tabs.Count}");

                // Сохраняем список открытых проектов перед закрытием
                if (tabCollection.Tabs.Count > 0)
                {
                    var settingsService = App.Services.GetRequiredService<ISettingsService>();
                    var openPaths = tabCollection.Tabs
                        .Select(t => t.FilePath)
                        .Where(p => !string.IsNullOrEmpty(p))
                        .Distinct()
                        .ToList();

                    settingsService.SaveOpenProjects(openPaths!);
                    System.Console.WriteLine($"[MainWindow] Saved {openPaths.Count} open projects");
                }

                // Если нет открытых вкладок - отменяем закрытие и показываем Welcome
                if (tabCollection.Tabs.Count == 0)
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

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            var hotKeyService = App.Services.GetRequiredService<IHotKeyService>();
            var gesture = new KeyGesture(e.Key, e.KeyModifiers);

            if (hotKeyService.HandleKeyPress(gesture))
            {
                e.Handled = true;
            }
        }
    }
}