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
using Writersword.Core.Models.Backup;
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
        // Имена файлов и папок склада общие со стороной, которая его ведёт:
        // см. BackupStoreLayout.
        private const string ObjectsDir = BackupStoreLayout.ObjectsDir;
        private const string SnapshotsDir = BackupStoreLayout.SnapshotsDir;
        private const string StoreMetaFile = BackupStoreLayout.StoreMetaFile;

        // Один заход не отправляет больше этого числа записей. История за год
        // при первой отправке — это тысячи файлов, и уходить в сеть на полчаса,
        // блокируя очередной проход координатора, незачем: остаток догонит
        // следующий заход.
        private const int MaxEntriesPerRun = 200;

        /// <summary>
        /// Подпапка, в которой живёт история версий.
        ///
        /// Отдельно от проектов: точка состоит из десятков объектов, и за
        /// неделю работы корень хранилища превращается в тысячу файлов с
        /// неразличимыми именами — отличить среди них книгу от куска истории
        /// нельзя даже владельцу.
        /// </summary>
        private const string HistoryFolder = "history";

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

            await _storage.EnsureFolderAsync(HistoryFolder, ct).ConfigureAwait(false);

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

            // Удаление идёт после отправки: если связь оборвётся между ними,
            // на сервере окажется лишнее, а не недостающее.
            if (await RemoveTombstonedAsync(storePath, projectName, index, ct).ConfigureAwait(false))
                indexChanged = true;

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

        /// <summary>
        /// Снять с сервера то, что здесь прорядили.
        ///
        /// Удаляется строго по надгробиям, а не по отсутствию записи локально.
        /// Отсутствие двояко: запись либо прорядили здесь, либо потеряли вместе
        /// с диском, и во втором случае серверная копия — единственная. Поэтому
        /// сносится только то, о чём прореживание сообщило само.
        ///
        /// Записи, до сервера так и не доехавшей, в указателе нет: её надгробие
        /// закрывается сразу, без обращения в сеть.
        /// </summary>
        private async Task<bool> RemoveTombstonedAsync(
            string storePath, string projectName, StoreIndex index, CancellationToken ct)
        {
            var stones = BackupTombstones.Read(storePath);
            if (stones.IsEmpty)
                return false;

            var knownObjects = new HashSet<string>(index.Objects, StringComparer.OrdinalIgnoreCase);
            var knownSnapshots = new HashSet<string>(index.Snapshots, StringComparer.OrdinalIgnoreCase);

            var doneSnapshots = new List<string>();
            var doneObjects = new List<string>();
            var calls = 0;

            // Манифесты уходят раньше объектов — порядок обратный отправке.
            // Точка, лишившаяся объектов раньше самой себя, до следующего захода
            // выглядела бы на сервере существующей, но нечитаемой.
            foreach (var id in stones.Snapshots)
            {
                if (calls >= MaxEntriesPerRun) break;
                ct.ThrowIfCancellationRequested();

                if (!knownSnapshots.Contains(id))
                {
                    doneSnapshots.Add(id);
                    continue;
                }

                if (!await DeleteRemoteAsync(BuildKey(projectName, "s-" + id), ct).ConfigureAwait(false))
                    continue;

                doneSnapshots.Add(id);
                calls++;
            }

            foreach (var hash in stones.Objects)
            {
                if (calls >= MaxEntriesPerRun) break;
                ct.ThrowIfCancellationRequested();

                if (!knownObjects.Contains(hash))
                {
                    doneObjects.Add(hash);
                    continue;
                }

                if (!await DeleteRemoteAsync(BuildKey(projectName, "o-" + hash), ct).ConfigureAwait(false))
                    continue;

                doneObjects.Add(hash);
                calls++;
            }

            if (doneSnapshots.Count == 0 && doneObjects.Count == 0)
                return false;

            var goneSnapshots = new HashSet<string>(doneSnapshots, StringComparer.OrdinalIgnoreCase);
            var goneObjects = new HashSet<string>(doneObjects, StringComparer.OrdinalIgnoreCase);

            index.Snapshots.RemoveAll(goneSnapshots.Contains);
            index.Objects.RemoveAll(goneObjects.Contains);

            // Надгробие живёт до подтверждения: пока запись не снята, следующий
            // заход попробует снова. Снятое вычёркивается, и список не растёт.
            BackupTombstones.Confirm(storePath, doneSnapshots, doneObjects);

            _log.Information("Backup store cleaned: {Snapshots} points, {Objects} objects removed for {Project}",
                doneSnapshots.Count, doneObjects.Count, projectName);

            return true;
        }

        /// <summary>
        /// Восстановить со склада на сервере то, чего здесь нет и что здесь не
        /// удаляли.
        ///
        /// Возвращает число восстановленных записей. Идёт молча: случай, ради
        /// которого это сделано, — умерший диск, и требовать в нём нажатия
        /// кнопки бессмысленно.
        /// </summary>
        public async Task<int> PullAsync(string storePath, string projectName, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(storePath))
                return 0;

            var index = await ReadIndexAsync(BuildKey(projectName, "index"), ct).ConfigureAwait(false);
            if (index.Objects.Count == 0 && index.Snapshots.Count == 0)
                return 0;

            var stones = BackupTombstones.Read(storePath);
            var deletedObjects = new HashSet<string>(stones.Objects, StringComparer.OrdinalIgnoreCase);
            var deletedSnapshots = new HashSet<string>(stones.Snapshots, StringComparer.OrdinalIgnoreCase);

            var localObjects = new HashSet<string>(
                EnumerateObjects(storePath).Select(x => x.Hash), StringComparer.OrdinalIgnoreCase);
            var localSnapshots = new HashSet<string>(
                EnumerateSnapshots(storePath).Select(x => x.Id), StringComparer.OrdinalIgnoreCase);

            var restored = 0;

            // Объекты раньше манифестов — тот же довод, что и при отправке:
            // точка, вернувшаяся раньше своего содержимого, нечитаема.
            foreach (var hash in index.Objects)
            {
                if (restored >= MaxEntriesPerRun) break;
                ct.ThrowIfCancellationRequested();

                if (localObjects.Contains(hash) || deletedObjects.Contains(hash))
                    continue;

                if (await PullFileAsync(
                        BuildKey(projectName, "o-" + hash),
                        BackupStoreLayout.ObjectPath(storePath, hash), ct).ConfigureAwait(false))
                {
                    restored++;
                }
            }

            foreach (var id in index.Snapshots)
            {
                if (restored >= MaxEntriesPerRun) break;
                ct.ThrowIfCancellationRequested();

                if (localSnapshots.Contains(id) || deletedSnapshots.Contains(id))
                    continue;

                if (await PullFileAsync(
                        BuildKey(projectName, "s-" + id),
                        BackupStoreLayout.SnapshotPath(storePath, id), ct).ConfigureAwait(false))
                {
                    restored++;
                }
            }

            if (restored > 0)
                _log.Information("Backup store restored: {Count} entries for {Project}", restored, projectName);

            return restored;
        }

        private async Task<bool> DeleteRemoteAsync(string key, CancellationToken ct)
        {
            try
            {
                await _storage.DeleteAsync(key, ct).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Debug(ex, "Failed to delete backup entry {Key}", key);
                return false;
            }
        }

        private async Task<bool> PullFileAsync(string key, string path, CancellationToken ct)
        {
            try
            {
                var content = await _storage.DownloadAsync(key, ct: ct).ConfigureAwait(false);
                if (content is null)
                    return false;

                var plain = _crypto.Decrypt(content.Data);

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                // Запись появляется в складе целиком: манифест, дописанный
                // наполовину, читался бы как испорченная точка, а испорченная
                // точка отменяет уборку всего склада.
                var temp = path + ".tmp";
                await File.WriteAllBytesAsync(temp, plain, ct).ConfigureAwait(false);
                File.Move(temp, path, overwrite: true);

                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Debug(ex, "Failed to restore backup entry {Key}", key);
                return false;
            }
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
        /// Ключ записи на сервере: подпапка истории плюс имя, выведенное
        /// через HMAC. По имени не восстановить ни хеш содержимого, ни номер
        /// точки — видно только, что это история.
        /// </summary>
        private string BuildKey(string projectName, string part)
            => HistoryFolder + "/" + _crypto.BuildRemoteKey("backup/" + projectName + "/" + part);

        private static IEnumerable<(string Hash, string Path)> EnumerateObjects(string storePath)
        {
            var dir = Path.Combine(storePath, ObjectsDir);
            if (!Directory.Exists(dir))
                yield break;

            foreach (var file in Directory.EnumerateFiles(
                         dir, "*" + BackupStoreLayout.ObjectExtension, SearchOption.AllDirectories))
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
