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
using Writersword.Src.Core.Interfaces.Services;
using Writersword.Src.Core.Interfaces.Services.Storage;
using Writersword.Src.Core.Interfaces.Services.UI;
using Writersword.Src.Core.Interfaces.WorkFlows;
using Writersword.Src.Infrastructure.Dock;
using Writersword.Src.Infrastructure.Services.Storage;
using Writersword.Src.Infrastructure.Services.WorkModes;
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
        private readonly IZipCacheService _cacheService;
        private readonly IDialogService _dialogService;
        private readonly ISettingsService _settingsService;
        private readonly INotificationService _notificationService;
        private readonly IDataComparisonService _comparisonService;

        public event Action<DocumentTabViewModel>? ProjectOpened;
        public event Action<DocumentTabViewModel>? ProjectSaved;
        public event Action<DocumentTabViewModel>? ProjectClosed;

        private readonly Dictionary<string, ZipFileStorageService> _openStorages = new Dictionary<string, ZipFileStorageService>();
        private readonly Dictionary<string, IWorkspaceAutoSaveService> _autoSaveServices = new Dictionary<string, IWorkspaceAutoSaveService>();

        public ProjectWorkflow(
               IProjectService projectService,
               IZipCacheService cacheService,
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

                            // Кеш уже содержит CustomData напрямую
                            dataIsSame = _comparisonService.AreDataEqual(cache, savedProject.ModulesData);

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
                var tabVM = new DocumentTabViewModel(project, filePath, onClose: null);

                // Создаём ZipFileStorage для работы с файлами в ZIP
                ZipFileStorageService? storage = null;
                if (!string.IsNullOrEmpty(filePath))
                {
                    storage = new ZipFileStorageService(filePath);
                    _openStorages[filePath] = storage;
                    tabVM.Context.FileStorage = storage;
                    Console.WriteLine($"[ProjectWorkflow] ZipFileStorage created for: {filePath}");

                    // Загружаем локальную конфигурацию workspace.json из ZIP
                    var workModeConfigService = App.Services.GetRequiredService<IWorkModeConfigurationService>();
                    var workModes = workModeConfigService.LoadConfiguration(project.Type, storage);

                    // Сохраняем загруженные WorkModes в проект
                    project.WorkModes = workModes;
                    Console.WriteLine($"[ProjectWorkflow] Loaded {workModes.Count} WorkModes for project");

                    // Создаём и запускаем WorkspaceAutoSaveService для этого проекта
                    var autoSaveService = App.Services.GetRequiredService<IWorkspaceAutoSaveService>();
                    autoSaveService.Start(filePath, project);
                    _autoSaveServices[filePath] = autoSaveService;
                    Console.WriteLine($"[ProjectWorkflow] WorkspaceAutoSave started for: {filePath}");
                }

                // 5. Если режим Compare - создаём RecoveryBanner
                if (recoveryChoice == RecoveryDialogResult.Compare)
                {
                    var cacheDate = _cacheService.GetCacheDate(filePath);
                    var saveDate = File.GetLastWriteTime(filePath);

                    if (cacheDate.HasValue)
                    {
                        // Захватываем локальные копии для замыкания
                        var capturedTab = tabVM;
                        var capturedPath = filePath;

                        tabVM.RecoveryBanner = new RecoveryBannerViewModel(
                            onSwitchVersion: async () => await SwitchVersionAsync(capturedTab, capturedPath),
                            onSave: async () => await SaveAndHideBannerAsync(capturedTab),
                            onDiscard: async () => await DiscardCacheAsync(capturedTab, capturedPath)
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

                // 6. Добавляем в недавние проекты
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
                    if (project.ModulesData.TryGetValue(module.ModuleId.ToString(), out var data))
                    {
                        module.SetCustomData(data);
                        Console.WriteLine($"[ProjectWorkflow] Reloaded module: {module.ModuleId}");
                    }
                    else
                    {
                        module.SetCustomData(null);
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

        /// <summary>Удалить кеш с подтверждением</summary>
        private async Task DiscardCacheAsync(DocumentTabViewModel tab, string filePath)
        {
            try
            {
                // Захватываем локальные копии для защиты от изменения
                var capturedTab = tab;
                var capturedPath = filePath;

                var result = await _dialogService.ShowMessageAsync(
                    "Удалить автосохранение?",
                    "Автосохранённая версия будет удалена. Продолжить?",
                    MessageBoxType.Warning,
                    MessageBoxButtons.YesNo
                );

                if (result == MessageBoxResult.Yes)
                {
                    if (capturedTab.RecoveryBanner?.IsViewingCache == true)
                    {
                        capturedTab.Context.CloseZipStorage();

                        try
                        {
                            var project = await _projectService.LoadAsync(capturedPath);
                            if (project != null)
                            {
                                capturedTab.UpdateProject(project);
                                await ReloadModulesFromProject(capturedTab);
                            }
                        }
                        finally
                        {
                            capturedTab.Context.ReopenZipStorage();
                        }
                    }

                    _cacheService.DeleteCache(capturedPath);

                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        capturedTab.Context.IsInCompareMode = false;
                        capturedTab.RecoveryBanner = null;
                        Console.WriteLine("[ProjectWorkflow] CompareMode disabled, RecoveryBanner hidden");
                    });

                    var mainViewModel = App.Services.GetRequiredService<MainWindowViewModel>();
                    var activeModules = mainViewModel.GetActiveModules();

                    foreach (var module in activeModules)
                    {
                        module.RefreshFromContext();
                    }

                    Console.WriteLine("[ProjectWorkflow] Cache discarded, editing enabled");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProjectWorkflow] ERROR DiscardCache: {ex.Message}");

                try
                {
                    tab.Context.ReopenZipStorage();
                }
                catch { }
            }
        }


        /// <summary>Сохранить текущую версию и скрыть баннер</summary>
        private async Task SaveAndHideBannerAsync(DocumentTabViewModel tab)
        {
            try
            {
                // Захватываем локальную копию для защиты от изменения
                var capturedTab = tab;

                bool success = await SaveDocumentAsync(capturedTab);

                if (success)
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        capturedTab.Context.IsInCompareMode = false;
                        capturedTab.RecoveryBanner = null;
                        Console.WriteLine("[ProjectWorkflow] CompareMode disabled, RecoveryBanner hidden");
                    });

                    var mainViewModel = App.Services.GetRequiredService<MainWindowViewModel>();
                    var activeModules = mainViewModel.GetActiveModules();

                    foreach (var module in activeModules)
                    {
                        module.RefreshFromContext();
                    }

                    Console.WriteLine("[ProjectWorkflow] Saved and enabled editing");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProjectWorkflow] ERROR SaveAndHideBanner: {ex.Message}");
            }
        }


        /// <summary>Переключить между кешем и сохранённой версией</summary>
        private async Task SwitchVersionAsync(DocumentTabViewModel tab, string filePath)
        {
            try
            {
                // Захватываем локальные копии для защиты от изменения
                var capturedTab = tab;
                var capturedPath = filePath;

                if (capturedTab.RecoveryBanner == null) return;

                var isViewingCache = capturedTab.RecoveryBanner.IsViewingCache;
                ProjectFile? project;

                capturedTab.Context.CloseZipStorage();

                try
                {
                    if (isViewingCache)
                    {
                        project = await _projectService.LoadAsync(capturedPath);
                        Console.WriteLine("[ProjectWorkflow] Switched to saved version");
                    }
                    else
                    {
                        project = await LoadProjectWithCacheData(capturedPath);
                        Console.WriteLine("[ProjectWorkflow] Switched to cache version");
                    }

                    if (project != null)
                    {
                        capturedTab.UpdateProject(project);
                        await ReloadModulesFromProject(capturedTab);
                        capturedTab.RecoveryBanner.IsViewingCache = !isViewingCache;

                        Console.WriteLine($"[ProjectWorkflow] Switched version, now viewing: {(capturedTab.RecoveryBanner.IsViewingCache ? "cache" : "saved")}");
                    }
                }
                finally
                {
                    capturedTab.Context.ReopenZipStorage();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProjectWorkflow] ERROR switching version: {ex.Message}");

                try
                {
                    tab.Context.ReopenZipStorage();
                }
                catch { }
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

                    // Собираем ТОЛЬКО CustomData из активных модулей
                    var activeCustomData = stateCollector.CollectCustomData(activeModules);

                    var cache = _cacheService.LoadCache(filePath);

                    allData = new Dictionary<string, object?>();

                    // Добавляем данные из кеша (для неактивных модулей)
                    if (cache != null)
                    {
                        foreach (var kvp in cache)
                        {
                            allData[kvp.Key] = kvp.Value; // CustomData из кеша
                        }
                    }

                    // Перезаписываем данными из активных модулей (приоритет у текущих)
                    foreach (var kvp in activeCustomData)
                    {
                        allData[kvp.Key] = kvp.Value; // CustomData из UI
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

                    // Кеш уже содержит CustomData напрямую
                    foreach (var kvp in cache)
                    {
                        allData[kvp.Key] = kvp.Value; // CustomData из кеша
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

                tab.RecoveryBanner = null;
                Console.WriteLine("[ProjectWorkflow] RecoveryBanner cleared");

                var filePath = tab.FilePath;

                if (!string.IsNullOrEmpty(filePath))
                {
                    if (_autoSaveServices.TryGetValue(filePath, out var autoSaveService))
                    {
                        await autoSaveService.SaveNowAsync();
                        autoSaveService.Dispose();
                        _autoSaveServices.Remove(filePath);
                        Console.WriteLine($"[ProjectWorkflow] WorkspaceAutoSave stopped for: {filePath}");
                    }

                    tab.Context.CloseZipStorage();
                    Console.WriteLine($"[ProjectWorkflow] ZipStorage closed for context");

                    // Отписываемся от событий DockFactory
                    var dockFactory = App.Services.GetRequiredService<DockFactory>();
                    dockFactory.UnsubscribeFromDockEvents(filePath);
                    Console.WriteLine($"[ProjectWorkflow] DockFactory unsubscribed from: {filePath}");

                    var project = _projectService.GetProjectByPath(filePath);
                    if (project != null)
                    {
                        _projectService.CloseProject(project);
                    }
                }

                // Уничтожаем все модули проекта
                tab.Dispose();
                Console.WriteLine("[ProjectWorkflow] All modules disposed via tab.Dispose()");

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
        /// Проверяет наличие несохранённых изменений в документе
        /// Сравнивает текущие данные модулей с данными из сохранённого файла
        /// </summary>
        /// <param name="tab">Вкладка документа для проверки</param>
        /// <returns>true если есть несохранённые изменения, иначе false</returns>
        public async Task<bool> HasUnsavedChanges(DocumentTabViewModel tab)
        {
            var filePath = tab.FilePath;

            // Новый проект (нет пути к файлу)
            if (string.IsNullOrEmpty(filePath))
            {
                var tabCollection = App.Services.GetRequiredService<ITabCollection>();
                var activeTab = tabCollection.ActiveTab;

                // Если это активная вкладка - проверяем наличие контента
                if (tab == activeTab)
                {
                    var mainViewModel = App.Services.GetRequiredService<MainWindowViewModel>();
                    var activeModules = mainViewModel.GetActiveModules();
                    var stateCollector = App.Services.GetRequiredService<IModuleStateCollectorService>();

                    // Собираем CustomData из активных модулей
                    var currentData = stateCollector.CollectCustomData(activeModules);

                    // Если хоть один модуль содержит данные - есть несохранённые изменения
                    bool hasContent = currentData.Any(kvp =>
                    {
                        if (kvp.Value == null) return false;
                        if (kvp.Value is string str) return !string.IsNullOrWhiteSpace(str);
                        return true;
                    });

                    Console.WriteLine($"[ProjectWorkflow] HasUnsavedChanges (new project, active): {hasContent}");
                    return hasContent;
                }

                // Неактивная новая вкладка - изменений нет
                Console.WriteLine($"[ProjectWorkflow] HasUnsavedChanges (new project, inactive): false");
                return false;
            }

            try
            {
                var tabCollection = App.Services.GetRequiredService<ITabCollection>();
                var activeTab = tabCollection.ActiveTab;
                var stateCollector = App.Services.GetRequiredService<IModuleStateCollectorService>();

                Dictionary<string, object?> allCurrentData;

                // Если это АКТИВНАЯ вкладка - собираем данные из UI модулей + кеша
                if (tab == activeTab)
                {
                    var mainViewModel = App.Services.GetRequiredService<MainWindowViewModel>();
                    var activeModules = mainViewModel.GetActiveModules();

                    // Собираем ТОЛЬКО CustomData из активных UI модулей
                    var activeCustomData = stateCollector.CollectCustomData(activeModules);

                    // Создаём словарь для объединения данных
                    allCurrentData = new Dictionary<string, object?>(activeCustomData);

                    // Добавляем данные из кеша (для неактивных/закрытых модулей)
                    var cache = _cacheService.LoadCache(filePath);
                    if (cache != null)
                    {
                        foreach (var kvp in cache)
                        {
                            // Если модуля нет в активных - берём его данные из кеша
                            if (!allCurrentData.ContainsKey(kvp.Key) && kvp.Value != null)
                            {
                                allCurrentData[kvp.Key] = kvp.Value; // Кеш уже содержит CustomData
                            }
                        }
                    }

                    // Фильтруем модули с пустыми данными
                    var nonEmptyData = allCurrentData
                        .Where(kvp =>
                            kvp.Value != null &&
                            !(kvp.Value is string str && string.IsNullOrWhiteSpace(str))
                        )
                        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                    Console.WriteLine($"[ProjectWorkflow] HasUnsavedChanges - active tab, collected {nonEmptyData.Count} non-empty modules");

                    // Если нет реальных данных - нет изменений
                    if (nonEmptyData.Count == 0)
                    {
                        Console.WriteLine($"[ProjectWorkflow] HasUnsavedChanges ({tab.Title}): False (no data)");
                        return false;
                    }

                    allCurrentData = nonEmptyData;
                }
                else
                {
                    // Если это НЕ активная вкладка - берём данные ТОЛЬКО из кеша
                    var cache = _cacheService.LoadCache(filePath);

                    if (cache == null || cache.Count == 0)
                    {
                        // Нет кеша = нет изменений (вкладка не была активирована)
                        Console.WriteLine($"[ProjectWorkflow] HasUnsavedChanges ({tab.Title}, inactive): false (no cache)");
                        return false;
                    }

                    // Кеш уже содержит CustomData напрямую
                    allCurrentData = new Dictionary<string, object?>(cache);

                    // Фильтруем модули с пустыми данными
                    var nonEmptyData = allCurrentData
                        .Where(kvp =>
                            kvp.Value != null &&
                            !(kvp.Value is string str && string.IsNullOrWhiteSpace(str))
                        )
                        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                    Console.WriteLine($"[ProjectWorkflow] HasUnsavedChanges - inactive tab, loaded {nonEmptyData.Count} non-empty modules from cache");

                    // Если нет реальных данных - нет изменений
                    if (nonEmptyData.Count == 0)
                    {
                        Console.WriteLine($"[ProjectWorkflow] HasUnsavedChanges ({tab.Title}, inactive): false (no data in cache)");
                        return false;
                    }

                    allCurrentData = nonEmptyData;
                }

                // Закрываем ZIP перед чтением файла
                tab.Context.CloseZipStorage();
                Console.WriteLine($"[ProjectWorkflow] ZIP closed for comparison");

                // Загружаем свежие данные напрямую из ZIP файла
                var savedProject = await _projectService.LoadAsync(filePath);

                // Переоткрываем ZIP после чтения
                tab.Context.ReopenZipStorage();
                Console.WriteLine($"[ProjectWorkflow] ZIP reopened after comparison");

                // Проверка на успешную загрузку файла
                if (savedProject == null)
                {
                    Console.WriteLine($"[ProjectWorkflow] HasUnsavedChanges - failed to load project from file");
                    return false;
                }

                // ModulesData уже содержит CustomData напрямую
                var savedCustomData = savedProject.ModulesData;

                // Сравниваем ТОЛЬКО CustomData (игнорируем SessionData)
                bool hasChanges = !_comparisonService.AreDataEqual(allCurrentData, savedCustomData);

                Console.WriteLine($"[ProjectWorkflow] HasUnsavedChanges ({tab.Title}): {hasChanges}");
                return hasChanges;
            }
            catch (Exception ex)
            {
                // В случае ошибки пытаемся переоткрыть ZIP
                try
                {
                    tab.Context.ReopenZipStorage();
                    Console.WriteLine($"[ProjectWorkflow] ZIP reopened after error");
                }
                catch { }

                // В случае ошибки считаем что изменений нет (безопасный fallback)
                Console.WriteLine($"[ProjectWorkflow] ERROR checking unsaved changes: {ex.Message}");
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
                // Кеш уже содержит CustomData напрямую
                foreach (var kvp in cache)
                {
                    if (kvp.Value != null)
                    {
                        project.ModulesData[kvp.Key] = kvp.Value;
                    }
                }
            }

            return project;
        }

        /// <summary>
        /// Получить WorkspaceAutoSaveService для указанного проекта
        /// Используется при переключении табов для немедленного сохранения
        /// </summary>
        public IWorkspaceAutoSaveService? GetAutoSaveServiceForProject(string filePath)
        {
            if (_autoSaveServices.TryGetValue(filePath, out var service))
            {
                return service;
            }

            Console.WriteLine($"[ProjectWorkflow] WARNING: No AutoSaveService found for: {filePath}");
            return null;
        }

        /// <summary>
        /// Получить FileStorage для указанного проекта
        /// Используется WorkspaceAutoSaveService для сохранения workspace.json
        /// </summary>
        public IProjectFileStorage? GetFileStorageForProject(string filePath)
        {
            if (_openStorages.TryGetValue(filePath, out var storage))
            {
                return storage;
            }

            Console.WriteLine($"[ProjectWorkflow] WARNING: No FileStorage found for: {filePath}");
            return null;
        }

        public void RegisterStorage(string filePath, DocumentTabViewModel tab)
        {
            var storage = new ZipFileStorageService(filePath);
            _openStorages[filePath] = storage;
            tab.Context.FileStorage = storage;

            var project = tab.GetProject();

            var workModeConfigService = App.Services.GetRequiredService<IWorkModeConfigurationService>();
            var workModes = workModeConfigService.LoadConfiguration(project.Type, storage);
            project.WorkModes = workModes;

            var autoSaveService = App.Services.GetRequiredService<IWorkspaceAutoSaveService>();
            autoSaveService.Start(filePath, project);
            _autoSaveServices[filePath] = autoSaveService;

            Console.WriteLine($"[ProjectWorkflow] Storage registered for: {filePath}");
        }

        public void UpdateStorageForProject(string filePath, IProjectFileStorage newStorage)
        {
            if (_openStorages.ContainsKey(filePath))
            {
                _openStorages[filePath] = (ZipFileStorageService)newStorage;
                Console.WriteLine($"[ProjectWorkflow] Storage updated for: {filePath}");
            }
        }
    }
}