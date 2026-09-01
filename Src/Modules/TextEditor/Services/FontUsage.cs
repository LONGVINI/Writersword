using Newtonsoft.Json;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Закреплённые и недавние гарнитуры.
    ///
    /// Список шрифтов на машине идёт сотнями, и нужных из них у человека три-четыре.
    /// Держать их наверху — работа не рукописи, а программы: они те же самые в любом
    /// проекте и на чужую машину не уезжают. Поэтому список лежит в данных программы
    /// рядом с картинками видов чтения, а не в архиве проекта.
    ///
    /// Порядок закреплённых — порядок закрепления: человек ставит звёздочки в том
    /// порядке, в каком ему удобно их видеть, и перетасовывать список по алфавиту
    /// значит отобрать у него этот выбор.
    ///
    /// Сбой чтения или записи файла ничего не роняет: в худшем случае список
    /// открывается без закреплённых. Настройка удобства не имеет права мешать писать.
    /// </summary>
    public static class FontUsage
    {
        private static readonly ILogger _logger = Log.ForContext(typeof(FontUsage));
        private static readonly object _lock = new();

        /// <summary>Сколько последних гарнитур помнить.</summary>
        private const int RecentLimit = 8;

        private static string FolderPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Writersword");

        private static string FilePath => Path.Combine(FolderPath, "fonts.json");

        private sealed class State
        {
            [JsonProperty("pinned")]
            public List<string> Pinned { get; set; } = new();

            [JsonProperty("recent")]
            public List<string> Recent { get; set; } = new();
        }

        private static State? _state;

        public static IReadOnlyList<string> Pinned
        {
            get
            {
                lock (_lock)
                {
                    EnsureLoaded();
                    return _state!.Pinned.ToArray();
                }
            }
        }

        public static IReadOnlyList<string> Recent
        {
            get
            {
                lock (_lock)
                {
                    EnsureLoaded();
                    return _state!.Recent.ToArray();
                }
            }
        }

        public static bool IsPinned(string family)
        {
            if (string.IsNullOrWhiteSpace(family)) return false;

            lock (_lock)
            {
                EnsureLoaded();
                return _state!.Pinned.Contains(family, StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Закрепить гарнитуру или снять закрепление. Возвращает новое состояние.
        /// </summary>
        public static bool TogglePin(string family)
        {
            if (string.IsNullOrWhiteSpace(family)) return false;

            lock (_lock)
            {
                EnsureLoaded();

                int at = _state!.Pinned.FindIndex(
                    f => string.Equals(f, family, StringComparison.OrdinalIgnoreCase));

                bool pinned;
                if (at >= 0)
                {
                    _state.Pinned.RemoveAt(at);
                    pinned = false;
                }
                else
                {
                    _state.Pinned.Add(family);
                    pinned = true;
                }

                Save();
                return pinned;
            }
        }

        /// <summary>
        /// Отметить гарнитуру использованной. Она встаёт первой среди недавних.
        /// </summary>
        public static void NoteUsed(string family)
        {
            if (string.IsNullOrWhiteSpace(family)) return;

            lock (_lock)
            {
                EnsureLoaded();

                _state!.Recent.RemoveAll(
                    f => string.Equals(f, family, StringComparison.OrdinalIgnoreCase));
                _state.Recent.Insert(0, family);

                if (_state.Recent.Count > RecentLimit)
                    _state.Recent.RemoveRange(RecentLimit, _state.Recent.Count - RecentLimit);

                Save();
            }
        }

        // ── Хранение ──────────────────────────────────────────────────────

        private static void EnsureLoaded()
        {
            if (_state is not null) return;

            _state = new State();

            try
            {
                if (!File.Exists(FilePath)) return;

                string json = File.ReadAllText(FilePath);
                if (string.IsNullOrWhiteSpace(json)) return;

                var loaded = JsonConvert.DeserializeObject<State>(json);
                if (loaded is null) return;

                _state.Pinned = loaded.Pinned ?? new List<string>();
                _state.Recent = loaded.Recent ?? new List<string>();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to read font usage from {Path}", FilePath);
            }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(FolderPath);
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(_state, Formatting.Indented));
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to write font usage to {Path}", FilePath);
            }
        }
    }
}
