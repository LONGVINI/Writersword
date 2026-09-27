using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Закладки чтения: где в каждой книге остановился читатель.
    ///
    /// Место в книге прежде жило только в сессии вида — вместе с кареткой, масштабом
    /// и режимом, — а сессия лежит в кеше проекта. Кеш пишется лишь при правках и
    /// удаляется при сохранении и при закрытии программы, если совпадает с файлом.
    /// Чтение ничего не правит, поэтому прочитанное место не доживало до следующего
    /// запуска: книга открывалась там, где стояла каретка, то есть обычно в начале.
    ///
    /// Закладка — не часть рукописи: в документ и в файл проекта она не попадает,
    /// правкой не считается и сохранения не требует. Лежит в данных программы, по
    /// записи на книгу, так же как позиции читалки на телефоне.
    ///
    /// Место хранится номером абзаца и строки внутри него, а не номером страницы:
    /// страницы сдвигаются от кегля, формата листа и подачи, а абзац остаётся тем же.
    /// Сбой чтения или записи ничего не роняет — в худшем случае книга откроется
    /// там, где стоит каретка.
    /// </summary>
    public static class ReadingBookmarkStore
    {
        /// <summary>Место в книге: абзац документа и строка внутри него.</summary>
        public readonly record struct Bookmark(int Paragraph, int Line);

        private sealed class Entry
        {
            [JsonPropertyName("para")] public int Paragraph { get; set; }
            [JsonPropertyName("line")] public int Line { get; set; }
            [JsonPropertyName("updated")] public DateTime UpdatedUtc { get; set; }
        }

        private static readonly object _lock = new();

        private static Dictionary<string, Entry>? _entries;

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Writersword",
            "reading-bookmarks.json");

        /// <summary>Закладка книги или null, если книгу здесь ещё не читали.</summary>
        public static Bookmark? Get(string book)
        {
            if (string.IsNullOrWhiteSpace(book)) return null;

            lock (_lock)
            {
                EnsureLoaded();
                return _entries!.TryGetValue(book, out var e)
                    ? new Bookmark(Math.Max(0, e.Paragraph), Math.Max(0, e.Line))
                    : null;
            }
        }

        /// <summary>Запоминает место в книге. Повтор того же места файл не трогает.</summary>
        public static void Save(string book, int paragraph, int line)
        {
            if (string.IsNullOrWhiteSpace(book) || paragraph < 0) return;

            line = Math.Max(0, line);

            lock (_lock)
            {
                EnsureLoaded();

                if (_entries!.TryGetValue(book, out var known)
                    && known.Paragraph == paragraph
                    && known.Line == line)
                {
                    return;
                }

                _entries[book] = new Entry
                {
                    Paragraph = paragraph,
                    Line = line,
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
                Serilog.Log.Warning(ex, "Failed to read reading bookmarks from {Path}", FilePath);
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
                // закладок всех книг пустой или недописанный файл.
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_entries, JsonOptions));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Failed to write reading bookmarks to {Path}", FilePath);
            }
        }
    }
}
