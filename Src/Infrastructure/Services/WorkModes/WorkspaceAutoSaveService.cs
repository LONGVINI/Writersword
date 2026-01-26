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
using Writersword.ViewModels;

namespace Writersword.Src.Infrastructure.Services.WorkModes
{
    /// <summary>
    /// Сервис автоматического сохранения локальной конфигурации workspace
    /// Сохраняет изменения в workspace.json внутри ZIP спустя 5 секунд после последнего изменения
    /// Использует debounce для оптимизации (не сохраняет при каждом клике)
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
        /// <param name="projectPath">Путь к .writersword файлу</param>
        /// <param name="project">Экземпляр проекта</param>
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
            Console.WriteLine("[WorkspaceAutoSave] NotifyChange() called");
            Console.WriteLine($"  _isDisposed: {_isDisposed}");
            Console.WriteLine($"  _currentProject is null: {_currentProject == null}");
            Console.WriteLine($"  _currentProjectPath is null: {_currentProjectPath == null}");

            if (_isDisposed || _currentProject == null || _currentProjectPath == null)
            {
                Console.WriteLine("[WorkspaceAutoSave] EXITING EARLY - condition failed");
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
        /// Вызывается автоматически через 5 секунд после последнего изменения
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

                // Получаем актуальный FileStorage для проекта
                var projectWorkflow = App.Services.GetRequiredService<IProjectWorkflow>();
                var fileStorage = projectWorkflow.GetFileStorageForProject(_currentProjectPath);

                if (fileStorage == null)
                {
                    Console.WriteLine("[WorkspaceAutoSave] FileStorage not found");
                    return;
                }

                // Собираем текущую конфигурацию из UI
                var currentConfig = CollectCurrentConfiguration();

                if (currentConfig == null)
                {
                    Console.WriteLine("[WorkspaceAutoSave] Failed to collect configuration");
                    return;
                }

                // Сохраняем ТОЛЬКО workspace.json в ZIP
                var workspaceConfigService = App.Services.GetRequiredService<IWorkspaceConfigService>();
                var success = workspaceConfigService.SaveToZip(fileStorage, currentConfig);

                if (success)
                {
                    Console.WriteLine("[WorkspaceAutoSave] workspace.json saved successfully");
                }
                else
                {
                    Console.WriteLine("[WorkspaceAutoSave] Failed to save workspace.json");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WorkspaceAutoSave] Error saving configuration: {ex.Message}");
            }
        }

        /// <summary>
        /// Собрать текущую конфигурацию из UI (активной вкладки)
        /// Берёт информацию из IWorkModeService о текущих WorkMode
        /// </summary>
        private WorkspaceLocalConfig? CollectCurrentConfiguration()
        {
            try
            {
                var workModeService = App.Services.GetRequiredService<IWorkModeService>();

                // Получаем ТОЛЬКО активный WorkMode
                var activeWorkMode = workModeService.GetActiveWorkMode();

                if (activeWorkMode == null)
                {
                    Console.WriteLine("[WorkspaceAutoSave] No active WorkMode to save");
                    return null;
                }

                // Проверяем что есть DockLayout
                if (!activeWorkMode.Settings.CustomSettings.ContainsKey("DockLayout"))
                {
                    Console.WriteLine("[WorkspaceAutoSave] No DockLayout in active WorkMode");
                    return null;
                }

                // Загружаем существующий workspace.json (если есть)
                var projectWorkflow = App.Services.GetRequiredService<IProjectWorkflow>();
                var fileStorage = projectWorkflow.GetFileStorageForProject(_currentProjectPath!);
                var workspaceConfigService = App.Services.GetRequiredService<IWorkspaceConfigService>();

                WorkspaceLocalConfig? existingConfig = null;
                if (fileStorage != null)
                {
                    existingConfig = workspaceConfigService.LoadFromZip(fileStorage);
                }

                // Если конфига нет - создаём новый
                if (existingConfig == null)
                {
                    existingConfig = new WorkspaceLocalConfig
                    {
                        WorkModes = new List<WorkMode>()
                    };
                }

                // Ищем активный WorkMode в существующем конфиге
                var existingWorkMode = existingConfig.WorkModes
                    .FirstOrDefault(wm => wm.WorkModeId == activeWorkMode.WorkModeId);

                if (existingWorkMode != null)
                {
                    // Обновляем существующий
                    var index = existingConfig.WorkModes.IndexOf(existingWorkMode);
                    existingConfig.WorkModes[index] = activeWorkMode;
                    Console.WriteLine($"[WorkspaceAutoSave] Updated existing WorkMode: {activeWorkMode.Title}");
                }
                else
                {
                    // Добавляем новый
                    existingConfig.WorkModes.Add(activeWorkMode);
                    Console.WriteLine($"[WorkspaceAutoSave] Added new WorkMode: {activeWorkMode.Title}");
                }

                Console.WriteLine($"[WorkspaceAutoSave] Total WorkModes in config: {existingConfig.WorkModes.Count}");
                return existingConfig;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WorkspaceAutoSave] Error collecting configuration: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Принудительно сохранить конфигурацию СЕЙЧАС
        /// Используется при закрытии проекта
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

                // Получаем актуальный FileStorage для проекта
                var projectWorkflow = App.Services.GetRequiredService<IProjectWorkflow>();
                var fileStorage = projectWorkflow.GetFileStorageForProject(_currentProjectPath);

                if (fileStorage == null)
                {
                    Console.WriteLine("[WorkspaceAutoSave] FileStorage not found");
                    return;
                }

                // Отменяем таймер если был
                _debounceSubscription?.Dispose();

                // Собираем и сохраняем
                var currentConfig = CollectCurrentConfiguration();
                if (currentConfig != null)
                {
                    var workspaceConfigService = App.Services.GetRequiredService<IWorkspaceConfigService>();
                    var success = workspaceConfigService.SaveToZip(fileStorage, currentConfig);

                    if (success)
                    {
                        Console.WriteLine("[WorkspaceAutoSave] Force save successful");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WorkspaceAutoSave] Error in force save: {ex.Message}");
            }
        }

        /// <summary>Освободить ресурсы</summary>
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