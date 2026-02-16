using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
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
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => SaveConfiguration());

            _logger.LogDebug("Change detected, will save in 5 seconds...");
        }

        /// <summary>
        /// Сохранить конфигурацию в workspace.json внутри ZIP
        /// Вызывается из UI потока через Dispatcher
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

                // ЗДЕСЬ НУЖЕН InvokeAsync, т.к. вызывается из таймера (фоновый поток)
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    try
                    {
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

                        if (!ValidateConfiguration(currentConfig))
                        {
                            _logger.LogError("CRITICAL: Configuration validation FAILED! REFUSING to save corrupted data!");
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
                        _logger.LogError(ex, "Error in SaveConfiguration inner block");
                    }
                });
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

                var dockFactory = App.Services.GetRequiredService<DockFactory>();

                var activeWorkMode = allWorkModes.FirstOrDefault(wm => wm.IsActive);

                _logger.LogDebug("CollectCurrentConfiguration:");
                _logger.LogDebug("Total WorkModes: {TotalCount}", allWorkModes.Count);
                _logger.LogDebug("ActiveWorkMode: {ActiveTitle}", activeWorkMode?.Title ?? "NULL");

                var workModesToSave = new List<WorkMode>();

                foreach (var wm in allWorkModes)
                {
                    _logger.LogDebug("Processing WorkMode: {Title}, IsActive={IsActive}", wm.Title, wm.IsActive);

                    if (wm == activeWorkMode)
                    {
                        var currentLayout = activeTab.Workspace.GetCurrentLayout();

                        if (currentLayout != null)
                        {
                            _logger.LogDebug("Saving as ACTIVE with full data");

                            var (layoutTree, updatedSlots) = dockFactory.SerializeCurrentLayout(currentLayout, wm, activeTab.ModuleContext);

                            var activeToSave = new WorkMode
                            {
                                Id = wm.Id,
                                WorkModeId = wm.WorkModeId,
                                Title = wm.Title,
                                Icon = wm.Icon,
                                IsActive = true,
                                Order = wm.Order,
                                IsCloseable = wm.IsCloseable,
                                ModuleSlots = updatedSlots,
                                LayoutTree = layoutTree
                            };

                            _logger.LogDebug("Saved ACTIVE WorkMode: {Title} ({SlotsCount} slots, {OpenCount} open)",
                                wm.Title,
                                updatedSlots.Count,
                                updatedSlots.Count(s => s.IsCurrentlyOpen));

                            workModesToSave.Add(activeToSave);
                        }
                        else
                        {
                            _logger.LogWarning("No layout for active WorkMode {Title}, skipping", wm.Title);
                        }
                    }
                    else
                    {
                        var hasInstanceIds = wm.ModuleSlots.Any(s => !string.IsNullOrEmpty(s.InstanceId));

                        if (!hasInstanceIds)
                        {
                            _logger.LogDebug("WorkMode {Title} was never used (no InstanceIds), skipping save", wm.Title);
                            continue;
                        }

                        _logger.LogDebug("Saving as INACTIVE (has {Count} modules with InstanceId)",
                            wm.ModuleSlots.Count(s => !string.IsNullOrEmpty(s.InstanceId)));

                        var inactiveToSave = new WorkMode
                        {
                            Id = wm.Id,
                            WorkModeId = wm.WorkModeId,
                            Title = wm.Title,
                            Icon = wm.Icon,
                            IsActive = false,
                            Order = wm.Order,
                            IsCloseable = wm.IsCloseable,
                            ModuleSlots = wm.ModuleSlots
                                .Where(s => !string.IsNullOrEmpty(s.InstanceId))
                                .Select(s => new ModuleSlot
                                {
                                    ModuleType = s.ModuleType,
                                    InstanceId = s.InstanceId,
                                    Path = s.Path,
                                    IsFloating = s.IsFloating,
                                    TabOrder = s.TabOrder,
                                    IsActiveTab = s.IsActiveTab,
                                    IsCurrentlyOpen = false,
                                    FloatX = s.FloatX,
                                    FloatY = s.FloatY,
                                    FloatWidth = s.FloatWidth,
                                    FloatHeight = s.FloatHeight
                                }).ToList(),
                            LayoutTree = wm.LayoutTree
                        };

                        _logger.LogDebug("Saved INACTIVE WorkMode: {Title} ({SlotsCount} slots with InstanceId)",
                            wm.Title, inactiveToSave.ModuleSlots.Count);

                        workModesToSave.Add(inactiveToSave);
                    }
                }

                var config = new WorkspaceLocalConfig
                {
                    ProjectName = _currentProject?.Title ?? "Unknown",
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
                    _logger.LogWarning("FileStorage not found for SaveNowAsync");
                    return;
                }

                _debounceSubscription?.Dispose();

                // УБРАЛИ Dispatcher.UIThread.InvokeAsync - МЫ УЖЕ В UI ПОТОКЕ!
                var currentConfig = await CollectCurrentConfigurationAsync();

                if (currentConfig != null)
                {
                    if (!ValidateConfiguration(currentConfig))
                    {
                        _logger.LogError("CRITICAL: Validation failed in SaveNowAsync! REFUSING to save!");
                        return;
                    }

                    var workspaceConfigService = App.Services.GetRequiredService<IWorkspaceConfigService>();
                    workspaceConfigService.SaveToZip(fileStorage, currentConfig);
                    _logger.LogDebug("Force save successful");
                }
                else
                {
                    _logger.LogWarning("CollectCurrentConfigurationAsync returned null");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in force save");
            }
        }

        /// <summary>
        /// Валидация конфигурации перед сохранением
        /// Проверяет что все InstanceId принадлежат текущему проекту
        /// </summary>
        private bool ValidateConfiguration(WorkspaceLocalConfig config)
        {
            try
            {
                var tabCollection = App.Services.GetRequiredService<ITabCollection>();
                var activeTab = tabCollection.Tabs?.FirstOrDefault(t => t.FilePath == _currentProjectPath);

                if (activeTab == null)
                {
                    _logger.LogError("Validation failed: No active tab for project");
                    return false;
                }

                foreach (var workMode in config.WorkModes)
                {
                    foreach (var slot in workMode.ModuleSlots.Where(s => s.IsCurrentlyOpen))
                    {
                        if (string.IsNullOrEmpty(slot.InstanceId))
                        {
                            _logger.LogError("Validation failed: Open module {ModuleId} has no InstanceId",
                                slot.ModuleType);
                            return false;
                        }

                        var module = activeTab.ModuleContext.GetModule(slot.InstanceId);

                        if (module == null)
                        {
                            _logger.LogError("Validation failed: InstanceId {InstanceId} for {ModuleId} " +
                                            "NOT FOUND in project context! Cross-project contamination detected!",
                                slot.InstanceId, slot.ModuleType);
                            return false;
                        }

                        if (module.ModuleId != slot.ModuleType)
                        {
                            _logger.LogError("Validation failed: InstanceId {InstanceId} belongs to {ActualModule}, " +
                                            "but slot expects {ExpectedModule}. Cross-project contamination!",
                                slot.InstanceId, module.ModuleId, slot.ModuleType);
                            return false;
                        }
                    }
                }

                _logger.LogDebug("Configuration validation PASSED");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Validation error");
                return false;
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