using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Serilog;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Models.Sync;

namespace Writersword.Core.Services.Sync
{
    /// <summary>
    /// Отправка истории версий проекта в удалённое хранилище.
    ///
    /// Склад устроен как у Git: имя объекта равно хешу его содержимого. Отсюда
    /// главное свойство — объект с данным именем всегда один и тот же, где бы он
    /// ни лежал. Значит синхронизация сводится к «залей то, чего там нет», и
    /// конфликтов не бывает в принципе: перезаписывать нечего.
    ///
    /// Что на сервере уже есть, выясняется из указателя, а не перебором папок:
    /// склад с историей за год содержит тысячи объектов, и опрашивать их по
    /// одному дольше, чем залить всё заново.
    ///
    /// Имена на сервере, как и у проектов, выведены через HMAC — по ним не
    /// восстановить ни хеши содержимого, ни номера точек.
    /// </summary>
    public sealed class BackupStoreSync
    {
        private const string ObjectsDir = "objects";
        private const string SnapshotsDir = "snapshots";
        private const string StoreMetaFile = "store.json";

        // Один заход не отправляет больше этого числа записей. История за год
        // при первой отправке — это тысячи файлов, и уходить в сеть на полчаса,
        // блокируя очередной проход координатора, незачем: остаток догонит
        // следующий заход.
        private const int MaxEntriesPerRun = 200;

        private readonly IRemoteStorage _storage;
        private readonly ProjectCrypto _crypto;
        private readonly ILogger _log;

        public BackupStoreSync(IRemoteStorage storage, ProjectCrypto crypto, ILogger logger)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
            _log = logger?.ForContext<BackupStoreSync>() ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>Указатель содержимого склада на сервере.</summary>
        private sealed class StoreIndex
        {
            /// <summary>Хеши объектов, лежащих на сервере.</summary>
            public List<string> Objects { get; set; } = new();

            /// <summary>Идентификаторы точек восстановления.</summary>
            public List<string> Snapshots { get; set; } = new();

            public DateTimeOffset UpdatedAt { get; set; }
        }

        /// <summary>
        /// Отправить недостающие записи склада.
        ///
        /// Возвращает число отправленных записей. Ноль означает, что на сервере
        /// уже всё есть, — это обычный исход при работе изо дня в день.
        /// </summary>
        public async Task<int> PushAsync(string storePath, string projectName, CancellationToken ct = default)
        {
            if (!Directory.Exists(storePath))
                return 0;

            var indexKey = BuildKey(projectName, "index");
            var index = await ReadIndexAsync(indexKey, ct).ConfigureAwait(false);

            var localObjects = EnumerateObjects(storePath);
            var localSnapshots = EnumerateSnapshots(storePath);

            var knownObjects = new HashSet<string>(index.Objects, StringComparer.OrdinalIgnoreCase);
            var knownSnapshots = new HashSet<string>(index.Snapshots, StringComparer.OrdinalIgnoreCase);

            var sent = 0;
            var indexChanged = false;

            // Объекты уходят раньше манифестов. Порядок важен: манифест ссылается
            // на объекты, и точка, попавшая на сервер раньше своего содержимого,
            // до следующего захода выглядела бы там существующей, но нечитаемой.
            foreach (var (hash, path) in localObjects)
            {
                if (sent >= MaxEntriesPerRun) break;
                ct.ThrowIfCancellationRequested();

                if (knownObjects.Contains(hash))
                    continue;

                if (!await PushFileAsync(BuildKey(projectName, "o-" + hash), path, ct).ConfigureAwait(false))
                    continue;

                index.Objects.Add(hash);
                knownObjects.Add(hash);
                indexChanged = true;
                sent++;
            }

            foreach (var (id, path) in localSnapshots)
            {
                if (sent >= MaxEntriesPerRun) break;
                ct.ThrowIfCancellationRequested();

                if (knownSnapshots.Contains(id))
                    continue;

                if (!await PushFileAsync(BuildKey(projectName, "s-" + id), path, ct).ConfigureAwait(false))
                    continue;

                index.Snapshots.Add(id);
                knownSnapshots.Add(id);
                indexChanged = true;
                sent++;
            }

            // Описание склада перезаписывается всегда: оно единственное здесь
            // изменяемое, и без него собрать историю обратно не из чего.
            var metaPath = Path.Combine(storePath, StoreMetaFile);
            if (File.Exists(metaPath))
                await PushFileAsync(BuildKey(projectName, "meta"), metaPath, ct).ConfigureAwait(false);

            if (indexChanged)
            {
                index.UpdatedAt = DateTimeOffset.UtcNow;
                await WriteIndexAsync(indexKey, index, ct).ConfigureAwait(false);
                _log.Information("Backup store pushed: {Count} entries for {Project}", sent, projectName);
            }

            return sent;
        }

        /// <summary>
        /// Записи склада, которые есть на сервере, но которых нет локально.
        ///
        /// Само восстановление истории здесь не делается: оно нужно при переезде
        /// на новую машину, а это отдельный сценарий со своими вопросами — куда
        /// разворачивать и что делать с уже имеющейся историей.
        /// </summary>
        public async Task<(int Objects, int Snapshots)> GetRemoteOnlyCountAsync(
            string storePath, string projectName, CancellationToken ct = default)
        {
            var index = await ReadIndexAsync(BuildKey(projectName, "index"), ct).ConfigureAwait(false);

            var localObjects = new HashSet<string>(
                EnumerateObjects(storePath).Select(x => x.Hash), StringComparer.OrdinalIgnoreCase);
            var localSnapshots = new HashSet<string>(
                EnumerateSnapshots(storePath).Select(x => x.Id), StringComparer.OrdinalIgnoreCase);

            return (index.Objects.Count(h => !localObjects.Contains(h)),
                    index.Snapshots.Count(s => !localSnapshots.Contains(s)));
        }

        private async Task<bool> PushFileAsync(string key, string path, CancellationToken ct)
        {
            try
            {
                // Записи склада тоже читаются с разрешением на чужую запись:
                // прореживание истории идёт своим чередом и может тронуть папку
                // ровно в этот момент.
                await using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    bufferSize: 64 * 1024, useAsync: true);

                var plain = new byte[stream.Length];
                await stream.ReadExactlyAsync(plain, ct).ConfigureAwait(false);
                var container = _crypto.Encrypt(plain);

                // Объекты неизменяемы, поэтому условие записи обратное обычному:
                // «только если файла ещё нет». Отказ по условию означает, что
                // объект уже там, и это успех, а не ошибка.
                await _storage
                    .UploadAsync(key, container, ifNoneMatch: "*", ct: ct)
                    .ConfigureAwait(false);

                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Debug(ex, "Failed to push backup entry {Key}", key);
                return false;
            }
        }

        private async Task<StoreIndex> ReadIndexAsync(string key, CancellationToken ct)
        {
            try
            {
                var content = await _storage.DownloadAsync(key, ct: ct).ConfigureAwait(false);
                if (content is null)
                    return new StoreIndex();

                var json = Encoding.UTF8.GetString(_crypto.Decrypt(content.Data));
                return JsonConvert.DeserializeObject<StoreIndex>(json) ?? new StoreIndex();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Испорченный указатель не повод терять историю: пустой означает
                // «на сервере ничего нет», и следующий заход зальёт всё заново.
                // Лишний трафик разово — меньшее зло, чем пропущенные точки.
                _log.Warning(ex, "Backup store index unreadable, treating as empty");
                return new StoreIndex();
            }
        }

        private async Task WriteIndexAsync(string key, StoreIndex index, CancellationToken ct)
        {
            var json = JsonConvert.SerializeObject(index);
            var container = _crypto.Encrypt(Encoding.UTF8.GetBytes(json));

            await _storage.UploadAsync(key, container, ct: ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Ключ записи на сервере. Плоский, без вложенных папок: WebDAV-клиент
        /// экранирует разделители пути в имени, и вложенность превратилась бы
        /// в один файл с косыми чертами в названии.
        /// </summary>
        private string BuildKey(string projectName, string part)
            => _crypto.BuildRemoteKey("backup/" + projectName + "/" + part);

        private static IEnumerable<(string Hash, string Path)> EnumerateObjects(string storePath)
        {
            var dir = Path.Combine(storePath, ObjectsDir);
            if (!Directory.Exists(dir))
                yield break;

            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                yield return (Path.GetFileNameWithoutExtension(file), file);
        }

        private static IEnumerable<(string Id, string Path)> EnumerateSnapshots(string storePath)
        {
            var dir = Path.Combine(storePath, SnapshotsDir);
            if (!Directory.Exists(dir))
                yield break;

            foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
                yield return (Path.GetFileNameWithoutExtension(file), file);
        }
    }
}
