using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Models.Project;
using Writersword.Resources.Localization;
using Writersword.Src.Core.Interfaces.Services.Storage;
using Writersword.Src.Core.Interfaces.Services.UI;
using Writersword.Src.Core.Interfaces.WorkFlows;
using Writersword.ViewModels;
using Writersword.Views;

namespace Writersword.Src.Infrastructure.Services.Project
{
    /// <summary>
    /// Реализация сервиса управления жизненным циклом проектов
    /// Управляет открытием, сохранением, закрытием документов
    /// Обрабатывает восстановление из кеша
    /// </summary>
    public class ProjectWorkflow : IProjectWorkflow
    {
        private readonly IProjectService _projectService;
        private readonly ICacheService _cacheService;
        private readonly IDialogService _dialogService;
        private readonly ISettingsService _settingsService;
        private readonly INotificationService _notificationService;

        public event Action<DocumentTabViewModel>? ProjectOpened;
        public event Action<DocumentTabViewModel>? ProjectSaved;
        public event Action<DocumentTabViewModel>? ProjectClosed;

        public ProjectWorkflow(
               IProjectService projectService,
               ICacheService cacheService,
               IDialogService dialogService,
               ISettingsService settingsService,
               INotificationService notificationService)
        {
            _projectService = projectService;
            _cacheService = cacheService;
            _dialogService = dialogService;
            _settingsService = settingsService;
            _notificationService = notificationService;
        }

        /// <summary>Открыть документ с поддержкой восстановления из кеша</summary>
        public async Task<DocumentTabViewModel?> OpenDocumentAsync(string? filePath = null)
        {
            try
            {
                // 1. Если путь не указан - показываем диалог выбора файла
                if (string.IsNullOrEmpty(filePath))
                {
                    filePath = await _dialogService.OpenFileAsync();
                    if (string.IsNullOrEmpty(filePath))
                    {
                        Console.WriteLine("[ProjectWorkflow] Open cancelled by user");
                        return null;
                    }
                }

                Console.WriteLine($"[ProjectWorkflow] Opening project: {filePath}");

                // 2. Проверяем есть ли кеш
                ProjectFile? project = null;
                RecoveryDialogResult recoveryChoice = RecoveryDialogResult.None;

                if (_cacheService.HasCache(filePath))
                {
                    var cacheDate = _cacheService.GetCacheDate(filePath);
                    var saveDate = File.GetLastWriteTime(filePath);

                    if (cacheDate.HasValue)
                    {
                        Console.WriteLine($"[ProjectWorkflow] Cache found - Cache: {cacheDate}, Save: {saveDate}");

                        // Показываем Recovery диалог
                        recoveryChoice = await _dialogService.ShowRecoveryDialogAsync(
                            cacheDate.Value,
                            saveDate
                        );

                        Console.WriteLine($"[ProjectWorkflow] Recovery choice: {recoveryChoice}");

                        // Обрабатываем выбор пользователя
                        switch (recoveryChoice)
                        {
                            case RecoveryDialogResult.Restore:
                                // Восстановить из кеша
                                project = await LoadProjectWithCacheData(filePath);
                                _cacheService.DeleteCache(filePath);
                                Console.WriteLine("[ProjectWorkflow] Restored from cache (cache deleted)");
                                break;

                            case RecoveryDialogResult.OpenSaved:
                                // Открыть сохранённую версию
                                project = await _projectService.LoadAsync(filePath);
                                Console.WriteLine("[ProjectWorkflow] Opened saved version (cache remains)");
                                break;

                            case RecoveryDialogResult.Compare:
                                // Загрузить кеш для сравнения
                                project = await LoadProjectWithCacheData(filePath);
                                Console.WriteLine("[ProjectWorkflow] Compare mode - viewing cache");
                                break;

                            case RecoveryDialogResult.Cancel:
                                Console.WriteLine("[ProjectWorkflow] Open cancelled by user");
                                return null;
                        }
                    }
                }

                // 3. Если не из кеша - загружаем из файла
                if (project == null)
                {
                    project = await _projectService.LoadAsync(filePath);
                    if (project == null)
                    {
                        await _dialogService.ShowMessageAsync(
                            "Ошибка",
                            "Не удалось загрузить проект",
                            MessageBoxType.Error,
                            MessageBoxButtons.OK
                        );
                        return null;
                    }
                }

                // 4. Создаём вкладку с собственным AutoSaveService
                var mainViewModel = App.Services.GetRequiredService<MainWindowViewModel>();
                var autoSaveService = App.Services.GetRequiredService<IAutoSaveService>();
                var tabVM = new DocumentTabViewModel(project, filePath, onClose: null, autoSaveService);

                // Передаём функцию получения активных модулей
                tabVM.SetActiveModulesProvider(() => mainViewModel.GetActiveModules());

                // 5. Если режим Compare - создаём RecoveryBanner
                if (recoveryChoice == RecoveryDialogResult.Compare)
                {
                    var cacheDate = _cacheService.GetCacheDate(filePath);
                    var saveDate = File.GetLastWriteTime(filePath);

                    if (cacheDate.HasValue)
                    {
                        tabVM.RecoveryBanner = new RecoveryBannerViewModel(
                            onSwitchVersion: async () => await SwitchVersionAsync(tabVM, filePath),
                            onSave: async () => await SaveAndHideBannerAsync(tabVM),
                            onDiscard: async () => await DiscardCacheAsync(tabVM, filePath)
                        )
                        {
                            IsViewingCache = true,
                            CacheDate = cacheDate.Value,
                            SaveDate = saveDate
                        };

                        tabVM.Context.IsInCompareMode = true;
                        Console.WriteLine("[ProjectWorkflow] RecoveryBanner created (Compare mode)");
                    }
                }
                else
                {
                    // Если НЕ Compare - убеждаемся что баннера нет
                    tabVM.RecoveryBanner = null;
                    tabVM.Context.IsInCompareMode = false;
                    Console.WriteLine("[ProjectWorkflow] No RecoveryBanner (not in Compare mode)");
                }

                // 6. Запускаем автосохранение
                tabVM.StartAutoSave();

                // 7. Добавляем в недавние проекты
                _settingsService.AddRecentProject(filePath);

                Console.WriteLine($"[ProjectWorkflow] Project opened: {project.Title}");
                ProjectOpened?.Invoke(tabVM);

                _notificationService.ShowSuccess(Strings.Notification_ProjectOpened);

                return tabVM;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProjectWorkflow] ERROR opening project: {ex.Message}");
                await _dialogService.ShowMessageAsync(
                    "Ошибка",
                    $"Не удалось открыть проект: {ex.Message}",
                    MessageBoxType.Error,
                    MessageBoxButtons.OK
                );
                return null;
            }
        }

        /// <summary>Переключить между кешем и сохранённой версией</summary>
        private async Task SwitchVersionAsync(DocumentTabViewModel tab, string filePath)
        {
            try
            {
                if (tab.RecoveryBanner == null) return;

                var isViewingCache = tab.RecoveryBanner.IsViewingCache;
                ProjectFile? project;

                if (isViewingCache)
                {
                    // Переключаемся на сохранённую версию
                    project = await _projectService.LoadAsync(filePath);
                    Console.WriteLine("[ProjectWorkflow] Switched to saved version");
                }
                else
                {
                    // Переключаемся на кеш
                    project = await LoadProjectWithCacheData(filePath);
                    Console.WriteLine("[ProjectWorkflow] Switched to cache version");
                }

                if (project != null)
                {
                    // Обновляем содержимое вкладки
                    tab.Content = project.ModulesData.TryGetValue("TextEditor", out var text) && text is string str
                        ? str
                        : "";

                    // Переключаем флаг
                    tab.RecoveryBanner.IsViewingCache = !isViewingCache;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProjectWorkflow] ERROR switching version: {ex.Message}");
            }
        }

        /// <summary>Сохранить текущую версию и скрыть баннер</summary>
        private async Task SaveAndHideBannerAsync(DocumentTabViewModel tab)
        {
            bool success = await SaveDocumentAsync(tab);
            if (success)
            {
                // Скрываем баннер
                tab.RecoveryBanner = null;

                // Выходим из режима сравнения
                tab.Context.IsInCompareMode = false;

                Console.WriteLine("[ProjectWorkflow] Saved and hidden recovery banner");
            }
        }

        /// <summary>Удалить кеш с подтверждением</summary>
        private async Task DiscardCacheAsync(DocumentTabViewModel tab, string filePath)
        {
            var result = await _dialogService.ShowMessageAsync(
                "Удалить автосохранение?",
                "Автосохранённая версия будет удалена. Продолжить?",
                MessageBoxType.Warning,
                MessageBoxButtons.YesNo
            );

            if (result == MessageBoxResult.Yes)
            {
                // Если просматриваем кеш - переключаемся на сохранённую версию
                if (tab.RecoveryBanner?.IsViewingCache == true)
                {
                    var project = await _projectService.LoadAsync(filePath);
                    if (project != null)
                    {
                        tab.Content = project.ModulesData.TryGetValue("TextEditor", out var text) && text is string str
                            ? str
                            : "";
                    }
                }

                // Удаляем кеш
                _cacheService.DeleteCache(filePath);

                // Скрываем баннер
                tab.RecoveryBanner = null;

                // Выходим из режима сравнения
                tab.Context.IsInCompareMode = false;

                Console.WriteLine("[ProjectWorkflow] Cache discarded, banner hidden");
            }
        }

        /// <summary>Сохранить документ</summary>
        public async Task<bool> SaveDocumentAsync(DocumentTabViewModel tab)
        {
            try
            {
                var project = tab.GetProject();
                var filePath = tab.FilePath;

                // Если проект новый (нет пути) - вызываем SaveAs
                if (string.IsNullOrEmpty(filePath))
                {
                    return await SaveAsDocumentAsync(tab);
                }

                Console.WriteLine($"[ProjectWorkflow] Saving project: {filePath}");

                // 1. Собираем CustomData всех АКТИВНЫХ модулей
                var mainViewModel = App.Services.GetRequiredService<MainWindowViewModel>();
                var activeModules = mainViewModel.GetActiveModules();

                var stateCollector = App.Services.GetRequiredService<IModuleStateCollectorService>();
                var currentCustomData = stateCollector.CollectCustomData(activeModules);

                // 2. Загружаем кеш (данные из закрытых модулей/WorkMode)
                var cache = _cacheService.LoadCache(filePath);

                // 3. ОБЪЕДИНЯЕМ: кеш + текущие данные (приоритет у текущих!)
                var allData = new Dictionary<string, object?>();

                // Сначала добавляем данные из кеша
                if (cache != null)
                {
                    foreach (var kvp in cache)
                    {
                        if (kvp.Value.CustomData != null)
                        {
                            allData[kvp.Key] = kvp.Value.CustomData;
                        }
                    }
                }

                // Затем перезаписываем данными текущих модулей (приоритет выше!)
                foreach (var kvp in currentCustomData)
                {
                    allData[kvp.Key] = kvp.Value;
                }

                // 4. Обновляем проект
                project.ModulesData = allData;
                project.LastModified = DateTime.Now;

                // 5. Сохраняем через ProjectService
                bool success = await _projectService.SaveAsync(project, filePath);

                if (success)
                {
                    // 6. УДАЛЯЕМ кеш (всё сохранено!)
                    _cacheService.DeleteCache(filePath);
                    Console.WriteLine("[ProjectWorkflow] Project saved successfully, cache deleted");

                    _notificationService.ShowSuccess(Strings.Notification_ProjectSaved);

                    ProjectSaved?.Invoke(tab);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProjectWorkflow] ERROR saving project: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> SaveAsDocumentAsync(DocumentTabViewModel tab)
        {
            try
            {
                // Показываем диалог выбора места сохранения
                var filePath = await _dialogService.SaveFileAsync();
                if (string.IsNullOrEmpty(filePath))
                {
                    Console.WriteLine("[ProjectWorkflow] SaveAs cancelled by user");
                    return false;
                }

                Console.WriteLine($"[ProjectWorkflow] SaveAs: {filePath}");

                // Обновляем путь и заголовок
                tab.FilePath = filePath;
                tab.Title = Path.GetFileNameWithoutExtension(filePath);

                // Получаем проект и обновляем дату
                var project = tab.GetProject();
                project.LastModified = DateTime.Now;

                // Сохраняем
                bool success = await _projectService.SaveAsync(project, filePath);

                if (success)
                {
                    // Убираем RecoveryBanner
                    tab.RecoveryBanner = null;
                    Console.WriteLine("[ProjectWorkflow] RecoveryBanner cleared");

                    // Перезапускаем автосохранение для нового пути
                    tab.StartAutoSave();

                    // Добавляем в недавние
                    _settingsService.AddRecentProject(filePath);

                    Console.WriteLine("[ProjectWorkflow] SaveAs successful");
                    ProjectSaved?.Invoke(tab);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProjectWorkflow] ERROR SaveAs: {ex.Message}");
                await _dialogService.ShowMessageAsync(
                    "Ошибка",
                    $"Не удалось сохранить проект: {ex.Message}",
                    MessageBoxType.Error,
                    MessageBoxButtons.OK
                );
                return false;
            }
        }

        /// <summary>Закрыть документ</summary>
        public async Task<bool> CloseDocumentAsync(DocumentTabViewModel tab, bool force = false)
        {
            try
            {
                Console.WriteLine($"[ProjectWorkflow] Closing tab: {tab.Title}, force: {force}");

                // Проверяем несохранённые изменения
                if (!force && await HasUnsavedChanges(tab))
                {
                    var result = await _dialogService.ShowMessageAsync(
                        "Несохранённые изменения",
                        $"Документ \"{tab.Title}\" содержит несохранённые изменения.\n\nСохранить перед закрытием?",
                        MessageBoxType.Question,
                        MessageBoxButtons.YesNoCancel
                    );

                    if (result == MessageBoxResult.Cancel)
                    {
                        Console.WriteLine("[ProjectWorkflow] Close cancelled by user");
                        return false;
                    }

                    if (result == MessageBoxResult.Yes)
                    {
                        bool saved = await SaveDocumentAsync(tab);
                        if (!saved)
                            return false;
                    }
                }

                // Убираем RecoveryBanner перед закрытием
                tab.RecoveryBanner = null;
                Console.WriteLine("[ProjectWorkflow] RecoveryBanner cleared");

                // Останавливаем автосохранение
                tab.StopAutoSave();

                var filePath = tab.FilePath;

                // Закрываем проект в ProjectService
                if (!string.IsNullOrEmpty(filePath))
                {
                    var project = _projectService.GetProjectByPath(filePath);
                    if (project != null)
                    {
                        _projectService.CloseProject(project);
                    }
                }

                Console.WriteLine("[ProjectWorkflow] Tab closed successfully");
                ProjectClosed?.Invoke(tab);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProjectWorkflow] ERROR closing tab: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Проверить есть ли несохранённые изменения
        /// Сравнивает ТЕКУЩИЕ данные (активные модули + кеш) с СОХРАНЁННЫМ файлом
        /// </summary>
        public async Task<bool> HasUnsavedChanges(DocumentTabViewModel tab)
        {
            var filePath = tab.FilePath;

            // Если проект новый (нет пути) - проверяем есть ли данные в модулях
            if (string.IsNullOrEmpty(filePath))
            {
                var mainViewModel = App.Services.GetRequiredService<MainWindowViewModel>();
                var activeModules = mainViewModel.GetActiveModules();
                var stateCollector = App.Services.GetRequiredService<IModuleStateCollectorService>();
                var currentData = stateCollector.CollectCustomData(activeModules);

                // Есть изменения если есть хоть какие-то данные
                var hasContent = currentData.Any(kvp => kvp.Value != null);
                Console.WriteLine($"[ProjectWorkflow] HasUnsavedChanges (new project): {hasContent}");
                return hasContent;
            }

            try
            {
                // 1. Собираем данные ТЕКУЩИХ модулей (из UI!)
                var mainViewModel = App.Services.GetRequiredService<MainWindowViewModel>();
                var activeModules = mainViewModel.GetActiveModules();
                var stateCollector = App.Services.GetRequiredService<IModuleStateCollectorService>();
                var currentData = stateCollector.CollectCustomData(activeModules);

                Console.WriteLine($"[ProjectWorkflow] Collected {currentData.Count} modules from UI");

                // 2. Загружаем кеш (данные закрытых модулей)
                var cache = _cacheService.LoadCache(filePath);

                // 3. ОБЪЕДИНЯЕМ: кеш + текущие (приоритет у текущих!)
                var allCurrentData = new Dictionary<string, object?>();

                // Сначала данные из кеша
                if (cache != null)
                {
                    foreach (var kvp in cache)
                    {
                        if (kvp.Value.CustomData != null)
                        {
                            allCurrentData[kvp.Key] = kvp.Value.CustomData;
                        }
                    }
                    Console.WriteLine($"[ProjectWorkflow] Added {cache.Count} modules from cache");
                }

                // Затем текущие данные (перезаписывают кеш!)
                foreach (var kvp in currentData)
                {
                    allCurrentData[kvp.Key] = kvp.Value;
                    Console.WriteLine($"[ProjectWorkflow] Current data: {kvp.Key}");
                }

                // 4. Загружаем сохранённый проект ИЗ ФАЙЛА (не из памяти!)
                var savedProject = await _projectService.LoadAsync(filePath);
                if (savedProject == null)
                {
                    Console.WriteLine($"[ProjectWorkflow] HasUnsavedChanges: could not load saved project from file");
                    return true; // Если не можем загрузить - считаем что есть изменения
                }

                Console.WriteLine($"[ProjectWorkflow] Loaded saved project from file: {savedProject.ModulesData.Count} modules");

                // 5. Сравниваем данные
                var hasChanges = !AreDataEqual(allCurrentData, savedProject.ModulesData);

                Console.WriteLine($"[ProjectWorkflow] HasUnsavedChanges ({tab.Title}): {hasChanges}");
                return hasChanges;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProjectWorkflow] ERROR checking unsaved changes: {ex.Message}");
                return true; // В случае ошибки считаем что есть изменения (безопаснее)
            }
        }

        /// <summary>Сравнить два словаря данных модулей</summary>
        private bool AreDataEqual(Dictionary<string, object?> data1, Dictionary<string, object?> data2)
        {
            // Если разное количество ключей - не равны
            if (data1.Count != data2.Count)
            {
                Console.WriteLine($"[ProjectWorkflow] AreDataEqual: different count ({data1.Count} vs {data2.Count})");
                return false;
            }

            // Проверяем каждый ключ
            foreach (var kvp in data1)
            {
                if (!data2.TryGetValue(kvp.Key, out var value2))
                {
                    Console.WriteLine($"[ProjectWorkflow] AreDataEqual: key '{kvp.Key}' not found in data2");
                    return false; // Ключ отсутствует во втором словаре
                }

                // Простое сравнение (для object можно улучшить)
                if (!Equals(kvp.Value, value2))
                {
                    Console.WriteLine($"[ProjectWorkflow] AreDataEqual: values differ for key '{kvp.Key}'");
                    return false;
                }
            }

            Console.WriteLine($"[ProjectWorkflow] AreDataEqual: data is equal");
            return true;
        }

        /// <summary>
        /// Загрузить проект и объединить с данными из кеша
        /// </summary>
        private async Task<ProjectFile?> LoadProjectWithCacheData(string filePath)
        {
            var project = await _projectService.LoadAsync(filePath);
            if (project == null) return null;

            var cache = _cacheService.LoadCache(filePath);
            if (cache != null)
            {
                // Обновляем данные из кеша
                foreach (var kvp in cache)
                {
                    if (kvp.Value.CustomData != null)
                    {
                        project.ModulesData[kvp.Key] = kvp.Value.CustomData;
                    }
                }
            }

            return project;
        }
    }
}