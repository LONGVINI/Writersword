using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Writersword.Core.Models.Sync;
using Writersword.Core.Services.Sync;

namespace Writersword.Mobile.Services
{
    /// <summary>
    /// Держит скачанные книги в свежести и говорит хранилищу, что телефон читает.
    ///
    /// Настольный SyncCoordinator сюда не годится, хотя и переносим: он написан
    /// для стороны, которая правит текст, и умеет отправлять. Телефон правок не
    /// вносит — ему нужно обратное: заметить, что на сервере версия новее, и
    /// забрать её. Отправлять он не должен вовсе, иначе однажды затрёт работу,
    /// сделанную за компьютером.
    ///
    /// Заодно телефон объявляет о себе. Читатель никому не мешает, и предупреждать
    /// о нём компьютер не станет, — но обратное важно: если книгу правят за
    /// компьютером прямо сейчас, читателю стоит знать, что текст под рукой вот-вот
    /// устареет.
    /// </summary>
    public sealed class MobileAutoSync : IDisposable
    {
        /// <summary>
        /// Как часто ходить на сервер.
        ///
        /// Пять минут — не осторожность, а батарея: телефон ходит в сеть через
        /// радио, и частые походы стоят заряда заметно дороже, чем те же походы у
        /// компьютера. Книга, устаревшая на пять минут, читателю не мешает.
        /// </summary>
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

        private static readonly Lazy<MobileAutoSync> _instance = new(() => new MobileAutoSync());

        /// <summary>Один цикл на приложение: книги общие для всех экранов.</summary>
        public static MobileAutoSync Instance => _instance.Value;

        /// <summary>
        /// Частота быстрого опроса, пока книга открыта на экране.
        ///
        /// Полный проход тут не нужен: спрашивается одна вещь — не занял ли книгу
        /// компьютер. Опрос условный (сервер отвечает «не менялось» без тела), и
        /// платит за него только открытая книга: ушли с вкладки — опрос встал.
        /// </summary>
        private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(30);

        private readonly ILogger _log = Log.ForContext<MobileAutoSync>();
        private CancellationTokenSource? _loop;
        private bool _disposed;

        private CancellationTokenSource? _watch;
        private string? _watchedBook;
        private PresenceReport? _lastReport;
        private bool? _desktopHolds;

        /// <summary>
        /// Книгу правят здесь. Отметка честная: компьютер по ней увидит, что на
        /// телефоне сейчас работают, а не читают.
        /// </summary>
        public bool Editing { get; set; }

        /// <summary>Компьютер, который держит открытую книгу, или null.</summary>
        public DevicePresence? DesktopOwner => _desktopHolds == true ? _lastReport?.Desktop : null;

        /// <summary>Книга обновлена с сервера. Читалка перечитает её, если она открыта.</summary>
        public event Action<string>? BookUpdated;

        /// <summary>Книгу правят на другом устройстве.</summary>
        public event Action<DevicePresence>? ForeignEditing;

        /// <summary>
        /// Книга занята компьютером или отпущена им. Аргумент — отметка компьютера,
        /// либо null, если книга свободна.
        /// </summary>
        public event Action<DevicePresence?>? DesktopLockChanged;

        public void Start()
        {
            if (_disposed || _loop is not null) return;

            _loop = new CancellationTokenSource();
            _ = RunAsync(_loop.Token);

            _log.Information("Automatic sync started, interval {Interval}", Interval);
        }

        public void Stop()
        {
            var loop = _loop;
            _loop = null;

            if (loop is null) return;

            loop.Cancel();
            loop.Dispose();
        }

        private async Task RunAsync(CancellationToken ct)
        {
            // Первый заход с задержкой: при запуске приложение поднимает списки и
            // открывает книгу, и лезть в сеть в этот момент значит соревноваться с
            // ним за то же радио.
            try { await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

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
                    // Цикл не имеет права умереть от единичного сбоя: он и есть вся
                    // автоматика.
                    _log.Warning(ex, "Automatic sync tick failed");
                }

                try { await Task.Delay(Interval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        private async Task TickAsync(CancellationToken ct)
        {
            var session = MobileSyncSession.Instance;

            if (!session.IsConnected)
            {
                var stored = session.LoadSettings();

                // Нет сохранённого пароля — нет и автоматики. Спрашивать его
                // посреди чтения бессмысленно: человек читает, а не настраивает.
                if (string.IsNullOrEmpty(stored.MasterPassword))
                    return;

                if (!await session.ConnectAsync(stored, ct).ConfigureAwait(false))
                    return;
            }

            if (session.Service is not { } service) return;

            foreach (var path in LocalBooks())
            {
                ct.ThrowIfCancellationRequested();

                var name = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrEmpty(name)) continue;

                var status = await service.GetStatusAsync(path, ct).ConfigureAwait(false);

                // Забираем только то, что новее нашего. Расхождение телефон не
                // разрешает: править он не умеет, значит расхождение означает беду,
                // и разбирать её должен человек за компьютером.
                if (status.State == SyncState.RemoteAhead)
                {
                    var result = await service.PullAsync(path, ct).ConfigureAwait(false);

                    if (result.Success)
                    {
                        _log.Information("Book updated from storage: {Name}", name);
                        BookUpdated?.Invoke(name);
                    }
                }
                else if (status.State == SyncState.Diverged)
                {
                    _log.Warning("Book {Name} diverged; leaving it to the desktop", name);
                }

                await AnnounceAsync(service, name, ct).ConfigureAwait(false);
            }
        }

        private async Task AnnounceAsync(
            Writersword.Core.Interfaces.Services.Storage.IProjectSyncService service,
            string name,
            CancellationToken ct)
        {
            var self = new DevicePresence
            {
                DeviceId = DeviceIdentity.Id,
                DeviceName = DeviceIdentity.Name,
                Kind = DeviceIdentity.Kind,

                // Отметка честная: пока телефон читает, компьютер по ней видит,
                // что мешать ему некому.
                Editing = this.Editing
            };

            var report = await service.AnnouncePresenceAsync(name, self, ct).ConfigureAwait(false);

            if (report.ForeignEditing && report.Other is { } other)
            {
                _log.Information("Book {Name} is being edited on {Device}", name, other);
                ForeignEditing?.Invoke(other);
            }

            // Объявление приносит и чужие отметки: если речь об открытой книге,
            // замок обновляется отсюда же и не ждёт своего опроса.
            if (string.Equals(name, _watchedBook, StringComparison.OrdinalIgnoreCase))
            {
                _lastReport = report;
                ApplyLock();
            }
        }

        // ── Старшинство компьютера ────────────────────────────────────────

        /// <summary>
        /// Следить за книгой, открытой на экране: не занял ли её компьютер.
        ///
        /// Правило старшинства простое и несимметричное — книга принадлежит
        /// компьютеру. Пока он её держит, телефон её не правит и говорит об этом
        /// вслух. Обратного нет: телефон компьютеру не мешает никогда.
        /// </summary>
        public void WatchBook(string name)
        {
            if (_disposed) return;

            if (string.Equals(name, _watchedBook, StringComparison.OrdinalIgnoreCase) && _watch is not null)
                return;

            StopWatching();

            _watchedBook = name;
            _watch = new CancellationTokenSource();

            _ = RunWatchAsync(name, _watch.Token);
        }

        public void StopWatching()
        {
            var watch = _watch;
            _watch = null;
            _watchedBook = null;

            if (watch is null) return;

            watch.Cancel();
            watch.Dispose();
        }

        private async Task RunWatchAsync(string name, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await WatchTickAsync(name, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log.Debug(ex, "Desktop lock check failed for {Name}", name);
                }

                try { await Task.Delay(WatchInterval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        private async Task WatchTickAsync(string name, CancellationToken ct)
        {
            var session = MobileSyncSession.Instance;

            // Связи нет — состояние замка остаётся прежним. Объявлять книгу
            // свободной по недоступности сервера значит разрешать правку именно
            // тогда, когда о чужой работе узнать неоткуда.
            if (!session.IsConnected || session.Service is not { } service)
            {
                ApplyLock();
                return;
            }

            var report = await service
                .PeekPresenceAsync(name, DeviceIdentity.Id, ct)
                .ConfigureAwait(false);

            // null означает «не менялось», а не «никого нет»: прежний отчёт
            // остаётся в силе.
            if (report is not null)
                _lastReport = report;

            ApplyLock();
        }

        /// <summary>
        /// Пересчитать замок и сообщить, если он переменился.
        ///
        /// Свежесть проверяется на каждом проходе, а не только при новом отчёте:
        /// умерший компьютер отметку за собой не убирает, файл на сервере не
        /// меняется, и без этой проверки книга осталась бы запертой навсегда.
        /// </summary>
        private void ApplyLock()
        {
            var desktop = _lastReport?.Desktop;
            bool holds = desktop is { IsFresh: true };

            if (_desktopHolds == holds) return;

            _desktopHolds = holds;

            if (holds)
                _log.Information("Book is held by desktop {Device}", desktop);
            else
                _log.Information("Book is free again");

            DesktopLockChanged?.Invoke(holds ? desktop : null);
        }

        private static IEnumerable<string> LocalBooks()
        {
            var dir = MobileSyncSession.ProjectsDirectory;
            if (!Directory.Exists(dir)) return Enumerable.Empty<string>();

            return Directory.EnumerateFiles(dir, "*.writersword", SearchOption.TopDirectoryOnly).ToList();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopWatching();
            Stop();
        }
    }
}
