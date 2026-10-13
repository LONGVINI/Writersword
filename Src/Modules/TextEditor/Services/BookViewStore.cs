using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Writersword.Modules.TextEditor.Models.Settings;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Вид листа при правке, выбранный для конкретной книги.
    ///
    /// Вид прежде был один на все книги: выбранный в одной рукописи, он тут же
    /// переезжал во все открытые и во все, что откроются потом. Теперь у каждой книги
    /// свой. Книга, для которой вид ещё не выбирали, открывается видом по умолчанию —
    /// он лежит в общих настройках и задаётся кнопкой «Вид по умолчанию» в окне видов.
    ///
    /// Вид — не часть рукописи: в документ и в файл проекта он не попадает, правкой
    /// не считается и сохранения не требует. Лежит в данных программы, по записи на
    /// книгу, как и место в рукописи (<see cref="EditingPlaceStore"/>). Сбой чтения
    /// или записи ничего не роняет — книга откроется видом по умолчанию.
    /// </summary>
    public static class BookViewStore
    {
        /// <summary>Вид книги: включён ли он, какой выбран и его рабочая копия.</summary>
        public sealed class Entry
        {
            [JsonPropertyName("enabled")] public bool ThemeEnabled { get; set; }
            [JsonPropertyName("themeId")] public string? ThemeId { get; set; }
            [JsonPropertyName("theme")] public ReadingTheme? Theme { get; set; }
            [JsonPropertyName("updated")] public DateTime UpdatedUtc { get; set; }
        }

        private static readonly object _lock = new();

        private static Dictionary<string, Entry>? _entries;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Writersword",
            "book-views.json");

        /// <summary>Вид книги или null, если для неё свой вид не выбирали.</summary>
        public static Entry? Get(string book)
        {
            if (string.IsNullOrWhiteSpace(book)) return null;

            lock (_lock)
            {
                EnsureLoaded();
                if (!_entries!.TryGetValue(book, out var e)) return null;

                // Копия: рабочая копия вида правится ползунками, и правка не должна
                // попадать в запомненное мимо Save.
                return new Entry
                {
                    ThemeEnabled = e.ThemeEnabled,
                    ThemeId = e.ThemeId,
                    Theme = e.Theme?.Clone(),
                    UpdatedUtc = e.UpdatedUtc
                };
            }
        }

        /// <summary>Запоминает вид книги.</summary>
        public static void Save(string book, bool themeEnabled, string? themeId, ReadingTheme? theme)
        {
            if (string.IsNullOrWhiteSpace(book)) return;

            lock (_lock)
            {
                EnsureLoaded();

                _entries![book] = new Entry
                {
                    ThemeEnabled = themeEnabled,
                    ThemeId = themeId,
                    Theme = theme?.Clone(),
                    UpdatedUtc = DateTime.UtcNow
                };

                Write();
            }
        }

        /// <summary>Книга снова открывается видом по умолчанию.</summary>
        public static void Remove(string book)
        {
            if (string.IsNullOrWhiteSpace(book)) return;

            lock (_lock)
            {
                EnsureLoaded();
                if (_entries!.Remove(book)) Write();
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
                Serilog.Log.Warning(ex, "Failed to read book views from {Path}", FilePath);
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
                // видов всех книг пустой или недописанный файл.
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_entries, JsonOptions));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Failed to write book views to {Path}", FilePath);
            }
        }
    }
}
