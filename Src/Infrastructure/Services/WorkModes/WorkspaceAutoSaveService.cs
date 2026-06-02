using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Interfaces.WorkFlows;
using Writersword.Core.Models.Project;
using Writersword.Core.Models.Settings;
using Writersword.Core.Models.WorkModes;
using Writersword.Infrastructure.Dock;
using Writersword.ViewModels;

namespace Writersword.Infrastructure.Services.WorkModes
{
    /// <summary>
    /// Сервис автоматического сохранения локальной конфигурации workspace.
    /// Сохраняет изменения в workspace.json внутри ZIP спустя 5 секунд после последнего изменения.
    /// SemaphoreSlim предотвращает конкурентные записи при частом переключении WorkMode.
    /// CancellationToken прерывает устаревшую операцию если пришло новое изменение.
    /// </summary>
    public class WorkspaceAutoSaveService : IWorkspaceAutoSaveService
    {
        private readonly ILogger<WorkspaceAutoSaveService> _logger;
        private readonly SemaphoreSlim _saveSemaphore = new SemaphoreSlim(1, 1);
        private IDisposable? _debounceSubscription;
        private CancellationTokenSource? _cts;
        private string? _currentProjectPath;
        private ProjectFile? _currentProject;
        private bool _isDisposed = false;

        private readonly TimeSpan _debounceDelay = TimeSpan.FromSeconds(5);

        public WorkspaceAutoSaveService()
        {
            _logger = App.Services.GetService<ILogger<WorkspaceAutoSaveService>>()!;
        }

        public void Start(string projectPath, ProjectFile project)
        {
            Stop();
            _currentProjectPath = projectPath;
            _currentProject = project;
            _logger.LogDebug("Started for: {ProjectPath}", projectPath);
        }

        public void Stop()
        {
            _debounceSubscription?.Dispose();
            _debounceSubscription = null;

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            _currentProjectPath = null;
            _currentProject = null;
            _logger.LogDebug("Stopped");
        }

        public void NotifyChange()
        {
            if (_isDisposed || _currentProject == null || _currentProjectPath == null)
                return;

            _debounceSubscription?.Dispose();

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _debounceSubscription = Observable
                .Timer(_debounceDelay)
                .Subscribe(_ =>
                {
                    if (!token.IsCancellationRequested)
                        ScheduleSave(token);
                });

            _logger.LogDebug("Change detected, will save in {Seconds} seconds", _debounceDelay.TotalSeconds);
        }

        private void ScheduleSave(CancellationToken token)
        {
            Task.Run(async () =>
            {
                if (token.IsCancellationRequested) return;

                if (!await _saveSemaphore.WaitAsync(TimeSpan.Zero))
                {
                    _logger.LogDebug("Save skipped: previous operation still running");
                    return;
                }

                try
                {
                    await SaveConfigurationAsync(token);
                }
                finally
                {
                    _saveSemaphore.Release();
                }
            });
        }

        public async Task SaveNowAsync()
        {
            if (_isDisposed || _currentProject == null || _currentProjectPath == null)
                return;

            try
            {
                _logger.LogDebug("Force saving workspace.json");

                _debounceSubscription?.Dispose();
                _debounceSubscription = null;

                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;

                var projectWorkflow = App.Services.GetRequiredService<IProjectWorkflow>();
                var fileStorage = projectWorkflow.GetFileStorageForProject(_currentProjectPath);

                if (fileStorage == null)
                {
                    _logger.LogWarning("FileStorage not found for SaveNowAsync");
                    return;
                }

                var currentConfig = await CollectCurrentConfigurationAsync(CancellationToken.None);

                if (currentConfig == null)
                {
                    _logger.LogWarning("CollectCurrentConfigurationAsync returned null");
                    return;
                }

                if (!ValidateConfiguration(currentConfig))
                {
                    _logger.LogError("Validation failed in SaveNowAsync, refusing to save");
                    return;
                }

                var workspaceConfigService = App.Services.GetRequiredService<IWorkspaceConfigService>();
                workspaceConfigService.SaveToZip(fileStorage, currentConfig);
                _logger.LogDebug("Force save successful");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in SaveNowAsync");
            }
        }

        private async Task SaveConfigurationAsync(CancellationToken token)
        {
            if (_isDisposed || _currentProject == null || _currentProjectPath == null)
                return;

            try
            {
                if (token.IsCancellationRequested) return;

                var projectWorkflow = App.Services.GetRequiredService<IProjectWorkflow>();
                var fileStorage = projectWorkflow.GetFileStorageForProject(_currentProjectPath);

                if (fileStorage == null)
                {
                    _logger.LogWarning("FileStorage not found");
                    return;
                }

                var currentConfig = await CollectCurrentConfigurationAsync(token);

                if (token.IsCancellationRequested) return;

                if (currentConfig == null)
                {
                    _logger.LogWarning("Failed to collect configuration");
                    return;
                }

                if (!ValidateConfiguration(currentConfig))
                {
                    _logger.LogError("Configuration validation failed, refusing to save");
                    return;
                }

                var workspaceConfigService = App.Services.GetRequiredService<IWorkspaceConfigService>();
                var success = workspaceConfigService.SaveToZip(fileStorage, currentConfig);

                if (success)
                    _logger.LogDebug("workspace.json saved successfully");
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Save cancelled (newer change arrived)");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving workspace");
            }
        }

        /// <summary>
        /// Собрать текущую конфигурацию из активного workspace.
        /// Активный WorkMode сериализуется из реального layout.
        /// Неактивные берутся как есть из ModuleSlots.
        /// </summary>
        private async Task<WorkspaceLocalConfig?> CollectCurrentConfigurationAsync(CancellationToken token)
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

                if (token.IsCancellationRequested) return null;

                var tabCollection = App.Services.GetRequiredService<ITabCollection>();
                var activeTab = await Dispatcher.UIThread.InvokeAsync(() =>
                    tabCollection.Tabs?.FirstOrDefault(t => t.FilePath == _currentProjectPath) as DocumentTabViewModel,
                    DispatcherPriority.Background);

                if (activeTab?.Workspace == null)
                {
                    _logger.LogWarning("No workspace for project: {Path}", _currentProjectPath);
                    return null;
                }

                if (token.IsCancellationRequested) return null;

                var (allWorkModes, activeWorkMode, dockFactory, currentLayout) =
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        var wms = activeTab.Workspace.GetWorkModeService().GetAllWorkModes();
                        var active = wms.FirstOrDefault(wm => wm.IsActive);
                        var factory = App.Services.GetRequiredService<DockFactory>();
                        var layout = activeTab.Workspace.GetCurrentLayout();
                        return (wms, active, factory, layout);
                    }, DispatcherPriority.Background);

                if (allWorkModes == null || allWorkModes.Count == 0)
                {
                    _logger.LogWarning("No WorkModes to save");
                    return null;
                }

                _logger.LogDebug("Collecting config: {Total} WorkModes, active: {ActiveTitle}",
                    allWorkModes.Count, activeWorkMode?.Title ?? "NULL");

                var workModesToSave = new List<WorkMode>();

                foreach (var wm in allWorkModes)
                {
                    if (token.IsCancellationRequested) return null;

                    if (wm == activeWorkMode)
                    {
                        if (currentLayout == null)
                        {
                            _logger.LogWarning("No layout for active WorkMode {Title}, skipping", wm.Title);
                            continue;
                        }

                        var (serializedLayout, updatedSlots) = await Dispatcher.UIThread.InvokeAsync(() =>
                            dockFactory.SerializeCurrentLayout(currentLayout, wm, activeTab.ModuleContext),
                            DispatcherPriority.Background);

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
                            SerializedDockLayout = serializedLayout
                        };

                        workModesToSave.Add(activeToSave);
                        _logger.LogDebug("Active WorkMode: {Title} ({SlotsCount} slots)", wm.Title, updatedSlots.Count);
                    }
                    else
                    {
                        if (wm.ModuleSlots.Count == 0)
                        {
                            _logger.LogDebug("WorkMode {Title} has no slots, skipping", wm.Title);
                            continue;
                        }

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
                                .Select(s => new ModuleSlot
                                {
                                    ModuleType = s.ModuleType,
                                    PreferredPosition = s.PreferredPosition,
                                    Category = s.Category
                                })
                                .ToList(),
                            SerializedDockLayout = wm.SerializedDockLayout
                        };

                        workModesToSave.Add(inactiveToSave);
                        _logger.LogDebug("Inactive WorkMode: {Title} ({SlotsCount} slots)", wm.Title, wm.ModuleSlots.Count);
                    }
                }

                var config = new WorkspaceLocalConfig
                {
                    ProjectName = _currentProject?.Title ?? "Unknown",
                    WorkModes = workModesToSave
                };

                _logger.LogDebug("Configuration collected: {Count} WorkModes", config.WorkModes.Count);
                return config;
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("CollectCurrentConfiguration cancelled");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error collecting configuration");
                return null;
            }
        }

        private bool ValidateConfiguration(WorkspaceLocalConfig config)
        {
            try
            {
                var tabCollection = App.Services.GetRequiredService<ITabCollection>();
                var activeTab = tabCollection.Tabs?.FirstOrDefault(t => t.FilePath == _currentProjectPath) as DocumentTabViewModel;

                if (activeTab == null)
                {
                    _logger.LogError("Validation failed: no tab for project {Path}", _currentProjectPath);
                    return false;
                }

                if (_currentProject != null)
                {
                    if (activeTab.GetProject()?.Id != _currentProject.Id)
                    {
                        _logger.LogError("Validation failed: ProjectId mismatch. Expected {Expected}, got {Actual}",
                            _currentProject.Id, activeTab.GetProject()?.Id);
                        return false;
                    }
                }

                foreach (var workMode in config.WorkModes)
                {
                    var seenModuleTypes = new HashSet<string>();

                    foreach (var slot in workMode.ModuleSlots)
                    {
                        if (string.IsNullOrEmpty(slot.ModuleType))
                        {
                            _logger.LogError("Validation failed: empty ModuleType in WorkMode {WorkMode}",
                                workMode.Title);
                            return false;
                        }

                        if (!seenModuleTypes.Add(slot.ModuleType))
                        {
                            _logger.LogError("Validation failed: duplicate ModuleType {ModuleType} in WorkMode {WorkMode}",
                                slot.ModuleType, workMode.Title);
                            return false;
                        }
                    }
                }

                _logger.LogDebug("Configuration validation passed");
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
            _saveSemaphore.Dispose();
            _logger.LogDebug("Disposed");
        }
    }
}