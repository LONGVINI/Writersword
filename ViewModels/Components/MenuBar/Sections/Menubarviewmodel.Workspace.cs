using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using Writersword.Core.Enums;
using Writersword.Core.Models.Settings;
using Writersword.Core.Interfaces.Services.UI;
using Writersword.WorkModes.Common;
using Writersword.Views;

namespace Writersword.ViewModels.Components.MenuBar
{
    public partial class MenuBarViewModel
    {
        /// <summary>
        /// Сохранить конфигурацию глобально (для всех проектов данного типа).
        /// Применится ко ВСЕМ будущим проектам типа "Novel", "Screenplay" и т.д.
        /// </summary>
        private async Task SaveWorkspaceGlobal()
        {
            var activeTab = _getActiveTab?.Invoke();
            if (activeTab == null)
            {
                _logger.LogDebug("SaveWorkspaceGlobal: no active tab");
                return;
            }

            try
            {
                var project = activeTab.GetProject();
                var projectTypeObj = _projectTypeRegistry.GetById(project.Type);
                string displayName = projectTypeObj?.DisplayName ?? project.Type;

                var result = await _dialogService.ShowMessageAsync(
                    "Сохранить как глобальные настройки?",
                    $"Текущая конфигурация будет применена ко всем новым проектам типа \"{displayName}\". " +
                    "Предыдущие глобальные настройки будут перезаписаны. Продолжить?",
                    MessageBoxType.Warning,
                    MessageBoxButtons.YesNo
                );

                if (result != MessageBoxResult.Yes)
                {
                    _logger.LogDebug("Save global cancelled");
                    return;
                }

                if (activeTab.Workspace == null)
                {
                    _logger.LogWarning("No Workspace on active tab");
                    return;
                }

                var currentWorkModes = activeTab.Workspace.GetAvailableWorkModes();
                var config = new WorkspaceConfig
                {
                    ProjectType = project.Type,
                    Name = $"{project.Type} Configuration",
                    WorkModes = currentWorkModes
                };

                _settingsService.SaveWorkspaceConfig(project.Type, config);

                _notificationService.ShowSuccess($"Конфигурация сохранена для типа {displayName}");
                _logger.LogDebug("Workspace saved globally for: {ProjectType}", project.Type);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving global workspace");
            }
        }

        /// <summary>
        /// Сбросить конфигурацию до глобальной.
        /// Удаляет workspace.json из ZIP и перезагружает глобальную конфигурацию.
        /// </summary>
        private async Task ResetWorkspaceToGlobal()
        {
            var activeTab = _getActiveTab?.Invoke();
            if (activeTab == null)
            {
                _logger.LogDebug("ResetWorkspaceToGlobal: no active tab");
                return;
            }

            try
            {
                var result = await _dialogService.ShowMessageAsync(
                    "Восстановить из глобальных настроек?",
                    "Локальная конфигурация будет удалена. Продолжить?",
                    MessageBoxType.Warning,
                    MessageBoxButtons.YesNo
                );

                if (result != MessageBoxResult.Yes)
                {
                    _logger.LogDebug("Reset to global cancelled");
                    return;
                }

                if (activeTab.Workspace == null)
                {
                    _logger.LogWarning("No Workspace on active tab");
                    return;
                }

                var project = activeTab.GetProject();
                var fileStorage = activeTab.Context.FileStorage;

                if (fileStorage != null)
                    _workspaceConfigService.DeleteFromZip(fileStorage);

                var globalWorkModes = _workModeConfigService.LoadConfiguration(project.Type, null);
                activeTab.Workspace.ReloadFromGlobalConfig(globalWorkModes);

                var mainVM = _mainViewModelProvider?.Invoke();
                var activeWorkMode = activeTab.Workspace.GetActiveWorkMode();
                mainVM?.ModulePanel.LoadModulesForWorkMode(activeWorkMode);

                _notificationService.ShowSuccess("Конфигурация восстановлена из глобальных настроек");
                _logger.LogDebug("Workspace reset to global");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resetting to global");
            }
        }

        /// <summary>
        /// Сбросить конфигурацию до дефолтной.
        /// Вся логика сброса слотов и пересоздания layout делегируется WorkspaceController.
        /// </summary>
        private async Task ResetWorkspaceToDefault()
        {
            _logger.LogDebug("ResetWorkspaceToDefault called");

            var activeTab = _getActiveTab?.Invoke();
            if (activeTab == null)
            {
                _logger.LogWarning("No active tab");
                return;
            }

            try
            {
                var result = await _dialogService.ShowMessageAsync(
                    "Сбросить до дефолта?",
                    "Текущий WorkMode будет сброшен до настроек по умолчанию. Продолжить?",
                    MessageBoxType.Warning,
                    MessageBoxButtons.YesNo
                );

                if (result != MessageBoxResult.Yes)
                {
                    _logger.LogDebug("Cancelled");
                    return;
                }

                if (activeTab.Workspace == null)
                {
                    _logger.LogWarning("No Workspace");
                    return;
                }

                var activeWorkMode = activeTab.Workspace.GetActiveWorkMode();
                if (activeWorkMode == null)
                {
                    _logger.LogWarning("No active WorkMode");
                    return;
                }

                var workModeRegistry = App.Services.GetRequiredService<WorkModeRegistry>();
                var registeredWorkMode = workModeRegistry.GetWorkMode(activeWorkMode.WorkModeId);

                if (registeredWorkMode == null)
                {
                    _logger.LogWarning("WorkMode not found in registry: {WorkModeId}", activeWorkMode.WorkModeId);
                    return;
                }

                var defaultConfig = registeredWorkMode.GetDefaultConfig();

                // Сброс слотов, очистка контекста и пересоздание layout — всё в контроллере.
                // DockLayout обновится автоматически через WorkspaceChanged event.
                activeTab.Workspace.ResetWorkModeToDefault(activeWorkMode, defaultConfig);

                var mainVM = _mainViewModelProvider?.Invoke();
                mainVM?.ModulePanel.LoadModulesForWorkMode(activeWorkMode);

                _notificationService.ShowSuccess($"WorkMode '{activeWorkMode.Title}' сброшен до дефолта");
                _logger.LogDebug("Reset to default complete");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resetting to default");
            }
        }
    }
}