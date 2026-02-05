using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Models.Project;
using Writersword.Core.Models.Settings;
using Writersword.Core.Models.WorkModes;
using Writersword.Src.Core.Interfaces.Services;
using Writersword.Src.Core.Interfaces.WorkFlows;
using Writersword.Src.Core.Interfaces.WorkModes;
using Writersword.Src.Infrastructure.Dock;
using Writersword.ViewModels;

namespace Writersword.Src.Infrastructure.Services.WorkModes
{
    /// <summary>
    /// Сервис автоматического сохранения локальной конфигурации workspace
    /// Сохраняет изменения в workspace.json внутри ZIP спустя 5 секунд после последнего изменения
    /// </summary>
    public class WorkspaceAutoSaveService : IWorkspaceAutoSaveService
    {
        private readonly ILogger<WorkspaceAutoSaveService> _logger;
        private IDisposable? _debounceSubscription;
        private string? _currentProjectPath;
        private ProjectFile? _currentProject;
        private bool _isDisposed = false;

        /// <summary>Задержка перед сохранением (5 секунд)</summary>
        private readonly TimeSpan _debounceDelay = TimeSpan.FromSeconds(5);

        public WorkspaceAutoSaveService()
        {
            _logger = App.Services.GetService<ILogger<WorkspaceAutoSaveService>>()!;
        }

        /// <summary>
        /// Запустить автосохранение для проекта
        /// </summary>
        public void Start(string projectPath, ProjectFile project)
        {
            Stop();

            _currentProjectPath = projectPath;
            _currentProject = project;

            _logger.LogDebug("Started for: {ProjectPath}", projectPath);
        }

        /// <summary>
        /// Остановить автосохранение
        /// </summary>
        public void Stop()
        {
            _debounceSubscription?.Dispose();
            _debounceSubscription = null;

            _currentProjectPath = null;
            _currentProject = null;

            _logger.LogDebug("Stopped");
        }

        /// <summary>
        /// Уведомить сервис об изменении конфигурации
        /// Запускает таймер debounce - сохранение произойдёт через 5 секунд
        /// </summary>
        public void NotifyChange()
        {
            if (_isDisposed || _currentProject == null || _currentProjectPath == null)
            {
                return;
            }

            _debounceSubscription?.Dispose();

            _debounceSubscription = Observable
                .Timer(_debounceDelay)
                .Subscribe(_ => SaveConfiguration());

            _logger.LogDebug("Change detected, will save in 5 seconds...");
        }

        /// <summary>
        /// Сохранить конфигурацию в workspace.json внутри ZIP
        /// </summary>
        private async void SaveConfiguration()
        {
            if (_isDisposed || _currentProject == null || _currentProjectPath == null)
            {
                return;
            }

            try
            {
                _logger.LogDebug("Saving workspace.json");

                var projectWorkflow = App.Services.GetRequiredService<IProjectWorkflow>();
                var fileStorage = projectWorkflow.GetFileStorageForProject(_currentProjectPath);

                if (fileStorage == null)
                {
                    _logger.LogWarning("FileStorage not found");
                    return;
                }

                var currentConfig = await CollectCurrentConfigurationAsync();

                if (currentConfig == null)
                {
                    _logger.LogWarning("Failed to collect configuration");
                    return;
                }

                var workspaceConfigService = App.Services.GetRequiredService<IWorkspaceConfigService>();
                var success = workspaceConfigService.SaveToZip(fileStorage, currentConfig);

                if (success)
                {
                    _logger.LogDebug("workspace.json saved successfully");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving workspace");
            }
        }

        /// <summary>
        /// Собрать текущую конфигурацию из UI
        /// Сохраняет ВСЕ WorkModes, но детали (модули) только для активного
        /// </summary>
        private async Task<WorkspaceLocalConfig?> CollectCurrentConfigurationAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(_currentProjectPath))
                {
                    _logger.LogWarning("No project path");
                    return null;
                }

                var projectWorkflow = App.Services.GetRequiredService<IProjectWorkflow>();
                var fileStorage = projectWorkflow.GetFileStorageForProject(_currentProjectPath);

                if (fileStorage == null)
                {
                    _logger.LogWarning("FileStorage not found");
                    return null;
                }

                var tabCollection = App.Services.GetRequiredService<ITabCollection>();
                var activeTab = tabCollection.Tabs?.FirstOrDefault(t => t.FilePath == _currentProjectPath);

                if (activeTab?.Workspace == null)
                {
                    _logger.LogWarning("No workspace for project");
                    return null;
                }

                var workModeService = activeTab.Workspace.GetWorkModeService();
                var allWorkModes = workModeService.GetAllWorkModes();

                if (allWorkModes == null || allWorkModes.Count == 0)
                {
                    _logger.LogWarning("No WorkModes to save");
                    return null;
                }

                var mainVM = App.Services.GetRequiredService<MainWindowViewModel>();
                var dockFactory = App.Services.GetRequiredService<DockFactory>();

                var activeWorkMode = allWorkModes.FirstOrDefault(wm => wm.IsActive);

                _logger.LogDebug("CollectCurrentConfiguration:");
                _logger.LogDebug("Total WorkModes: {TotalCount}", allWorkModes.Count);
                _logger.LogDebug("ActiveWorkMode: {ActiveTitle}", activeWorkMode?.Title ?? "NULL");
                _logger.LogDebug("DockLayout: {HasLayout}", mainVM.DockLayout != null ? "EXISTS" : "NULL");

                var workModesToSave = new List<WorkMode>();

                foreach (var wm in allWorkModes)
                {
                    _logger.LogDebug("Processing WorkMode: {Title}, IsActive={IsActive}", wm.Title, wm.IsActive);

                    if (wm == activeWorkMode && mainVM.DockLayout != null)
                    {
                        _logger.LogDebug("Saving as ACTIVE with full data");

                        var (containers, updatedSlots) = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            return dockFactory.SerializeCurrentLayout(mainVM.DockLayout, wm);
                        });

                        var validInstanceIds = wm.ModuleSlots
                            .Where(s => !string.IsNullOrEmpty(s.InstanceId))
                            .Select(s => s.InstanceId)
                            .ToHashSet();

                        var filteredSlots = updatedSlots
                            .Where(s => string.IsNullOrEmpty(s.InstanceId) || validInstanceIds.Contains(s.InstanceId))
                            .ToList();

                        var foreignCount = updatedSlots.Count - filteredSlots.Count;
                        if (foreignCount > 0)
                        {
                            _logger.LogDebug("FILTERED OUT {ForeignCount} foreign modules from slots", foreignCount);
                        }

                        var activeToSave = new WorkMode
                        {
                            Id = wm.Id,
                            WorkModeId = wm.WorkModeId,
                            Title = wm.Title,
                            Icon = wm.Icon,
                            IsActive = true,
                            Order = wm.Order,
                            IsCloseable = wm.IsCloseable,
                            ModuleSlots = filteredSlots,
                            Containers = containers
                        };

                        _logger.LogDebug("DEBUG activeToSave:");
                        _logger.LogDebug("Id: {Id}", activeToSave.Id);
                        _logger.LogDebug("WorkModeId: {WorkModeId}", activeToSave.WorkModeId);
                        _logger.LogDebug("Title: {Title}", activeToSave.Title);
                        _logger.LogDebug("ModuleSlots.Count: {SlotsCount}", activeToSave.ModuleSlots?.Count ?? -1);
                        _logger.LogDebug("Containers.Count: {ContainersCount}", activeToSave.Containers?.Count ?? -1);

                        workModesToSave.Add(activeToSave);
                        _logger.LogDebug("Saved ACTIVE WorkMode: {Title} ({ContainersCount} containers, {SlotsCount} slots)", wm.Title, containers.Count, filteredSlots.Count);
                    }
                    else
                    {
                        _logger.LogDebug("Saving as INACTIVE (structure only)");

                        var inactiveToSave = new WorkMode
                        {
                            Id = wm.Id,
                            WorkModeId = wm.WorkModeId,
                            Title = wm.Title,
                            Icon = wm.Icon,
                            IsActive = false,
                            Order = wm.Order,
                            IsCloseable = wm.IsCloseable,
                            ModuleSlots = new List<ModuleSlot>(),
                            Containers = new List<SplitContainer>()
                        };

                        workModesToSave.Add(inactiveToSave);
                        _logger.LogDebug("Saved INACTIVE WorkMode: {Title} (structure only)", wm.Title);
                    }
                }

                var config = new WorkspaceLocalConfig
                {
                    WorkModes = workModesToSave
                };

                _logger.LogDebug("Collected configuration: {Count} WorkModes", config.WorkModes.Count);
                return config;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error collecting configuration");
                return null;
            }
        }

        /// <summary>
        /// Принудительно сохранить конфигурацию СЕЙЧАС
        /// </summary>
        public async Task SaveNowAsync()
        {
            if (_isDisposed || _currentProject == null || _currentProjectPath == null)
            {
                return;
            }

            try
            {
                _logger.LogDebug("Force saving NOW");

                var projectWorkflow = App.Services.GetRequiredService<IProjectWorkflow>();
                var fileStorage = projectWorkflow.GetFileStorageForProject(_currentProjectPath);

                if (fileStorage == null)
                {
                    return;
                }

                _debounceSubscription?.Dispose();

                var currentConfig = await CollectCurrentConfigurationAsync();
                if (currentConfig != null)
                {
                    var workspaceConfigService = App.Services.GetRequiredService<IWorkspaceConfigService>();
                    workspaceConfigService.SaveToZip(fileStorage, currentConfig);
                    _logger.LogDebug("Force save successful");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in force save");
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            Stop();

            _logger.LogDebug("Disposed");
        }
    }
}