using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Serilog;

namespace Writersword.Core.Services.Sync
{
    /// <summary>
    /// Память о том, в каком состоянии проект был при последней синхронизации.
    ///
    /// Без неё нельзя отличить «я правил локально» от «правил кто-то на другом
    /// устройстве»: сравнение локального файла с серверным даёт только факт
    /// различия, но не говорит, чья версия новее. Поэтому запоминается пара —
    /// ETag серверной версии и хеш локального файла на тот момент.
    ///
    /// Файл состояния лежит рядом с настройками приложения, а не в папке
    /// проекта: он привязан к устройству, а не к книге, и в облако попадать
    /// не должен.
    /// </summary>
    public sealed class SyncStateStore
    {
        private readonly string _statePath;
        private readonly ILogger _log;
        private readonly object _gate = new();
        private Dictionary<string, SyncEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
        private bool _loaded;

        public SyncStateStore(ILogger logger, string? overridePath = null)
        {
            _log = logger?.ForContext<SyncStateStore>() ?? throw new ArgumentNullException(nameof(logger));

            _statePath = overridePath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Writersword",
                "sync-state.json");
        }

        /// <summary>Запись о последней успешной синхронизации одного проекта.</summary>
        public sealed class SyncEntry
        {
            /// <summary>ETag серверной версии, от которой отсчитывается локальная.</summary>
            public string ETag { get; set; } = string.Empty;

            /// <summary>SHA-256 локального файла на момент синхронизации.</summary>
            public string LocalHash { get; set; } = string.Empty;

            public DateTimeOffset SyncedAt { get; set; }
        }

        public SyncEntry? Get(string localPath)
        {
            EnsureLoaded();

            lock (_gate)
            {
                return _entries.TryGetValue(Normalize(localPath), out var entry) ? entry : null;
            }
        }

        public void Set(string localPath, string etag, string localHash)
        {
            EnsureLoaded();

            lock (_gate)
            {
                _entries[Normalize(localPath)] = new SyncEntry
                {
                    ETag = etag,
                    LocalHash = localHash,
                    SyncedAt = DateTimeOffset.UtcNow
                };
            }

            Save();
        }

        public void Remove(string localPath)
        {
            EnsureLoaded();

            lock (_gate)
            {
                if (!_entries.Remove(Normalize(localPath)))
                    return;
            }

            Save();
        }

        /// <summary>
        /// SHA-256 файла потоком, без чтения целиком в память.
        /// Проект с картинками может весить сотни мегабайт, а хеш считается
        /// при каждой проверке состояния.
        /// </summary>
        public static string ComputeFileHash(string path)
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);

            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        public static string ComputeHash(byte[] data)
            => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        private static string Normalize(string path)
            => Path.GetFullPath(path);

        private void EnsureLoaded()
        {
            lock (_gate)
            {
                if (_loaded) return;
                _loaded = true;

                if (!File.Exists(_statePath))
                    return;

                try
                {
                    var json = File.ReadAllText(_statePath);
                    var parsed = JsonConvert.DeserializeObject<Dictionary<string, SyncEntry>>(json);

                    if (parsed is not null)
                        _entries = new Dictionary<string, SyncEntry>(parsed, StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    // Потеря состояния не фатальна: худшее следствие — лишний
                    // вопрос пользователю при первом сравнении. Ронять запуск
                    // приложения из-за этого нельзя.
                    _log.Warning(ex, "Failed to read sync state, starting from scratch");
                    _entries = new Dictionary<string, SyncEntry>(StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        private void Save()
        {
            try
            {
                var directory = Path.GetDirectoryName(_statePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                string json;
                lock (_gate)
                {
                    json = JsonConvert.SerializeObject(_entries, Formatting.Indented);
                }

                // Запись через временный файл: обрыв питания посреди записи
                // оставит целым либо старое состояние, либо новое, но не обрубок.
                var temp = _statePath + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, _statePath, overwrite: true);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to persist sync state");
            }
        }
    }
}
