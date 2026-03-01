using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Models.Project;
using Writersword.Core.Models.Settings;
using Writersword.Resources.Localization;
using Writersword.Src.Core.Interfaces.Services;
using Writersword.Src.Core.Interfaces.Services.Storage;
using Writersword.Src.Core.Interfaces.Services.UI;
using Writersword.Src.Core.Interfaces.WorkFlows;
using Writersword.Src.Infrastructure.Dock;
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
        private readonly ILogger<ProjectWorkflow> _logger;
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
            _logger = App.Services.GetService<ILogger<ProjectWorkflow>>()!;
            _projectService = projectService;
            _cacheService = cacheService;
            _dialogService = dialogService;
            _settingsService = settingsService;
            _notificationService = notificationService;
            _comparisonService = comparisonService;
        }

        /// <summary>
        /// Открыть документ с поддержкой восстановления из кеша
        /// </summary>
        /// <param name="filePath">Путь к файлу проекта</param>
        /// <param name="initializeWorkspace">Инициализировать workspace сразу (false для lazy loading)</param>
        public async Task<DocumentTabViewModel?> OpenDocumentAsync(string? filePath = null, bool initializeWorkspace = true)
        {
            try
            {
                // 1. Если путь не указан - показываем диалог выбора файла
                if (string.IsNullOrEmpty(filePath))
                {
                    filePath = await _dialogService.OpenFileAsync();
                    if (string.IsNullOrEmpty(filePath))
                    {
                        _logger.LogDebug("Open cancelled by user");
                        return null;
                    }
                }

                _logger.LogDebug("Opening project: {FilePath}, InitializeWorkspace: {Init}", filePath, initializeWorkspace);

                // 2. Проверяем есть ли кеш (ТОЛЬКО если инициализируем workspace)
                ProjectFile? project = null;
                RecoveryDialogResult recoveryChoice = RecoveryDialogResult.None;

                if (initializeWorkspace && _cacheService.HasCache(filePath))
                {
                    var cacheDate = _cacheService.GetCacheDate(filePath);
                    var saveDate = File.GetLastWriteTime(filePath);

                    if (cacheDate.HasValue)
                    {
                        _logger.LogDebug("Cache found - Cache: {CacheDate}, Save: {SaveDate}", cacheDate, saveDate);

                        var savedProject = await _projectService.LoadAsync(filePath);
                        var cache = _cacheService.LoadCache(filePath);

                        bool dataIsSame = false;

                        if (savedProject != null && cache != null)
                        {
                            dataIsSame = _comparisonService.AreDataEqual(cache, savedProject.ModulesData);
                            _logger.LogDebug("Data comparison: {Comparison}", dataIsSame ? "SAME" : "DIFFERENT");
                        }

                        if (dataIsSame)
                        {
                            _logger.LogDebug("Data is identical, skipping Recovery dialog");
                            project = savedProject;
                            recoveryChoice = RecoveryDialogResult.None;
                        }
                        else
                        {
                            _logger.LogDebug("Data differs, showing Recovery dialog");

                            recoveryChoice = await _dialogService.ShowRecoveryDialogAsync(
                                cacheDate.Value,
                                saveDate
                            );

                            _logger.LogDebug("Recovery choice: {Choice}", recoveryChoice);

                            switch (recoveryChoice)
                            {
                                case RecoveryDialogResult.Restore:
                                    project = await LoadProjectWithCacheData(filePath);
                                    _cacheService.DeleteCache(filePath);
                                    _logger.LogDebug("Restored from cache (cache deleted)");
                                    break;

                                case RecoveryDialogResult.OpenSaved:
                                    project = await _projectService.LoadAsync(filePath);
                                    _logger.LogDebug("Opened saved version (cache remains)");
                                    break;

                                case RecoveryDialogResult.Compare:
                                    project = await LoadProjectWithCacheData(filePath);
                                    _logger.LogDebug("Compare mode - viewing cache");
                                    break;

                                case RecoveryDialogResult.Cancel:
                                    _logger.LogDebug("Open cancelled by user");
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

                // 4. Создаём вкладку
                var tabVM = new DocumentTabViewModel(project, filePath, onClose: null);

                // 5. Если НЕ инициализируем workspace - возвращаем "пустую" вкладку
                if (!initializeWorkspace)
                {
                    _logger.LogDebug("Created LAZY tab (workspace not initialized): {Title}", project.Title);
                    _settingsService.AddRecentProject(filePath);
                    return tabVM;
                }

                // 6. Инициализируем workspace (для первой вкладки или при ручном открытии)
                var storage = new ZipFileStorageService(filePath);
                _openStorages[filePath] = storage;
                tabVM.Context.FileStorage = storage;
                _logger.LogDebug("ZipFileStorage created for: {FilePath}", filePath);

                var workModeConfigService = App.Services.GetRequiredService<IWorkModeConfigurationService>();
                var workModes = workModeConfigService.LoadConfiguration(project.Type, storage);
                project.WorkModes = workModes;
                _logger.LogDebug("Loaded {Count} WorkModes for project", workModes.Count);

                var autoSaveService = App.Services.GetRequiredService<IWorkspaceAutoSaveService>();
                _autoSaveServices[filePath] = autoSaveService;
                _logger.LogDebug("WorkspaceAutoSave created for: {FilePath}", filePath);

                tabVM.InitializeWorkspace(workModes);
                _logger.LogDebug("WorkspaceController initialized (NOT activated yet) for: {FilePath}", filePath);

                // 7. Если режим Compare - создаём RecoveryBanner
                if (recoveryChoice == RecoveryDialogResult.Compare)
                {
                    var cacheDate = _cacheService.GetCacheDate(filePath);
                    var saveDate = File.GetLastWriteTime(filePath);

                    if (cacheDate.HasValue)
                    {
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

                        var cacheUpdateService = App.Services.GetRequiredService<ICacheUpdateService>();
                        cacheUpdateService.Stop();
                        _logger.LogDebug("Compare mode enabled, cache disabled");
                    }
                }
                else
                {
                    tabVM.RecoveryBanner = null;
                    tabVM.Context.IsInCompareMode = false;
                    _logger.LogDebug("No RecoveryBanner (not in Compare mode)");
                }

                // 8. Добавляем в недавние проекты
                _settingsService.AddRecentProject(filePath);

                _logger.LogInformation("Project opened: {Title}", project.Title);
                ProjectOpened?.Invoke(tabVM);

                _notificationService.ShowSuccess(Strings.Notification_ProjectOpened);

                return tabVM;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error opening project");
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
        /// Инициализировать workspace для ленивой вкладки
        /// Вызывается при первом переключении на вкладку
        /// </summary>
        /// <summary>
        public async Task<bool> EnsureWorkspaceInitialized(DocumentTabViewModel tab)
        {
            if (tab.IsLoaded)
            {
                _logger.LogDebug("Workspace already loaded for: {Title}", tab.Title);
                return true;
            }

            var filePath = tab.FilePath;
            if (string.IsNullOrEmpty(filePath))
            {
                _logger.LogWarning("Cannot initialize workspace - no file path");
                return false;
            }

            try
            {
                _logger.LogDebug("Lazy loading workspace for: {Title}", tab.Title);

                ProjectFile? project = null;
                RecoveryDialogResult recoveryChoice = RecoveryDialogResult.None;

                if (_cacheService.HasCache(filePath))
                {
                    var cacheDate = _cacheService.GetCacheDate(filePath);
                    var saveDate = File.GetLastWriteTime(filePath);

                    if (cacheDate.HasValue)
                    {
                        _logger.LogDebug("Cache found for lazy tab - Cache: {CacheDate}, Save: {SaveDate}", cacheDate, saveDate);

                        var savedProject = await _projectService.LoadAsync(filePath);
                        var cache = _cacheService.LoadCache(filePath);

                        bool dataIsSame = false;

                        if (savedProject != null && cache != null)
                        {
                            dataIsSame = _comparisonService.AreDataEqual(cache, savedProject.ModulesData);
                            _logger.LogDebug("Data comparison: {Comparison}", dataIsSame ? "SAME" : "DIFFERENT");
                        }

                        if (dataIsSame)
                        {
                            _logger.LogDebug("Data is identical, skipping Recovery dialog");
                            project = savedProject;
                            recoveryChoice = RecoveryDialogResult.None;
                        }
                        else
                        {
                            _logger.LogDebug("Data differs, showing Recovery dialog");

                            recoveryChoice = await _dialogService.ShowRecoveryDialogAsync(
                                cacheDate.Value,
                                saveDate
                            );

                            _logger.LogDebug("Recovery choice: {Choice}", recoveryChoice);

                            switch (recoveryChoice)
                            {
                                case RecoveryDialogResult.Restore:
                                    project = await LoadProjectWithCacheData(filePath);
                                    _cacheService.DeleteCache(filePath);
                                    _logger.LogDebug("Restored from cache (cache deleted)");
                                    break;

                                case RecoveryDialogResult.OpenSaved:
                                    project = await _projectService.LoadAsync(filePath);
                                    _logger.LogDebug("Opened saved version (cache remains)");
                                    break;

                                case RecoveryDialogResult.Compare:
                                    project = await LoadProjectWithCacheData(filePath);
                                    _logger.LogDebug("Compare mode - viewing cache");
                                    break;

                                case RecoveryDialogResult.Cancel:
                                    _logger.LogDebug("Open cancelled by user");
                                    return false;
                            }
                        }
                    }
                }

                if (project == null)
                {
                    project = await _projectService.LoadAsync(filePath);
                    if (project == null)
                    {
                        _logger.LogError("Failed to load project: {FilePath}", filePath);
                        return false;
                    }
                }

                tab.UpdateProject(project);

                var storage = new ZipFileStorageService(filePath);
                _openStorages[filePath] = storage;
                tab.Context.FileStorage = storage;
                _logger.LogDebug("ZipFileStorage created for: {FilePath}", filePath);

                var workModeConfigService = App.Services.GetRequiredService<IWorkModeConfigurationService>();
                var workModes = workModeConfigService.LoadConfiguration(project.Type, storage);
                project.WorkModes = workModes;
                _logger.LogDebug("Loaded {Count} WorkModes for project", workModes.Count);

                var autoSaveService = App.Services.GetRequiredService<IWorkspaceAutoSaveService>();
                _autoSaveServices[filePath] = autoSaveService;
                _logger.LogDebug("WorkspaceAutoSave created for: {FilePath}", filePath);

                tab.InitializeWorkspace(workModes);
                _logger.LogDebug("WorkspaceController lazy initialized for: {FilePath}", filePath);

                if (recoveryChoice == RecoveryDialogResult.Compare)
                {
                    var cacheDate = _cacheService.GetCacheDate(filePath);
                    var saveDate = File.GetLastWriteTime(filePath);

                    if (cacheDate.HasValue)
                    {
                        var capturedTab = tab;
                        var capturedPath = filePath;

                        capturedTab.RecoveryBanner = new RecoveryBannerViewModel(
                            onSwitchVersion: async () => await SwitchVersionAsync(capturedTab, capturedPath),
                            onSave: async () => await SaveAndHideBannerAsync(capturedTab),
                            onDiscard: async () => await DiscardCacheAsync(capturedTab, capturedPath)
                        )
                        {
                            IsViewingCache = true,
                            CacheDate = cacheDate.Value,
                            SaveDate = saveDate
                        };

                        capturedTab.Context.IsInCompareMode = true;

                        var cacheUpdateService = App.Services.GetRequiredService<ICacheUpdateService>();
                        cacheUpdateService.Stop();
                        _logger.LogDebug("Compare mode enabled for lazy tab, cache disabled");
                    }
                }
                else
                {
                    tab.RecoveryBanner = null;
                    tab.Context.IsInCompareMode = false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during lazy workspace initialization");
                await _dialogService.ShowMessageAsync(
                    "Ошибка",
                    $"Не удалось загрузить проект: {ex.Message}",
                    MessageBoxType.Error,
                    MessageBoxButtons.OK
                );
                return false;
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

                _logger.LogDebug("Reloading {Count} modules from project data", activeModules.Count);

                foreach (var module in activeModules)
                {
                    if (project.ModulesData.TryGetValue(module.moduleType, out var data))
                    {
                        module.SetCustomData(data);
                        _logger.LogDebug("Reloaded module: {moduleType}", module.moduleType);
                    }
                    else
                    {
                        module.SetCustomData(null);
                        _logger.LogDebug("Cleared module (no data): {moduleType}", module.moduleType);
                    }
                }

                _logger.LogDebug("All modules reloaded successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reloading modules");
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
                        _logger.LogDebug("CompareMode disabled, RecoveryBanner hidden, IsReadOnly = false");
                    });

                    // Обновляем модули через WorkspaceController
                    if (capturedTab.Workspace != null)
                    {
                        capturedTab.Workspace.RefreshModulesFromContext();
                    }

                    // Обновляем модули через WorkspaceController
                    if (capturedTab.Workspace != null)
                    {
                        capturedTab.Workspace.RefreshModulesFromContext();
                    }

                    // Включаем автосохранение обратно
                    if (capturedTab.Workspace != null)
                    {
                        var cacheUpdateService = App.Services.GetRequiredService<ICacheUpdateService>();
                        cacheUpdateService.Stop();
                        cacheUpdateService.Start(capturedPath, () => capturedTab.Workspace.GetActiveModules());
                    }

                    _logger.LogDebug("Cache discarded, editing enabled, cache service started");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error discarding cache");

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
                        _logger.LogDebug("CompareMode disabled, RecoveryBanner hidden");
                    });

                    // Обновляем модули через WorkspaceController
                    if (capturedTab.Workspace != null)
                    {
                        capturedTab.Workspace.RefreshModulesFromContext();
                    }

                    // Включаем автосохранение обратно
                    if (capturedTab.Workspace != null)
                    {
                        var cacheUpdateService = App.Services.GetRequiredService<ICacheUpdateService>();
                        cacheUpdateService.Stop();
                        cacheUpdateService.Start(capturedTab.FilePath, () => capturedTab.Workspace.GetActiveModules());
                    }

                    _logger.LogDebug("Saved and enabled editing, cache service started");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving and hiding banner");
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
                        _logger.LogDebug("Switched to saved version");
                    }
                    else
                    {
                        project = await LoadProjectWithCacheData(capturedPath);
                        _logger.LogDebug("Switched to cache version");
                    }

                    if (project != null)
                    {
                        capturedTab.UpdateProject(project);
                        await ReloadModulesFromProject(capturedTab);
                        capturedTab.RecoveryBanner.IsViewingCache = !isViewingCache;

                        _logger.LogDebug("Switched version, now viewing: {Version}", capturedTab.RecoveryBanner.IsViewingCache ? "cache" : "saved");
                    }
                }
                finally
                {
                    capturedTab.Context.ReopenZipStorage();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error switching version");

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

                _logger.LogDebug("Saving project: {FilePath}", filePath);

                var tabCollection = App.Services.GetRequiredService<ITabCollection>();
                var activeTab = tabCollection.ActiveTab;
                var stateCollector = App.Services.GetRequiredService<IModuleStateCollectorService>();

                Dictionary<string, object?> allData;

                if (tab == activeTab)
                {
                    _logger.LogDebug("Saving ACTIVE tab: {Title}", tab.Title);

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
                        _logger.LogDebug("Project saved, cache deleted");
                        _notificationService.ShowSuccess(Strings.Notification_ProjectSaved);
                        ProjectSaved?.Invoke(tab);
                        return true;
                    }

                    return false;
                }
                else
                {
                    _logger.LogDebug("Saving INACTIVE tab: {Title}", tab.Title);

                    var cache = _cacheService.LoadCache(filePath);

                    if (cache == null || cache.Count == 0)
                    {
                        _logger.LogError("No cache for inactive tab!");
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
                        _logger.LogDebug("Project saved, cache deleted");
                        _notificationService.ShowSuccess(Strings.Notification_ProjectSaved);
                        ProjectSaved?.Invoke(tab);
                        return true;
                    }

                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving project");

                // В случае ошибки переоткрываем ZIP
                try
                {
                    tab.Context.ReopenZipStorage();
                }
                catch { }

                return false;
            }
        }

        /// <summary>
        /// Сохранить документ с новым именем
        /// </summary>
        public async Task<bool> SaveAsDocumentAsync(DocumentTabViewModel tab)
        {
            try
            {
                // Показываем диалог выбора места сохранения
                var filePath = await _dialogService.SaveFileAsync(tab.Title);
                if (string.IsNullOrEmpty(filePath))
                {
                    _logger.LogDebug("SaveAs cancelled by user");
                    return false;
                }

                _logger.LogDebug("SaveAs: {FilePath}", filePath);

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
                    _logger.LogDebug("RecoveryBanner cleared");

                    // Добавляем в недавние
                    _settingsService.AddRecentProject(filePath);

                    _logger.LogDebug("SaveAs successful");
                    ProjectSaved?.Invoke(tab);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in SaveAs");
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
                _logger.LogDebug("Closing tab: {Title}, force: {Force}", tab.Title, force);

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
                        _logger.LogDebug("Close cancelled by user");
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
                _logger.LogDebug("RecoveryBanner cleared");

                var filePath = tab.FilePath;

                if (!string.IsNullOrEmpty(filePath))
                {
                    if (_autoSaveServices.TryGetValue(filePath, out var autoSaveService))
                    {
                        autoSaveService.Dispose();
                        _autoSaveServices.Remove(filePath);
                        _logger.LogDebug("WorkspaceAutoSave stopped for: {FilePath}", filePath);
                    }

                    tab.Context.CloseZipStorage();
                    _logger.LogDebug("ZipStorage closed for context");

                    var project = _projectService.GetProjectByPath(filePath);
                    if (project != null)
                    {
                        _projectService.CloseProject(project);
                    }
                }

                // Уничтожаем все модули проекта
                tab.Dispose();
                tab.Dispose();
                _logger.LogDebug("All modules disposed via tab.Dispose()");

                _logger.LogDebug("Tab closed successfully");
                ProjectClosed?.Invoke(tab);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error closing tab");
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

                    _logger.LogDebug("HasUnsavedChanges (new project, active): {HasContent}", hasContent);
                    return hasContent;
                }

                // Неактивная новая вкладка - изменений нет
                _logger.LogDebug("HasUnsavedChanges (new project, inactive): false");
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

                    _logger.LogDebug("HasUnsavedChanges - active tab, collected {Count} non-empty modules", nonEmptyData.Count);

                    // Если нет реальных данных - нет изменений
                    if (nonEmptyData.Count == 0)
                    {
                        _logger.LogDebug("HasUnsavedChanges ({Title}): False (no data)", tab.Title);
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
                        _logger.LogDebug("HasUnsavedChanges ({Title}, inactive): false (no cache)", tab.Title);
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

                    _logger.LogDebug("HasUnsavedChanges - inactive tab, loaded {Count} non-empty modules from cache", nonEmptyData.Count);

                    // Если нет реальных данных - нет изменений
                    if (nonEmptyData.Count == 0)
                    {
                        _logger.LogDebug("HasUnsavedChanges ({Title}, inactive): false (no data in cache)", tab.Title);
                        return false;
                    }

                    allCurrentData = nonEmptyData;
                }

                // Закрываем ZIP перед чтением файла
                tab.Context.CloseZipStorage();
                _logger.LogDebug("ZIP closed for comparison");

                // Загружаем свежие данные напрямую из ZIP файла
                var savedProject = await _projectService.LoadAsync(filePath);

                // Переоткрываем ZIP после чтения
                tab.Context.ReopenZipStorage();
                _logger.LogDebug("ZIP reopened after comparison");

                // Проверка на успешную загрузку файла
                if (savedProject == null)
                {
                    _logger.LogWarning("HasUnsavedChanges - failed to load project from file");
                    return false;
                }

                // ModulesData уже содержит CustomData напрямую
                var savedCustomData = savedProject.ModulesData;

                // Сравниваем ТОЛЬКО CustomData (игнорируем SessionData)
                bool hasChanges = !_comparisonService.AreDataEqual(allCurrentData, savedCustomData);

                _logger.LogDebug("HasUnsavedChanges ({Title}): {HasChanges}", tab.Title, hasChanges);
                return hasChanges;
            }
            catch (Exception ex)
            {
                // В случае ошибки пытаемся переоткрыть ZIP
                try
                {
                    tab.Context.ReopenZipStorage();
                    _logger.LogDebug("ZIP reopened after error");
                }
                catch { }

                // В случае ошибки считаем что изменений нет (безопасный fallback)
                _logger.LogError(ex, "Error checking unsaved changes");
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

            _logger.LogWarning("No AutoSaveService found for: {FilePath}", filePath);
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

            _logger.LogWarning("No FileStorage found for: {FilePath}", filePath);
            return null;
        }

        /// <summary>
        /// Зарегистрировать хранилище для проекта
        /// </summary>
        public void RegisterStorage(string filePath, DocumentTabViewModel tab)
        {
            var storage = new ZipFileStorageService(filePath);
            _openStorages[filePath] = storage;
            tab.Context.FileStorage = storage;

            var project = tab.GetProject();

            var workModeConfigService = App.Services.GetRequiredService<IWorkModeConfigurationService>();
            var workModes = workModeConfigService.LoadDefaultConfiguration(project.Type);
            project.WorkModes = workModes;

            var autoSaveService = App.Services.GetRequiredService<IWorkspaceAutoSaveService>();
            _autoSaveServices[filePath] = autoSaveService;

            tab.InitializeWorkspace(workModes);

            var workspaceConfigService = App.Services.GetRequiredService<IWorkspaceConfigService>();
            workspaceConfigService.SaveToZip(storage, new WorkspaceLocalConfig { WorkModes = workModes });

            _logger.LogDebug("Storage registered for: {FilePath}", filePath);
        }

        /// <summary>
        /// Обновить хранилище для проекта
        /// </summary>
        public void UpdateStorageForProject(string filePath, IProjectFileStorage newStorage)
        {
            if (_openStorages.ContainsKey(filePath))
            {
                _openStorages[filePath] = (ZipFileStorageService)newStorage;
                _logger.LogDebug("Storage updated for: {FilePath}", filePath);
            }
        }
    }
}