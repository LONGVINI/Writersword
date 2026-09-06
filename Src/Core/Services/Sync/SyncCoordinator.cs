using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Models.Sync;

namespace Writersword.Core.Services.Sync
{
    /// <summary>
    /// Автоматическая синхронизация открытых проектов.
    ///
    /// Нажатие кнопки — не механизм: кнопку забывают, и забывают именно тогда,
    /// когда работа была важной. Поэтому здесь ничего не ждёт действий автора:
    /// координатор сам замечает, что файл изменился, и отправляет его.
    ///
    /// Изменения ловятся не подпиской на события сохранения, а сравнением
    /// времени записи файла. Так покрывается любой путь, которым файл мог
    /// измениться — автосохранение, ручное сохранение, восстановление из
    /// резервной копии, — и не требуется вмешательство в существующие сервисы.
    ///
    /// Отправка молчит только там, где это безопасно. Расхождение версий
    /// автоматически не разрешается никогда: молча затереть работу, сделанную
    /// на другом устройстве, хуже, чем не отправить вовсе.
    /// </summary>
    public sealed class SyncCoordinator : IDisposable
    {
        /// <summary>Ключ мастер-пароля в хранилище секретов.</summary>
        public const string MasterPasswordKey = "sync.master";

        private readonly ProjectSyncFactory _factory;
        private readonly ISecretStore _secrets;
        private readonly Func<IReadOnlyList<string>> _openProjects;
        private readonly IBackupService? _backups;
        private readonly ILogger _log;

        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Dictionary<string, DateTime> _lastSeenWrite = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SyncState> _states = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Когда история версий этого проекта сверялась с хранилищем последний раз.
        ///
        /// Раньше сверка шла следом за каждой отправкой проекта, то есть раз в
        /// две минуты. Смысла в этом ритме нет: точка восстановления возникает
        /// не чаще раза в час, и двадцать девять заходов из тридцати не находят
        /// ничего. А цена есть — обращение в сеть на каждое автосохранение.
        /// </summary>
        private readonly Dictionary<string, DateTimeOffset> _lastHistorySync = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Минимальный промежуток между сверками истории.</summary>
        private static readonly TimeSpan HistoryInterval = TimeSpan.FromHours(1);

        /// <summary>
        /// Как часто смотреть, кто ещё держит книгу.
        ///
        /// Пятнадцать секунд — не роскошь: спрашивается файл отметок с условием
        /// по версии, и неизменившийся сервер не отдаёт вовсе, ни тела, ни
        /// трафика. Своя отметка при этом обновляется на общем проходе, раз в
        /// пару минут: писать её четыре раза в минуту незачем.
        /// </summary>
        private static readonly TimeSpan PresenceInterval = TimeSpan.FromSeconds(15);

        /// <summary>Кто держит книгу, по последнему опросу. Ключ — путь к книге.</summary>
        private readonly Dictionary<string, DevicePresence> _foreign =
            new(StringComparer.OrdinalIgnoreCase);

        private CancellationTokenSource? _loop;
        private CancellationTokenSource? _presenceLoop;
        private bool _disposed;

        /// <summary>
        /// История версий необязательна: на телефоне склада нет, и там
        /// синхронизируется только сам проект.
        /// </summary>
        public SyncCoordinator(
            ProjectSyncFactory factory,
            ISecretStore secrets,
            Func<IReadOnlyList<string>> openProjects,
            ILogger logger,
            IBackupService? backups = null)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
            _openProjects = openProjects ?? throw new ArgumentNullException(nameof(openProjects));
            _backups = backups;
            _log = logger?.ForContext<SyncCoordinator>() ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Состояние проекта изменилось. Подписчик решает, показывать ли
        /// значок в строке состояния и спрашивать ли автора.
        /// </summary>
        public event EventHandler<ProjectSyncState>? ProjectStateChanged;

        /// <summary>
        /// Книга открыта ещё на одном устройстве.
        ///
        /// Сводить две правки одного текста автоматически нельзя — выбор
        /// принадлежит автору. Но предупредить, что вторая сторона сейчас за
        /// работой, можно заранее, и тогда до расхождения дело не дойдёт.
        ///
        /// Событие, а не запрет: программа не вправе запирать книгу, потому что
        /// однажды запрёт её насовсем — устройство умирает, не убрав отметку.
        /// </summary>
        public event EventHandler<ForeignPresenceEventArgs>? ForeignPresenceDetected;

        /// <summary>Работает ли фоновая проверка.</summary>
        public bool IsRunning => _loop is not null;

        /// <summary>Состояние конкретного проекта, каким его видели в последний раз.</summary>
        public SyncState StateOf(string localPath)
        {
            lock (_states)
            {
                return _states.TryGetValue(localPath, out var s) ? s : SyncState.Disabled;
            }
        }

        /// <summary>
        /// Запустить фоновую проверку.
        ///
        /// Интервал по умолчанию берётся из настроек; ноль в них означает, что
        /// автоматики нет и синхронизация только ручная.
        /// </summary>
        public void Start()
        {
            ThrowIfDisposed();

            if (_loop is not null)
                return;

            var settings = _factory.LoadSettings();
            if (!settings.IsEnabled || !settings.IsConfigured || settings.PollInterval <= TimeSpan.Zero)
            {
                _log.Debug("Sync coordinator not started: disabled or not configured");
                return;
            }

            _loop = new CancellationTokenSource();
            _ = RunLoopAsync(settings.PollInterval, _loop.Token);

            _presenceLoop = new CancellationTokenSource();
            _ = RunPresenceLoopAsync(_presenceLoop.Token);

            _log.Information("Sync coordinator started, interval {Interval}", settings.PollInterval);
        }

        /// <summary>Остановить фоновую проверку.</summary>
        public void Stop()
        {
            var loop = _loop;
            _loop = null;

            if (loop is null)
                return;

            loop.Cancel();
            loop.Dispose();

            var presence = _presenceLoop;
            _presenceLoop = null;
            presence?.Cancel();
            presence?.Dispose();

            lock (_foreign) _foreign.Clear();

            _log.Information("Sync coordinator stopped");
        }

        /// <summary>Перечитать настройки и перезапуститься под них.</summary>
        public void Restart()
        {
            Stop();
            lock (_states) _states.Clear();
            lock (_lastSeenWrite) _lastSeenWrite.Clear();
            lock (_lastHistorySync) _lastHistorySync.Clear();
            Start();
        }

        private async Task RunLoopAsync(TimeSpan interval, CancellationToken ct)
        {
            // Первый проход с задержкой: при запуске программы проекты ещё
            // открываются, и лезть в сеть в этот момент значит соревноваться
            // за диск и процессор с загрузкой документа.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await TickAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Цикл не имеет права умереть от единичного сбоя: он и есть
                    // весь механизм автоматической отправки.
                    _log.Warning(ex, "Sync tick failed");
                }

                try
                {
                    await Task.Delay(interval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>
        /// Кто ещё держит эту книгу, по последнему опросу. null — никто.
        /// </summary>
        public DevicePresence? ForeignOn(string localPath)
        {
            lock (_foreign)
                return _foreign.TryGetValue(localPath, out var found) ? found : null;
        }

        /// <summary>
        /// Быстрый опрос отметок.
        ///
        /// Отдельным циклом, а не в общем проходе: общий ходит раз в пару минут,
        /// потому что отправляет книги, а узнать, что за ту же книгу сел кто-то
        /// ещё, хочется до того, как оба напишут по абзацу. Опрос ничего не
        /// отправляет и спрашивает с условием по версии — неизменившийся файл
        /// сервер не отдаёт.
        /// </summary>
        private async Task RunPresenceLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(PresenceInterval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }

                try
                {
                    await PeekPresenceAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log.Debug(ex, "Presence peek tick failed");
                }
            }
        }

        private async Task PeekPresenceAsync(CancellationToken ct)
        {
            var sync = _factory.Current;
            if (sync is null || !sync.IsConnected) return;

            foreach (var path in _openProjects().Where(p => !string.IsNullOrWhiteSpace(p)).Distinct())
            {
                ct.ThrowIfCancellationRequested();

                var name = Path.GetFileNameWithoutExtension(path);
                var report = await sync
                    .PeekPresenceAsync(name, DeviceIdentity.Id, ct)
                    .ConfigureAwait(false);

                // null означает «ничего не изменилось» — не то же самое, что
                // «никого нет»: снимок трогать нельзя.
                if (report is null) continue;

                UpdateForeign(path, report.Other);
            }
        }

        /// <summary>
        /// Обновляет снимок и сообщает о приходе. О приходе — один раз: повторять
        /// одно и то же каждые пятнадцать секунд значит приучить человека
        /// отмахиваться, не читая.
        /// </summary>
        private void UpdateForeign(string path, DevicePresence? other)
        {
            bool arrived;

            lock (_foreign)
            {
                _foreign.TryGetValue(path, out var known);

                if (other is null)
                {
                    _foreign.Remove(path);
                    arrived = false;
                }
                else
                {
                    arrived = known is null
                              || !string.Equals(known.DeviceId, other.DeviceId, StringComparison.Ordinal)
                              || (other.Editing && !known.Editing);

                    _foreign[path] = other;
                }
            }

            if (!arrived || other is null) return;

            _log.Warning("Project {Path} is also open on {Device}", path, other);

            ForeignPresenceDetected?.Invoke(this, new ForeignPresenceEventArgs
            {
                LocalPath = path,
                Other = other
            });
        }

        private async Task TickAsync(CancellationToken ct)
        {
            var sync = _factory.Current;
            if (sync is null)
                return;

            if (!sync.IsConnected && !await TryConnectAsync(sync, ct).ConfigureAwait(false))
                return;

            foreach (var path in _openProjects().Where(p => !string.IsNullOrWhiteSpace(p)).Distinct())
            {
                ct.ThrowIfCancellationRequested();
                await SyncOneAsync(sync, path, ct).ConfigureAwait(false);

                // История сверяется здесь, а не внутри отправки: восстановить её
                // нужно и тогда, когда сам проект менять не пришлось — на новой
                // машине он приходит с сервера один раз, а склад остаётся пустым.
                if (DueForHistory(path))
                    await SyncHistoryAsync(sync, path, ct).ConfigureAwait(false);

                await AnnounceAsync(sync, path, ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Подключиться сохранённым мастер-паролем.
        ///
        /// Пароль не спрашивается: диалог посреди работы автор либо закроет не
        /// глядя, либо будет им раздражён. Нет сохранённого пароля — нет и
        /// автоматики, о чём сказано в настройках.
        /// </summary>
        private async Task<bool> TryConnectAsync(IProjectSyncService sync, CancellationToken ct)
        {
            var master = _secrets.Read(MasterPasswordKey);

            if (string.IsNullOrEmpty(master))
            {
                _log.Debug("No stored master password, automatic sync stays idle");
                return false;
            }

            try
            {
                return await sync.ConnectAsync(master, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Debug(ex, "Automatic connect failed");
                return false;
            }
        }

        private async Task SyncOneAsync(IProjectSyncService sync, string path, CancellationToken ct)
        {
            if (!File.Exists(path))
                return;

            // Время записи — дешёвый способ понять, что смотреть глубже незачем.
            // Отпечаток файла считается только когда он действительно изменился;
            // на проекте с иллюстрациями разница ощутима.
            var write = File.GetLastWriteTimeUtc(path);
            var previousState = StateOf(path);

            lock (_lastSeenWrite)
            {
                if (_lastSeenWrite.TryGetValue(path, out var seen)
                    && seen == write
                    && previousState == SyncState.InSync)
                {
                    return;
                }

                _lastSeenWrite[path] = write;
            }

            var status = await sync.GetStatusAsync(path, ct).ConfigureAwait(false);

            switch (status.State)
            {
                case SyncState.LocalAhead:
                case SyncState.RemoteMissing:
                    await PushAsync(sync, path, ct).ConfigureAwait(false);
                    break;

                case SyncState.RemoteAhead:
                case SyncState.Diverged:
                    // Автоматически не разрешается: обе стороны содержат работу,
                    // и выбор между ними принадлежит автору, а не таймеру.
                    Report(path, status.State);
                    break;

                default:
                    Report(path, status.State);
                    break;
            }
        }

        private async Task PushAsync(IProjectSyncService sync, string path, CancellationToken ct)
        {
            var result = await sync.PushAsync(path, force: false, ct).ConfigureAwait(false);

            if (result.Success)
            {
                _log.Debug("Auto-pushed {Path}", path);
                Report(path, SyncState.InSync);
                return;
            }

            _log.Debug("Auto-push skipped for {Path}: {State}", path, result.State);
            Report(path, result.State);

            // Отправка не прошла — время записи забывается, чтобы следующий
            // проход попробовал снова, а не счёл файл уже отправленным.
            lock (_lastSeenWrite)
            {
                _lastSeenWrite.Remove(path);
            }
        }

        /// <summary>
        /// Объявить, что книга открыта здесь, и посмотреть, открыта ли она ещё где-то.
        ///
        /// Отметка обновляется на каждом проходе: она протухает сама через
        /// несколько минут, и редкое обновление объявляло бы живое устройство
        /// мёртвым. Работа здесь считается правкой: настольная программа книгу
        /// не только показывает.
        ///
        /// Решение, говорить ли о чужом присутствии, принимает UpdateForeign — он
        /// же обслуживает быстрый опрос, и правило там одно на обоих.
        /// </summary>
        private async Task AnnounceAsync(IProjectSyncService sync, string path, CancellationToken ct)
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(path);

                var self = new DevicePresence
                {
                    DeviceId = DeviceIdentity.Id,
                    DeviceName = DeviceIdentity.Name,
                    Kind = DeviceIdentity.Kind,
                    Editing = true
                };

                var report = await sync.AnnouncePresenceAsync(name, self, ct).ConfigureAwait(false);

                // Объявление заодно приносит и чужие отметки — снимок обновляется
                // отсюда же, чтобы быстрому опросу не пришлось ходить лишний раз
                // сразу после общего прохода.
                UpdateForeign(path, report.Other);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Debug(ex, "Presence announce failed for {Path}", path);
            }
        }


        /// <summary>
        /// Пора ли сверять историю этого проекта.
        /// </summary>
        private bool DueForHistory(string path)
        {
            var now = DateTimeOffset.UtcNow;

            lock (_lastHistorySync)
            {
                if (_lastHistorySync.TryGetValue(path, out var previous) && now - previous < HistoryInterval)
                    return false;

                _lastHistorySync[path] = now;
                return true;
            }
        }

        /// <summary>
        /// Свести историю версий с хранилищем в обе стороны.
        ///
        /// Отправка идёт первой. Она же и сносит с сервера прореженное — по
        /// надгробиям, которые оставило прореживание, а не по отсутствию записи
        /// на диске: отсутствие означает и удаление, и потерю, а действия в этих
        /// случаях противоположные.
        ///
        /// Восстановление идёт следом и забирает то, чего здесь нет и о чём
        /// надгробий нет тоже. Молча: случай, ради которого это сделано, —
        /// умерший диск, и требовать в нём нажатия кнопки бессмысленно.
        ///
        /// Сбой здесь не влияет на состояние синхронизации проекта: отправленный
        /// текст остаётся отправленным, даже если история отстала.
        /// </summary>
        private async Task SyncHistoryAsync(IProjectSyncService sync, string path, CancellationToken ct)
        {
            if (_backups is null)
                return;

            try
            {
                var storePath = _backups.GetStoragePath(path);
                if (string.IsNullOrEmpty(storePath))
                    return;

                var name = Path.GetFileNameWithoutExtension(path);

                var sent = await sync.PushBackupStoreAsync(storePath, name, ct).ConfigureAwait(false);
                if (sent > 0)
                    _log.Debug("Pushed {Count} history entries for {Path}", sent, path);

                var restored = await sync.PullBackupStoreAsync(storePath, name, ct).ConfigureAwait(false);
                if (restored > 0)
                    _log.Information("Restored {Count} history entries for {Path}", restored, path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Debug(ex, "History sync failed for {Path}", path);

                // Неудачная сверка не считается состоявшейся: следующий проход
                // попробует снова, не дожидаясь часа.
                lock (_lastHistorySync)
                {
                    _lastHistorySync.Remove(path);
                }
            }
        }

        /// <summary>
        /// Отправить проект немедленно, не дожидаясь очередного прохода.
        /// Вызывается при закрытии проекта и при выходе из программы.
        /// </summary>
        public async Task<SyncResult> FlushAsync(string path, CancellationToken ct = default)
        {
            ThrowIfDisposed();

            var sync = _factory.Current;
            if (sync is null)
                return SyncResult.Fail(SyncState.Disabled, "Synchronization is not configured.");

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!sync.IsConnected && !await TryConnectAsync(sync, ct).ConfigureAwait(false))
                    return SyncResult.Fail(SyncState.Offline, "Remote storage is not connected.");

                var result = await sync.PushAsync(path, force: false, ct).ConfigureAwait(false);
                Report(path, result.Success ? SyncState.InSync : result.State);

                // При закрытии проекта и выходе из программы история сверяется
                // без оглядки на частоту: другого случая может уже не быть.
                if (result.Success)
                    await SyncHistoryAsync(sync, path, ct).ConfigureAwait(false);

                return result;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Отправить все открытые проекты. Вызывается при выходе.</summary>
        public async Task FlushAllAsync(CancellationToken ct = default)
        {
            foreach (var path in _openProjects().Where(p => !string.IsNullOrWhiteSpace(p)).Distinct())
            {
                try
                {
                    await FlushAsync(path, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.Warning(ex, "Flush failed for {Path}", path);
                }
            }
        }

        private void Report(string path, SyncState state)
        {
            bool changed;
            lock (_states)
            {
                changed = !_states.TryGetValue(path, out var previous) || previous != state;
                _states[path] = state;
            }

            if (!changed)
                return;

            try
            {
                ProjectStateChanged?.Invoke(this, new ProjectSyncState(path, state));
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Sync state subscriber threw");
            }
        }

        private void ThrowIfDisposed()
            => ObjectDisposedException.ThrowIf(_disposed, this);

        public void Dispose()
        {
            if (_disposed) return;

            Stop();
            _gate.Dispose();
            _disposed = true;
        }
    }

    /// <summary>Состояние синхронизации одного проекта.</summary>
    public sealed class ProjectSyncState
    {
        public ProjectSyncState(string localPath, SyncState state)
        {
            LocalPath = localPath;
            State = state;
        }

        public string LocalPath { get; }
        public SyncState State { get; }
    }
}
