using Microsoft.Extensions.DependencyInjection;
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
        private IDisposable? _debounceSubscription;
        private string? _currentProjectPath;
        private ProjectFile? _currentProject;
        private bool _isDisposed = false;

        /// <summary>Задержка перед сохранением (5 секунд)</summary>
        private readonly TimeSpan _debounceDelay = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Запустить автосохранение для проекта
        /// </summary>
        public void Start(string projectPath, ProjectFile project)
        {
            Stop();

            _currentProjectPath = projectPath;
            _currentProject = project;

            Console.WriteLine($"[WorkspaceAutoSave] Started for: {projectPath}");
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

            Console.WriteLine("[WorkspaceAutoSave] Stopped");
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

            Console.WriteLine("[WorkspaceAutoSave] Change detected, will save in 5 seconds...");
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
                Console.WriteLine($"[WorkspaceAutoSave] Saving workspace.json");

                var projectWorkflow = App.Services.GetRequiredService<IProjectWorkflow>();
                var fileStorage = projectWorkflow.GetFileStorageForProject(_currentProjectPath);

                if (fileStorage == null)
                {
                    Console.WriteLine("[WorkspaceAutoSave] FileStorage not found");
                    return;
                }

                var currentConfig = await CollectCurrentConfigurationAsync();

                if (currentConfig == null)
                {
                    Console.WriteLine("[WorkspaceAutoSave] Failed to collect configuration");
                    return;
                }

                var workspaceConfigService = App.Services.GetRequiredService<IWorkspaceConfigService>();
                var success = workspaceConfigService.SaveToZip(fileStorage, currentConfig);

                if (success)
                {
                    Console.WriteLine("[WorkspaceAutoSave] workspace.json saved successfully");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WorkspaceAutoSave] Error saving: {ex.Message}");
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
                    Console.WriteLine("[WorkspaceAutoSave] No project path");
                    return null;
                }

                // Получаем WorkModes из активного WorkspaceController проекта
                var projectWorkflow = App.Services.GetRequiredService<IProjectWorkflow>();
                var fileStorage = projectWorkflow.GetFileStorageForProject(_currentProjectPath);

                if (fileStorage == null)
                {
                    Console.WriteLine("[WorkspaceAutoSave] FileStorage not found");
                    return null;
                }

                var tabCollection = App.Services.GetRequiredService<ITabCollection>();
                var activeTab = tabCollection.Tabs?.FirstOrDefault(t => t.FilePath == _currentProjectPath);

                if (activeTab?.Workspace == null)
                {
                    Console.WriteLine("[WorkspaceAutoSave] No workspace for project");
                    return null;
                }

                var workModeService = activeTab.Workspace.GetWorkModeService();
                var allWorkModes = workModeService.GetAllWorkModes();

                if (allWorkModes == null || allWorkModes.Count == 0)
                {
                    Console.WriteLine("[WorkspaceAutoSave] No WorkModes to save");
                    return null;
                }

                var mainVM = App.Services.GetRequiredService<MainWindowViewModel>();
                var dockFactory = App.Services.GetRequiredService<DockFactory>();

                // Находим активный WorkMode
                var activeWorkMode = allWorkModes.FirstOrDefault(wm => wm.IsActive);

                Console.WriteLine($"[WorkspaceAutoSave] CollectCurrentConfiguration:");
                Console.WriteLine($"[WorkspaceAutoSave]   Total WorkModes: {allWorkModes.Count}");
                Console.WriteLine($"[WorkspaceAutoSave]   ActiveWorkMode: {activeWorkMode?.Title ?? "NULL"}");
                Console.WriteLine($"[WorkspaceAutoSave]   DockLayout: {(mainVM.DockLayout != null ? "EXISTS" : "NULL")}");

                // Создаём список для сохранения
                var workModesToSave = new List<WorkMode>();

                foreach (var wm in allWorkModes)
                {
                    Console.WriteLine($"[WorkspaceAutoSave] Processing WorkMode: {wm.Title}, IsActive={wm.IsActive}");

                    if (wm == activeWorkMode && mainVM.DockLayout != null)
                    {
                        Console.WriteLine($"[WorkspaceAutoSave]   → Saving as ACTIVE with full data");

                        // Для АКТИВНОГО - сериализуем полный layout с модулями
                        // ВАЖНО: SerializeCurrentLayout должен вызываться из UI потока
                        var (containers, updatedSlots) = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            return dockFactory.SerializeCurrentLayout(mainVM.DockLayout, wm);
                        });

                        // ФИЛЬТРАЦИЯ: Оставляем только "свои" модули
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
                            Console.WriteLine($"[WorkspaceAutoSave] FILTERED OUT {foreignCount} foreign modules from slots!");
                        }

                        // Создаём копию активного WorkMode с полными данными
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

                        // DEBUG: Проверяем что создалось
                        Console.WriteLine($"[WorkspaceAutoSave] DEBUG activeToSave:");
                        Console.WriteLine($"  Id: {activeToSave.Id}");
                        Console.WriteLine($"  WorkModeId: {activeToSave.WorkModeId}");
                        Console.WriteLine($"  Title: {activeToSave.Title}");
                        Console.WriteLine($"  ModuleSlots.Count: {activeToSave.ModuleSlots?.Count ?? -1}");
                        Console.WriteLine($"  Containers.Count: {activeToSave.Containers?.Count ?? -1}");


                        workModesToSave.Add(activeToSave);
                        Console.WriteLine($"[WorkspaceAutoSave] Saved ACTIVE WorkMode: {wm.Title} ({containers.Count} containers, {filteredSlots.Count} slots)");
                    }
                    else
                    {
                        Console.WriteLine($"[WorkspaceAutoSave]   → Saving as INACTIVE (structure only)");

                        // Для НЕАКТИВНЫХ - только базовая инфа без модулей
                        var inactiveToSave = new WorkMode
                        {
                            Id = wm.Id,
                            WorkModeId = wm.WorkModeId,
                            Title = wm.Title,
                            Icon = wm.Icon,
                            IsActive = false,
                            Order = wm.Order,
                            IsCloseable = wm.IsCloseable,
                            ModuleSlots = new List<ModuleSlot>(),  // Пусто
                            Containers = new List<SplitContainer>() // Пусто
                        };

                        workModesToSave.Add(inactiveToSave);
                        Console.WriteLine($"[WorkspaceAutoSave] Saved INACTIVE WorkMode: {wm.Title} (structure only)");
                    }
                }

                var config = new WorkspaceLocalConfig
                {
                    WorkModes = workModesToSave
                };

                Console.WriteLine($"[WorkspaceAutoSave] Collected configuration: {config.WorkModes.Count} WorkModes");
                return config;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WorkspaceAutoSave] Error collecting: {ex.Message}");
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
                Console.WriteLine("[WorkspaceAutoSave] Force saving NOW");

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
                    Console.WriteLine("[WorkspaceAutoSave] Force save successful");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WorkspaceAutoSave] Error in force save: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            Stop();

            Console.WriteLine("[WorkspaceAutoSave] Disposed");
        }
    }
}