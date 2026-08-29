using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Writersword.Core.Exceptions;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Models.Sync;

namespace Writersword.Core.Services.Sync
{
    /// <summary>
    /// Синхронизация проекта с удалённым хранилищем.
    ///
    /// Все три составляющие сведены здесь: транспорт (IRemoteStorage), шифрование
    /// (ProjectCrypto) и память о прошлой синхронизации (SyncStateStore). Ни одна
    /// из них о двух других не знает, и заменить транспорт можно не трогая
    /// остального.
    ///
    /// Правило, которому подчинено всё поведение: локальный файл — источник
    /// правды. Сервер может быть недоступен часами, и это нормальный режим,
    /// а не сбой.
    /// </summary>
    public sealed class ProjectSyncService : IProjectSyncService
    {
        /// <summary>
        /// Имя описателя хранилища. Единственный файл на сервере с постоянным
        /// именем — в нём соль и верификатор пароля, ничего секретного.
        /// </summary>
        private const string VaultKey = "index.dat";

        private readonly IRemoteStorage _storage;
        private readonly SyncStateStore _state;
        private readonly ILogger _log;

        private ProjectCrypto? _crypto;
        private bool _disposed;

        public ProjectSyncService(IRemoteStorage storage, SyncStateStore state, ILogger logger)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _log = logger?.ForContext<ProjectSyncService>() ?? throw new ArgumentNullException(nameof(logger));
        }

        public bool IsConnected => _crypto is not null;

        public event EventHandler<SyncStatus>? StatusChanged;

        public async Task<bool> ConnectAsync(string masterPassword, CancellationToken ct = default)
        {
            ThrowIfDisposed();

            if (string.IsNullOrEmpty(masterPassword))
                throw new ArgumentException("Master password must not be empty.", nameof(masterPassword));

            if (!await _storage.EnsureAvailableAsync(ct).ConfigureAwait(false))
            {
                _log.Information("Cannot connect: remote storage is unavailable");
                return false;
            }

            var vault = await _storage.DownloadAsync(VaultKey, ct: ct).ConfigureAwait(false);

            if (vault is null)
                return await InitializeVaultAsync(masterPassword, ct).ConfigureAwait(false);

            try
            {
                var salt = ProjectCrypto.ReadVaultSalt(vault.Data);
                var crypto = ProjectCrypto.FromSalt(masterPassword, salt);

                if (!crypto.VerifyAgainstVault(vault.Data))
                {
                    crypto.Dispose();
                    _log.Information("Master password rejected by vault verifier");
                    return false;
                }

                _crypto?.Dispose();
                _crypto = crypto;

                _log.Information("Connected to remote vault");
                return true;
            }
            catch (CryptographicException ex)
            {
                _log.Warning(ex, "Vault descriptor is unusable");
                return false;
            }
        }

        /// <summary>
        /// Создать хранилище с нуля.
        ///
        /// Запись идёт с условием If-None-Match: *, то есть «только если файла
        /// ещё нет». Без него два устройства, подключающиеся одновременно,
        /// создали бы описатели с разными солями, и то, что записало вторым,
        /// сделало бы уже выгруженные проекты нечитаемыми.
        /// </summary>
        private async Task<bool> InitializeVaultAsync(string masterPassword, CancellationToken ct)
        {
            var crypto = ProjectCrypto.CreateNew(masterPassword);

            try
            {
                var etag = await _storage
                    .UploadAsync(VaultKey, crypto.BuildVaultFile(), ifNoneMatch: "*", ct: ct)
                    .ConfigureAwait(false);

                if (etag is null)
                {
                    // Кто-то успел создать хранилище между проверкой и записью.
                    crypto.Dispose();
                    _log.Information("Vault was created concurrently, reconnecting");
                    return await ConnectAsync(masterPassword, ct).ConfigureAwait(false);
                }

                _crypto?.Dispose();
                _crypto = crypto;

                _log.Information("Initialized new remote vault");
                return true;
            }
            catch
            {
                crypto.Dispose();
                throw;
            }
        }

        public void Disconnect()
        {
            _crypto?.Dispose();
            _crypto = null;
            _log.Information("Disconnected from remote vault");
        }

        public async Task<SyncStatus> GetStatusAsync(string localPath, CancellationToken ct = default)
        {
            ThrowIfDisposed();

            if (_crypto is null)
                return SyncStatus.Simple(SyncState.Disabled);

            var key = _crypto.BuildRemoteKey(ProjectKeyOf(localPath));

            RemoteEntry? remote;
            try
            {
                remote = await _storage.GetInfoAsync(key, ct).ConfigureAwait(false);
            }
            catch (RemoteStorageException ex)
            {
                return new SyncStatus { State = SyncState.Offline, Error = ex.Message };
            }
            catch (Exception ex) when (!ct.IsCancellationRequested
                                       && ex is HttpRequestException or IOException or TaskCanceledException)
            {
                return new SyncStatus { State = SyncState.Offline, Error = ex.Message };
            }

            var localExists = File.Exists(localPath);
            var known = _state.Get(localPath);

            if (remote is null)
            {
                return new SyncStatus
                {
                    State = localExists ? SyncState.RemoteMissing : SyncState.Disabled,
                    KnownETag = known?.ETag
                };
            }

            if (!localExists)
            {
                return new SyncStatus
                {
                    State = SyncState.LocalMissing,
                    RemoteETag = remote.ETag,
                    RemoteModified = remote.LastModified,
                    RemoteLength = remote.Length
                };
            }

            // Локальная сторона изменилась, если хеш файла разошёлся с тем,
            // что был на момент последней синхронизации. Серверная — если её
            // ETag отличается от известного нам.
            var localHash = SyncStateStore.ComputeFileHash(localPath);
            var localChanged = known is null || !string.Equals(known.LocalHash, localHash, StringComparison.Ordinal);
            var remoteChanged = known is null || !string.Equals(known.ETag, remote.ETag, StringComparison.Ordinal);

            var state = (localChanged, remoteChanged) switch
            {
                (false, false) => SyncState.InSync,
                (true, false) => SyncState.LocalAhead,
                (false, true) => SyncState.RemoteAhead,
                (true, true) => SyncState.Diverged
            };

            return new SyncStatus
            {
                State = state,
                RemoteETag = remote.ETag,
                KnownETag = known?.ETag,
                RemoteModified = remote.LastModified,
                RemoteLength = remote.Length
            };
        }

        public async Task<SyncResult> PushAsync(string localPath, bool force = false, CancellationToken ct = default)
        {
            ThrowIfDisposed();

            if (_crypto is null)
                return SyncResult.Fail(SyncState.Disabled, "Remote storage is not connected.");

            if (!File.Exists(localPath))
                return SyncResult.Fail(SyncState.LocalMissing, "Local project file does not exist.");

            var key = _crypto.BuildRemoteKey(ProjectKeyOf(localPath));
            var known = _state.Get(localPath);

            try
            {
                var plain = await File.ReadAllBytesAsync(localPath, ct).ConfigureAwait(false);
                var localHash = SyncStateStore.ComputeHash(plain);
                var container = _crypto.Encrypt(plain);

                // Условие записи и есть вся защита от затирания. Если известного
                // ETag нет, значит проект выгружается впервые — тогда условие
                // обратное: записать только если файла ещё нет.
                string? ifMatch = null;
                string? ifNoneMatch = null;

                if (!force)
                {
                    if (known is null)
                        ifNoneMatch = "*";
                    else
                        ifMatch = known.ETag;
                }

                var etag = await _storage
                    .UploadAsync(key, container, ifMatch, ifNoneMatch, ct)
                    .ConfigureAwait(false);

                if (etag is null)
                {
                    _log.Information("Push rejected: remote version has diverged for {Path}", localPath);
                    return SyncResult.Fail(SyncState.Diverged,
                        "Remote version has changed since the last synchronization.");
                }

                _state.Set(localPath, etag, localHash);

                var result = SyncResult.Ok(SyncState.InSync, etag);
                RaiseStatusChanged(new SyncStatus { State = SyncState.InSync, RemoteETag = etag, KnownETag = etag });
                return result;
            }
            catch (RemoteStorageException ex)
            {
                _log.Warning(ex, "Push failed for {Path}", localPath);
                return SyncResult.Fail(SyncState.Offline, ex.Message);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested
                                       && ex is HttpRequestException or IOException or TaskCanceledException)
            {
                _log.Debug(ex, "Push could not reach the server for {Path}", localPath);
                return SyncResult.Fail(SyncState.Offline, ex.Message);
            }
        }

        public async Task<SyncResult> PullAsync(string localPath, CancellationToken ct = default)
        {
            ThrowIfDisposed();

            if (_crypto is null)
                return SyncResult.Fail(SyncState.Disabled, "Remote storage is not connected.");

            var key = _crypto.BuildRemoteKey(ProjectKeyOf(localPath));

            try
            {
                var remote = await _storage.DownloadAsync(key, ct: ct).ConfigureAwait(false);

                if (remote is null)
                    return SyncResult.Fail(SyncState.RemoteMissing, "Project is not present in remote storage.");

                byte[] plain;
                try
                {
                    plain = _crypto.Decrypt(remote.Data);
                }
                catch (CryptographicException ex)
                {
                    // Расшифровка не удалась при верном пароле означает порчу
                    // данных при передаче или хранении. Локальный файл в этом
                    // случае трогать нельзя ни в коем случае.
                    _log.Error(ex, "Failed to decrypt remote container for {Path}", localPath);
                    return SyncResult.Fail(SyncState.Diverged,
                        "Remote container could not be decrypted and was left untouched.");
                }

                string? backupPath = null;
                if (File.Exists(localPath))
                {
                    var known = _state.Get(localPath);
                    var localHash = SyncStateStore.ComputeFileHash(localPath);

                    // Резервная копия делается только если локальная версия
                    // содержит несохранённую на сервер работу. Копировать файл,
                    // который и так совпадает с известным состоянием, незачем.
                    if (known is null || !string.Equals(known.LocalHash, localHash, StringComparison.Ordinal))
                        backupPath = CreateBackup(localPath);
                }

                WriteAtomic(localPath, plain);
                _state.Set(localPath, remote.ETag, SyncStateStore.ComputeHash(plain));

                _log.Information("Pulled remote version for {Path}", localPath);

                RaiseStatusChanged(new SyncStatus
                {
                    State = SyncState.InSync,
                    RemoteETag = remote.ETag,
                    KnownETag = remote.ETag
                });

                return SyncResult.Ok(SyncState.InSync, remote.ETag, backupPath);
            }
            catch (RemoteStorageException ex)
            {
                _log.Warning(ex, "Pull failed for {Path}", localPath);
                return SyncResult.Fail(SyncState.Offline, ex.Message);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested
                                       && ex is HttpRequestException or IOException or TaskCanceledException)
            {
                _log.Debug(ex, "Pull could not reach the server for {Path}", localPath);
                return SyncResult.Fail(SyncState.Offline, ex.Message);
            }
        }

        /// <summary>
        /// Ключ проекта на сервере выводится из имени файла без расширения.
        ///
        /// Не из полного пути: на телефоне и на компьютере проект лежит в разных
        /// папках, и путь развёл бы одну книгу на два несвязанных контейнера.
        /// </summary>
        private static string ProjectKeyOf(string localPath)
        {
            var name = Path.GetFileNameWithoutExtension(localPath);

            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Cannot derive a project key from the given path.", nameof(localPath));

            return name;
        }

        private string CreateBackup(string localPath)
        {
            var directory = Path.GetDirectoryName(localPath) ?? ".";
            var name = Path.GetFileNameWithoutExtension(localPath);
            var extension = Path.GetExtension(localPath);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

            var backupPath = Path.Combine(directory, $"{name}.local-{stamp}{extension}");

            var counter = 1;
            while (File.Exists(backupPath))
                backupPath = Path.Combine(directory, $"{name}.local-{stamp}-{counter++}{extension}");

            File.Copy(localPath, backupPath);
            _log.Information("Local version saved as {Backup}", backupPath);

            return backupPath;
        }

        /// <summary>
        /// Запись через временный файл в той же папке.
        ///
        /// Прямая запись поверх проекта означает, что обрыв питания посреди неё
        /// оставит обрубок вместо книги. File.Move в пределах одного тома
        /// атомарен, поэтому файл в любой момент времени либо старый, либо новый.
        /// </summary>
        private static void WriteAtomic(string path, byte[] data)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var temp = path + ".incoming";

            try
            {
                File.WriteAllBytes(temp, data);
                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                if (File.Exists(temp))
                {
                    try { File.Delete(temp); }
                    catch (IOException) { }
                }

                throw;
            }
        }

        private void RaiseStatusChanged(SyncStatus status)
        {
            try
            {
                StatusChanged?.Invoke(this, status);
            }
            catch (Exception ex)
            {
                // Исключение подписчика не должно ломать синхронизацию:
                // данные уже записаны, событие лишь уведомляет интерфейс.
                _log.Warning(ex, "Sync status subscriber threw");
            }
        }

        private void ThrowIfDisposed()
            => ObjectDisposedException.ThrowIf(_disposed, this);

        public void Dispose()
        {
            if (_disposed) return;

            _crypto?.Dispose();
            _crypto = null;
            _storage.Dispose();
            _disposed = true;
        }
    }
}
