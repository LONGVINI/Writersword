using Newtonsoft.Json;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Models.Sync;

namespace Writersword.Core.Services.Sync
{
    /// <summary>
    /// Кто держит книгу открытой прямо сейчас.
    ///
    /// Синхронизация умеет не сводить расхождение молча: если обе стороны правили
    /// один текст, выбор между ними принадлежит автору. Но лучше до расхождения
    /// не доводить — а для этого устройства должны знать друг о друге заранее, а
    /// не выяснять постфактум, что работали одновременно.
    ///
    /// Это осведомление, а не запрет. Заперев книгу, программа однажды заперла бы
    /// её насовсем: устройство умирает, не убрав за собой, и отметка остаётся.
    /// Поэтому отметки протухают по времени, а решение принимает человек.
    ///
    /// Все отметки одной книги лежат в одном файле, а не порознь. Порознь их
    /// нельзя было бы прочитать: имена на сервере необратимы, перечислять папку
    /// хранилище не умеет, и устройство не узнало бы, чьи отметки искать. Файл
    /// один, а гонку за него разрешает версия: запись идёт с оглядкой на ту,
    /// которую читали, и разошедшаяся запись повторяется.
    ///
    /// Имя файла выведено так же, как имена книг, — через HMAC: по нему не
    /// восстановить ни книгу, ни устройство.
    /// </summary>
    public sealed class PresenceService
    {
        /// <summary>Подпапка отметок. Отдельно, чтобы не мешались с книгами.</summary>
        private const string PresenceFolder = "presence";

        /// <summary>Сколько раз перечитать и повторить запись при гонке.</summary>
        private const int WriteAttempts = 3;

        private readonly IRemoteStorage _storage;
        private readonly ProjectCrypto _crypto;
        private readonly ILogger _log;

        /// <summary>
        /// Версия файла отметок, какой мы её видели в последний раз.
        ///
        /// Нужна быстрому опросу. Спрашивать «отдай, только если изменился» можно
        /// хоть каждые несколько секунд: неизменившийся файл сервер не отдаёт
        /// вовсе — ни тела, ни трафика. Без этой памяти каждый опрос тянул бы файл
        /// целиком, и частый опрос стал бы непозволительным.
        /// </summary>
        private readonly Dictionary<string, string> _seenVersion = new(StringComparer.Ordinal);

        public PresenceService(IRemoteStorage storage, ProjectCrypto crypto, ILogger logger)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
            _log = logger?.ForContext<PresenceService>() ?? throw new ArgumentNullException(nameof(logger));
        }

        private sealed class PresenceFile
        {
            [JsonProperty("devices")]
            public Dictionary<string, DevicePresence> Devices { get; set; } =
                new(StringComparer.Ordinal);
        }

        /// <summary>
        /// Объявить о себе и узнать про других.
        ///
        /// Возвращает свежую отметку другого устройства, если такая есть.
        /// Протухшие отметки заодно выметаются: файл не должен расти вечно от
        /// устройств, которых давно нет.
        /// </summary>
        public async Task<PresenceReport> AnnounceAsync(
            string projectName, DevicePresence self, CancellationToken ct = default)
        {
            await _storage.EnsureFolderAsync(PresenceFolder, ct).ConfigureAwait(false);

            var key = BuildKey(projectName);

            for (int attempt = 0; attempt < WriteAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                var (file, etag) = await ReadAsync(key, ct).ConfigureAwait(false);

                DevicePresence? other = null;
                DevicePresence? desktop = null;

                foreach (var (id, presence) in file.Devices.ToList())
                {
                    if (string.Equals(id, self.DeviceId, StringComparison.Ordinal))
                        continue;

                    if (!presence.IsFresh)
                    {
                        file.Devices.Remove(id);
                        continue;
                    }

                    // Правящее устройство важнее читающего: предупреждать нужно о
                    // том, с кем можно столкнуться, а читатель ничьей работы не
                    // тронет.
                    if (other is null || (presence.Editing && !other.Editing))
                        other = presence;

                    if (presence.IsDesktop && (desktop is null || presence.Editing))
                        desktop = presence;
                }

                self.UpdatedAt = DateTimeOffset.UtcNow;
                file.Devices[self.DeviceId] = self;

                if (await WriteAsync(key, file, etag, ct).ConfigureAwait(false))
                {
                    if (other is not null)
                        _log.Debug("Project {Project} is also open on {Device}", projectName, other);

                    return new PresenceReport { Other = other, Desktop = desktop };
                }

                // Запись не прошла: другое устройство успело объявиться между
                // чтением и записью. Перечитываем — заодно и увидим его.
                _log.Debug("Presence write raced for {Project}, retrying", projectName);
            }

            _log.Debug("Presence for {Project} could not be announced", projectName);
            return new PresenceReport();
        }

        /// <summary>
        /// Посмотреть, кто держит книгу, ничего о себе не сообщая.
        ///
        /// Разведено с объявлением намеренно, и это главное в устройстве быстрого
        /// опроса. Своя отметка обязана обновляться не чаще, чем нужно для
        /// свежести — раз в пару минут; чужие же интересны как можно раньше.
        /// Объявляться каждые пятнадцать секунд значило бы писать на сервер
        /// четыре раза в минуту без всякой нужды.
        ///
        /// Возвращает null, если с прошлого раза ничего не изменилось: спрашивать
        /// с условием по версии дёшево, а разбирать неизменившееся — нет.
        /// </summary>
        public async Task<PresenceReport?> PeekAsync(
            string projectName, string selfDeviceId, CancellationToken ct = default)
        {
            var key = BuildKey(projectName);

            string? known;
            lock (_seenVersion)
                _seenVersion.TryGetValue(key, out known);

            try
            {
                var content = await _storage
                    .DownloadAsync(key, ifNoneMatch: known, ct: ct)
                    .ConfigureAwait(false);

                if (content is null)
                {
                    // Файла нет либо он не менялся — различить нельзя, и различать
                    // не нужно: в обоих случаях новостей нет.
                    return known is null ? new PresenceReport() : null;
                }

                lock (_seenVersion)
                    _seenVersion[key] = content.ETag;

                var json = Encoding.UTF8.GetString(_crypto.Decrypt(content.Data));
                var file = JsonConvert.DeserializeObject<PresenceFile>(json) ?? new PresenceFile();

                DevicePresence? other = null;
                DevicePresence? desktop = null;

                foreach (var (id, presence) in file.Devices)
                {
                    if (string.Equals(id, selfDeviceId, StringComparison.Ordinal)) continue;
                    if (!presence.IsFresh) continue;

                    if (other is null || (presence.Editing && !other.Editing))
                        other = presence;

                    if (presence.IsDesktop && (desktop is null || presence.Editing))
                        desktop = presence;
                }

                return new PresenceReport { Other = other, Desktop = desktop };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Debug(ex, "Failed to peek presence for {Project}", projectName);
                return null;
            }
        }

        /// <summary>
        /// Убрать свою отметку. Зовётся при закрытии книги и выходе: без этого
        /// другое устройство ещё несколько минут считало бы книгу занятой.
        /// </summary>
        public async Task ReleaseAsync(
            string projectName, string deviceId, CancellationToken ct = default)
        {
            var key = BuildKey(projectName);

            for (int attempt = 0; attempt < WriteAttempts; attempt++)
            {
                var (file, etag) = await ReadAsync(key, ct).ConfigureAwait(false);

                if (!file.Devices.Remove(deviceId))
                    return;

                if (await WriteAsync(key, file, etag, ct).ConfigureAwait(false))
                    return;
            }

            // Не убранная отметка протухнет сама — беда невелика.
            _log.Debug("Presence for {Project} could not be released", projectName);
        }

        private async Task<(PresenceFile File, string? ETag)> ReadAsync(string key, CancellationToken ct)
        {
            try
            {
                var content = await _storage.DownloadAsync(key, ct: ct).ConfigureAwait(false);
                if (content is null)
                    return (new PresenceFile(), null);

                var json = Encoding.UTF8.GetString(_crypto.Decrypt(content.Data));
                var file = JsonConvert.DeserializeObject<PresenceFile>(json) ?? new PresenceFile();
                file.Devices ??= new Dictionary<string, DevicePresence>(StringComparer.Ordinal);

                return (file, content.ETag);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Испорченный файл отметок не повод терять синхронизацию: считаем,
                // что отметок нет, и пишем свою заново.
                _log.Debug(ex, "Failed to read presence file");
                return (new PresenceFile(), null);
            }
        }

        private async Task<bool> WriteAsync(
            string key, PresenceFile file, string? etag, CancellationToken ct)
        {
            try
            {
                var json = JsonConvert.SerializeObject(file);
                var container = _crypto.Encrypt(Encoding.UTF8.GetBytes(json));

                // Файла не было — пишем с условием «только если его всё ещё нет».
                // Иначе — с оглядкой на прочитанную версию.
                var result = etag is null
                    ? await _storage.UploadAsync(key, container, ifNoneMatch: "*", ct: ct).ConfigureAwait(false)
                    : await _storage.UploadAsync(key, container, ifMatch: etag, ct: ct).ConfigureAwait(false);

                if (result is null) return false;

                // Своя же запись меняет версию файла. Не запомнив её, ближайший
                // опрос притащил бы файл целиком, чтобы увидеть в нём себя.
                lock (_seenVersion)
                    _seenVersion[key] = result;

                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Debug(ex, "Failed to write presence file");
                return false;
            }
        }

        private string BuildKey(string projectName)
            => PresenceFolder + "/" + _crypto.BuildRemoteKey("presence/" + projectName);
    }
}
