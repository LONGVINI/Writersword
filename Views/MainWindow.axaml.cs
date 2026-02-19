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
        private bool _isClosing = false;

        public MainWindow()
        {
            _logger = App.Services.GetService<ILogger<MainWindow>>()!;

            InitializeComponent();

            this.Opened += (s, e) =>
            {
                _logger.LogDebug("MainWindow opened - DataContext: {DataContextType}", DataContext?.GetType().Name);
            };

            Closing += OnClosing;
            KeyDown += OnKeyDown;
        }

        /// <summary>
        /// Обработчик попытки закрытия главного окна
        /// Проверяет несохранённые изменения в каждой вкладке и предлагает сохранить
        /// </summary>
        private async void OnClosing(object? sender, CancelEventArgs e)
        {
            if (_isClosing)
            {
                e.Cancel = false;
                return;
            }

            e.Cancel = true;
            _isClosing = true;

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

            if (tabCollection.Tabs.Count == 0)
            {
                _logger.LogDebug("No tabs, showing welcome");
                _isClosing = false;
                await App.ShowWelcomeScreen(this);
                return;
            }

            var activeTab = tabCollection.ActiveTab;
            if (activeTab != null && !string.IsNullOrEmpty(activeTab.FilePath))
            {
                try
                {
                    var stateCollector = App.Services.GetRequiredService<IModuleStateCollectorService>();
                    var cacheService = App.Services.GetRequiredService<IZipCacheService>();

                    var activeModules = vm.GetActiveModules();
                    var (customData, sessionData) = stateCollector.CollectAllData(activeModules);

                    if (customData.Count > 0)
                    {
                        var project = activeTab.GetProject();
                        await cacheService.SaveCacheAsync(activeTab.FilePath, project.Id, customData, sessionData);
                        activeTab.MarkAsModified();
                        _logger.LogDebug("Active tab cached: {Count} modules", customData.Count);
                    }

                    if (activeTab.Workspace != null)
                    {
                        await activeTab.Workspace.SaveWorkspaceAsync();
                        _logger.LogDebug("Workspace saved for: {Title}", activeTab.Title);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error caching active tab");
                }
            }

            var settingsService = App.Services.GetRequiredService<ISettingsService>();
            var openPaths = tabCollection.Tabs
                .Select(t => t.FilePath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct()
                .ToList();
            settingsService.SaveOpenProjects(openPaths!);
            _logger.LogDebug("Saved {Count} open projects", openPaths.Count);

            var tabs = tabCollection.Tabs.ToList();

            foreach (var tab in tabs)
            {
                if (!await projectWorkflow.HasUnsavedChanges(tab))
                {
                    _logger.LogDebug("Tab {Title} - no changes", tab.Title);
                    continue;
                }

                _logger.LogDebug("Tab {Title} has unsaved changes", tab.Title);

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
                    _isClosing = false;
                    return;
                }

                if (result == MessageBoxResult.Yes)
                {
                    bool saved = await projectWorkflow.SaveDocumentAsync(tab);
                    if (!saved)
                    {
                        _logger.LogWarning("Save failed for {Title}", tab.Title);
                        _isClosing = false;
                        return;
                    }
                    _logger.LogDebug("Tab saved: {Title}", tab.Title);
                }
                else if (result == MessageBoxResult.No)
                {
                    if (!string.IsNullOrEmpty(tab.FilePath))
                    {
                        var cacheService = App.Services.GetRequiredService<IZipCacheService>();
                        cacheService.DeleteCache(tab.FilePath);
                        _logger.LogDebug("Cache deleted for: {Title}", tab.Title);
                    }
                }
            }

            _logger.LogInformation("OnClosing finished - shutting down");

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
                e.Handled = true;
        }
    }
}