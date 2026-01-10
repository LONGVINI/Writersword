using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Writersword.Src.Core.Interfaces.Services.Input;
using Writersword.Src.Core.Interfaces.Services.Storage;
using Writersword.Src.Core.Interfaces.Services.UI;
using Writersword.Src.Core.Interfaces.WorkFlows;
using Writersword.Src.Infrastructure.Dock;
using Writersword.ViewModels;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace Writersword.Views
{
    /// <summary>
    /// Главное окно приложения
    /// </summary>
    public partial class MainWindow : Window
    {
        private bool _isClosing = false; // Флаг защиты от рекурсии

        public MainWindow()
        {
            InitializeComponent();

            // Один обработчик для всей логики закрытия
            Closing += OnClosing;

            KeyDown += OnKeyDown;
        }

        /// <summary>
        /// Обработчик попытки закрытия главного окна.
        /// Проверяет несохранённые изменения в каждой вкладке и предлагает сохранить.
        /// </summary>
        private async void OnClosing(object? sender, CancelEventArgs e)
        {
            // КРИТИЧЕСКИ ВАЖНО: Защита от рекурсии!
            // Shutdown() вызывает OnClosing снова - без флага будет Stack Overflow
            if (_isClosing)
            {
                e.Cancel = false; // Разрешаем закрытие
                return;
            }

            e.Cancel = true; // Останавливаем стандартное закрытие
            _isClosing = true; // Устанавливаем флаг

            System.Console.WriteLine("[MainWindow] OnClosing started");

            if (DataContext is not MainWindowViewModel vm)
            {
                if (Application.Current?.ApplicationLifetime
                    is IClassicDesktopStyleApplicationLifetime lifetime)
                {
                    lifetime.Shutdown(0);
                }
                return;
            }

            var tabCollection = App.Services.GetRequiredService<ITabCollection>();
            var projectWorkflow = App.Services.GetRequiredService<IProjectWorkflow>();
            var dialogService = App.Services.GetRequiredService<IDialogService>();

            System.Console.WriteLine($"[MainWindow] Open tabs count: {tabCollection.Tabs.Count}");

            // 1. Если нет вкладок - показываем Welcome
            if (tabCollection.Tabs.Count == 0)
            {
                System.Console.WriteLine("[MainWindow] No tabs, showing welcome");
                _isClosing = false; // Сбрасываем флаг
                await Writersword.App.ShowWelcomeScreen(this);
                return; // НЕ закрывать приложение
            }

            // 2. Сохраняем список открытых проектов (для восстановления при следующем запуске)
            var settingsService = App.Services.GetRequiredService<ISettingsService>();
            var openPaths = tabCollection.Tabs
                .Select(t => t.FilePath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct()
                .ToList();
            settingsService.SaveOpenProjects(openPaths!);
            System.Console.WriteLine($"[MainWindow] Saved {openPaths.Count} open projects");

            // 3. Проверяем каждую вкладку на несохранённые изменения
            var tabs = tabCollection.Tabs.ToList(); // Копия списка

            foreach (var tab in tabs)
            {
                // Пропускаем вкладки без изменений
                if (!await projectWorkflow.HasUnsavedChanges(tab))
                {
                    System.Console.WriteLine($"[MainWindow] Tab {tab.Title} - no changes");
                    continue;
                }

                System.Console.WriteLine($"[MainWindow] Tab {tab.Title} has unsaved changes");

                // Показываем диалог для КАЖДОЙ несохранённой вкладки
                var result = await dialogService.ShowMessageAsync(
                    "Несохранённые изменения",
                    $"Документ \"{tab.Title}\" содержит несохранённые изменения.\n\nСохранить перед закрытием?",
                    MessageBoxType.Question,
                    MessageBoxButtons.YesNoCancel
                );

                System.Console.WriteLine($"[MainWindow] User choice for {tab.Title}: {result}");

                if (result == MessageBoxResult.Cancel)
                {
                    System.Console.WriteLine("[MainWindow] Closing cancelled by user");
                    _isClosing = false; // Сбрасываем флаг
                    return; // STOP - не закрываем приложение
                }

                if (result == MessageBoxResult.Yes)
                {
                    // Сохраняем вкладку
                    System.Console.WriteLine($"[MainWindow] Saving tab: {tab.Title}");
                    bool saved = await projectWorkflow.SaveDocumentAsync(tab);

                    if (!saved)
                    {
                        System.Console.WriteLine($"[MainWindow] Save failed for {tab.Title}");
                        _isClosing = false; // Сбрасываем флаг
                        return; // STOP - не закрываем приложение
                    }

                    System.Console.WriteLine($"[MainWindow] Tab saved: {tab.Title}");
                }
                // Если "Нет" - просто продолжаем дальше
            }

            // 4. Останавливаем кеширование для всех вкладок
            foreach (var tab in tabCollection.Tabs)
            {
                tab.StopCaching();
            }

            // 5. Закрываем все Float окна
            System.Console.WriteLine("[MainWindow] Closing all Float windows");
            HostWindow.CloseAllWindows();

            System.Console.WriteLine("[MainWindow] OnClosing finished - shutting down");

            // 6. Закрываем приложение
            // Флаг уже установлен, Shutdown() вызовет OnClosing снова, но мы выйдем сразу
            if (Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown(0);
            }
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