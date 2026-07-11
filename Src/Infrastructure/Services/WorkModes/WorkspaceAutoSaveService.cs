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

            // Захватываем путь и проект в момент вызова: сохранение выполняется
            // параллельно переключению вкладок, и Stop() (вызывается из Suspend)
            // обнуляет поля сервиса раньше, чем сохранение доходит до записи.
            // Без захвата сбор конфигурации и валидация видели null и сохранение
            // молча отменялось ("Validation failed: no tab for project null").
            var projectPath = _currentProjectPath;
            var project = _currentProject;

            try
            {
                _logger.LogDebug("Force saving workspace.json");

                _debounceSubscription?.Dispose();
                _debounceSubscription = null;

                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;

                var projectWorkflow = App.Services.GetRequiredService<IProjectWorkflow>();
                var fileStorage = projectWorkflow.GetFileStorageForProject(projectPath);

                if (fileStorage == null)
                {
                    _logger.LogWarning("FileStorage not found for SaveNowAsync");
                    return;
                }

                // Семафор сериализует принудительное сохранение с дебаунс-сохранениями
                // (ScheduleSave) и с параллельными вызовами SaveNowAsync: раньше вызов
                // не синхронизировался и две записи могли конкурировать за ZIP-файл.
                if (!await _saveSemaphore.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false))
                {
                    _logger.LogWarning("SaveNowAsync skipped: another save operation is still running");
                    return;
                }

                try
                {
                    // ConfigureAwait(false): продолжения не возвращаются на UI-поток.
                    // Раньше await захватывал UI-контекст, и перезапись ZIP-архива
                    // проекта (SaveToZip) выполнялась на UI-потоке — при переключении
                    // вкладок это блокировало интерфейс на время записи файла.
                    var currentConfig = await CollectCurrentConfigurationAsync(projectPath, project, CancellationToken.None).ConfigureAwait(false);

                    if (currentConfig == null)
                    {
                        _logger.LogWarning("CollectCurrentConfigurationAsync returned null");
                        return;
                    }

                    if (!ValidateConfiguration(currentConfig, projectPath, project))
                    {
                        _logger.LogError("Validation failed in SaveNowAsync, refusing to save");
                        return;
                    }

                    // Явный уход в пул потоков: WaitAsync и Collect могли завершиться
                    // синхронно, оставив выполнение на UI-потоке несмотря на ConfigureAwait.
                    var workspaceConfigService = App.Services.GetRequiredService<IWorkspaceConfigService>();
                    await Task.Run(() => workspaceConfigService.SaveToZip(fileStorage, currentConfig)).ConfigureAwait(false);
                    _logger.LogDebug("Force save successful");
                }
                finally
                {
                    _saveSemaphore.Release();
                }
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

            // Захватываем путь и проект в момент старта: Stop() может обнулить
            // поля сервиса пока сохранение выполняется на фоновом потоке.
            var projectPath = _currentProjectPath;
            var project = _currentProject;

            try
            {
                if (token.IsCancellationRequested) return;

                var projectWorkflow = App.Services.GetRequiredService<IProjectWorkflow>();
                var fileStorage = projectWorkflow.GetFileStorageForProject(projectPath);

                if (fileStorage == null)
                {
                    _logger.LogWarning("FileStorage not found");
                    return;
                }

                var currentConfig = await CollectCurrentConfigurationAsync(projectPath, project, token);

                if (token.IsCancellationRequested) return;

                if (currentConfig == null)
                {
                    _logger.LogWarning("Failed to collect configuration");
                    return;
                }

                if (!ValidateConfiguration(currentConfig, projectPath, project))
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
        /// Путь и проект передаются параметрами (захвачены вызывающим кодом):
        /// поля сервиса могут быть обнулены Stop() пока сохранение идёт в фоне.
        /// </summary>
        private async Task<WorkspaceLocalConfig?> CollectCurrentConfigurationAsync(
            string projectPath, ProjectFile? project, CancellationToken token)
        {
            try
            {
                if (string.IsNullOrEmpty(projectPath))
                {
                    _logger.LogWarning("No project path");
                    return null;
                }

                var projectWorkflow = App.Services.GetRequiredService<IProjectWorkflow>();
                var fileStorage = projectWorkflow.GetFileStorageForProject(projectPath);

                if (fileStorage == null)
                {
                    _logger.LogWarning("FileStorage not found");
                    return null;
                }

                if (token.IsCancellationRequested) return null;

                var tabCollection = App.Services.GetRequiredService<ITabCollection>();
                var activeTab = await Dispatcher.UIThread.InvokeAsync(() =>
                    tabCollection.Tabs?.FirstOrDefault(t => t.FilePath == projectPath) as DocumentTabViewModel,
                    DispatcherPriority.Background);

                // DispatcherOperation не поддерживает ConfigureAwait напрямую (это не Task),
                // а завершает его именно UI-поток — без явного ухода вся дальнейшая сборка
                // конфигурации workspace (в т.ч. сериализация лэйаута) выполнялась бы на
                // UI-потоке вместо фонового, для которого она и задумана (Task.Run в ScheduleSave).
                await Task.Run(() => { }).ConfigureAwait(false);

                if (activeTab?.Workspace == null)
                {
                    _logger.LogWarning("No workspace for project: {Path}", projectPath);
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

                await Task.Run(() => { }).ConfigureAwait(false);

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

                        await Task.Run(() => { }).ConfigureAwait(false);

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
                    ProjectName = project?.Title ?? "Unknown",
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

        private bool ValidateConfiguration(WorkspaceLocalConfig config, string projectPath, ProjectFile? project)
        {
            try
            {
                var tabCollection = App.Services.GetRequiredService<ITabCollection>();
                var activeTab = tabCollection.Tabs?.FirstOrDefault(t => t.FilePath == projectPath) as DocumentTabViewModel;

                if (activeTab == null)
                {
                    _logger.LogError("Validation failed: no tab for project {Path}", projectPath);
                    return false;
                }

                if (project != null)
                {
                    if (activeTab.GetProject()?.Id != project.Id)
                    {
                        _logger.LogError("Validation failed: ProjectId mismatch. Expected {Expected}, got {Actual}",
                            project.Id, activeTab.GetProject()?.Id);
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