using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Writersword.Core.Services.Storage;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Models.Cache;

namespace Writersword.Infrastructure.Services.Storage
{
    /// <summary>
    /// Кеш данных модулей в базе .writersword.wsasd рядом с проектом.
    /// Ключ данных модуля — moduleType (строка), не InstanceId (GUID).
    /// При загрузке проверяется ProjectId для защиты от кросс-проектного загрязнения.
    ///
    /// Прежде кеш лежал в ZIP, и это было худшее место для архива из всех
    /// возможных: он переписывается на каждом переключении вкладки и на каждом
    /// автосохранении, а ZipArchiveMode.Update поднимает архив в память и
    /// перекладывает файл целиком. Строка модуля в базе меняется на месте,
    /// остальные строки при этом не читаются и не переписываются.
    ///
    /// Вторым следствием ушла пляска с временным файлом и подменой: запись идёт
    /// в транзакции, и оборванная запись откатывается журналом, а не оставляет
    /// обрубок вместо точки восстановления.
    /// </summary>
    public class SqliteCacheService : IProjectCacheService
    {
        /// <summary>
        /// Версия формата кеша. Единица осталась за прежним ZIP-кешем.
        /// </summary>
        private const int FormatVersion = 2;

        private const string MetaProjectId = "project_id";
        private const string MetaProjectPath = "project_path";
        private const string MetaCacheDate = "cache_date";
        private const string MetaProjectFileHash = "project_file_hash";
        private const string MetaFormatVersion = "format_version";

        private readonly ILogger<SqliteCacheService> _logger;
        private readonly IHashService _hashService;

        // Гарантирует что только одна операция одновременно обращается к файлу кеша.
        private readonly System.Threading.SemaphoreSlim _fileLock = new(1, 1);

        public SqliteCacheService(IHashService hashService)
        {
            _logger = App.Services.GetService<ILogger<SqliteCacheService>>()!;
            _hashService = hashService;
        }

        private string GetCachePath(string projectPath)
        {
            var fullPath = Path.GetFullPath(projectPath);
            return fullPath + ".wsasd";
        }

        /// <summary>
        /// Путь резервной копии кеша. Создаётся не на каждой записи, а только
        /// через MoveCacheToBackup: копировать базу целиком ради страховки
        /// означало бы вернуть ту самую перезапись всего файла, ради ухода от
        /// которой кеш и переехал в базу. От обрыва посреди записи защищает
        /// транзакция.
        /// </summary>
        private string GetBackupPath(string projectPath) => GetCachePath(projectPath) + ".bak";

        public bool HasCache(string projectPath)
        {
            if (File.Exists(GetCachePath(projectPath))) return true;

            // Основной файл мог не пережить аварию — резервная копия остаётся
            // полноценной точкой восстановления и поднимает режим сравнения.
            return File.Exists(GetBackupPath(projectPath));
        }

        // ── Файл кеша ─────────────────────────────────────────────────────

        /// <summary>
        /// Узнать формат файла по первым байтам, не открывая его драйвером.
        /// У базы это «SQLite format 3», у кеша от прежних сборок — сигнатура ZIP.
        /// </summary>
        private static bool HasSqliteSignature(string path)
        {
            try
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                if (stream.Length < 16) return false;

                Span<byte> header = stackalloc byte[16];
                if (stream.Read(header) < header.Length) return false;

                return header.StartsWith("SQLite format 3\0"u8);
            }
            catch
            {
                return false;
            }
        }

        private static bool HasZipSignature(string path)
        {
            try
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                if (stream.Length < 4) return false;

                Span<byte> header = stackalloc byte[4];
                if (stream.Read(header) < header.Length) return false;

                return header.StartsWith("PK\u0003\u0004"u8);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Спутники базы: журнал и разделяемая память. Удаляются и переносятся
        /// вместе с ней — оставшийся журнал от другой базы делает файл нечитаемым.
        /// </summary>
        private static IEnumerable<string> Companions(string databasePath)
        {
            yield return databasePath + "-wal";
            yield return databasePath + "-shm";
        }

        private static void DeleteWithCompanions(string databasePath)
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);

            foreach (var companion in Companions(databasePath))
                if (File.Exists(companion)) File.Delete(companion);
        }

        /// <summary>
        /// Читается ли файл как кеш: это база, и в ней есть таблица метаданных.
        /// </summary>
        private bool IsReadableCache(string path)
        {
            if (!File.Exists(path)) return false;
            if (!HasSqliteSignature(path)) return false;

            try
            {
                using var connection = OpenConnection(path, createIfMissing: false);

                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'meta'";

                return Convert.ToInt64(command.ExecuteScalar()) > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Готовит файл кеша к работе. Кеш от прежних сборок лежит в ZIP —
        /// читать его нечем и незачем: он живёт минуты и описывает сессию,
        /// которой уже нет. Такой файл убирается, и вызывающий работает как при
        /// отсутствии кеша.
        ///
        /// Повреждённый файл восстанавливается из резервной копии всегда,
        /// отсутствующий — только при restoreMissingFromBackup: во время работы
        /// файл могли убрать намеренно (переключение воркмода), и подменять его
        /// копией нельзя. Проверки при открытии проекта, наоборот, обязаны
        /// увидеть уцелевшую копию.
        /// </summary>
        private bool EnsureCacheReadable(string cachePath, bool restoreMissingFromBackup = false)
        {
            if (IsReadableCache(cachePath)) return true;

            if (File.Exists(cachePath) && HasZipSignature(cachePath))
            {
                try
                {
                    DeleteWithCompanions(cachePath);
                    _logger.LogWarning(
                        "Cache left by an older build is not a database — discarded: {CachePath}", cachePath);
                }
                catch (IOException ex)
                {
                    _logger.LogError(ex, "Failed to discard cache from an older build: {CachePath}", cachePath);
                    return false;
                }
            }

            if (!File.Exists(cachePath) && !restoreMissingFromBackup) return false;

            var backupPath = cachePath + ".bak";
            if (IsReadableCache(backupPath))
            {
                try
                {
                    DeleteWithCompanions(cachePath);
                    File.Copy(backupPath, cachePath, overwrite: true);
                    _logger.LogWarning(
                        "Cache file was damaged or missing — restored from backup: {CachePath}", cachePath);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to restore cache from backup: {CachePath}", cachePath);
                    return false;
                }
            }

            if (File.Exists(cachePath))
                _logger.LogError(
                    "Cache file is damaged and has no usable backup: {CachePath}", cachePath);

            return false;
        }

        /// <summary>
        /// Соединение с базой кеша.
        ///
        /// Pooling отключён намеренно: пул держит дескриптор файла открытым
        /// после Close, а кеш приходится переносить в резервную копию и удалять
        /// обычными файловыми операциями — с удержанным дескриптором они падают.
        /// </summary>
        private SqliteConnection OpenConnection(string cachePath, bool createIfMissing)
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = cachePath,
                Mode = createIfMissing ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            };

            var connection = new SqliteConnection(builder.ToString());
            connection.Open();

            Execute(connection, "PRAGMA journal_mode = WAL");
            Execute(connection, "PRAGMA synchronous = NORMAL");

            return connection;
        }

        private static void Execute(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private static void EnsureSchema(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS meta(
                    key   TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS modules(
                    module_type  TEXT PRIMARY KEY,
                    custom_json  TEXT NOT NULL,
                    session_json TEXT NULL,
                    hash         TEXT NOT NULL,
                    size         INTEGER NOT NULL,
                    modified     INTEGER NOT NULL DEFAULT 0
                );
                """;
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Сводит журнал в базу и обнуляет его. После этого файл кеша полон сам
        /// по себе: его можно переносить и копировать, не таща спутников.
        /// </summary>
        private static void Checkpoint(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            command.ExecuteNonQuery();
        }

        private static string? ReadMeta(SqliteConnection connection, string key)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM meta WHERE key = @key";
            command.Parameters.AddWithValue("@key", key);

            return command.ExecuteScalar() as string;
        }

        private static void WriteMeta(SqliteConnection connection, string key, string value)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO meta(key, value) VALUES(@key, @value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value
                """;
            command.Parameters.AddWithValue("@key", key);
            command.Parameters.AddWithValue("@value", value);
            command.ExecuteNonQuery();
        }

        private static DateTime? ParseDate(string? value)
            => DateTime.TryParse(
                value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : null;

        private static string FormatDate(DateTime value)
            => value.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

        // ── Чтение ────────────────────────────────────────────────────────

        public DateTime? GetCacheDate(string projectPath)
        {
            var cachePath = GetCachePath(projectPath);
            if (!File.Exists(cachePath) && !File.Exists(GetBackupPath(projectPath))) return null;

            if (!_fileLock.Wait(TimeSpan.FromSeconds(3)))
            {
                // Таймаут означает что UI-поток заблокирован ожиданием этого же лока
                // (классический дедлок при откате вкладки или закрытии приложения).
                // Возвращаем null — вызывающий код использует project.ModulesData как fallback.
                _logger.LogWarning("Cache lock timeout — returning null, caller uses in-memory fallback");
                return null;
            }
            try
            {
                if (!EnsureCacheReadable(cachePath, restoreMissingFromBackup: true)) return null;

                using var connection = OpenConnection(cachePath, createIfMissing: false);
                return ParseDate(ReadMeta(connection, MetaCacheDate));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading cache date");
                return null;
            }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>
        /// Загрузить CustomData из кеша
        /// Возвращает словарь moduleType → CustomData
        /// Проверяет ProjectId чтобы убедиться что кеш принадлежит именно этому проекту
        /// </summary>
        /// <param name="projectPath">Путь к .writersword файлу</param>
        /// <param name="expectedProjectId">ID проекта для верификации. Если null — верификация пропускается</param>
        public Dictionary<string, object?>? LoadCache(string projectPath, string? expectedProjectId = null)
        {
            var result = LoadCacheWithSession(projectPath, expectedProjectId, withSession: false);
            return result?.CustomData;
        }

        /// <summary>
        /// Загрузить CustomData И SessionData из кеша одним обращением к базе.
        /// </summary>
        public (Dictionary<string, object?> CustomData, Dictionary<string, object?> SessionData)?
            LoadCacheWithSession(string projectPath, string? expectedProjectId = null)
            => LoadCacheWithSession(projectPath, expectedProjectId, withSession: true);

        private (Dictionary<string, object?> CustomData, Dictionary<string, object?> SessionData)?
            LoadCacheWithSession(string projectPath, string? expectedProjectId, bool withSession)
        {
            var cachePath = GetCachePath(projectPath);
            if (!File.Exists(cachePath) && !File.Exists(GetBackupPath(projectPath)))
            {
                _logger.LogDebug("Cache not found: {CachePath}", cachePath);
                return null;
            }

            if (!_fileLock.Wait(TimeSpan.FromSeconds(3)))
            {
                // Таймаут означает что UI-поток заблокирован ожиданием этого же лока
                // (классический дедлок при откате вкладки или закрытии приложения).
                // Возвращаем null — вызывающий код использует project.ModulesData как fallback.
                _logger.LogWarning("Cache lock timeout — returning null, caller uses in-memory fallback");
                return null;
            }
            try
            {
                if (!EnsureCacheReadable(cachePath)) return null;

                var customData = new Dictionary<string, object?>();
                var sessionData = new Dictionary<string, object?>();

                using var connection = OpenConnection(cachePath, createIfMissing: false);

                var projectId = ReadMeta(connection, MetaProjectId);

                if (!string.IsNullOrEmpty(expectedProjectId) && projectId != expectedProjectId)
                {
                    _logger.LogError(
                        "Cache ProjectId mismatch: expected {Expected}, got {Actual}. Cache belongs to another project, ignoring.",
                        expectedProjectId, projectId);
                    return null;
                }

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = withSession
                        ? "SELECT module_type, custom_json, session_json FROM modules"
                        : "SELECT module_type, custom_json FROM modules";

                    using var reader = command.ExecuteReader();

                    while (reader.Read())
                    {
                        var moduleType = reader.GetString(0);

                        customData[moduleType] =
                            JsonConvert.DeserializeObject<object>(reader.GetString(1));

                        if (!withSession) continue;

                        if (reader.IsDBNull(2)) continue;

                        sessionData[moduleType] =
                            JsonConvert.DeserializeObject<object>(reader.GetString(2));
                    }
                }

                _logger.LogDebug(
                    "Cache loaded: ProjectId={ProjectId}, Date={CacheDate}, Modules={ModulesCount}",
                    projectId, ReadMeta(connection, MetaCacheDate), customData.Count);

                return (customData, sessionData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading cache: {CachePath}", cachePath);
                return null;
            }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>
        /// Загрузить метаданные кеша без загрузки данных модулей
        /// </summary>
        public ModuleCacheMetadata? LoadCacheMetadata(string projectPath)
        {
            var cachePath = GetCachePath(projectPath);
            if (!File.Exists(cachePath) && !File.Exists(GetBackupPath(projectPath))) return null;

            if (!_fileLock.Wait(TimeSpan.FromSeconds(3)))
            {
                // Таймаут означает что UI-поток заблокирован ожиданием этого же лока
                // (классический дедлок при откате вкладки или закрытии приложения).
                // Возвращаем null — вызывающий код использует project.ModulesData как fallback.
                _logger.LogWarning("Cache lock timeout — returning null, caller uses in-memory fallback");
                return null;
            }
            try
            {
                if (!EnsureCacheReadable(cachePath, restoreMissingFromBackup: true)) return null;

                using var connection = OpenConnection(cachePath, createIfMissing: false);

                var metadata = new ModuleCacheMetadata
                {
                    ProjectId = ReadMeta(connection, MetaProjectId) ?? "",
                    ProjectPath = ReadMeta(connection, MetaProjectPath) ?? "",
                    ProjectFileHash = ReadMeta(connection, MetaProjectFileHash) ?? "",
                    CacheDate = ParseDate(ReadMeta(connection, MetaCacheDate)) ?? default,
                    Version = int.TryParse(ReadMeta(connection, MetaFormatVersion), out var version)
                        ? version
                        : FormatVersion
                };

                using var command = connection.CreateCommand();
                command.CommandText = "SELECT module_type, hash, size, modified FROM modules";

                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    metadata.Modules[reader.GetString(0)] = new ModuleHashMetadata
                    {
                        Hash = reader.GetString(1),
                        Size = reader.GetInt64(2),
                        LastModified = DateTimeOffset
                            .FromUnixTimeMilliseconds(reader.GetInt64(3))
                            .LocalDateTime
                    };
                }

                return metadata;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading cache metadata");
                return null;
            }
            finally
            {
                _fileLock.Release();
            }
        }

        public object? GetModuleCustomData(string projectPath, string moduleType)
        {
            var cachePath = GetCachePath(projectPath);
            if (!File.Exists(cachePath) && !File.Exists(GetBackupPath(projectPath))) return null;

            if (!_fileLock.Wait(TimeSpan.FromSeconds(3)))
            {
                _logger.LogWarning("Cache lock timeout — returning null, caller uses in-memory fallback");
                return null;
            }
            try
            {
                if (!EnsureCacheReadable(cachePath)) return null;

                using var connection = OpenConnection(cachePath, createIfMissing: false);

                using var command = connection.CreateCommand();
                command.CommandText = "SELECT custom_json FROM modules WHERE module_type = @module";
                command.Parameters.AddWithValue("@module", moduleType);

                return command.ExecuteScalar() is string json
                    ? JsonConvert.DeserializeObject<object>(json)
                    : null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading module data from cache: {ModuleType}", moduleType);
                return null;
            }
            finally
            {
                _fileLock.Release();
            }
        }

        // ── Запись ────────────────────────────────────────────────────────

        /// <summary>
        /// Сохранить кеш проекта.
        /// Обновляются строки только переданных модулей, строки остальных
        /// остаются нетронутыми.
        /// Файл кеша удаляется только через DeleteCache() (при Ctrl+S или закрытии проекта).
        /// </summary>
        /// <param name="projectPath">Путь к .writersword файлу</param>
        /// <param name="projectId">ID проекта — записывается в метадату для верификации при загрузке</param>
        /// <param name="customDataDict">Данные модулей: moduleType → data</param>
        /// <param name="sessionDataDict">Сессионные данные модулей: moduleType → data</param>
        public async Task SaveCacheAsync(
            string projectPath,
            string projectId,
            Dictionary<string, object?> customDataDict,
            Dictionary<string, object?> sessionDataDict)
        {
            var cachePath = GetCachePath(projectPath);

            await _fileLock.WaitAsync().ConfigureAwait(false);
            try
            {
                _logger.LogDebug("Saving cache: {ModulesCount} modules", customDataDict.Count);

                // Подготавливаем данные модулей которые нужно записать.
                var modulesToSave = new Dictionary<string, (string CustomDataJson, string? SessionDataJson)>();

                foreach (var kvp in customDataDict)
                {
                    var moduleType = kvp.Key;
                    var customData = kvp.Value;

                    if (customData == null || (customData is string str && string.IsNullOrWhiteSpace(str)))
                    {
                        _logger.LogDebug("Skipping module without data: {moduleType}", moduleType);
                        continue;
                    }

                    // Сериализуем снапшот — живая коллекция может меняться из UI-треда
                    // пока фоновый поток сериализует, что даёт InvalidOperationException.
                    object? customDataToSerialize = customData is IDictionary<string, object?> cd
                        ? new Dictionary<string, object?>(cd)
                        : customData;
                    var customDataJson = JsonConvert.SerializeObject(customDataToSerialize, Formatting.Indented);

                    string? sessionDataJson = null;
                    if (sessionDataDict.TryGetValue(moduleType, out var sessionData) && sessionData != null)
                    {
                        object? sessionDataToSerialize = sessionData is IDictionary<string, object?> sd
                            ? new Dictionary<string, object?>(sd)
                            : sessionData;
                        sessionDataJson = JsonConvert.SerializeObject(sessionDataToSerialize, Formatting.Indented);
                    }

                    modulesToSave[moduleType] = (customDataJson, sessionDataJson);
                }

                if (modulesToSave.Count == 0)
                {
                    _logger.LogDebug("Nothing to save");
                    return;
                }

                bool fileExists = EnsureCacheReadable(cachePath);

                // Нечитаемый файл на месте кеша убирается до создания базы:
                // открыть его драйвером всё равно нельзя, а оставленный рядом
                // он не дал бы записать ни одной точки восстановления.
                if (!fileExists && File.Exists(cachePath))
                {
                    try
                    {
                        DeleteWithCompanions(cachePath);
                        _logger.LogWarning("Unreadable cache file replaced: {CachePath}", cachePath);
                    }
                    catch (IOException ex)
                    {
                        _logger.LogError(ex, "Failed to replace unreadable cache file: {CachePath}", cachePath);
                        return;
                    }
                }

                // Если файл принадлежит другому проекту — кеш заводится заново.
                if (fileExists)
                {
                    string? existingProjectId;
                    using (var probe = OpenConnection(cachePath, createIfMissing: false))
                        existingProjectId = ReadMeta(probe, MetaProjectId);

                    if (!string.IsNullOrEmpty(existingProjectId) && existingProjectId != projectId)
                    {
                        _logger.LogWarning(
                            "Cache belongs to different project ({ExistingId}), recreating for {NewId}",
                            existingProjectId, projectId);

                        DeleteWithCompanions(cachePath);
                        fileExists = false;
                    }
                }

                // Хеш файла проекта — для быстрого сравнения при открытии.
                // Позволяет пропустить загрузку 400 МБ данных до показа диалога восстановления.
                // Сам файл проекта в этот момент может быть кратковременно занят другой
                // операцией сохранения (хранилище держит его открытым в RELEASE-режиме) —
                // несколько попыток с паузой снимают эту гонку без изменения результата
                // при успехе.
                string projectFileHash = "";
                const int hashReadAttempts = 3;
                for (int attempt = 1; attempt <= hashReadAttempts; attempt++)
                {
                    try
                    {
                        // Глобальный шлюз файла проекта: хеширование не пересекается
                        // с записью в хранилище и сохранением проекта.
                        // FileShare.ReadWrite — чтобы не блокировать удерживаемый
                        // в RELEASE-режиме дескриптор хранилища.
                        using var fileGate = ProjectFileLock.Acquire(projectPath);
                        using var sha = SHA256.Create();
                        using var fs = new FileStream(
                            projectPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        projectFileHash = Convert.ToHexString(sha.ComputeHash(fs));
                        break;
                    }
                    catch (IOException ex) when (attempt < hashReadAttempts)
                    {
                        _logger.LogDebug(ex, "Project file busy, retrying hash computation ({Attempt}/{Total})", attempt, hashReadAttempts);
                        await Task.Delay(100 * attempt).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to compute project file hash");
                        projectFileHash = "";
                        break;
                    }
                }

                var now = DateTime.Now;
                var modifiedMs = new DateTimeOffset(now).ToUnixTimeMilliseconds();

                using (var connection = OpenConnection(cachePath, createIfMissing: true))
                {
                    EnsureSchema(connection);

                    // Транзакция: оборванная запись откатывается целиком, и кеш
                    // остаётся тем, чем был до неё, — прежней рабочей точкой
                    // восстановления, а не наполовину обновлённым файлом.
                    using var transaction = connection.BeginTransaction();

                    WriteMeta(connection, MetaFormatVersion, FormatVersion.ToString());
                    WriteMeta(connection, MetaProjectId, projectId);
                    WriteMeta(connection, MetaProjectPath, projectPath);
                    WriteMeta(connection, MetaCacheDate, FormatDate(now));
                    WriteMeta(connection, MetaProjectFileHash, projectFileHash);

                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = """
                            INSERT INTO modules(module_type, custom_json, session_json, hash, size, modified)
                            VALUES(@module, @custom, @session, @hash, @size, @modified)
                            ON CONFLICT(module_type) DO UPDATE SET
                                custom_json  = excluded.custom_json,
                                session_json = excluded.session_json,
                                hash         = excluded.hash,
                                size         = excluded.size,
                                modified     = excluded.modified
                            """;

                        var moduleParameter = command.Parameters.Add("@module", SqliteType.Text);
                        var customParameter = command.Parameters.Add("@custom", SqliteType.Text);
                        var sessionParameter = command.Parameters.Add("@session", SqliteType.Text);
                        var hashParameter = command.Parameters.Add("@hash", SqliteType.Text);
                        var sizeParameter = command.Parameters.Add("@size", SqliteType.Integer);
                        var modifiedParameter = command.Parameters.Add("@modified", SqliteType.Integer);

                        foreach (var kvp in modulesToSave)
                        {
                            var moduleType = kvp.Key;
                            var (customDataJson, sessionDataJson) = kvp.Value;

                            moduleParameter.Value = moduleType;
                            customParameter.Value = customDataJson;
                            sessionParameter.Value = (object?)sessionDataJson ?? DBNull.Value;
                            hashParameter.Value = _hashService.ComputeHash(customDataJson);
                            sizeParameter.Value = Encoding.UTF8.GetByteCount(customDataJson);
                            modifiedParameter.Value = modifiedMs;

                            command.ExecuteNonQuery();

                            _logger.LogDebug("Updated cache entry for: {moduleType}", moduleType);
                        }
                    }

                    transaction.Commit();

                    // Журнал сводится в базу сразу: файл кеша переносят и копируют
                    // обычными файловыми операциями, и содержимое не должно
                    // оставаться в спутниках.
                    Checkpoint(connection);
                }

                _logger.LogDebug("Cache updated: {ModulesCount} modules written", modulesToSave.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving cache");
            }
            finally
            {
                _fileLock.Release();
            }
        }

        // ── Удаление и перенос ────────────────────────────────────────────

        public void DeleteCache(string projectPath)
        {
            var cachePath = GetCachePath(projectPath);
            var backupPath = GetBackupPath(projectPath);

            if (!File.Exists(cachePath) && !File.Exists(backupPath)) return;

            if (!_fileLock.Wait(TimeSpan.FromSeconds(3)))
            {
                _logger.LogWarning("Cache lock timeout in DeleteCache — skipping delete");
                return;
            }
            try
            {
                // Удаляются все следы точки восстановления: оставшаяся резервная
                // копия воскресила бы уже принятую или отклонённую версию.
                foreach (var path in new[] { cachePath, backupPath })
                {
                    if (!File.Exists(path) && !File.Exists(path + "-wal")) continue;

                    DeleteWithCompanions(path);
                    _logger.LogDebug("Cache deleted: {CachePath}", path);
                }
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Error deleting cache: {CachePath}", cachePath);
            }
            finally
            {
                _fileLock.Release();
            }
        }

        public void MoveCacheToBackup(string projectPath)
        {
            var cachePath = GetCachePath(projectPath);
            if (!File.Exists(cachePath)) return;

            if (!_fileLock.Wait(TimeSpan.FromSeconds(3)))
            {
                _logger.LogWarning("Cache lock timeout in MoveCacheToBackup — skipping");
                return;
            }
            try
            {
                var backupPath = GetBackupPath(projectPath);

                // Прежняя копия уходит целиком вместе со спутниками: чужой
                // журнал рядом с базой делает её нечитаемой.
                DeleteWithCompanions(backupPath);

                File.Move(cachePath, backupPath, overwrite: true);

                foreach (var companion in Companions(cachePath))
                    if (File.Exists(companion)) File.Delete(companion);

                _logger.LogDebug("Cache moved to backup: {BackupPath}", backupPath);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Error moving cache to backup: {CachePath}", cachePath);
            }
            finally
            {
                _fileLock.Release();
            }
        }

        // ── Данные проекта ────────────────────────────────────────────────

        public Dictionary<string, object?>? ReadProjectDataWithoutLock(string projectPath)
        {
            // «WithoutLock» относится к внутреннему замку КЕША. Замок файла
            // проекта здесь больше не нужен: база сама разводит читателей и
            // писателя, и застать её посреди записи невозможно.
            try
            {
                using var storage = new SqliteFileStorageService(projectPath, Serilog.Log.Logger);

                if (storage.ReadFile("project.json") is null)
                {
                    _logger.LogWarning("project.json not found in: {ProjectPath}", projectPath);
                    return null;
                }

                // Данные модулей лежат отдельными записями, а не внутри
                // project.json. Прочитать один project.json и взять оттуда
                // ModulesData недостаточно — он их не содержит.
                var result = new Dictionary<string, object?>();

                foreach (var entry in storage.EnumerateEntries())
                {
                    var path = entry.Path;

                    if (!path.StartsWith("modules/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!path.EndsWith("/CustomData.json", StringComparison.OrdinalIgnoreCase)) continue;

                    var parts = path.Split('/');
                    if (parts.Length < 3) continue;

                    var data = storage.ReadFile(path);
                    if (data is null) continue;

                    result[parts[1]] = Encoding.UTF8.GetString(data);
                }

                _logger.LogDebug("Read project data without lock: {ModulesCount} modules", result.Count);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading project data without lock");
                return null;
            }
        }
    }
}
