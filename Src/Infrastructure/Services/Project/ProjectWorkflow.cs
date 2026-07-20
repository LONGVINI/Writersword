using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Models.Project;
using Writersword.Core.Models.Settings;
using Writersword.Resources.Localization;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Interfaces.Services.UI;
using Writersword.Core.Interfaces.WorkFlows;
using Writersword.Infrastructure.Services.Storage;
using Writersword.ViewModels;
using Writersword.Views;

namespace Writersword.Infrastructure.Services.Project
{
    /// <summary>
    /// Реализация сервиса управления жизненным циклом проектов.
    /// Управляет открытием, сохранением, закрытием документов.
    /// Обрабатывает восстановление из кеша.
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

        public event Action<IDocumentTab>? ProjectOpened;
        public event Action<IDocumentTab>? ProjectSaved;
        public event Action<IDocumentTab>? ProjectClosed;

        private readonly Dictionary<string, ZipFileStorageService> _openStorages = new();
        private readonly Dictionary<string, IWorkspaceAutoSaveService> _autoSaveServices = new();

        // Сессионные данные модулей (каретка, скролл, зум) для каждой стороны
        // режима сравнения: ключ — "<путь проекта>|cache" или "<путь проекта>|saved".
        // Позволяет при Switch Version возвращаться к позиции, где пользователь
        // остановился в каждой из версий. Очищается при выходе из compare mode.
        private readonly Dictionary<string, Dictionary<string, object?>> _compareSessionCache = new();

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

        // ── Открытие ──────────────────────────────────────────────────────

        /// <summary>
        /// Открыть документ с поддержкой восстановления из кеша.
        /// </summary>
        public async Task<IDocumentTab?> OpenDocumentAsync(string? filePath = null, bool initializeWorkspace = true)
        {
            try
            {
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

                ProjectFile? project = null;
                RecoveryDialogResult recoveryChoice = RecoveryDialogResult.None;

                if (initializeWorkspace && _cacheService.HasCache(filePath))
                {
                    // Быстрая проверка через хеш файла проекта из метаданных кеша.
                    // Читаем только cache.json (~100 байт) вместо загрузки 400 МБ данных.
                    var cacheMeta = await Task.Run(() => _cacheService.LoadCacheMetadata(filePath));
                    var cacheDate = cacheMeta?.CacheDate;
                    var saveDate = File.GetLastWriteTime(filePath);

                    if (cacheDate.HasValue)
                    {
                        _logger.LogDebug("Cache found - Cache: {CacheDate}, Save: {SaveDate}", cacheDate, saveDate);

                        bool dataIsSame = false;
                        bool hashMatched = false;

                        if (!string.IsNullOrEmpty(cacheMeta?.ProjectFileHash))
                        {
                            // Быстрый путь: сравниваем хеши файла (~50 мс) вместо загрузки данных.
                            var currentHash = await Task.Run(() =>
                            {
                                // Глобальный шлюз файла: хеширование не пересекается с записью
                                // конфигов/кеша в этот же ZIP из других потоков.
                                using var fileGate = ProjectFileLock.Acquire(filePath);
                                using var sha = System.Security.Cryptography.SHA256.Create();
                                using var fs = new FileStream(
                                    filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                                return Convert.ToHexString(sha.ComputeHash(fs));
                            });
                            hashMatched = currentHash == cacheMeta.ProjectFileHash;
                            dataIsSame = hashMatched;
                            _logger.LogDebug("Hash comparison: {Result}", hashMatched ? "SAME" : "DIFFERENT");
                        }

                        if (!hashMatched)
                        {
                            // Несовпадение хеша файла НЕ означает несовпадение данных модулей:
                            // ZIP проекта перезаписывается и без правок содержимого — например,
                            // автосохранение workspace.json (layout) при каждом переключении
                            // вкладки меняет хеш всего файла. Раньше это сразу трактовалось как
                            // «есть несохранённые данные», и каждая вкладка при каждом запуске
                            // открывалась в режиме восстановления. Теперь при несовпадении хеша
                            // выполняется полное каноническое сравнение данных модулей.
                            var savedProject = await _projectService.LoadAsync(filePath);
                            var cache = await Task.Run(() => _cacheService.LoadCache(filePath));
                            if (savedProject != null && cache != null)
                            {
                                var savedRelevantOnOpen = savedProject.ModulesData
                                    .Where(kvp => cache.ContainsKey(kvp.Key))
                                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                                dataIsSame = _comparisonService.AreDataEqual(cache, savedRelevantOnOpen);
                            }
                            if (dataIsSame) project = savedProject;
                            _logger.LogDebug("Full data comparison: {Result}", dataIsSame ? "SAME" : "DIFFERENT");
                        }

                        if (dataIsSame)
                        {
                            _logger.LogDebug("Data is identical, skipping recovery");
                            project ??= await _projectService.LoadAsync(filePath);
                        }
                        else
                        {
                            // Модальный диалог восстановления убран: он блокировал всё
                            // приложение в момент открытия/активации вкладки (в том числе
                            // ломал drag вкладок) и заставлял выбирать версию вслепую.
                            // Вместо него проект сразу открывается в Compare-режиме:
                            // показывается восстановленная (кешевая) версия, а сверху —
                            // немодальный баннер «Переключить версию / Сохранить /
                            // Отклонить». Обе версии сохраняются, пока пользователь
                            // явно не выберет; автосейв кеша в этом режиме отключён.
                            _logger.LogDebug("Data differs — opening in Compare mode with recovery banner");

                            recoveryChoice = RecoveryDialogResult.Compare;
                            project = await LoadProjectWithCacheData(filePath);
                            _logger.LogDebug("Compare mode - viewing cache");
                        }
                    }
                }

                if (project == null)
                {
                    project = await _projectService.LoadAsync(filePath);
                    if (project == null)
                    {
                        await _dialogService.ShowMessageAsync(
                            "Ошибка", "Не удалось загрузить проект",
                            MessageBoxType.Error, MessageBoxButtons.OK);
                        return null;
                    }
                }

                var tabVM = new DocumentTabViewModel(project, filePath, onClose: null);

                if (!initializeWorkspace)
                {
                    _logger.LogDebug("Created LAZY tab (workspace not initialized): {Title}", project.Title);
                    _settingsService.AddRecentProject(filePath);
                    return tabVM;
                }

                var storage = new ZipFileStorageService(filePath);
                if (_openStorages.TryGetValue(filePath, out var oldStorage))
                    oldStorage.Dispose();
                _openStorages[filePath] = storage;
                tabVM.Context.FileStorage = storage;
                tabVM.Context.StorageFactory = path => new ZipFileStorageService(path);
                _logger.LogDebug("ZipFileStorage created for: {FilePath}", filePath);

                var workModeConfigService = App.Services.GetRequiredService<IWorkModeConfigurationService>();
                var workModes = workModeConfigService.LoadConfiguration(project.Type, storage);
                project.WorkModes = workModes;
                _logger.LogDebug("Loaded {Count} WorkModes for project", workModes.Count);

                var autoSaveService = App.Services.GetRequiredService<IWorkspaceAutoSaveService>();
                _autoSaveServices[filePath] = autoSaveService;

                // storage передаётся в WorkspaceController — локальные настройки
                // применяются внутри Activate() ПОСЛЕ создания живых модулей
                tabVM.InitializeWorkspace(workModes, storage);
                _logger.LogDebug("WorkspaceController initialized for: {FilePath}", filePath);

                if (recoveryChoice == RecoveryDialogResult.Compare)
                {
                    var cacheDate = await Task.Run(() => _cacheService.GetCacheDate(filePath));
                    var saveDate = File.GetLastWriteTime(filePath);

                    if (cacheDate.HasValue)
                    {
                        var capturedTab = tabVM;
                        var capturedPath = filePath;

                        tabVM.RecoveryBanner = new RecoveryBannerViewModel(
                            onSwitchVersion: async () => await SwitchVersionAsync(capturedTab, capturedPath),
                            onSave: async () => await SaveAndHideBannerAsync(capturedTab),
                            onDiscard: async () => await DiscardCacheAsync(capturedTab, capturedPath))
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
                }

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
                    "Ошибка", $"Не удалось открыть проект: {ex.Message}",
                    MessageBoxType.Error, MessageBoxButtons.OK);
                return null;
            }
        }

        // ── Lazy init ─────────────────────────────────────────────────────

        /// <summary>
        /// Инициализировать workspace для ленивой вкладки.
        /// Вызывается при первом переключении на вкладку.
        /// </summary>
        public async Task<bool> EnsureWorkspaceInitialized(IDocumentTab documentTab)
        {
            var tab = (DocumentTabViewModel)documentTab;
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
                    var cacheDate = await Task.Run(() => _cacheService.GetCacheDate(filePath));
                    var saveDate = File.GetLastWriteTime(filePath);

                    if (cacheDate.HasValue)
                    {
                        _logger.LogDebug("Cache found for lazy tab - Cache: {CacheDate}, Save: {SaveDate}", cacheDate, saveDate);

                        // Task.Run: _fileLock.Wait() в ZipCacheService (singleton) синхронно
                        // блокирует UI-поток. Фоновый авто-сейв держит тот же лок и ждёт
                        // Dispatcher.UIThread.InvokeAsync — классический дедлок.
                        // Task.Run переносит ожидание на пул потоков.
                        var savedProject = await _projectService.LoadAsync(filePath);
                        var cache = await Task.Run(() => _cacheService.LoadCache(filePath));

                        bool dataIsSame = false;
                        if (savedProject != null && cache != null)
                        {
                            // Сравниваем только те модули которые есть в кеше.
                            // Остальные не были активны в прошлой сессии — не могли измениться.
                            var savedRelevantOnOpen = savedProject.ModulesData
                                .Where(kvp => cache.ContainsKey(kvp.Key))
                                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                            dataIsSame = _comparisonService.AreDataEqual(cache, savedRelevantOnOpen);
                            _logger.LogDebug("Data comparison: {Comparison}", dataIsSame ? "SAME" : "DIFFERENT");
                        }

                        if (dataIsSame)
                        {
                            _logger.LogDebug("Data is identical, skipping recovery");
                            project = savedProject;
                        }
                        else
                        {
                            // Модальный диалог восстановления убран: активация вкладки
                            // (в том числе при перетаскивании) не должна прерываться
                            // блокирующим окном. Вкладка сразу открывается в Compare-режиме:
                            // показывается восстановленная (кешевая) версия + немодальный
                            // баннер «Переключить версию / Сохранить / Отклонить».
                            // Обе версии сохраняются, пока пользователь явно не выберет;
                            // автосейв кеша в этом режиме отключён.
                            _logger.LogDebug("Data differs — opening in Compare mode with recovery banner");

                            recoveryChoice = RecoveryDialogResult.Compare;
                            project = await LoadProjectWithCacheData(filePath);
                            _logger.LogDebug("Compare mode - viewing cache");
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
                tab.Context.StorageFactory = path => new ZipFileStorageService(path);
                _logger.LogDebug("ZipFileStorage created for: {FilePath}", filePath);

                var workModeConfigService = App.Services.GetRequiredService<IWorkModeConfigurationService>();
                var workModes = workModeConfigService.LoadConfiguration(project.Type, storage);
                project.WorkModes = workModes;
                _logger.LogDebug("Loaded {Count} WorkModes for project", workModes.Count);

                var autoSaveService = App.Services.GetRequiredService<IWorkspaceAutoSaveService>();
                _autoSaveServices[filePath] = autoSaveService;

                // storage передаётся в WorkspaceController — локальные настройки
                // применяются внутри Activate() ПОСЛЕ создания живых модулей
                tab.InitializeWorkspace(workModes, storage);
                _logger.LogDebug("WorkspaceController lazy initialized for: {FilePath}", filePath);

                if (recoveryChoice == RecoveryDialogResult.Compare)
                {
                    var cacheDate = await Task.Run(() => _cacheService.GetCacheDate(filePath));
                    var saveDate = File.GetLastWriteTime(filePath);

                    if (cacheDate.HasValue)
                    {
                        var capturedTab = tab;
                        var capturedPath = filePath;

                        capturedTab.RecoveryBanner = new RecoveryBannerViewModel(
                            onSwitchVersion: async () => await SwitchVersionAsync(capturedTab, capturedPath),
                            onSave: async () => await SaveAndHideBannerAsync(capturedTab),
                            onDiscard: async () => await DiscardCacheAsync(capturedTab, capturedPath))
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
                    "Ошибка", $"Не удалось загрузить проект: {ex.Message}",
                    MessageBoxType.Error, MessageBoxButtons.OK);
                return false;
            }
        }

        // ── Перезагрузка модулей ──────────────────────────────────────────

        /// <summary>
        /// Перезагрузить все активные модули из данных проекта.
        /// Используется при переключении версий в Compare mode.
        /// После перезагрузки CustomData применяет локальные настройки из ZIP.
        /// </summary>
        private async Task ReloadModulesFromProject(DocumentTabViewModel tab)
        {
            try
            {
                // Модули берутся у ЦЕЛЕВОЙ вкладки, а не у активной вкладки окна:
                // пользователь мог переключить вкладку, пока открыт диалог
                // подтверждения, и перезагрузка применялась к модулям ЧУЖОЙ вкладки —
                // в проекте целевой вкладки нет данных для чужих модулей, и
                // SetCustomData(null) обнулял их живое состояние (пропадали все
                // персонажи другого проекта до перезапуска приложения).
                var activeModules = tab.Workspace?.GetActiveModules()
                    ?? tab.ModuleContext.GetAllModules();
                var project = tab.GetProject();

                _logger.LogDebug("Reloading {Count} modules from project data", activeModules.Count);

                foreach (var module in activeModules)
                {
                    project.ModulesData.TryGetValue(module.moduleType, out var data);

                    // Двухфазный путь для модулей с фоновой подготовкой данных:
                    // тяжёлая десериализация уходит с UI-потока, применение остаётся
                    // на нём. Это заметно ускоряет Switch Version в режиме сравнения.
                    if (module is IPreparedDataModule preparedModule)
                    {
                        object? prepared = await Task.Run(() => preparedModule.PrepareCustomData(data));
                        preparedModule.ApplyPreparedCustomData(prepared);
                    }
                    else
                    {
                        module.SetCustomData(data);
                    }

                    if (data is not null)
                        _logger.LogDebug("Reloaded module: {moduleType}", module.moduleType);
                    else
                        _logger.LogDebug("Cleared module (no data): {moduleType}", module.moduleType);
                }

                // Применяем локальные настройки из ZIP после перезагрузки
                var storage = tab.Context?.FileStorage;
                if (storage != null)
                {
                    var service = App.Services.GetRequiredService<ILocalSettingsStorageService>();

                    foreach (var module in activeModules)
                    {
                        if (module is not IConfigurableModule configurable) continue;

                        try
                        {
                            var settings = service.Load(storage, module.moduleType, configurable.SettingsType);
                            if (settings is not null)
                            {
                                configurable.ApplyLocalSettings(settings);
                                _logger.LogDebug("Local settings re-applied: {ModuleType}", module.moduleType);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to re-apply local settings for {ModuleType}", module.moduleType);
                        }
                    }
                }
                else
                {
                    _logger.LogWarning("Cannot re-apply local settings — FileStorage is null");
                }

                _logger.LogDebug("All modules reloaded successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reloading modules");
            }
        }

        // ── Compare mode ──────────────────────────────────────────────────

        private async Task DiscardCacheAsync(DocumentTabViewModel tab, string filePath)
        {
            try
            {
                var capturedTab = tab;
                var capturedPath = filePath;

                var result = await _dialogService.ShowMessageAsync(
                    "Удалить автосохранение?",
                    "Автосохранённая версия будет удалена. Продолжить?",
                    MessageBoxType.Warning, MessageBoxButtons.YesNo);

                if (result == MessageBoxResult.Yes)
                {
                    if (capturedTab.RecoveryBanner?.IsViewingCache == true)
                    {
                        capturedTab.Context.CloseZipStorage();

                        ProjectFile? discardProject = null;
                        try
                        {
                            discardProject = await _projectService.LoadAsync(capturedPath);
                            if (discardProject != null)
                                capturedTab.UpdateProject(discardProject);
                        }
                        finally
                        {
                            capturedTab.Context.ReopenZipStorage();
                        }

                        // Перезагрузка модулей ПОСЛЕ переоткрытия хранилища: внутри
                        // применяются локальные настройки из ZIP, а раньше хранилище
                        // в этот момент было закрыто ("FileStorage is null").
                        if (discardProject != null)
                            await ReloadModulesFromProject(capturedTab);
                    }

                    _cacheService.DeleteCache(capturedPath);

                    // Выход из compare mode — сохранённые позиции версий больше не нужны.
                    _compareSessionCache.Remove(capturedPath + "|cache");
                    _compareSessionCache.Remove(capturedPath + "|saved");

                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        capturedTab.Context.IsInCompareMode = false;
                        capturedTab.RecoveryBanner = null;
                        _logger.LogDebug("CompareMode disabled, RecoveryBanner hidden");
                    });

                    capturedTab.Workspace?.RefreshModulesFromContext();

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
                try { tab.Context.ReopenZipStorage(); } catch { }
            }
        }

        private async Task SaveAndHideBannerAsync(DocumentTabViewModel tab)
        {
            try
            {
                var capturedTab = tab;
                bool success = await SaveDocumentAsync(capturedTab);

                if (success)
                {
                    // Выход из compare mode — сохранённые позиции версий больше не нужны.
                    if (capturedTab.FilePath is not null)
                    {
                        _compareSessionCache.Remove(capturedTab.FilePath + "|cache");
                        _compareSessionCache.Remove(capturedTab.FilePath + "|saved");
                    }

                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        capturedTab.Context.IsInCompareMode = false;
                        capturedTab.RecoveryBanner = null;
                        _logger.LogDebug("CompareMode disabled, RecoveryBanner hidden");
                    });

                    capturedTab.Workspace?.RefreshModulesFromContext();

                    if (capturedTab.Workspace != null)
                    {
                        var cacheUpdateService = App.Services.GetRequiredService<ICacheUpdateService>();
                        cacheUpdateService.Stop();
                        cacheUpdateService.Start(capturedTab.FilePath!, () => capturedTab.Workspace.GetActiveModules());
                    }

                    _logger.LogDebug("Saved and enabled editing, cache service started");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving and hiding banner");
            }
        }

        private async Task SwitchVersionAsync(DocumentTabViewModel tab, string filePath)
        {
            try
            {
                var capturedTab = tab;
                var capturedPath = filePath;

                if (capturedTab.RecoveryBanner == null) return;

                var isViewingCache = capturedTab.RecoveryBanner.IsViewingCache;
                ProjectFile? project;

                // Запоминаем позицию (каретка, скролл, зум) текущей версии,
                // чтобы вернуться к ней при обратном переключении.
                var sessionModules = capturedTab.Workspace?.GetActiveModules()
                    ?? capturedTab.ModuleContext.GetAllModules();
                string currentSessionKey = capturedPath + "|" + (isViewingCache ? "cache" : "saved");
                string targetSessionKey = capturedPath + "|" + (isViewingCache ? "saved" : "cache");

                var currentSessions = new Dictionary<string, object?>();
                foreach (var m in sessionModules)
                {
                    try { currentSessions[m.moduleType] = m.GetSessionData(); }
                    catch (Exception sex)
                    { _logger.LogWarning(sex, "Failed to capture session for {ModuleType}", m.moduleType); }
                }
                _compareSessionCache[currentSessionKey] = currentSessions;

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
                        capturedTab.UpdateProject(project);
                }
                finally
                {
                    capturedTab.Context.ReopenZipStorage();
                }

                // Перезагрузка модулей ПОСЛЕ переоткрытия хранилища: внутри применяются
                // локальные настройки из ZIP, а раньше хранилище в этот момент было
                // закрыто ("FileStorage is null").
                if (project != null)
                {
                    await ReloadModulesFromProject(capturedTab);

                    // Перезагрузка пересоздаёт вьюмодели модулей — состояние read-only
                    // режима сравнения теряется. Применяем контекст заново ко всем
                    // активным модулям, как это делается при входе в compare mode.
                    capturedTab.Workspace?.RefreshModulesFromContext();

                    // Восстанавливаем позицию, где пользователь остановился
                    // в целевой версии при прошлом её просмотре.
                    if (_compareSessionCache.TryGetValue(targetSessionKey, out var targetSessions))
                    {
                        foreach (var m in sessionModules)
                        {
                            if (!targetSessions.TryGetValue(m.moduleType, out var session)
                                || session is null) continue;
                            try { m.SetSessionData(session); }
                            catch (Exception sex)
                            { _logger.LogWarning(sex, "Failed to restore session for {ModuleType}", m.moduleType); }
                        }
                    }

                    capturedTab.RecoveryBanner.IsViewingCache = !isViewingCache;

                    _logger.LogDebug("Switched version, now viewing: {Version}",
                        capturedTab.RecoveryBanner.IsViewingCache ? "cache" : "saved");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error switching version");
                try { tab.Context.ReopenZipStorage(); } catch { }
            }
        }

        // ── Сохранение ────────────────────────────────────────────────────

        public async Task<bool> SaveDocumentAsync(IDocumentTab documentTab, bool showNotification = true)
        {
            var tab = (DocumentTabViewModel)documentTab;
            try
            {
                var project = tab.GetProject();
                var filePath = tab.FilePath;

                if (string.IsNullOrEmpty(filePath))
                    return await SaveAsDocumentAsync(tab);

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
                    var activeCustomData = stateCollector.CollectCustomData(activeModules);
                    var cache = _cacheService.LoadCache(filePath);

                    allData = new Dictionary<string, object?>();

                    if (cache != null)
                        foreach (var kvp in cache)
                            allData[kvp.Key] = kvp.Value;

                    foreach (var kvp in activeCustomData)
                        allData[kvp.Key] = kvp.Value;

                    // Защита от потери данных: если модуль присутствовал в ZIP на диске,
                    // но не попал в allData (GetCustomData вернул null или бросил исключение,
                    // а кеш пуст) — берём старое значение из ZIP.
                    // Это предотвращает затирание данных при временном сбое сбора.
                    tab.Context.CloseZipStorage();
                    var savedProject = await _projectService.LoadAsync(filePath);
                    tab.Context.ReopenZipStorage();

                    if (savedProject != null)
                    {
                        foreach (var kvp in savedProject.ModulesData)
                        {
                            if (!allData.ContainsKey(kvp.Key) && kvp.Value != null
                                && !(kvp.Value is string s0 && string.IsNullOrWhiteSpace(s0)))
                            {
                                allData[kvp.Key] = kvp.Value;
                                _logger.LogWarning(
                                    "Module {M} missing from collected data — preserved from ZIP", kvp.Key);
                            }
                        }
                    }

                    project.ModulesData = allData;
                    project.LastModified = DateTime.Now;

                    tab.Context.CloseZipStorage();
                    bool success = await _projectService.SaveAsync(project, filePath);
                    tab.Context.ReopenZipStorage();

                    if (success)
                    {
                        _cacheService.DeleteCache(filePath);
                        _logger.LogDebug("Project saved, cache deleted");
                        if (showNotification) _notificationService.ShowSuccess(Strings.Notification_ProjectSaved);
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
                    foreach (var kvp in cache)
                        allData[kvp.Key] = kvp.Value;

                    // Защита от потери данных: если модуль был в ZIP но не попал в кеш —
                    // берём старое значение из ZIP.
                    tab.Context.CloseZipStorage();
                    var savedProject = await _projectService.LoadAsync(filePath);
                    tab.Context.ReopenZipStorage();

                    if (savedProject != null)
                    {
                        foreach (var kvp in savedProject.ModulesData)
                        {
                            if (!allData.ContainsKey(kvp.Key) && kvp.Value != null
                                && !(kvp.Value is string s1 && string.IsNullOrWhiteSpace(s1)))
                            {
                                allData[kvp.Key] = kvp.Value;
                                _logger.LogWarning(
                                    "Module {M} missing from cache — preserved from ZIP (inactive tab)", kvp.Key);
                            }
                        }
                    }

                    project.ModulesData = allData;
                    project.LastModified = DateTime.Now;

                    tab.Context.CloseZipStorage();
                    bool success = await _projectService.SaveAsync(project, filePath);
                    tab.Context.ReopenZipStorage();

                    if (success)
                    {
                        _cacheService.DeleteCache(filePath);
                        _logger.LogDebug("Project saved, cache deleted");
                        if (showNotification) _notificationService.ShowSuccess(Strings.Notification_ProjectSaved);
                        ProjectSaved?.Invoke(tab);
                        return true;
                    }

                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving project");
                try { tab.Context.ReopenZipStorage(); } catch { }
                return false;
            }
        }

        public async Task<bool> SaveAsDocumentAsync(IDocumentTab documentTab)
        {
            var tab = (DocumentTabViewModel)documentTab;
            try
            {
                var filePath = await _dialogService.SaveFileAsync(tab.Title);
                if (string.IsNullOrEmpty(filePath))
                {
                    _logger.LogDebug("SaveAs cancelled by user");
                    return false;
                }

                _logger.LogDebug("SaveAs: {FilePath}", filePath);

                tab.FilePath = filePath;
                tab.Title = Path.GetFileNameWithoutExtension(filePath);

                var project = tab.GetProject();
                project.LastModified = DateTime.Now;

                bool success = await _projectService.SaveAsync(project, filePath);

                if (success)
                {
                    tab.RecoveryBanner = null;
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
                    "Ошибка", $"Не удалось сохранить проект: {ex.Message}",
                    MessageBoxType.Error, MessageBoxButtons.OK);
                return false;
            }
        }

        // ── Закрытие ──────────────────────────────────────────────────────

        public async Task<bool> CloseDocumentAsync(IDocumentTab documentTab, bool force = false)
        {
            var tab = (DocumentTabViewModel)documentTab;
            try
            {
                _logger.LogDebug("Closing tab: {Title}, force: {Force}", tab.Title, force);

                if (!force && await HasUnsavedChanges(tab))
                {
                    var result = await _dialogService.ShowMessageAsync(
                        "Несохранённые изменения",
                        $"Документ \"{tab.Title}\" содержит несохранённые изменения.\n\nСохранить перед закрытием?",
                        MessageBoxType.Question, MessageBoxButtons.YesNoCancel);

                    if (result == MessageBoxResult.Cancel)
                    {
                        _logger.LogDebug("Close cancelled by user");
                        return false;
                    }

                    if (result == MessageBoxResult.Yes)
                    {
                        bool saved = await SaveDocumentAsync(tab);
                        if (!saved) return false;
                    }
                }

                tab.RecoveryBanner = null;

                var filePath = tab.FilePath;

                if (!string.IsNullOrEmpty(filePath))
                {
                    if (_autoSaveServices.TryGetValue(filePath, out var autoSaveService))
                    {
                        autoSaveService.Dispose();
                        _autoSaveServices.Remove(filePath);
                        if (_openStorages.TryGetValue(filePath, out var closingStorage))
                        {
                            closingStorage.Dispose();
                            _openStorages.Remove(filePath);
                        }
                        _logger.LogDebug("WorkspaceAutoSave stopped for: {FilePath}", filePath);
                    }

                    tab.Context.CloseZipStorage();

                    var project = _projectService.GetProjectByPath(filePath);
                    if (project != null)
                        _projectService.CloseProject(project);
                }

                tab.Dispose();
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

        // ── Проверка изменений ────────────────────────────────────────────

        public async Task<bool> HasUnsavedChanges(IDocumentTab documentTab)
        {
            var tab = (DocumentTabViewModel)documentTab;
            var filePath = tab.FilePath;

            // ── Новый несохранённый проект ────────────────────────────────
            if (string.IsNullOrEmpty(filePath))
            {
                var tabCollection = App.Services.GetRequiredService<ITabCollection>();
                var activeTab = tabCollection.ActiveTab;

                if (tab == activeTab)
                {
                    var mainViewModel = App.Services.GetRequiredService<MainWindowViewModel>();
                    var activeModules = mainViewModel.GetActiveModules();
                    var stateCollector = App.Services.GetRequiredService<IModuleStateCollectorService>();
                    var currentData = stateCollector.CollectCustomData(activeModules);

                    bool hasContent = currentData.Any(kvp =>
                    {
                        if (kvp.Value == null) return false;
                        if (kvp.Value is string str) return !string.IsNullOrWhiteSpace(str);
                        return true;
                    });

                    _logger.LogDebug("HasUnsavedChanges (new project, active): {HasContent}", hasContent);
                    return hasContent;
                }

                _logger.LogDebug("HasUnsavedChanges (new project, inactive): false");
                return false;
            }

            try
            {
                var tabCollection = App.Services.GetRequiredService<ITabCollection>();
                var activeTab = tabCollection.ActiveTab;
                var stateCollector = App.Services.GetRequiredService<IModuleStateCollectorService>();

                // ── Собираем текущие данные только тех модулей что были активны ──
                // Логика: данные активного модуля берём живьём (он мог измениться
                // и ещё не успел записаться в кеш). Остальные модули берём из кеша
                // (они записались при переключении). Модули которых нет ни там ни там
                // не были активны в этой сессии — они не могли измениться, поэтому
                // мы их вообще не включаем в сравнение. Для них savedProject.ModulesData
                // тоже фильтруется ниже.
                Dictionary<string, object?> allCurrentData;

                if (tab == activeTab)
                {
                    var mainViewModel = App.Services.GetRequiredService<MainWindowViewModel>();
                    var activeModules = mainViewModel.GetActiveModules();
                    var activeCustomData = stateCollector.CollectCustomData(activeModules);

                    // Начинаем с живых данных активных модулей.
                    allCurrentData = new Dictionary<string, object?>(activeCustomData);

                    // Добавляем данные из кеша для модулей которых нет среди активных
                    // (они были активны ранее в этой сессии, переключились → записались в кеш).
                    var cache = _cacheService.LoadCache(filePath);
                    if (cache != null)
                        foreach (var kvp in cache)
                            if (!allCurrentData.ContainsKey(kvp.Key) && kvp.Value != null)
                                allCurrentData[kvp.Key] = kvp.Value;

                    // Убираем пустые записи — модуль мог вернуть null или пустую строку.
                    allCurrentData = allCurrentData
                        .Where(kvp => kvp.Value != null && !(kvp.Value is string str && string.IsNullOrWhiteSpace(str)))
                        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                    if (allCurrentData.Count == 0)
                    {
                        _logger.LogDebug("HasUnsavedChanges ({Title}): False (no data)", tab.Title);
                        return false;
                    }
                }
                else
                {
                    // Неактивная вкладка — все данные только из кеша.
                    var cache = _cacheService.LoadCache(filePath);

                    if (cache == null || cache.Count == 0)
                    {
                        _logger.LogDebug("HasUnsavedChanges ({Title}, inactive): false (no cache)", tab.Title);
                        return false;
                    }

                    allCurrentData = cache
                        .Where(kvp => kvp.Value != null && !(kvp.Value is string str && string.IsNullOrWhiteSpace(str)))
                        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                    if (allCurrentData.Count == 0)
                    {
                        _logger.LogDebug("HasUnsavedChanges ({Title}, inactive): false (no data in cache)", tab.Title);
                        return false;
                    }
                }

                tab.Context.CloseZipStorage();
                var savedProject = await _projectService.LoadAsync(filePath);
                tab.Context.ReopenZipStorage();

                if (savedProject == null)
                {
                    _logger.LogWarning("HasUnsavedChanges - failed to load project from file");
                    return false;
                }

                // Сравниваем ТОЛЬКО те модули которые были активны в этой сессии.
                // Модули которых нет в allCurrentData не могли измениться —
                // незачем их включать в сравнение, это только создаёт ложные срабатывания.
                var savedRelevant = savedProject.ModulesData
                    .Where(kvp => allCurrentData.ContainsKey(kvp.Key))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                _logger.LogDebug("Comparing [{CurrentCount}] current keys vs [{SavedCount}] saved keys: {Keys}",
                    allCurrentData.Count,
                    savedRelevant.Count,
                    string.Join(", ", allCurrentData.Keys));

                bool hasChanges = !_comparisonService.AreDataEqual(allCurrentData, savedRelevant);
                _logger.LogDebug("HasUnsavedChanges ({Title}): {HasChanges}", tab.Title, hasChanges);
                return hasChanges;
            }
            catch (Exception ex)
            {
                try { tab.Context.ReopenZipStorage(); } catch { }
                _logger.LogError(ex, "Error checking unsaved changes");
                return false;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────

        private async Task<ProjectFile?> LoadProjectWithCacheData(string filePath)
        {
            var project = await _projectService.LoadAsync(filePath);
            if (project == null) return null;

            var cache = await Task.Run(() => _cacheService.LoadCache(filePath));
            if (cache != null)
                foreach (var kvp in cache)
                    if (kvp.Value != null)
                        project.ModulesData[kvp.Key] = kvp.Value;

            return project;
        }

        public IWorkspaceAutoSaveService? GetAutoSaveServiceForProject(string filePath)
        {
            if (_autoSaveServices.TryGetValue(filePath, out var service))
                return service;

            _logger.LogWarning("No AutoSaveService found for: {FilePath}", filePath);
            return null;
        }

        public IProjectFileStorage? GetFileStorageForProject(string filePath)
        {
            if (_openStorages.TryGetValue(filePath, out var storage))
                return storage;

            _logger.LogWarning("No FileStorage found for: {FilePath}", filePath);
            return null;
        }

        public void RegisterStorage(string filePath, IDocumentTab documentTab)
        {
            var tab = (DocumentTabViewModel)documentTab;
            var storage = new ZipFileStorageService(filePath);
            _openStorages[filePath] = storage;
            tab.Context.FileStorage = storage;
            tab.Context.StorageFactory = path => new ZipFileStorageService(path);

            var project = tab.GetProject();

            var workModeConfigService = App.Services.GetRequiredService<IWorkModeConfigurationService>();
            var workModes = workModeConfigService.LoadDefaultConfiguration(project.Type);
            project.WorkModes = workModes;

            var autoSaveService = App.Services.GetRequiredService<IWorkspaceAutoSaveService>();
            _autoSaveServices[filePath] = autoSaveService;

            tab.InitializeWorkspace(workModes, storage);

            var workspaceConfigService = App.Services.GetRequiredService<IWorkspaceConfigService>();
            workspaceConfigService.SaveToZip(storage, new WorkspaceLocalConfig { WorkModes = workModes });

            _logger.LogDebug("Storage registered for: {FilePath}", filePath);
        }

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