using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Writersword.Src.Core.Interfaces.Services.Input;
using Writersword.Src.Core.Interfaces.Services.Storage;
using Writersword.Src.Core.Interfaces.Services.UI;
using Writersword.Src.Core.Interfaces.WorkFlows;
using Writersword.Src.Infrastructure.Dock;
using Writersword.ViewModels;

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

            this.Opened += (s, e) =>
            {
                Console.WriteLine("===========================================");
                Console.WriteLine($"[MainWindow.Opened] DataContext: {DataContext}");
                Console.WriteLine($"[MainWindow.Opened] DataContext type: {DataContext?.GetType().Name}");

                if (DataContext is MainWindowViewModel vm)
                {
                    Console.WriteLine($"[MainWindow.Opened] MenuBar: {vm.MenuBar}");
                    Console.WriteLine($"[MainWindow.Opened] MenuBar type: {vm.MenuBar?.GetType().Name}");
                }
                Console.WriteLine("===========================================");
            };

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
            // Защита от рекурсии
            if (_isClosing)
            {
                e.Cancel = false; // Разрешаем закрытие
                return;
            }

            e.Cancel = true; // Останавливаем стандартное закрытие
            _isClosing = true; // Устанавливаем флаг

            Console.WriteLine("[MainWindow] OnClosing started");

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

            Console.WriteLine($"[MainWindow] Open tabs count: {tabCollection.Tabs.Count}");

            // 1. Если нет вкладок - показываем Welcome
            if (tabCollection.Tabs.Count == 0)
            {
                Console.WriteLine("[MainWindow] No tabs, showing welcome");
                _isClosing = false; // Сбрасываем флаг
                await App.ShowWelcomeScreen(this);
                return; // НЕ закрывать приложение
            }

            // 1.5. ПРИНУДИТЕЛЬНО сохраняем workspace.json для ВСЕХ открытых вкладок
            Console.WriteLine("[MainWindow] Saving workspace configurations for all tabs");
            await vm.SaveActiveWorkspaceConfigurationAsync();

            // 2. Сохраняем список открытых проектов (для восстановления при следующем запуске)
            var settingsService = App.Services.GetRequiredService<ISettingsService>();
            var openPaths = tabCollection.Tabs
                .Select(t => t.FilePath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct()
                .ToList();
            settingsService.SaveOpenProjects(openPaths!);
            Console.WriteLine($"[MainWindow] Saved {openPaths.Count} open projects");

            // 3. Проверяем каждую вкладку на несохранённые изменения
            var tabs = tabCollection.Tabs.ToList(); // Копия списка

            foreach (var tab in tabs)
            {
                // Пропускаем вкладки без изменений
                if (!await projectWorkflow.HasUnsavedChanges(tab))
                {
                    Console.WriteLine($"[MainWindow] Tab {tab.Title} - no changes");
                    continue;
                }

                Console.WriteLine($"[MainWindow] Tab {tab.Title} has unsaved changes");

                // Показываем диалог для КАЖДОЙ несохранённой вкладки
                var result = await dialogService.ShowMessageAsync(
                    "Несохранённые изменения",
                    $"Документ \"{tab.Title}\" содержит несохранённые изменения.\n\nСохранить перед закрытием?",
                    MessageBoxType.Question,
                    MessageBoxButtons.YesNoCancel
                );

                Console.WriteLine($"[MainWindow] User choice for {tab.Title}: {result}");

                if (result == MessageBoxResult.Cancel)
                {
                    Console.WriteLine("[MainWindow] Closing cancelled by user");
                    _isClosing = false; // Сбрасываем флаг
                    return; // STOP - не закрываем приложение
                }

                if (result == MessageBoxResult.Yes)
                {
                    // Сохраняем вкладку
                    Console.WriteLine($"[MainWindow] Saving tab: {tab.Title}");
                    bool saved = await projectWorkflow.SaveDocumentAsync(tab);

                    if (!saved)
                    {
                        Console.WriteLine($"[MainWindow] Save failed for {tab.Title}");
                        _isClosing = false; // Сбрасываем флаг
                        return; // STOP - не закрываем приложение
                    }

                    Console.WriteLine($"[MainWindow] Tab saved: {tab.Title}");
                }
                // Если "Нет" - просто продолжаем дальше
            }

            Console.WriteLine("[MainWindow] OnClosing finished - shutting down");

            // 5. Закрываем приложение
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