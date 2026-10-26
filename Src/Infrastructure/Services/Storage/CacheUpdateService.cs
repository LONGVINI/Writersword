using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Writersword.Core.Services.Storage;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Models.Project;
using Writersword.Core.Interfaces.Services.Storage;

namespace Writersword.Infrastructure.Services.Storage
{
    /// <summary>
    /// Сервис фонового кеширования состояния модулей.
    /// Периодически сохраняет данные модулей в базу .wsasd рядом с проектом.
    /// Сохраняет только если данные отличаются от сохранённого файла проекта.
    /// Ключ данных модуля — moduleType, не InstanceId.
    /// SemaphoreSlim гарантирует что одновременно выполняется не более одной операции кеширования.
    /// </summary>
    public class CacheUpdateService : ICacheUpdateService, IDisposable
    {
        private readonly ILogger<CacheUpdateService> _logger;
        private readonly IProjectCacheService _cacheService;
        private readonly IModuleStateCollectorService _stateCollector;
        private readonly IDataComparisonService _comparisonService;
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        private IDisposable? _cacheUpdateSubscription;
        private DebounceTimer? _debounceTimer;
        private TimeSpan _interval = TimeSpan.FromSeconds(10);
        private string? _currentProjectPath;
        private Func<IEnumerable<IModule>>? _getActiveModules;
        private bool _disposed;

        // Отметка состояния модулей (см. BuildMixedSignature) на момент последней
        // записи кеша — по пути проекта. Доступ только из операции кеширования,
        // которые не идут внахлёст (_semaphore).
        private readonly Dictionary<string, string> _lastMixedSignatures = new(StringComparer.OrdinalIgnoreCase);

        // Итог прошлой проверки модулей без отслеживания правок — по пути проекта:
        // быстрые отпечатки их данных, отметка файла проекта и вывод «есть правки».
        // Вывод зависит только от данных модулей и от файла; если не изменилось ни
        // то ни другое, он прежний, и файл не читается, а данные не нормализуются.
        // Доступ только из операции кеширования (_semaphore).
        private readonly Dictionary<string, UntrackedVerdict> _untrackedVerdicts = new(StringComparer.OrdinalIgnoreCase);

        private sealed record UntrackedVerdict(string FileStamp, Dictionary<string, string> Fingerprints, bool Dirty);

        public event EventHandler? CacheSaved;

        public CacheUpdateService(
            IProjectCacheService cacheService,
            IModuleStateCollectorService stateCollector,
            IDataComparisonService comparisonService)
        {
            _logger = App.Services.GetService<ILogger<CacheUpdateService>>()!;
            _cacheService = cacheService;
            _stateCollector = stateCollector;
            _comparisonService = comparisonService;
        }

        /// <summary>
        /// Вкладка снова совпадает с файлом проекта (правки откатили по Ctrl+Z), а
        /// кеш остался от состояния с правками. Такой кеш ничего не защищает, но при
        /// следующем открытии отличается от файла и открывает проект в режиме
        /// сравнения с версией, которую пользователь сам отменил. Удаляется.
        /// </summary>
        private void DropStaleCache(Writersword.ViewModels.DocumentTabViewModel tab, string projectPath)
        {
            if (tab.Context?.IsInCompareMode == true) return;
            if (!_cacheService.HasCache(projectPath)) return;

            _cacheService.DeleteCache(projectPath);
            tab.ForgetCachedRevisions();
            _logger.LogInformation("Cache removed: tab matches the project file again {Path}", projectPath);
        }

        /// <summary>
        /// Запустить фоновое кеширование для проекта.
        /// </summary>
        public void Start(string projectPath, Func<IEnumerable<IModule>> getActiveModules)
        {
            Stop();

            _currentProjectPath = projectPath;
            _getActiveModules = getActiveModules;

            _cacheUpdateSubscription = Observable
                .Interval(_interval)
                .Subscribe(_ => ScheduleCacheUpdate());

            // Information, а не Debug: по этой строке видно, что защита от падения
            // вообще включилась для проекта. Её отсутствие в журнале — сразу диагноз.
            _logger.LogInformation("Cache protection started for {ProjectPath}, interval {Seconds}s",
                projectPath, _interval.TotalSeconds);
        }

        /// <summary>
        /// Остановить фоновое кеширование.
        /// </summary>
        public void Stop()
        {
            _cacheUpdateSubscription?.Dispose();
            _cacheUpdateSubscription = null;

            _debounceTimer?.Dispose();
            _debounceTimer = null;

            _currentProjectPath = null;
            _getActiveModules = null;

            _logger.LogDebug("Stopped");
        }

        /// <summary>
        /// Принудительно сохранить в кеш немедленно.
        /// </summary>
        public void SaveToCache()
        {
            ScheduleCacheUpdate();
        }

        /// <summary>
        /// Установить интервал кеширования.
        /// </summary>
        public void SetInterval(TimeSpan interval)
        {
            _interval = interval;
            _logger.LogDebug("Interval set to: {Seconds}s", interval.TotalSeconds);
        }

        /// <summary>
        /// Запускает обновление кеша в фоне, не блокируя поток таймера.
        /// SemaphoreSlim защищает от накопления параллельных операций.
        /// </summary>
        private void ScheduleCacheUpdate()
        {
            Task.Run(async () =>
            {
                if (!await _semaphore.WaitAsync(TimeSpan.Zero))
                {
                    _logger.LogDebug("Cache update skipped: previous operation still running");
                    return;
                }

                try
                {
                    // Диагностика: кеш восстановления выключен (DiagnosticSwitches).
                    if (Writersword.Infrastructure.Diagnostics.DiagnosticSwitches.BackgroundWorkDisabled)
                        return;

                    var tickStopwatch = System.Diagnostics.Stopwatch.StartNew();
                    long allocatedBefore = GC.GetTotalAllocatedBytes(false);

                    await PerformCacheUpdateAsync();

                    // Диагностика памяти: тик кеша, выделивший много, виден в журнале.
                    long allocatedMb = (GC.GetTotalAllocatedBytes(false) - allocatedBefore) / (1024 * 1024);
                    if (allocatedMb >= 50)
                        _logger.LogWarning("Cache tick: {ElapsedMs}ms, выделено {AllocatedMb} МБ",
                            tickStopwatch.ElapsedMilliseconds, allocatedMb);
                }
                finally
                {
                    _semaphore.Release();
                }
            });
        }

        /// <summary>
        /// Выполнить обновление кеша.
        /// Сохраняет только если данные отличаются от сохранённого файла проекта.
        /// </summary>
        private async Task PerformCacheUpdateAsync()
        {
            var projectPath = _currentProjectPath;
            var getModulesCallback = _getActiveModules;

            if (string.IsNullOrEmpty(projectPath) || getModulesCallback == null)
            {
                _logger.LogDebug("Skipped: service stopped");
                return;
            }

            // Пользователь прямо сейчас переключает вкладки или воркмоды. Сбор данных
            // снимает снимок модели на UI-потоке и в этот момент встал бы в очередь
            // к переключениям. Пропущенный тик догонит следующий.
            if (Writersword.Infrastructure.WorkFlows.QuietPeriodScheduler.IsBusy)
            {
                _logger.LogDebug("Skipped: user is switching tabs or work modes");
                return;
            }

            try
            {
                // Вкладка проекта ищется там же, на UI-потоке: коллекция вкладок живёт на нём.
                var (activeModules, tab) = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    (getModulesCallback(), FindTab(projectPath)));

                // DispatcherOperation не поддерживает ConfigureAwait напрямую (это не Task),
                // а завершает его именно UI-поток — без явного ухода вся дальнейшая сборка
                // CustomData по всем персонажам и запись в кэш выполнялись бы на UI-потоке,
                // и при большом числе персонажей фризили интерфейс каждые _interval секунд.
                await Task.Run(() => { }).ConfigureAwait(false);

                if (_getActiveModules == null)
                {
                    _logger.LogDebug("Skipped: service stopped during execution");
                    return;
                }

                // Быстрый путь: модули сами сообщают о правках. Вкладка без правок или с
                // кешем, записанным после последней правки, не требует ничего — прежде
                // каждый тик собирал все модули и читал файл проекта целиком ради
                // сравнения, и память росла даже когда пользователь ничего не делал.
                if (tab != null)
                {
                    var modulesList = activeModules.ToList();
                    var decision = tab.DecideCacheWrite(modulesList, out var revisions);

                    if (decision == Writersword.ViewModels.DocumentTabViewModel.CacheWriteDecision.SkipClean)
                        DropStaleCache(tab, projectPath);

                    if (decision == Writersword.ViewModels.DocumentTabViewModel.CacheWriteDecision.SkipClean
                        || decision == Writersword.ViewModels.DocumentTabViewModel.CacheWriteDecision.SkipUnchanged)
                    {
                        _logger.LogDebug("Cache tick skipped: {Decision}", decision);
                        return;
                    }

                    if (decision == Writersword.ViewModels.DocumentTabViewModel.CacheWriteDecision.Write)
                    {
                        Writersword.Infrastructure.Diagnostics.SwitchProfiler.SetStage(
                            "тик кеша: сбор данных (отслеживаемые) " + System.IO.Path.GetFileName(projectPath));
                        var (trackedCustom, trackedSession) = _stateCollector.CollectAllData(modulesList);

                        if (trackedCustom.Count == 0)
                        {
                            _logger.LogWarning(
                                "Cache tick: modules returned no data ({Count} active) — nothing is protected",
                                modulesList.Count);
                            return;
                        }

                        await _cacheService.SaveCacheAsync(projectPath, tab.GetProject().Id, trackedCustom, trackedSession);
                        tab.RememberCachedRevisions(revisions!);

                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            CacheSaved?.Invoke(this, EventArgs.Empty);
                        });

                        _logger.LogInformation("Cache updated (tracked changes): {Count} modules, {Path}",
                            trackedCustom.Count, projectPath + ".wsasd");
                        return;
                    }
                }

                // Вкладка с модулями без отслеживания правок. Сначала сравниваются только
                // их данные: если правок нет ни в них, ни по отметкам остальных модулей —
                // тик завершается без полного сбора. Прежде каждый тик собирал все модули
                // (со снимком всего документа на UI-потоке) и почти всегда писал кеш:
                // набор модулей воркмода не совпадает с набором в файле, и общее сравнение
                // видело «изменилось» без единой правки.
                bool untrackedEvaluated = false;
                string? mixedSignature = null;
                if (tab != null)
                {
                    var modulesList = activeModules.ToList();
                    var untrackedModules = modulesList
                        .Where(m => m is not IChangeTrackingModule { TracksChanges: true })
                        .ToList();

                    Writersword.Infrastructure.Diagnostics.SwitchProfiler.SetStage(
                        "тик кеша: сбор данных (без отслеживания) " + System.IO.Path.GetFileName(projectPath));
                    var (untrackedCustom, _) = _stateCollector.CollectAllData(untrackedModules);

                    // Быстрые отпечатки данных: сериализация прямо в хеш, без строк
                    // и деревьев JSON. Прежде каждые десять секунд данные модулей
                    // (у персонажей — сотни карточек) и их копия из файла целиком
                    // превращались в отсортированный JSON ради сравнения: сотни
                    // мегабайт мусора за тик и полные сборки, замораживавшие
                    // интерфейс. Теперь полное сравнение — только когда изменились
                    // данные или файл проекта.
                    Writersword.Infrastructure.Diagnostics.SwitchProfiler.SetStage(
                        "тик кеша: отпечатки (без отслеживания) " + System.IO.Path.GetFileName(projectPath));
                    var fingerprints = Fingerprints(untrackedModules, untrackedCustom);
                    var fileStamp = ProjectFileStamp(projectPath);

                    IReadOnlyDictionary<string, object?>? savedForUntracked = null;
                    bool? knownDirty = null;

                    if (fingerprints != null
                        && _untrackedVerdicts.TryGetValue(projectPath, out var verdict)
                        && verdict.FileStamp == fileStamp
                        && SameFingerprints(verdict.Fingerprints, fingerprints))
                    {
                        knownDirty = verdict.Dirty;
                    }
                    else
                    {
                        // Из файла читаются только данные модулей без отслеживания правок:
                        // сравниваются только они. Прежде на каждом тике читались данные
                        // всех модулей, включая весь текст рукописи.
                        Writersword.Infrastructure.Diagnostics.SwitchProfiler.SetStage(
                            "тик кеша: сравнение с файлом (без отслеживания) " + System.IO.Path.GetFileName(projectPath));
                        savedForUntracked = _cacheService.ReadProjectDataWithoutLock(
                            projectPath, untrackedModules.Select(m => m.moduleType));
                    }

                    if (knownDirty.HasValue || savedForUntracked != null)
                    {
                        bool untrackedDirty = knownDirty
                            ?? tab.EvaluateUntrackedDirty(untrackedModules, untrackedCustom, savedForUntracked!);
                        tab.SetUntrackedDirty(untrackedDirty);
                        untrackedEvaluated = true;

                        if (fingerprints != null)
                            _untrackedVerdicts[projectPath] = new UntrackedVerdict(fileStamp, fingerprints, untrackedDirty);
                        else
                            _untrackedVerdicts.Remove(projectPath);

                        if (!untrackedDirty && !tab.HasTrackedChanges(modulesList))
                        {
                            _logger.LogDebug("Cache tick skipped: no changes in {Path}", projectPath);
                            return;
                        }

                        // Правки есть, но с прошлой записи кеша ничего не менялось: ни
                        // ревизии модулей с отслеживанием, ни данные остальных. Кеш уже
                        // содержит это состояние — переписывать его каждые несколько
                        // секунд (у документа это весь текст) незачем. Пропавший кеш
                        // (сохранение, смена воркмода) пишется заново.
                        mixedSignature = BuildMixedSignature(modulesList, untrackedCustom, fingerprints);

                        if (_lastMixedSignatures.TryGetValue(projectPath, out var previousSignature)
                            && previousSignature == mixedSignature
                            && _cacheService.HasCache(projectPath))
                        {
                            _logger.LogDebug("Cache tick skipped: nothing changed since the last cache write in {Path}", projectPath);
                            return;
                        }
                    }
                }

                Writersword.Infrastructure.Diagnostics.SwitchProfiler.SetStage(
                    "тик кеша: сбор данных (полный путь) " + System.IO.Path.GetFileName(projectPath));
                var (customData, sessionData) = _stateCollector.CollectAllData(activeModules);

                if (customData.Count == 0)
                {
                    // Warning: модули есть, а данных нет — обычно это модуль, чей
                    // сбор состояния вернул null. Молчать нельзя: в этом состоянии
                    // защиты от падения не существует.
                    _logger.LogWarning(
                        "Cache tick: modules returned no data ({Count} active) — nothing is protected",
                        activeModules.Count());
                    return;
                }

                bool hasAnyRealData = false;
                foreach (var kvp in customData)
                {
                    if (kvp.Value == null) continue;
                    if (kvp.Value is string str)
                    {
                        if (!string.IsNullOrWhiteSpace(str)) { hasAnyRealData = true; break; }
                    }
                    else
                    {
                        hasAnyRealData = true;
                        break;
                    }
                }

                if (!hasAnyRealData)
                {
                    _logger.LogDebug("No real data, skipping");
                    if (_cacheService.HasCache(projectPath))
                    {
                        _cacheService.DeleteCache(projectPath);
                        _logger.LogDebug("Deleted outdated cache");
                    }
                    return;
                }

                // Сравнение с файлом нужно, только если правки ещё не установлены
                // выше. Если модули без отслеживания уже сравнены, а модули с
                // отслеживанием сообщили о правках сами, ответ известен — и чтение
                // всех данных проекта со сравнением всего документа на каждом тике
                // ничего бы не добавило.
                var savedProjectData = untrackedEvaluated
                    ? null
                    : _cacheService.ReadProjectDataWithoutLock(projectPath);

                if (savedProjectData != null)
                {
                    bool dataChanged = customData.Count != savedProjectData.Count;

                    if (!dataChanged)
                    {
                        foreach (var kvp in customData)
                        {
                            if (!savedProjectData.TryGetValue(kvp.Key, out var savedData))
                            {
                                dataChanged = true;
                                break;
                            }

                            if (kvp.Value is string currentStr && savedData is string savedStr)
                            {
                                if (currentStr != savedStr)
                                {
                                    dataChanged = true;
                                    _logger.LogDebug("Cache diff in module: {Module}", kvp.Key);
                                    break;
                                }
                            }
                            else if (kvp.Value == null || savedData == null)
                            {
                                if (!Equals(kvp.Value, savedData)) { dataChanged = true; break; }
                            }
                            else
                            {
                                // Модуль вернул объект (не строку): из файла данные приходят
                                // строкой JSON или JObject-ом, из живого модуля — .NET-объектом.
                                // Сравнение выполняется канонически через IHashService: объект,
                                // JObject и JSON-строка с одинаковым содержимым дают один хеш.
                                // Прежний вариант (JToken.FromObject над строкой) давал JValue
                                // вместо распарсенного объекта, DeepEquals всегда возвращал false,
                                // кеш писался при каждом проходе без изменений, и вкладка при
                                // каждом открытии попадала в режим восстановления.
                                var hashService = App.Services.GetRequiredService<Writersword.Core.Interfaces.Services.IHashService>();
                                if (hashService.ComputeHash(kvp.Value) != hashService.ComputeHash(savedData))
                                {
                                    dataChanged = true;
                                    _logger.LogDebug("Cache diff in module: {Module}", kvp.Key);
                                    break;
                                }
                            }
                        }
                    }

                    // Прежний путь заодно отвечает на вопрос о несохранённых правках
                    // модулей, которые сами о них не сообщают. Общий итог dataChanged
                    // для этого не годится: он истинен при любом несовпадении набора
                    // модулей воркмода с файлом и при другой записи той же строки
                    // документа — точка загоралась на вкладках без правок. Вкладка
                    // сравнивает только модули без отслеживания правок.
                    if (tab != null && !untrackedEvaluated)
                        tab.SetUntrackedDirty(tab.EvaluateUntrackedDirty(activeModules, customData, savedProjectData));

                    if (!dataChanged)
                    {
                        _logger.LogDebug("No changes from project file, skipping");
                        return;
                    }
                }

                ProjectFile? project = null;
                try
                {
                    // Файл проекта — база, открывается на чтение своим слоем
                    // хранения. Глобальный шлюз здесь больше не нужен: база
                    // сама разводит читателей и писателя.
                    using var storage = new SqliteFileStorageService(projectPath, Serilog.Log.Logger);

                    var projectBytes = storage.ReadFile("project.json");
                    if (projectBytes != null)
                    {
                        var json = System.Text.Encoding.UTF8.GetString(projectBytes);
                        project = Newtonsoft.Json.JsonConvert.DeserializeObject<ProjectFile>(json);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error reading ProjectId");
                }

                if (project == null)
                {
                    _logger.LogError("Cannot get ProjectId, aborting cache update");
                    return;
                }

                await _cacheService.SaveCacheAsync(projectPath, project.Id, customData, sessionData);

                // Отметка снята до сбора данных: правка, пришедшая во время записи,
                // её сдвинет, и следующий тик запишет кеш снова.
                if (mixedSignature != null)
                    _lastMixedSignatures[projectPath] = mixedSignature;

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    CacheSaved?.Invoke(this, EventArgs.Empty);
                });

                _logger.LogInformation("Cache updated: {Count} modules, {Path}",
                    customData.Count, projectPath + ".wsasd");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cache update failed");
            }
        }

        /// <summary>
        /// Отметка состояния модулей вкладки со смешанным составом: ревизии модулей,
        /// которые сами сообщают о правках, и хеши данных остальных. Совпадение
        /// отметок значит, что с прошлой записи кеша не менялось ничего.
        /// </summary>
        private static string BuildMixedSignature(
            IEnumerable<IModule> modules,
            IReadOnlyDictionary<string, object?> untrackedData,
            IReadOnlyDictionary<string, string>? fingerprints)
        {
            var hashService = App.Services.GetRequiredService<Writersword.Core.Interfaces.Services.IHashService>();
            var signature = new System.Text.StringBuilder();

            foreach (var module in modules.OrderBy(m => m.moduleType, StringComparer.Ordinal))
            {
                signature.Append(module.moduleType).Append('=');

                if (module is IChangeTrackingModule { TracksChanges: true } tracking)
                    signature.Append('r').Append(tracking.Revision);
                else if (fingerprints != null && fingerprints.TryGetValue(module.moduleType, out var fingerprint))
                    signature.Append('f').Append(fingerprint);
                else if (untrackedData.TryGetValue(module.moduleType, out var data))
                    signature.Append('h').Append(data is null ? "null" : hashService.ComputeHash(data));
                else
                    signature.Append('-');

                signature.Append(';');
            }

            return signature.ToString();
        }

        /// <summary>
        /// Быстрые отпечатки данных модулей: данные сериализуются прямо в SHA-256,
        /// без промежуточной строки и дерева JSON, поэтому памяти почти не
        /// требуют. Отпечаток годится только для сравнения с прошлым отпечатком
        /// тех же данных: ключи не сортируются, и с файлом он не сравним. null —
        /// данные какого-то модуля не сериализовались; тогда тик идёт полным путём.
        /// </summary>
        private Dictionary<string, string>? Fingerprints(
            IEnumerable<IModule> modules, IReadOnlyDictionary<string, object?> data)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var module in modules)
            {
                if (!data.TryGetValue(module.moduleType, out var value))
                {
                    result[module.moduleType] = "-";
                    continue;
                }

                try
                {
                    result[module.moduleType] = StreamHash(value);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Fingerprint failed for {Module}, full comparison", module.moduleType);
                    return null;
                }
            }

            return result;
        }

        private static readonly Newtonsoft.Json.JsonSerializer FingerprintSerializer =
            Newtonsoft.Json.JsonSerializer.Create(new Newtonsoft.Json.JsonSerializerSettings
            {
                ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore
            });

        private static string StreamHash(object? value)
        {
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
                System.Security.Cryptography.HashAlgorithmName.SHA256);

            if (value is null)
            {
                hash.AppendData(System.Text.Encoding.UTF8.GetBytes("null"));
            }
            else if (value is string text)
            {
                hash.AppendData(System.Text.Encoding.UTF8.GetBytes(text));
            }
            else
            {
                using var sink = new HashSinkStream(hash);
                using var writer = new System.IO.StreamWriter(sink, new System.Text.UTF8Encoding(false), 64 * 1024, leaveOpen: true);
                using (var json = new Newtonsoft.Json.JsonTextWriter(writer) { CloseOutput = false })
                    FingerprintSerializer.Serialize(json, value);
                writer.Flush();
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }

        private static bool SameFingerprints(Dictionary<string, string> a, Dictionary<string, string> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var (key, value) in a)
                if (!b.TryGetValue(key, out var other) || other != value)
                    return false;
            return true;
        }

        /// <summary>
        /// Отметка файла проекта: размер и время записи самого файла и его журнала
        /// WAL. Сохранение пишет в журнал, а в основной файл — при переносе
        /// журнала, поэтому смотрятся оба.
        /// </summary>
        private static string ProjectFileStamp(string projectPath)
        {
            static string Stamp(string path)
            {
                var info = new System.IO.FileInfo(path);
                return info.Exists ? info.Length + ":" + info.LastWriteTimeUtc.Ticks : "none";
            }

            try
            {
                return Stamp(projectPath) + "|" + Stamp(projectPath + "-wal");
            }
            catch
            {
                return Guid.NewGuid().ToString("N");
            }
        }

        /// <summary>Поток, который ничего не хранит: всё записанное уходит в хеш.</summary>
        private sealed class HashSinkStream : System.IO.Stream
        {
            private readonly System.Security.Cryptography.IncrementalHash _hash;

            public HashSinkStream(System.Security.Cryptography.IncrementalHash hash) => _hash = hash;

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Write(byte[] buffer, int offset, int count) => _hash.AppendData(buffer, offset, count);
            public override void Write(ReadOnlySpan<byte> buffer) => _hash.AppendData(buffer);
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        /// <summary>
        /// Вкладка открытого проекта по пути к файлу. Только UI-поток.
        /// </summary>
        private static Writersword.ViewModels.DocumentTabViewModel? FindTab(string projectPath)
        {
            var tabs = App.Services.GetService<Writersword.Core.Interfaces.WorkFlows.ITabCollection>();
            if (tabs == null) return null;

            return tabs.Tabs
                .OfType<Writersword.ViewModels.DocumentTabViewModel>()
                .FirstOrDefault(t => string.Equals(t.FilePath, projectPath, StringComparison.OrdinalIgnoreCase));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();
            _semaphore.Dispose();

            _logger.LogDebug("CacheUpdateService disposed");
        }
    }
}