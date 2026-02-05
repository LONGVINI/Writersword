using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Writersword.Core.Interfaces.Services;
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
        private readonly ILogger<MainWindow> _logger;
        private bool _isClosing = false; // Флаг защиты от рекурсии

        public MainWindow()
        {
            _logger = App.Services.GetService<ILogger<MainWindow>>()!;

            InitializeComponent();

            this.Opened += (s, e) =>
            {
                _logger.LogDebug("MainWindow opened - DataContext: {DataContextType}", DataContext?.GetType().Name);

                if (DataContext is MainWindowViewModel vm)
                {
                    _logger.LogDebug("MenuBar: {MenuBarType}", vm.MenuBar?.GetType().Name);
                }
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

            _logger.LogDebug("OnClosing started");

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

            _logger.LogDebug("Open tabs count: {Count}", tabCollection.Tabs.Count);

            // 1. Если нет вкладок - показываем Welcome
            if (tabCollection.Tabs.Count == 0)
            {
                _logger.LogDebug("No tabs, showing welcome");
                _isClosing = false; // Сбрасываем флаг
                await App.ShowWelcomeScreen(this);
                return; // НЕ закрывать приложение
            }

            // 1.5. ПРИНУДИТЕЛЬНО сохраняем workspace.json для ВСЕХ открытых вкладок
            _logger.LogDebug("Saving workspace configurations for all tabs");
            await vm.SaveActiveWorkspaceConfigurationAsync();

            // 1.6. ПРИНУДИТЕЛЬНО сохраняем активную вкладку в кеш перед проверкой изменений
            // Это необходимо потому что CacheUpdateService работает раз в 10 секунд
            // и может не успеть сохранить изменения если пользователь быстро закрыл приложение
            _logger.LogDebug("Force saving active tab to cache");
            var activeTab = tabCollection.ActiveTab;
            if (activeTab != null && !string.IsNullOrEmpty(activeTab.FilePath))
            {
                try
                {
                    var stateCollector = App.Services.GetRequiredService<IModuleStateCollectorService>();
                    var cacheService = App.Services.GetRequiredService<IZipCacheService>();

                    // Получаем активные модули текущего WorkMode
                    var activeModules = vm.GetActiveModules();

                    // Собираем CustomData и SessionData
                    var (customData, sessionData) = stateCollector.CollectAllData(activeModules);

                    if (customData.Count > 0)
                    {
                        // Получаем ProjectId для кеша
                        var project = activeTab.GetProject();

                        // Сохраняем кеш принудительно
                        await cacheService.SaveCacheAsync(activeTab.FilePath, project.Id, customData, sessionData);

                        // Отмечаем вкладку как изменённую (для правильной работы HasUnsavedChanges)
                        activeTab.MarkAsModified();

                        _logger.LogDebug("Active tab cached: {Count} modules", customData.Count);
                    }
                    else
                    {
                        _logger.LogDebug("Active tab has no data to cache");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error caching active tab");
                }
            }

            // 2. Сохраняем список открытых проектов (для восстановления при следующем запуске)
            var settingsService = App.Services.GetRequiredService<ISettingsService>();
            var openPaths = tabCollection.Tabs
                .Select(t => t.FilePath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct()
                .ToList();
            settingsService.SaveOpenProjects(openPaths!);
            _logger.LogDebug("Saved {Count} open projects", openPaths.Count);

            // 3. Проверяем каждую вкладку на несохранённые изменения
            var tabs = tabCollection.Tabs.ToList(); // Копия списка

            foreach (var tab in tabs)
            {
                // Пропускаем вкладки без изменений
                if (!await projectWorkflow.HasUnsavedChanges(tab))
                {
                    _logger.LogDebug("Tab {Title} - no changes", tab.Title);
                    continue;
                }

                _logger.LogDebug("Tab {Title} has unsaved changes", tab.Title);

                // Показываем диалог для КАЖДОЙ несохранённой вкладки
                var result = await dialogService.ShowMessageAsync(
                    "Несохранённые изменения",
                    $"Документ \"{tab.Title}\" содержит несохранённые изменения.\n\nСохранить перед закрытием?",
                    MessageBoxType.Question,
                    MessageBoxButtons.YesNoCancel
                );

                _logger.LogDebug("User choice for {Title}: {Result}", tab.Title, result);

                if (result == MessageBoxResult.Cancel)
                {
                    _logger.LogDebug("Closing cancelled by user");
                    _isClosing = false; // Сбрасываем флаг
                    return; // STOP - не закрываем приложение
                }

                if (result == MessageBoxResult.Yes)
                {
                    // Сохраняем вкладку
                    _logger.LogDebug("Saving tab: {Title}", tab.Title);
                    bool saved = await projectWorkflow.SaveDocumentAsync(tab);

                    if (!saved)
                    {
                        _logger.LogWarning("Save failed for {Title}", tab.Title);
                        _isClosing = false; // Сбрасываем флаг
                        return; // STOP - не закрываем приложение
                    }

                    _logger.LogDebug("Tab saved: {Title}", tab.Title);
                }
                else if (result == MessageBoxResult.No)
                {
                    // Пользователь выбрал "НЕ СОХРАНЯТЬ" - удаляем кеш
                    if (!string.IsNullOrEmpty(tab.FilePath))
                    {
                        var cacheService = App.Services.GetRequiredService<IZipCacheService>();
                        cacheService.DeleteCache(tab.FilePath);
                        _logger.LogDebug("Cache deleted for {Title}", tab.Title);
                    }
                }
            }

            _logger.LogInformation("OnClosing finished - shutting down");

            // 5. Закрываем приложение
            // Флаг уже установлен, Shutdown() вызовет OnClosing снова, но мы выйдем сразу
            if (Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown(0);
            }
        }

        /// <summary>
        /// Обработчик нажатия клавиш для горячих клавиш
        /// </summary>
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