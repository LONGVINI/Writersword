using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Writersword.Modules.Characters.Services
{
    /// <summary>
    /// Место в модуле персонажей, где остановился автор: открытый персонаж,
    /// вкладка модуля, вкладка карточки и прокрутка карточки.
    ///
    /// Прежде место жило только в сессии модуля, а сессия лежит в кеше проекта.
    /// Кеш пишется лишь при правках и удаляется при сохранении и при закрытии
    /// программы, если совпадает с файлом. Открыть персонажа или переключить
    /// вкладку — не правка, поэтому место не доживало ни до перезапуска, ни до
    /// пересоздания модуля: модуль открывался на списке, без персонажа. Та же
    /// беда была у рукописи — её решает EditingPlaceStore текстового редактора,
    /// здесь то же решение.
    ///
    /// Место — не часть проекта: в файл проекта не попадает, правкой не
    /// считается и сохранения не требует. Лежит в данных программы, по записи
    /// на проект. Сбой чтения или записи ничего не роняет — в худшем случае
    /// модуль откроется на списке, как раньше.
    /// </summary>
    public static class CharactersPlaceStore
    {
        /// <summary>Место в модуле.</summary>
        public readonly record struct Place(
            string? CharacterId, int MainTabIndex, int CardTabIndex, double CardScrollY);

        private sealed class Entry
        {
            [JsonPropertyName("character")] public string? CharacterId { get; set; }
            [JsonPropertyName("tab")] public int MainTabIndex { get; set; }
            [JsonPropertyName("cardTab")] public int CardTabIndex { get; set; }
            [JsonPropertyName("scroll")] public double CardScrollY { get; set; }
            [JsonPropertyName("updated")] public DateTime UpdatedUtc { get; set; }
        }

        private static readonly object _lock = new();

        private static Dictionary<string, Entry>? _entries;

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Writersword",
            "characters-places.json");

        /// <summary>Место в проекте или null, если модуль в нём ещё не открывали.</summary>
        public static Place? Get(string project)
        {
            if (string.IsNullOrWhiteSpace(project)) return null;

            lock (_lock)
            {
                EnsureLoaded();
                if (!_entries!.TryGetValue(project, out var e)) return null;

                return new Place(
                    string.IsNullOrWhiteSpace(e.CharacterId) ? null : e.CharacterId,
                    Math.Max(0, e.MainTabIndex),
                    Math.Max(0, e.CardTabIndex),
                    Math.Max(0, e.CardScrollY));
            }
        }

        /// <summary>
        /// Запоминает место. Повтор того же места файл не трогает: сообщения о
        /// смене места приходят после каждой остановки прокрутки, и большая часть
        /// их — то же самое место.
        /// </summary>
        public static void Save(string project, Place place)
        {
            if (string.IsNullOrWhiteSpace(project)) return;

            var characterId = string.IsNullOrWhiteSpace(place.CharacterId) ? null : place.CharacterId;
            var scroll = Math.Max(0, place.CardScrollY);

            lock (_lock)
            {
                EnsureLoaded();

                if (_entries!.TryGetValue(project, out var known)
                    && string.Equals(known.CharacterId, characterId, StringComparison.Ordinal)
                    && known.MainTabIndex == place.MainTabIndex
                    && known.CardTabIndex == place.CardTabIndex
                    && Math.Abs(known.CardScrollY - scroll) < 0.5)
                {
                    return;
                }

                _entries[project] = new Entry
                {
                    CharacterId = characterId,
                    MainTabIndex = Math.Max(0, place.MainTabIndex),
                    CardTabIndex = Math.Max(0, place.CardTabIndex),
                    CardScrollY = scroll,
                    UpdatedUtc = DateTime.UtcNow
                };

                Write();
            }
        }

        private static void EnsureLoaded()
        {
            if (_entries is not null) return;

            _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

            try
            {
                var path = FilePath;
                if (!File.Exists(path)) return;

                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return;

                var loaded = JsonSerializer.Deserialize<Dictionary<string, Entry>>(json, JsonOptions);
                if (loaded is null) return;

                _entries = new Dictionary<string, Entry>(loaded, StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Failed to read characters places from {Path}", FilePath);
            }
        }

        private static void Write()
        {
            try
            {
                var path = FilePath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                // Через временный файл: оборванная запись не должна оставить вместо
                // мест всех проектов пустой или недописанный файл.
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_entries, JsonOptions));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Failed to write characters places to {Path}", FilePath);
            }
        }
    }
}
