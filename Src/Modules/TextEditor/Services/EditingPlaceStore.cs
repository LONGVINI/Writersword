using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Место в рукописи, где остановился автор: абзац, символ каретки и прокрутка.
    ///
    /// Прежде место жило только в сессии вида, а сессия лежит в кеше проекта. Кеш
    /// пишется лишь при правках и удаляется при сохранении и при закрытии программы,
    /// если совпадает с файлом. Прокрутка и движение каретки правкой не считаются,
    /// поэтому место не доживало до следующего запуска: рукопись открывалась в начале.
    /// Та же беда была у чтения — её решает <see cref="ReadingBookmarkStore"/>, здесь
    /// то же решение для правки.
    ///
    /// Место — не часть рукописи: в документ и в файл проекта оно не попадает,
    /// правкой не считается и сохранения не требует. Лежит в данных программы, по
    /// записи на рукопись.
    ///
    /// Вместе с прокруткой хранятся масштаб и режим, при которых она снята: прокрутка
    /// — это точки экрана, и при другом масштабе её пересчитывают, а при другом
    /// режиме (черновик вместо страниц) она ничего не значит, и место ставится по
    /// каретке. Сбой чтения или записи ничего не роняет — в худшем случае рукопись
    /// откроется в начале, как раньше.
    /// </summary>
    public static class EditingPlaceStore
    {
        /// <summary>Место в рукописи.</summary>
        public readonly record struct Place(
            int Paragraph, int Char, double ScrollY, double Zoom, string? ViewMode);

        private sealed class Entry
        {
            [JsonPropertyName("para")] public int Paragraph { get; set; }
            [JsonPropertyName("ch")] public int Char { get; set; }
            [JsonPropertyName("scroll")] public double ScrollY { get; set; }
            [JsonPropertyName("zoom")] public double Zoom { get; set; }
            [JsonPropertyName("viewMode")] public string? ViewMode { get; set; }
            [JsonPropertyName("updated")] public DateTime UpdatedUtc { get; set; }
        }

        private static readonly object _lock = new();

        private static Dictionary<string, Entry>? _entries;

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Writersword",
            "editing-places.json");

        /// <summary>Место в рукописи или null, если её здесь ещё не открывали.</summary>
        public static Place? Get(string document)
        {
            if (string.IsNullOrWhiteSpace(document)) return null;

            lock (_lock)
            {
                EnsureLoaded();
                if (!_entries!.TryGetValue(document, out var e)) return null;

                return new Place(
                    Math.Max(0, e.Paragraph),
                    Math.Max(0, e.Char),
                    Math.Max(0, e.ScrollY),
                    e.Zoom,
                    e.ViewMode);
            }
        }

        /// <summary>
        /// Запоминает место. Повтор того же места файл не трогает: сообщения о смене
        /// места приходят после каждой остановки прокрутки, и большая часть их —
        /// то же самое место.
        /// </summary>
        public static void Save(string document, Place place)
        {
            if (string.IsNullOrWhiteSpace(document) || place.Paragraph < 0) return;

            double scroll = Math.Max(0, place.ScrollY);

            lock (_lock)
            {
                EnsureLoaded();

                if (_entries!.TryGetValue(document, out var known)
                    && known.Paragraph == place.Paragraph
                    && known.Char == place.Char
                    && Math.Abs(known.ScrollY - scroll) < 0.5
                    && Math.Abs(known.Zoom - place.Zoom) < 0.0001
                    && string.Equals(known.ViewMode, place.ViewMode, StringComparison.Ordinal))
                {
                    return;
                }

                _entries[document] = new Entry
                {
                    Paragraph = place.Paragraph,
                    Char = Math.Max(0, place.Char),
                    ScrollY = scroll,
                    Zoom = place.Zoom,
                    ViewMode = place.ViewMode,
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
                Serilog.Log.Warning(ex, "Failed to read editing places from {Path}", FilePath);
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
                // мест всех рукописей пустой или недописанный файл.
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_entries, JsonOptions));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Failed to write editing places to {Path}", FilePath);
            }
        }
    }
}
