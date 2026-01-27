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

                var currentConfig = CollectCurrentConfiguration();

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
        /// ОБНОВЛЕНО: Сериализует структуру из DockFactory в новый формат
        /// </summary>
        private WorkspaceLocalConfig? CollectCurrentConfiguration()
        {
            try
            {
                var workModeService = App.Services.GetRequiredService<IWorkModeService>();
                var activeWorkMode = workModeService.GetActiveWorkMode();

                if (activeWorkMode == null)
                {
                    Console.WriteLine("[WorkspaceAutoSave] No active WorkMode");
                    return null;
                }

                // НОВОЕ: Сериализуем текущий layout из UI через DockFactory
                var mainVM = App.Services.GetRequiredService<MainWindowViewModel>();
                var dockFactory = App.Services.GetRequiredService<DockFactory>();

                if (mainVM.DockLayout != null)
                {
                    // Сериализуем layout и получаем обновлённые данные
                    var (containers, updatedSlots) = dockFactory.SerializeCurrentLayout(mainVM.DockLayout, activeWorkMode);

                    // Обновляем WorkMode
                    activeWorkMode.Containers = containers;
                    activeWorkMode.ModuleSlots = updatedSlots;

                    Console.WriteLine($"[WorkspaceAutoSave] Serialized: {containers.Count} containers, {updatedSlots.Count} slots");
                }

                // Загружаем существующий workspace.json
                var projectWorkflow = App.Services.GetRequiredService<IProjectWorkflow>();
                var fileStorage = projectWorkflow.GetFileStorageForProject(_currentProjectPath!);
                var workspaceConfigService = App.Services.GetRequiredService<IWorkspaceConfigService>();

                WorkspaceLocalConfig? existingConfig = null;
                if (fileStorage != null)
                {
                    existingConfig = workspaceConfigService.LoadFromZip(fileStorage);
                }

                if (existingConfig == null)
                {
                    existingConfig = new WorkspaceLocalConfig
                    {
                        WorkModes = new List<WorkMode>()
                    };
                }

                // Обновляем или добавляем активный WorkMode
                var existingWorkMode = existingConfig.WorkModes
                    .FirstOrDefault(wm => wm.WorkModeId == activeWorkMode.WorkModeId);

                if (existingWorkMode != null)
                {
                    var index = existingConfig.WorkModes.IndexOf(existingWorkMode);
                    existingConfig.WorkModes[index] = activeWorkMode;
                    Console.WriteLine($"[WorkspaceAutoSave] Updated WorkMode: {activeWorkMode.Title}");
                }
                else
                {
                    existingConfig.WorkModes.Add(activeWorkMode);
                    Console.WriteLine($"[WorkspaceAutoSave] Added WorkMode: {activeWorkMode.Title}");
                }

                return existingConfig;
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

                var currentConfig = CollectCurrentConfiguration();
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