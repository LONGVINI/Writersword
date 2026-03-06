using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Writersword.Src.Core.Interfaces.Services.Storage;
using Writersword.ViewModels.Components;
using Writersword.ViewModels.Settings;
using Writersword.Views.Settings;

namespace Writersword.ViewModels.Components.MenuBar
{
    public partial class MenuBarViewModel
    {
        private async void NewProject()
        {
            _logger.LogDebug("NewProject clicked");

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow != null)
            {
                await App.ShowWelcomeScreen(desktop.MainWindow);
            }
        }

        private async Task OpenProject()
        {
            _logger.LogDebug("OpenProject clicked");

            var tab = await _projectWorkflow.OpenDocumentAsync();
            if (tab == null) return;

            var existing = _tabCollection.FindByPath(tab.FilePath);
            if (existing != null)
            {
                _tabCollection.ActiveTab = existing;
                return;
            }

            _tabCollection.Add(tab);
            _tabCollection.ActiveTab = tab;
            LoadRecentProjects();
        }

        private async Task OpenRecentProject(string filePath)
        {
            _logger.LogDebug("Opening recent project: {FilePath}", filePath);

            if (!File.Exists(filePath))
            {
                _logger.LogWarning("File not found: {FilePath}", filePath);
                var item = RecentProjects.FirstOrDefault(r => r.FilePath == filePath);
                if (item != null) RecentProjects.Remove(item);
                return;
            }

            var existing = _tabCollection.FindByPath(filePath);
            if (existing != null)
            {
                _tabCollection.ActiveTab = existing;
                return;
            }

            var tab = await _projectWorkflow.OpenDocumentAsync(filePath);
            if (tab != null)
            {
                _tabCollection.Add(tab);
                _tabCollection.ActiveTab = tab;
                _settingsService.AddRecentProject(filePath);
                LoadRecentProjects();
            }
        }

        private async Task SaveProject()
        {
            var activeTab = _getActiveTab?.Invoke();
            if (activeTab == null) return;

            _logger.LogDebug("SaveProject: {TabTitle}", activeTab.Title);
            await _projectWorkflow.SaveDocumentAsync(activeTab);
        }

        private async Task SaveAsProject()
        {
            var activeTab = _getActiveTab?.Invoke();
            if (activeTab == null) return;

            _logger.LogDebug("SaveAsProject: {TabTitle}", activeTab.Title);
            await _projectWorkflow.SaveAsDocumentAsync(activeTab);
            LoadRecentProjects();
        }

        private async Task SaveAllProjects()
        {
            _logger.LogDebug("SaveAllProjects called");

            var allTabs = _tabCollection.Tabs;
            if (!allTabs.Any()) return;

            int saved = 0, failed = 0;

            foreach (var tab in allTabs.ToList())
            {
                try
                {
                    if (!await _projectWorkflow.HasUnsavedChanges(tab)) continue;

                    if (await _projectWorkflow.SaveDocumentAsync(tab))
                        saved++;
                    else
                        failed++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error saving tab: {Title}", tab.Title);
                    failed++;
                }
            }

            if (failed > 0)
                _notificationService.ShowError($"Сохранено: {saved}, ошибок: {failed}");
            else if (saved > 0)
                _notificationService.ShowSuccess($"Сохранено проектов: {saved}");
        }

        private async Task CloseTab()
        {
            var activeTab = _getActiveTab?.Invoke();
            if (activeTab == null) return;

            _logger.LogDebug("CloseTab: {Title}", activeTab.Title);
            await SaveWorkspaceBeforeClose(activeTab);

            if (!await _projectWorkflow.CloseDocumentAsync(activeTab)) return;

            activeTab.RecoveryBanner = null;
            _tabCollection.Remove(activeTab);
            await HandleNoTabsLeft();
        }

        private async Task CloseAllTabs()
        {
            _logger.LogDebug("CloseAllTabs called");

            foreach (var tab in _tabCollection.Tabs.ToList())
            {
                await SaveWorkspaceBeforeClose(tab);

                if (!await _projectWorkflow.CloseDocumentAsync(tab)) continue;

                tab.RecoveryBanner = null;
                _tabCollection.Remove(tab);
            }

            await HandleNoTabsLeft();
        }

        private async Task CloseOtherTabs()
        {
            var activeTab = _getActiveTab?.Invoke();
            if (activeTab == null) return;

            _logger.LogDebug("CloseOtherTabs: keeping {Title}", activeTab.Title);

            foreach (var tab in _tabCollection.Tabs.Where(t => t != activeTab).ToList())
            {
                await SaveWorkspaceBeforeClose(tab);

                if (!await _projectWorkflow.CloseDocumentAsync(tab)) continue;

                tab.RecoveryBanner = null;
                _tabCollection.Remove(tab);
            }
        }

        private async Task OpenSettings()
        {
            _logger.LogDebug("OpenSettings called");

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow != null)
            {
                var settingsVM = new SettingsViewModel();
                var settingsView = new SettingsView { DataContext = settingsVM };
                await settingsView.ShowDialog(desktop.MainWindow);
            }
        }

        private void Exit()
        {
            _logger.LogDebug("Exit clicked");

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow != null)
            {
                desktop.MainWindow.Close();
            }
        }

        // ── Вспомогательные ───────────────────────────────────────────────────

        private async Task SaveWorkspaceBeforeClose(DocumentTabViewModel tab)
        {
            if (string.IsNullOrEmpty(tab.FilePath)) return;

            var autoSave = _projectWorkflow.GetAutoSaveServiceForProject(tab.FilePath);
            if (autoSave != null)
                await autoSave.SaveNowAsync();
        }

        private async Task HandleNoTabsLeft()
        {
            if (_tabCollection.Tabs.Count > 0) return;

            _mainViewModelProvider?.Invoke()?.ClearUIWhenNoTabs();

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow != null)
            {
                await App.ShowWelcomeScreen(desktop.MainWindow);
            }
        }
    }
}