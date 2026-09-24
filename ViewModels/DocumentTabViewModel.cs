using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables.Fluent;
using System.Threading.Tasks;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Interfaces.WorkFlows;
using Writersword.Core.Interfaces.Workspace;
using Writersword.Core.Models.Project;
using Writersword.Core.Services;
using Writersword.Infrastructure.Dock;
using Writersword.Infrastructure.Workspace;
using Writersword.Modules.Common;

namespace Writersword.ViewModels
{
    /// <summary>
    /// ViewModel для одной вкладки документа
    /// Каждая вкладка имеет свой изолированный WorkspaceController и ProjectModuleContext
    /// </summary>
    public class DocumentTabViewModel : ViewModelBase, IDocumentTab
    {
        private readonly ILogger<DocumentTabViewModel> _logger;
        private readonly ProjectFile _project;
        private readonly Func<DocumentTabViewModel, Task>? _onClose;
        private bool _isActive;
        private string? _filePath = "";
        private RecoveryBannerViewModel? _recoveryBanner;

        /// <summary>Загружен ли проект полностью (workspace инициализирован)</summary>
        public bool IsLoaded { get; private set; } = false;

        private bool _hasUnsavedChanges = false;

        /// <summary>ID вкладки (для UI)</summary>
        public string Id { get; }

        /// <summary>
        /// Контекст документа — передаётся модулям для управления состоянием
        /// </summary>
        public DocumentContext Context { get; }

        /// <summary>
        /// Изолированный контейнер модулей для этого проекта
        /// При закрытии проекта все модули автоматически уничтожаются
        /// </summary>
        public ProjectModuleContext ModuleContext { get; }

        /// <summary>
        /// Контроллер рабочего пространства (WorkModes, Layout, Float окна)
        /// </summary>
        public IWorkspaceController? Workspace { get; private set; }

        /// <summary>
        /// Баннер восстановления версий (null если нет кеша)
        /// </summary>
        public RecoveryBannerViewModel? RecoveryBanner
        {
            get => _recoveryBanner;
            set => this.RaiseAndSetIfChanged(ref _recoveryBanner, value);
        }

        /// <summary>Есть ли баннер восстановления</summary>
        public bool HasRecoveryBanner => RecoveryBanner != null;

        /// <summary>Заголовок вкладки</summary>
        public string Title
        {
            get => _project.Title;
            set
            {
                _project.Title = value;
                this.RaisePropertyChanged();
            }
        }

        /// <summary>Содержимое документа (текст из TextEditor модуля)</summary>
        public string Content
        {
            get
            {
                if (_project.ModulesData.TryGetValue("TextEditor", out var data))
                {
                    if (data is string text)
                        return text;
                }
                return "";
            }
            set
            {
                _project.ModulesData["TextEditor"] = value;
                this.RaisePropertyChanged();
            }
        }

        public string? FilePath
        {
            get => _filePath;
            set
            {
                _filePath = value;
                this.RaisePropertyChanged();
            }
        }

        /// <summary>Активна ли вкладка</summary>
        public bool IsActive
        {
            get => _isActive;
            set
            {
                _isActive = value;
                this.RaisePropertyChanged();
            }
        }

        /// <summary>Команда закрытия вкладки</summary>
        public ReactiveCommand<Unit, Unit> CloseCommand { get; }

        public DocumentTabViewModel(
            ProjectFile project,
            string filePath = "",
            Func<DocumentTabViewModel, Task>? onClose = null)
        {
            _logger = App.Services.GetService<ILogger<DocumentTabViewModel>>()!;
            _project = project;
            _filePath = filePath;
            _onClose = onClose;
            Id = Guid.NewGuid().ToString();

            Context = new DocumentContext(project, filePath);

            var moduleFactory = App.Services.GetRequiredService<ModuleFactory>();
            ModuleContext = new ProjectModuleContext(project.Id, moduleFactory);
            _logger.LogDebug("ProjectModuleContext created for: {ProjectTitle}", project.Title);

            ModuleContext.ModuleDataChanged += OnModuleDataChanged;
            ModuleContext.ModuleRemoving += OnModuleRemoving;

            CloseCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                _logger.LogDebug("CloseCommand executed");
                if (_onClose != null)
                    await _onClose(this);
                else
                    _logger.LogError("onClose callback is null");
            });

            this.WhenAnyValue(x => x.RecoveryBanner)
                .Subscribe(_ => this.RaisePropertyChanged(nameof(HasRecoveryBanner)))
                .DisposeWith(_disposables);
        }

        /// <summary>
        /// Инициализировать WorkspaceController без активации
        /// Вызывается из ProjectWorkflow после загрузки WorkModes
        /// </summary>
        public void InitializeWorkspace(List<Core.Models.WorkModes.WorkMode> loadedWorkModes, IProjectFileStorage? storage = null)
        {
            if (string.IsNullOrEmpty(_filePath))
            {
                _logger.LogWarning("Cannot initialize workspace - no file path");
                return;
            }

            var dockFactory = App.Services.GetRequiredService<DockFactory>();
            var autoSave = App.Services.GetRequiredService<IWorkspaceAutoSaveService>();

            Workspace = new WorkspaceController(
                this,
                _filePath,
                loadedWorkModes,
                dockFactory,
                autoSave,
                storage
            );

            IsLoaded = true;
            _logger.LogDebug("WorkspaceController initialized for: {Title}", Title);
        }

        /// <summary>
        /// Активировать Workspace при первом переходе на вкладку
        /// </summary>
        public void EnsureWorkspaceActivated()
        {
            if (Workspace == null)
            {
                _logger.LogWarning("Workspace not initialized, cannot activate");
                return;
            }

            Workspace.Activate();
            _logger.LogDebug("Workspace activated for: {Title}", Title);
        }

        /// <summary>
        /// Ключ отложенных сохранений вкладки для QuietPeriodScheduler: путь файла
        /// проекта, а у ещё не сохранённого проекта — идентичность объекта вкладки.
        /// Один ключ из всех мест, где ставится сохранение ушедшей вкладки, — чтобы
        /// одна и та же работа выполнялась один раз.
        /// </summary>
        public static string GetDeferredSaveKey(DocumentTabViewModel tab)
            => string.IsNullOrEmpty(tab.FilePath)
                ? "tab#" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(tab)
                : tab.FilePath;

        /// <summary>
        /// Сохранить в кеш асинхронно
        /// Сохраняет только если данные отличаются от сохранённого файла проекта
        /// Ключ — moduleType
        /// </summary>
        public async Task SaveToCacheAsync(Func<IEnumerable<IModule>> getActiveModules)
        {
            try
            {
                var stateCollector = App.Services.GetRequiredService<IModuleStateCollectorService>();
                var cacheService = App.Services.GetRequiredService<IProjectCacheService>();
                var projectService = App.Services.GetRequiredService<IProjectService>();

                var activeModules = getActiveModules().ToList();

                if (activeModules.Count == 0)
                    return;

                // У несохранённого проекта нет файла — ни кеша, ни сравнения с файлом.
                if (string.IsNullOrEmpty(FilePath))
                    return;

                // Быстрый путь: модули сами сообщают о правках. Если вкладка чистая или с
                // прошлой записи кеша ничего не менялось — работы нет вообще: ни снимков
                // модулей на UI-потоке, ни чтения файла проекта на сотни мегабайт.
                var decision = DecideCacheWrite(activeModules, out var revisions);

                if (decision == CacheWriteDecision.SkipClean)
                {
                    _logger.LogDebug("No unsaved changes, cache not needed: {Title}", Title);
                    return;
                }

                if (decision == CacheWriteDecision.SkipUnchanged)
                {
                    _logger.LogDebug("Cache is up to date, nothing changed since last write: {Title}", Title);
                    return;
                }

                if (decision == CacheWriteDecision.Write)
                {
                    var (trackedCustom, trackedSession) = await Task.Run(() => stateCollector.CollectAllData(activeModules));

                    if (trackedCustom.Count == 0)
                        return;

                    await cacheService.SaveCacheAsync(FilePath!, _project.Id, trackedCustom, trackedSession);
                    RememberCachedRevisions(revisions!);
                    _logger.LogDebug("Cache saved (tracked changes): {Title}", Title);
                    return;
                }

                // Сбор данных модулей на фоновом потоке: полная сериализация документа
                // и сотен персонажей выполнялась здесь синхронно на UI-потоке и давала
                // жёсткое провисание в момент каждого переключения вкладки. Тяжёлые
                // модули (IStateSnapshotModule) внутри GetCustomData сами прыгают на
                // UI-поток только за быстрым снимком модели — тот же проверенный путь,
                // что у периодического автосейва CacheUpdateService.
                //
                // Сначала — только модули без отслеживания правок: их данные сравниваются
                // с файлом. Модули с отметками отвечают за себя сами. Если правок нет ни
                // там, ни там — дальше делать нечего: без этой проверки полный сбор (со
                // снимком всего документа на UI-потоке) и запись кеша шли при каждом уходе
                // с вкладки, потому что набор модулей воркмода почти никогда не совпадает
                // с набором в файле и общее сравнение всегда видело «изменилось».
                var untrackedModules = activeModules
                    .Where(m => m is not IChangeTrackingModule { TracksChanges: true })
                    .ToList();

                var (untrackedCustom, _) = await Task.Run(() => stateCollector.CollectAllData(untrackedModules));

                var savedProject = await projectService.LoadAsync(FilePath!);

                if (savedProject != null)
                {
                    var savedModulesData = savedProject.ModulesData;
                    bool untrackedDirty = await Task.Run(
                        () => EvaluateUntrackedDirty(untrackedModules, untrackedCustom, savedModulesData));
                    SetUntrackedDirty(untrackedDirty);

                    if (!untrackedDirty && !HasTrackedChanges(activeModules))
                    {
                        _logger.LogDebug("No changes, cache not needed: {Title}", Title);
                        return;
                    }
                }

                var (customData, sessionData) = await Task.Run(() => stateCollector.CollectAllData(activeModules));

                if (customData.Count == 0)
                    return;

                if (savedProject != null)
                {
                    // Сравнение в фоне и КАНОНИЧЕСКОЕ (через IHashService: объект,
                    // JObject и JSON-строка с одинаковым содержимым дают один хеш).
                    // Наивное сравнение Equals(объект, строка-из-проекта) всегда давало
                    // «изменилось» для модулей, возвращающих объекты (Characters),
                    // кеш писался при каждом переключении вкладки без единой правки,
                    // и при следующем открытии вкладка попадала в режим восстановления.
                    var hashService = App.Services.GetRequiredService<Writersword.Core.Interfaces.Services.IHashService>();
                    bool dataChanged = await Task.Run(() =>
                    {
                        if (customData.Count != savedProject.ModulesData.Count)
                            return true;

                        foreach (var kvp in customData)
                        {
                            if (!savedProject.ModulesData.TryGetValue(kvp.Key, out var savedData))
                                return true;

                            if (kvp.Value is string currentStr && savedData is string savedStr)
                            {
                                // Быстрый путь для строковых данных (TextEditor).
                                if (currentStr != savedStr
                                    && hashService.ComputeHash(currentStr) != hashService.ComputeHash(savedStr))
                                    return true;
                            }
                            else if (hashService.ComputeHash(kvp.Value) != hashService.ComputeHash(savedData))
                            {
                                return true;
                            }
                        }

                        return false;
                    });

                    if (dataChanged)
                    {
                        await cacheService.SaveCacheAsync(FilePath!, _project.Id, customData, sessionData);
                        _logger.LogDebug("Cache saved (differs from project file)");
                    }
                    else
                    {
                        _logger.LogDebug("No changes, cache not needed");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SaveToCache failed");
            }
        }

        /// <summary>
        /// Обновить данные проекта (используется при переключении версий)
        /// </summary>
        public void UpdateProject(ProjectFile newProject)
        {
            _project.ModulesData.Clear();
            foreach (var kvp in newProject.ModulesData)
                _project.ModulesData[kvp.Key] = kvp.Value;

            _project.LastModified = newProject.LastModified;
            _project.ProjectPinnedColors = newProject.ProjectPinnedColors;
            _project.ProjectRecentColors = newProject.ProjectRecentColors;
            _project.AvatarRingsAll = newProject.AvatarRingsAll;
            _project.ProjectPalettes = newProject.ProjectPalettes;
            _project.GlobalPaletteOrder = newProject.GlobalPaletteOrder;

            this.RaisePropertyChanged(nameof(Content));

            _logger.LogDebug("Project data updated: {Count} modules", _project.ModulesData.Count);
        }

        /// <summary>Получить проект</summary>
        public ProjectFile GetProject() => _project;

        // ── Несохранённые правки ──────────────────────────────────────────
        //
        // Модули сообщают о правках сами (IChangeTrackingModule): у каждого есть отметка
        // состояния данных. Вкладка помнит отметки на момент последнего сохранения и
        // сравнивает с текущими — два числа на модуль вместо полного сбора данных и
        // сравнения хешей с файлом проекта.
        //
        // _hasUnsavedChanges — правки, которые по отметкам не видны: режим сравнения
        // версий (открыта версия из автосохранения), модуль с несохранёнными правками
        // закрыт или убран сменой воркмода, явная отметка MarkAsModified.
        // _untrackedDirty — итог последнего полного сравнения для вкладки, где есть
        // модуль без отслеживания правок (прежний путь).

        private readonly object _changeLock = new();
        private readonly Dictionary<string, ModuleChangeStamp> _savedStamps = new(StringComparer.Ordinal);
        private Dictionary<string, long>? _lastCachedRevisions;
        private bool _untrackedDirty;
        private volatile bool _isDirty;

        // Хеш данных модуля без отслеживания правок, которого нет в файле проекта
        // (модуль добавлен в воркмод после последнего сохранения): его данные
        // сравнивать не с чем, поэтому исходным считается первое увиденное состояние.
        private readonly Dictionary<string, string> _untrackedBaselineHashes = new(StringComparer.Ordinal);

        // Почему вкладка помечена несохранённой — для диагностики (см. RecomputeDirty).
        private string? _forcedDirtyReason;
        private string? _untrackedDirtyModules;

        private static readonly Serilog.ILogger DirtyLog =
            Serilog.Log.ForContext("SourceContext", "DirtyTracker");

        /// <summary>
        /// В проекте есть несохранённые правки. Показывается точкой на вкладке.
        /// Меняется только на UI-потоке.
        /// </summary>
        public bool IsDirty
        {
            get => _isDirty;
            private set
            {
                if (_isDirty == value) return;
                _isDirty = value;
                this.RaisePropertyChanged();
            }
        }

        /// <summary>Решение о записи кеша (см. DecideCacheWrite).</summary>
        public enum CacheWriteDecision
        {
            /// <summary>Есть модуль без отслеживания правок — нужен прежний полный путь.</summary>
            NotTracked,

            /// <summary>Несохранённых правок нет: данные совпадают с файлом, кеш не нужен.</summary>
            SkipClean,

            /// <summary>С прошлой записи кеша ничего не менялось.</summary>
            SkipUnchanged,

            /// <summary>Правки есть и кеш устарел — собрать данные и записать без сравнения с файлом.</summary>
            Write
        }

        /// <summary>Отметить что есть несохранённые изменения</summary>
        public void MarkAsModified()
        {
            var caller = new System.Diagnostics.StackTrace(1, false).GetFrame(0)?.GetMethod();
            _forcedDirtyReason = "MarkAsModified из "
                + (caller != null ? caller.DeclaringType?.Name + "." + caller.Name : "?");

            _hasUnsavedChanges = true;
            _logger.LogDebug("Marked as modified: {Title}", Title);
            RunOnUiThread(RecomputeDirty);
        }

        /// <summary>
        /// Сбросить флаг изменений после успешного сохранения. Сохранённым считается
        /// текущее состояние всех модулей.
        /// </summary>
        public void MarkAsSaved() => MarkAsSaved(CaptureChangeStamps());

        /// <summary>
        /// Сбросить флаг изменений после успешного сохранения. stampsAtSave — отметки
        /// на момент сбора данных для сохранения: правки, сделанные пока шла запись,
        /// остаются несохранёнными.
        /// </summary>
        public void MarkAsSaved(IReadOnlyDictionary<string, ModuleChangeStamp> stampsAtSave)
        {
            lock (_changeLock)
            {
                foreach (var kvp in stampsAtSave)
                    _savedStamps[kvp.Key] = kvp.Value;

                // Кеш после сохранения удаляется — прежние ревизии кеша больше не значат ничего.
                _lastCachedRevisions = null;

                // Теперь данные всех модулей есть в файле — сравнивать есть с чем.
                _untrackedBaselineHashes.Clear();
            }

            _hasUnsavedChanges = false;
            _untrackedDirty = false;
            _forcedDirtyReason = null;
            _untrackedDirtyModules = null;
            _logger.LogDebug("Marked as saved: {Title}", Title);
            RunOnUiThread(RecomputeDirty);
        }

        /// <summary>Проверить есть ли несохранённые изменения</summary>
        public bool HasUnsavedChanges() => _isDirty;

        /// <summary>
        /// Текущие отметки всех модулей, отслеживающих правки. Снимаются перед сбором
        /// данных для сохранения и передаются в MarkAsSaved после успешной записи.
        /// </summary>
        public Dictionary<string, ModuleChangeStamp> CaptureChangeStamps()
        {
            var result = new Dictionary<string, ModuleChangeStamp>(StringComparer.Ordinal);

            foreach (var module in ModuleContext.GetAllModules())
            {
                if (module is IChangeTrackingModule { TracksChanges: true } tracking)
                    result[module.moduleType] = tracking.ChangeStamp;
            }

            return result;
        }

        /// <summary>
        /// Принять текущее состояние модуля за исходное. Вызывается DockFactory сразу
        /// после того, как модуль получил свои данные: всё, что он загрузил, — это
        /// состояние файла (или кеша, если вкладка и так помечена изменённой).
        /// </summary>
        public void AcceptModuleBaseline(IModule module)
        {
            if (module is not IChangeTrackingModule { TracksChanges: true } tracking)
                return;

            // Фоновым приоритетом и всегда отложенно: проверки событий, которые породила
            // сама загрузка (SetCustomData, создание вью), стоят в очереди с тем же
            // приоритетом раньше — к этому моменту они уже отработали или будут
            // отброшены AcceptLoadedState. Раньше отметка снималась до них, проверка
            // затем засчитывала «правку мимо истории», и только что открытая вкладка
            // получала точку без единой правки.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                tracking.AcceptLoadedState();

                lock (_changeLock)
                    _savedStamps[module.moduleType] = tracking.ChangeStamp;

                RecomputeDirty();
            }, Avalonia.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// Есть ли несохранённые правки, видимые без сравнения данных: явная отметка
        /// или отметка какого-либо из этих модулей, отличная от сохранённой. Можно
        /// вызывать с любого потока.
        /// </summary>
        public bool HasTrackedChanges(IEnumerable<IModule> modules)
        {
            if (_hasUnsavedChanges)
                return true;

            lock (_changeLock)
            {
                foreach (var module in modules)
                {
                    if (module is not IChangeTrackingModule { TracksChanges: true } tracking)
                        continue;

                    if (_savedStamps.TryGetValue(module.moduleType, out var saved)
                        && tracking.ChangeStamp != saved)
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Есть ли несохранённые правки в модулях без отслеживания правок. Сравниваются
        /// только их данные: модули с отметками отвечают за себя сами, а модули, которых
        /// нет среди активных (другой воркмод, выгружены), правок не содержат.
        /// <para>
        /// Модуль, данных которого нет в файле проекта (добавлен в воркмод после
        /// сохранения), сравнивается с первым увиденным состоянием своих данных.
        /// </para>
        /// Хеширование тяжёлое — вызывать с фонового потока.
        /// </summary>
        public bool EvaluateUntrackedDirty(
            IEnumerable<IModule> modules,
            IReadOnlyDictionary<string, object?> currentData,
            IReadOnlyDictionary<string, object?> savedData)
        {
            var hashService = App.Services.GetRequiredService<Writersword.Core.Interfaces.Services.IHashService>();
            List<string>? differing = null;

            foreach (var module in modules)
            {
                if (module is IChangeTrackingModule { TracksChanges: true })
                    continue;

                var key = module.moduleType;
                if (!currentData.TryGetValue(key, out var current))
                    continue;

                bool differs;

                if (savedData.TryGetValue(key, out var saved))
                {
                    if (current is string currentStr && saved is string savedStr)
                    {
                        differs = currentStr != savedStr
                                  && hashService.ComputeHash(currentStr) != hashService.ComputeHash(savedStr);
                    }
                    else if (current == null || saved == null)
                    {
                        differs = !Equals(current, saved);
                    }
                    else
                    {
                        differs = hashService.ComputeHash(current) != hashService.ComputeHash(saved);
                    }
                }
                else
                {
                    var hash = hashService.ComputeHash(current);

                    lock (_changeLock)
                    {
                        if (_untrackedBaselineHashes.TryGetValue(key, out var baseline))
                        {
                            differs = baseline != hash;
                        }
                        else
                        {
                            _untrackedBaselineHashes[key] = hash;
                            differs = false;
                        }
                    }
                }

                if (differs)
                    (differing ??= new List<string>()).Add(key);
            }

            _untrackedDirtyModules = differing != null ? string.Join(", ", differing) : null;
            return differing != null;
        }

        /// <summary>
        /// Итог полного сравнения данных с файлом для вкладки, где есть модуль без
        /// отслеживания правок. Вызывается прежним путём записи кеша.
        /// </summary>
        public void SetUntrackedDirty(bool dirty)
        {
            RunOnUiThread(() =>
            {
                _untrackedDirty = dirty;
                RecomputeDirty();
            });
        }

        /// <summary>
        /// Решить, нужно ли писать кеш для этих модулей. Можно вызывать с любого потока.
        /// revisions — ревизии модулей на момент решения; после успешной записи их
        /// передают в RememberCachedRevisions.
        /// </summary>
        public CacheWriteDecision DecideCacheWrite(
            IReadOnlyCollection<IModule> modules,
            out Dictionary<string, long>? revisions)
        {
            revisions = new Dictionary<string, long>(StringComparer.Ordinal);

            foreach (var module in modules)
            {
                if (module is not IChangeTrackingModule { TracksChanges: true } tracking)
                {
                    revisions = null;
                    return CacheWriteDecision.NotTracked;
                }

                revisions[module.moduleType] = tracking.Revision;
            }

            if (!_isDirty)
                return CacheWriteDecision.SkipClean;

            lock (_changeLock)
            {
                if (_lastCachedRevisions != null && SameRevisions(_lastCachedRevisions, revisions))
                    return CacheWriteDecision.SkipUnchanged;
            }

            return CacheWriteDecision.Write;
        }

        /// <summary>Запомнить ревизии модулей, с которыми кеш записан.</summary>
        public void RememberCachedRevisions(Dictionary<string, long> revisions)
        {
            lock (_changeLock)
                _lastCachedRevisions = revisions;
        }

        private static bool SameRevisions(Dictionary<string, long> a, Dictionary<string, long> b)
        {
            if (a.Count != b.Count) return false;

            foreach (var kvp in b)
            {
                if (!a.TryGetValue(kvp.Key, out var value) || value != kvp.Value)
                    return false;
            }

            return true;
        }

        private void OnModuleDataChanged(IModule module) => RecomputeDirty();

        /// <summary>
        /// Модуль закрывают или убирают сменой воркмода. Его несохранённые правки уже
        /// собраны в данные проекта и кеш, но отметки модуля исчезнут вместе с ним —
        /// поэтому вкладка запоминает «есть несохранённое» до следующего сохранения.
        /// </summary>
        private void OnModuleRemoving(IModule module)
        {
            if (module is IChangeTrackingModule { TracksChanges: true } tracking)
            {
                lock (_changeLock)
                {
                    if (_savedStamps.TryGetValue(module.moduleType, out var saved)
                        && saved != tracking.ChangeStamp)
                    {
                        _hasUnsavedChanges = true;
                        _forcedDirtyReason = $"модуль {module.moduleType} убран с несохранёнными правками "
                            + $"(сохранено {saved}, сейчас {tracking.ChangeStamp})";
                    }

                    _savedStamps.Remove(module.moduleType);
                    _lastCachedRevisions = null;
                }
            }

            RunOnUiThread(RecomputeDirty);
        }

        private void RecomputeDirty()
        {
            bool dirty = _hasUnsavedChanges || _untrackedDirty;
            string? reason = _hasUnsavedChanges
                ? _forcedDirtyReason ?? "явная отметка"
                : _untrackedDirty
                    ? "данные модулей без отслеживания отличаются от файла: " + (_untrackedDirtyModules ?? "?")
                    : null;

            if (!dirty)
            {
                lock (_changeLock)
                {
                    foreach (var module in ModuleContext.GetAllModules())
                    {
                        if (module is not IChangeTrackingModule { TracksChanges: true } tracking)
                            continue;

                        // Модуль ещё загружается и исходного состояния не получил — правок нет.
                        if (!_savedStamps.TryGetValue(module.moduleType, out var saved))
                            continue;

                        var current = tracking.ChangeStamp;
                        if (current != saved)
                        {
                            dirty = true;
                            reason = $"модуль {module.moduleType}: сохранено {saved}, сейчас {current}";
                            break;
                        }
                    }
                }
            }

            // Диагностика: в журнал попадает каждый переход вкладки в «несохранённую»
            // с причиной — по ней видно, какой модуль или какой путь зажёг точку.
            if (dirty && !_isDirty)
                DirtyLog.Warning("Вкладка {Title} помечена несохранённой: {Reason}", Title, reason);
            else if (!dirty && _isDirty)
                DirtyLog.Warning("Вкладка {Title} снова сохранённая", Title);

            IsDirty = dirty;
        }

        private static void RunOnUiThread(Action action)
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                action();
            else
                Avalonia.Threading.Dispatcher.UIThread.Post(action);
        }

        /// <summary>
        /// Очистка ресурсов
        /// Уничтожает WorkspaceController и все модули проекта
        /// </summary>
        public override void Dispose()
        {
            _logger.LogDebug("Disposing: {Title}", Title);

            Workspace?.Dispose();
            Workspace = null;

            if (ModuleContext != null)
            {
                ModuleContext.ModuleDataChanged -= OnModuleDataChanged;
                ModuleContext.ModuleRemoving -= OnModuleRemoving;
            }

            ModuleContext?.Dispose();

            base.Dispose();
            _logger.LogDebug("Disposed: {Title}", Title);
        }
    }
}