using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Serilog;
using Writersword.Core.Interfaces.Services;

namespace Writersword.Core.Services.Storage
{
    /// <summary>Сведения о записи проекта без её содержимого.</summary>
    public sealed record ProjectEntryInfo(string Path, string Hash, long Size, DateTimeOffset Modified);

    /// <summary>
    /// Файлы проекта в базе SQLite.
    ///
    /// Пришло на смену ZIP по одной причине: ZipArchive в режиме Update держит
    /// весь архив в памяти и переписывает файл целиком при закрытии. Правка
    /// одного абзаца в проекте с гигабайтом иллюстраций означала перезапись
    /// гигабайта — и так на каждое сохранение.
    ///
    /// База пишет страницами: меняются только страницы изменившихся строк.
    /// Картинки, лежащие рядом, при этом не читаются и не переписываются вовсе.
    ///
    /// Содержимое адресуется по хешу, как в складе истории версий: одна и та же
    /// картинка, вставленная в трёх местах, хранится один раз. Запись, чьё
    /// содержимое уже есть в базе, стоит одной строки в таблице путей.
    /// </summary>
    public sealed class SqliteFileStorageService : IProjectFileStorage
    {
        /// <summary>
        /// Версия формата. Растёт при несовместимых изменениях схемы; по ней
        /// программа поймёт, что проект создан более новой версией, и откажется
        /// его портить, вместо того чтобы читать вслепую.
        /// </summary>
        private const int FormatVersion = 1;

        private readonly string _databasePath;
        private readonly ILogger _log;
        private readonly object _sync = new();

        private SqliteConnection? _connection;

        // volatile: признак читается вне замка теми, кто только хочет узнать
        // состояние. Решение о работе с базой принимается всё равно под
        // замком — там же, где хранилище закрывается.
        private volatile bool _disposed;

        public SqliteFileStorageService(string databasePath, ILogger logger)
        {
            _databasePath = databasePath ?? throw new ArgumentNullException(nameof(databasePath));
            _log = logger?.ForContext<SqliteFileStorageService>() ?? throw new ArgumentNullException(nameof(logger));

            try
            {
                Open();
            }
            catch
            {
                // Конструктор, бросивший исключение, не оставляет объекта, и
                // закрыть соединение потом будет некому: файл базы остался бы
                // занятым до выхода из программы.
                _connection?.Dispose();
                _connection = null;
                throw;
            }
        }

        private void Open()
        {
            EnsureDatabaseFile();

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private
            };

            _connection = new SqliteConnection(builder.ToString());
            _connection.Open();

            // WAL: запись идёт в отдельный журнал, а не переписывает страницы
            // базы на месте. Читатели при этом не блокируют писателя — важно,
            // потому что синхронизация читает файл проекта параллельно работе.
            //
            // synchronous=NORMAL: сбрасывать на диск при контрольной точке, а не
            // на каждой записи. Внезапное отключение питания в худшем случае
            // теряет последние секунды, но не рушит базу — в отличие от ZIP,
            // где обрыв посреди перезаписи оставлял обрубок вместо проекта.
            Execute("PRAGMA journal_mode = WAL");
            Execute("PRAGMA synchronous = NORMAL");

            // 64 МБ страничного кеша: чтение галереи персонажей не должно
            // ходить на диск за каждой миниатюрой.
            Execute("PRAGMA cache_size = -65536");

            Execute("""
                CREATE TABLE IF NOT EXISTS meta(
                    key   TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS objects(
                    hash TEXT PRIMARY KEY,
                    data BLOB NOT NULL,
                    size INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS entries(
                    path     TEXT PRIMARY KEY,
                    hash     TEXT NOT NULL,
                    modified INTEGER NOT NULL DEFAULT 0
                );

                CREATE INDEX IF NOT EXISTS ix_entries_hash ON entries(hash);
                """);

            Execute("INSERT OR IGNORE INTO meta(key, value) VALUES('format_version', @v)",
                ("@v", FormatVersion.ToString()));

            _log.Debug("Opened project database {Path}", _databasePath);
        }

        /// <summary>
        /// Убедиться, что файл — база, а не что-то другое.
        ///
        /// Без этой проверки попытка открыть чужой файл заканчивается
        /// исключением из недр драйвера со стектрейсом на пол-экрана, из
        /// которого не следует ни что за файл, ни что с ним делать. Формат
        /// узнаётся по первым байтам: у базы это «SQLite format 3», у прежних
        /// проектов — сигнатура ZIP.
        /// </summary>
        private void EnsureDatabaseFile()
        {
            if (!File.Exists(_databasePath))
                return;

            Span<byte> header = stackalloc byte[16];

            using (var stream = new FileStream(
                _databasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                // Файл короче заголовка: пустышка от прерванного создания,
                // открывать нечего, база создастся заново.
                if (stream.Length == 0)
                    return;

                if (stream.Read(header) < header.Length)
                    throw new InvalidDataException($"File is too small to be a project: {_databasePath}");
            }

            if (header.StartsWith("SQLite format 3\0"u8))
                return;

            if (header.StartsWith("PK\u0003\u0004"u8))
                throw new InvalidDataException(
                    $"Project was created by an older version and uses the ZIP format: {_databasePath}");

            throw new InvalidDataException($"File is not a Writersword project: {_databasePath}");
        }

        /// <summary>
        /// Нормализация пути: разделитель всегда прямой слеш.
        ///
        /// Путь — первичный ключ таблицы, и «TextEditor/images/a.png» с
        /// «TextEditor\images\a.png» иначе стали бы двумя разными записями.
        /// В ZIP та же нормализация делалась по той же причине.
        /// </summary>
        private static string Normalize(string relativePath)
            => relativePath.Replace('\\', '/').TrimStart('/');

        public void WriteFile(string relativePath, byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            lock (_sync)
            {
                // Проверка состояния и работа с соединением — под одним замком.
                // Раньше признак закрытия читался до входа в замок: закрытие
                // проекта успевало обнулить соединение, пока фоновая запись
                // ждала замок, и запись падала на обращении к нему.
                var connection = _connection;
                if (_disposed || connection is null)
                {
                    _log.Error("Cannot write, storage is disposed: {Path}", relativePath);
                    return;
                }

                var path = Normalize(relativePath);
                var hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

                using var transaction = connection.BeginTransaction();

                // Содержимое пишется, только если такого ещё нет. Повторная
                // вставка той же картинки не стоит ничего.
                Execute("INSERT OR IGNORE INTO objects(hash, data, size) VALUES(@h, @d, @s)",
                    transaction, ("@h", hash), ("@d", data), ("@s", (long)data.Length));

                Execute("INSERT INTO entries(path, hash, modified) VALUES(@p, @h, @m) " +
                        "ON CONFLICT(path) DO UPDATE SET hash = excluded.hash, modified = excluded.modified",
                    transaction, ("@p", path), ("@h", hash),
                    ("@m", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

                transaction.Commit();

                _log.Debug("Written: {Path} ({Size} bytes)", path, data.Length);
            }
        }

        public byte[]? ReadFile(string relativePath)
        {
            lock (_sync)
            {
                var connection = _connection;
                if (_disposed || connection is null)
                {
                    _log.Error("Cannot read, storage is disposed: {Path}", relativePath);
                    return null;
                }

                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT o.data FROM entries e JOIN objects o ON o.hash = e.hash WHERE e.path = @p";
                command.Parameters.AddWithValue("@p", Normalize(relativePath));

                var result = command.ExecuteScalar();
                return result as byte[];
            }
        }

        public bool FileExists(string relativePath)
        {
            lock (_sync)
            {
                var connection = _connection;
                if (_disposed || connection is null) return false;

                using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1 FROM entries WHERE path = @p";
                command.Parameters.AddWithValue("@p", Normalize(relativePath));

                return command.ExecuteScalar() is not null;
            }
        }

        /// <summary>
        /// Убрать путь из проекта.
        ///
        /// Само содержимое остаётся: на него могут ссылаться другие записи, а
        /// разбираться, кто последний, — работа уборки. Освобождает место
        /// «Сжать проект», как и раньше.
        /// </summary>
        public void DeleteFile(string relativePath)
        {
            lock (_sync)
            {
                if (_disposed || _connection is null)
                {
                    _log.Error("Cannot delete, storage is disposed: {Path}", relativePath);
                    return;
                }

                Execute("DELETE FROM entries WHERE path = @p", ("@p", Normalize(relativePath)));
                _log.Debug("Deleted: {Path}", Normalize(relativePath));
            }
        }

        public IEnumerable<string> GetFiles(string relativePath)
        {
            lock (_sync)
            {
                var connection = _connection;
                if (_disposed || connection is null) return Array.Empty<string>();

                var prefix = Normalize(relativePath).TrimEnd('/');
                prefix = prefix.Length == 0 ? string.Empty : prefix + "/";

                using var command = connection.CreateCommand();
                command.CommandText = "SELECT path FROM entries WHERE path LIKE @prefix ORDER BY path";
                command.Parameters.AddWithValue("@prefix", prefix + "%");

                var result = new List<string>();
                using var reader = command.ExecuteReader();

                while (reader.Read())
                    result.Add(reader.GetString(0));

                return result;
            }
        }

        /// <summary>
        /// Перечислить записи проекта: путь, хеш содержимого, размер, время.
        ///
        /// Содержимое при этом не читается. Для истории версий это решающее:
        /// снятие точки прежде читало весь проект целиком, чтобы посчитать
        /// хеши, — а здесь они уже посчитаны и лежат в таблице. Читать нужно
        /// только то, чего ещё нет в складе.
        /// </summary>
        public IReadOnlyList<ProjectEntryInfo> EnumerateEntries()
        {
            lock (_sync)
            {
                var connection = _connection;
                if (_disposed || connection is null) return Array.Empty<ProjectEntryInfo>();

                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT e.path, e.hash, o.size, e.modified " +
                    "FROM entries e JOIN objects o ON o.hash = e.hash ORDER BY e.path";

                var result = new List<ProjectEntryInfo>();
                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    result.Add(new ProjectEntryInfo(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetInt64(2),
                        DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3))));
                }

                return result;
            }
        }

        /// <summary>
        /// Довести записанное до файла базы.
        ///
        /// Каждая запись здесь и так завершается фиксацией, поэтому терять
        /// нечего — в отличие от прежнего архива, где до сброса всё жило только
        /// в памяти. Метод переносит журнал WAL в основной файл, чтобы на диске
        /// лежал один самодостаточный файл: важно перед отправкой в хранилище
        /// и перед снятием точки восстановления.
        /// </summary>
        public void Flush()
        {
            lock (_sync)
            {
                if (_disposed || _connection is null) return;

                try
                {
                    Execute("PRAGMA wal_checkpoint(TRUNCATE)");
                    _log.Debug("Checkpointed {Path}", _databasePath);
                }
                catch (SqliteException ex)
                {
                    // Контрольная точка не удаётся, пока базу читает кто-то ещё.
                    // Это не потеря данных: они уже зафиксированы, просто пока
                    // лежат в журнале рядом.
                    _log.Debug(ex, "Checkpoint skipped");
                }
            }
        }

        /// <summary>
        /// Убрать содержимое, на которое не осталось ссылок, и сжать файл.
        /// Вызывается из «Сжать проект».
        /// </summary>
        public int Compact()
        {
            lock (_sync)
            {
                var connection = _connection;
                if (_disposed || connection is null) return 0;

                int removed;

                using (var transaction = connection.BeginTransaction())
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText =
                        "DELETE FROM objects WHERE hash NOT IN (SELECT hash FROM entries)";
                    removed = command.ExecuteNonQuery();

                    transaction.Commit();
                }

                // VACUUM перестраивает файл и потому не работает внутри
                // транзакции — отсюда и отдельный вызов после фиксации.
                Execute("VACUUM");

                _log.Information("Compacted {Path}: {Count} objects removed", _databasePath, removed);
                return removed;
            }
        }

        /// <summary>Размер данных проекта в байтах, без учёта служебных страниц.</summary>
        public long GetContentSize()
        {
            lock (_sync)
            {
                var connection = _connection;
                if (_disposed || connection is null) return 0;

                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT COALESCE(SUM(size), 0) FROM objects WHERE hash IN (SELECT hash FROM entries)";

                return Convert.ToInt64(command.ExecuteScalar());
            }
        }

        private void Execute(string sql, params (string Name, object Value)[] parameters)
            => Execute(sql, null, parameters);

        private void Execute(string sql, SqliteTransaction? transaction,
            params (string Name, object Value)[] parameters)
        {
            // Вызывающие проверяют состояние под замком; здесь проверка нужна
            // на случай пути, который эту проверку обошёл: закрытое хранилище
            // должно называть себя, а не выдавать обращение по пустой ссылке.
            var connection = _connection
                ?? throw new ObjectDisposedException(nameof(SqliteFileStorageService));

            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = transaction;

            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value);

            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;

                if (_connection is not null)
                {
                    try
                    {
                        // Журнал сливается в основной файл при закрытии: иначе рядом
                        // с проектом остаются -wal и -shm, и пользователь видит три
                        // файла вместо одного.
                        Execute("PRAGMA wal_checkpoint(TRUNCATE)");
                    }
                    catch (SqliteException ex)
                    {
                        _log.Debug(ex, "Checkpoint on dispose skipped");
                    }
                }

                var connection = _connection;
                _connection = null;
                _disposed = true;

                if (connection is not null)
                {
                    // Пул держит соединение открытым и после Dispose, а вместе с
                    // ним и файл. Для программы, которая закрывает проект и тут
                    // же его отправляет или переименовывает, это означало бы
                    // «файл занят». Чистится пул только этого соединения:
                    // общая очистка гасила пулы остальных открытых проектов.
                    SqliteConnection.ClearPool(connection);
                    connection.Dispose();
                }

                _log.Debug("Closed project database {Path}", _databasePath);
            }
        }
    }
}
