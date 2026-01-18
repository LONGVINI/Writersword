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
using Writersword.Core.Models.Modules;
using Writersword.Core.Models.Project;
using Writersword.Resources.Localization;
using Writersword.Src.Core.Interfaces.Services.Storage;
using Writersword.Src.Core.Interfaces.Services.UI;
using Writersword.Src.Core.Interfaces.WorkFlows;
using Writersword.Src.Infrastructure.Services.Storage;
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
        private readonly IDataComparisonService _comparisonService;

        public event Action<DocumentTabViewModel>? ProjectOpened;
        public event Action<DocumentTabViewModel>? ProjectSaved;
        public event Action<DocumentTabViewModel>? ProjectClosed;

        private readonly Dictionary<string, ZipFileStorageService> _openStorages = new Dictionary<string, ZipFileStorageService>();

        public ProjectWorkflow(
               IProjectService projectService,
               ICacheService cacheService,
               IDialogService dialogService,
               ISettingsService settingsService,
               INotificationService notificationService,
               IDataComparisonService comparisonService)
        {
            _projectService = projectService;
            _cacheService = cacheService;
            _dialogService = dialogService;
            _settingsService = settingsService;
            _notificationService = notificationService;
            _comparisonService = comparisonService;
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

                        // СРАВНИВАЕМ данные в кеше и в файле
                        var savedProject = await _projectService.LoadAsync(filePath);
                        var cache = _cacheService.LoadCache(filePath);

                        bool dataIsSame = false;

                        if (savedProject != null && cache != null)
                        {
                            // Извлекаем CustomData из кеша
                            var cacheData = new Dictionary<string, object?>();
                            foreach (var kvp in cache)
                            {
                                if (kvp.Value.CustomData != null)
                                {
                                    cacheData[kvp.Key] = kvp.Value.CustomData;
                                }
                            }

                            // Сравниваем с данными из файла
                            dataIsSame = _comparisonService.AreDataEqual(cacheData, savedProject.ModulesData);

                            Console.WriteLine($"[ProjectWorkflow] Data comparison: {(dataIsSame ? "SAME" : "DIFFERENT")}");
                        }

                        // Если данные ОДИНАКОВЫЕ - НЕ показываем диалог
                        if (dataIsSame)
                        {
                            Console.WriteLine("[ProjectWorkflow] Data is identical, skipping Recovery dialog");
    
                            // НЕ удаляем кеш! Он актуален и будет использоваться CacheUpdateService
                            // Кеш удалится только при успешном Ctrl+S
    
                            project = savedProject;
                            recoveryChoice = RecoveryDialogResult.None;
                        }
                        else
                        {
                            // Данные РАЗНЫЕ - показываем Recovery диалог
                            Console.WriteLine("[ProjectWorkflow] Data differs, showing Recovery dialog");

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
                var cacheUpdateService = App.Services.GetRequiredService<ICacheUpdateService>();
                var tabVM = new DocumentTabViewModel(project, filePath, onClose: null, cacheUpdateService);

                // Передаём функцию получения активных модулей
                tabVM.SetActiveModulesProvider(() => mainViewModel.GetActiveModules());

                // Создаём ZipFileStorage для работы с файлами в ZIP
                if (!string.IsNullOrEmpty(filePath))
                {
                    var storage = new ZipFileStorageService(filePath);
                    _openStorages[filePath] = storage;
                    tabVM.Context.FileStorage = storage;
                    Console.WriteLine($"[ProjectWorkflow] ZipFileStorage created for: {filePath}");
                }

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
                    tabVM.RecoveryBanner = null;
                    tabVM.Context.IsInCompareMode = false;
                    Console.WriteLine("[ProjectWorkflow] No RecoveryBanner (not in Compare mode)");
                }

                // 6.Запускаем автосохранение ТОЛЬКО если НЕ в Compare mode
                if (recoveryChoice != RecoveryDialogResult.Compare)
                {
                    tabVM.StartCaching();
                    Console.WriteLine("[ProjectWorkflow] AutoSave started");
                }
                else
                {
                    Console.WriteLine("[ProjectWorkflow] AutoSave NOT started (Compare mode)");
                }

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
                    // Обновляем данные проекта
                    tab.UpdateProject(project);

                    // КРИТИЧЕСКИ ВАЖНО: Перезагружаем модули из новых данных!
                    await ReloadModulesFromProject(tab);

                    // Переключаем флаг
                    tab.RecoveryBanner.IsViewingCache = !isViewingCache;

                    Console.WriteLine($"[ProjectWorkflow] Switched version, now viewing: {(tab.RecoveryBanner.IsViewingCache ? "cache" : "saved")}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProjectWorkflow] ERROR switching version: {ex.Message}");
            }
        }

        /// <summary>
        /// Перезагрузить все активные модули из данных проекта
        /// Используется при переключении версий в Compare mode
        /// </summary>
        private async Task ReloadModulesFromProject(DocumentTabViewModel tab)
        {
            try
            {
                var mainViewModel = App.Services.GetRequiredService<MainWindowViewModel>();
                var activeModules = mainViewModel.GetActiveModules();
                var project = tab.GetProject();

                Console.WriteLine($"[ProjectWorkflow] Reloading {activeModules.Count} modules from project data");

                foreach (var module in activeModules)
                {
                    // Получаем данные модуля из проекта
                    if (project.ModulesData.TryGetValue(module.ModuleId.ToString(), out var data))
                    {
                        var state = new ModuleState
                        {
                            CustomData = data
                        };

                        // Перезагружаем состояние модуля
                        module.RestoreState(state);
                        Console.WriteLine($"[ProjectWorkflow] Reloaded module: {module.ModuleId}");
                    }
                    else
                    {
                        // Если данных нет - очищаем модуль
                        var emptyState = new ModuleState
                        {
                            CustomData = null
                        };
                        module.RestoreState(emptyState);
                        Console.WriteLine($"[ProjectWorkflow] Cleared module (no data): {module.ModuleId}");
                    }
                }

                Console.WriteLine("[ProjectWorkflow] All modules reloaded successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProjectWorkflow] ERROR reloading modules: {ex.Message}");
            }
        }

        /// <summary>Сохранить текущую версию и скрыть баннер</summary>
        private async Task SaveAndHideBannerAsync(DocumentTabViewModel tab)
        {
            // СНАЧАЛА скрываем баннер - сразу в UI потоке!
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Скрываем баннер СРАЗУ
                tab.RecoveryBanner = null;

                // Выходим из Compare mode
                tab.Context.IsInCompareMode = false;

                Console.WriteLine("[ProjectWorkflow] RecoveryBanner hidden BEFORE save");
            });

            // ПОТОМ сохраняем
            bool success = await SaveDocumentAsync(tab);

            if (success)
            {
                // Запускаем автосохранение
                tab.StartCaching();

                Console.WriteLine("[ProjectWorkflow] Saved and enabled editing");
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
                // Если просматриваем кеш - загружаем сохранённую версию
                if (tab.RecoveryBanner?.IsViewingCache == true)
                {
                    var project = await _projectService.LoadAsync(filePath);
                    if (project != null)
                    {
                        tab.UpdateProject(project);
                    }
                }

                // Удаляем кеш
                _cacheService.DeleteCache(filePath);

                // СНАЧАЛА скрываем баннер
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    // Скрываем баннер СРАЗУ
                    tab.RecoveryBanner = null;

                    // Выходим из Compare mode
                    tab.Context.IsInCompareMode = false;

                    Console.WriteLine("[ProjectWorkflow] RecoveryBanner hidden BEFORE delete");
                });

                // Если просматриваем кеш - загружаем сохранённую версию
                if (tab.RecoveryBanner?.IsViewingCache == true)  // Это уже null, но оставим на всякий случай
                {
                    var project = await _projectService.LoadAsync(filePath);
                    if (project != null)
                    {
                        tab.UpdateProject(project);
                    }
                }

                // Удаляем кеш
                _cacheService.DeleteCache(filePath);

                // Запускаем автосохранение
                tab.StartCaching();

                Console.WriteLine("[ProjectWorkflow] Cache discarded, editing enabled");
            }
        }

        /// <summary>Сохранить документ</summary>
        public async Task<bool> SaveDocumentAsync(DocumentTabViewModel tab)
        {
            try
            {
                var project = tab.GetProject();
                var filePath = tab.FilePath;

                if (string.IsNullOrEmpty(filePath))
                {
                    return await SaveAsDocumentAsync(tab);
                }

                Console.WriteLine($"[ProjectWorkflow] Saving project: {filePath}");

                var tabCollection = App.Services.GetRequiredService<ITabCollection>();
                var activeTab = tabCollection.ActiveTab;
                var stateCollector = App.Services.GetRequiredService<IModuleStateCollectorService>();

                Dictionary<string, object?> allData;

                if (tab == activeTab)
                {
                    Console.WriteLine($"[ProjectWorkflow] Saving ACTIVE tab: {tab.Title}");

                    var mainViewModel = App.Services.GetRequiredService<MainWindowViewModel>();
                    var activeModules = mainViewModel.GetActiveModules();
                    var currentCustomData = stateCollector.CollectCustomData(activeModules);

                    var cache = _cacheService.LoadCache(filePath);

                    allData = new Dictionary<string, object?>();

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

                    foreach (var kvp in currentCustomData)
                    {
                        allData[kvp.Key] = kvp.Value;
                    }

                    project.ModulesData = allData;
                    project.LastModified = DateTime.Now;

                    // Закрываем ZIP перед сохранением
                    tab.Context.CloseZipStorage();

                    bool success = await _projectService.SaveAsync(project, filePath);

                    // Открываем ZIP обратно
                    tab.Context.ReopenZipStorage();

                    if (success)
                    {
                        _cacheService.DeleteCache(filePath);
                        Console.WriteLine("[ProjectWorkflow] Project saved, cache deleted");
                        _notificationService.ShowSuccess(Strings.Notification_ProjectSaved);
                        ProjectSaved?.Invoke(tab);
                        return true;
                    }

                    return false;
                }
                else
                {
                    Console.WriteLine($"[ProjectWorkflow] Saving INACTIVE tab: {tab.Title}");

                    var cache = _cacheService.LoadCache(filePath);

                    if (cache == null || cache.Count == 0)
                    {
                        Console.WriteLine($"[ProjectWorkflow] ERROR: No cache for inactive tab!");
                        return false;
                    }

                    allData = new Dictionary<string, object?>();
                    foreach (var kvp in cache)
                    {
                        if (kvp.Value.CustomData != null)
                        {
                            allData[kvp.Key] = kvp.Value.CustomData;
                        }
                    }

                    project.ModulesData = allData;
                    project.LastModified = DateTime.Now;

                    // Закрываем ZIP перед сохранением
                    tab.Context.CloseZipStorage();

                    bool success = await _projectService.SaveAsync(project, filePath);

                    // Открываем ZIP обратно
                    tab.Context.ReopenZipStorage();

                    if (success)
                    {
                        _cacheService.DeleteCache(filePath);
                        Console.WriteLine("[ProjectWorkflow] Project saved, cache deleted");
                        _notificationService.ShowSuccess(Strings.Notification_ProjectSaved);
                        ProjectSaved?.Invoke(tab);
                        return true;
                    }

                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProjectWorkflow] ERROR saving project: {ex.Message}");

                // В случае ошибки переоткрываем ZIP
                try
                {
                    tab.Context.ReopenZipStorage();
                }
                catch { }

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
                    tab.StartCaching();

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
                tab.StartCaching();

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

                // Закрываем ZipFileStorage
                if (!string.IsNullOrEmpty(filePath) && _openStorages.TryGetValue(filePath, out var storage))
                {
                    storage.Dispose();
                    _openStorages.Remove(filePath);
                    Console.WriteLine($"[ProjectWorkflow] ZipFileStorage closed for: {filePath}");
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
        /// Сравнивает текущие данные с данными В ПАМЯТИ (не перезагружая файл!)
        /// </summary>
        public async Task<bool> HasUnsavedChanges(DocumentTabViewModel tab)
        {
            var filePath = tab.FilePath;

            // Если проект новый (нет пути) - проверяем есть ли данные в модулях
            if (string.IsNullOrEmpty(filePath))
            {
                var tabCollection = App.Services.GetRequiredService<ITabCollection>();
                var activeTab = tabCollection.ActiveTab;

                // Проверяем только если это активная вкладка
                if (tab == activeTab)
                {
                    var mainViewModel = App.Services.GetRequiredService<MainWindowViewModel>();
                    var activeModules = mainViewModel.GetActiveModules();
                    var stateCollector = App.Services.GetRequiredService<IModuleStateCollectorService>();

                    // Проверяем есть ли хоть какие-то данные в модулях
                    var currentData = stateCollector.CollectCustomData(activeModules);

                    // Проверяем есть ли РЕАЛЬНЫЕ данные (не пустые строки)
                    bool hasContent = currentData.Any(kvp =>
                    {
                        if (kvp.Value == null) return false;
                        if (kvp.Value is string str) return !string.IsNullOrWhiteSpace(str);
                        return true;
                    });

                    Console.WriteLine($"[ProjectWorkflow] HasUnsavedChanges (new project, active): {hasContent}");
                    return hasContent;
                }

                Console.WriteLine($"[ProjectWorkflow] HasUnsavedChanges (new project, inactive): false");
                return false;
            }

            try
            {
                var tabCollection = App.Services.GetRequiredService<ITabCollection>();
                var activeTab = tabCollection.ActiveTab;
                var stateCollector = App.Services.GetRequiredService<IModuleStateCollectorService>();

                Dictionary<string, object?> allCurrentData;

                // Если это АКТИВНАЯ вкладка - собираем CustomData из UI модулей + кеш
                if (tab == activeTab)
                {
                    var mainViewModel = App.Services.GetRequiredService<MainWindowViewModel>();
                    var activeModules = mainViewModel.GetActiveModules();

                    // Собираем ТОЛЬКО CustomData активных модулей
                    var activeCustomData = stateCollector.CollectCustomData(activeModules);

                    // Добавляем CustomData из кеша (закрытые модули/WorkMode)
                    allCurrentData = new Dictionary<string, object?>(activeCustomData);

                    var cache = _cacheService.LoadCache(filePath);
                    if (cache != null)
                    {
                        foreach (var kvp in cache)
                        {
                            // Если модуль не в активных - берём из кеша
                            if (!allCurrentData.ContainsKey(kvp.Key) && kvp.Value.CustomData != null)
                            {
                                allCurrentData[kvp.Key] = kvp.Value.CustomData;
                            }
                        }
                    }

                    Console.WriteLine($"[ProjectWorkflow] HasUnsavedChanges - active tab, collected {allCurrentData.Count} modules");
                }
                else
                {
                    // Если это НЕ активная вкладка - берём только из кеша
                    var cache = _cacheService.LoadCache(filePath);

                    if (cache == null || cache.Count == 0)
                    {
                        // Нет кеша = нет изменений
                        Console.WriteLine($"[ProjectWorkflow] HasUnsavedChanges ({tab.Title}, inactive): false (no cache)");
                        return false;
                    }

                    // Берём ТОЛЬКО CustomData из кеша
                    allCurrentData = new Dictionary<string, object?>();
                    foreach (var kvp in cache)
                    {
                        if (kvp.Value.CustomData != null)
                        {
                            allCurrentData[kvp.Key] = kvp.Value.CustomData;
                        }
                    }

                    Console.WriteLine($"[ProjectWorkflow] HasUnsavedChanges - inactive tab, loaded {allCurrentData.Count} modules from cache");
                }

                // КРИТИЧНО: Берём данные ИЗ ПАМЯТИ, а не перезагружаем файл!
                var savedProject = tab.GetProject();

                // Сравниваем CustomData
                bool hasChanges = !_comparisonService.AreDataEqual(allCurrentData, savedProject.ModulesData);

                Console.WriteLine($"[ProjectWorkflow] HasUnsavedChanges ({tab.Title}): {hasChanges}");
                return hasChanges;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProjectWorkflow] ERROR checking unsaved changes: {ex.Message}");
                // В случае ошибки считаем что изменений нет (безопаснее)
                return false;
            }
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