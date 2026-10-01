using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Writersword.Modules.Characters.Services
{
    /// <summary>
    /// Черновики редактора анкет: какие листы открыты, какой из них активный
    /// и несохранённое состояние каждого листа.
    ///
    /// Анкета попадает в проект только по «Сохранить». До этого правки живут
    /// здесь — в данных программы, по записи на проект, — и переживают
    /// закрытие программы и выключение компьютера: при следующем открытии
    /// проекта листы возвращаются такими, какими их оставили, с отметкой
    /// несохранённого.
    ///
    /// Сбой чтения или записи ничего не роняет: в худшем случае листы
    /// откроются пустыми, а сохранённые анкеты проекта не затрагиваются.
    /// </summary>
    public static class AnketaDraftStore
    {
        /// <summary>Лист редактора.</summary>
        public sealed class SheetEntry
        {
            [JsonPropertyName("anketa")] public string AnketaId { get; set; } = string.Empty;

            /// <summary>Анкета ещё ни разу не сохранялась: в проекте её нет, есть только черновик.</summary>
            [JsonPropertyName("new")] public bool IsNew { get; set; }

            /// <summary>Несохранённое состояние листа; null — лист совпадает с сохранённой анкетой.</summary>
            [JsonPropertyName("draft")] public string? Draft { get; set; }
        }

        /// <summary>Редактор анкет одного проекта.</summary>
        public sealed class State
        {
            [JsonPropertyName("active")] public string? ActiveId { get; set; }
            [JsonPropertyName("sheets")] public List<SheetEntry> Sheets { get; set; } = new();
            [JsonPropertyName("updated")] public DateTime UpdatedUtc { get; set; }
        }

        private static readonly object _lock = new();

        private static Dictionary<string, State>? _entries;

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Writersword",
            "anketa-drafts.json");

        /// <summary>Листы проекта или null, если редактор в нём ещё не открывали.</summary>
        public static State? Get(string project)
        {
            if (string.IsNullOrWhiteSpace(project)) return null;

            lock (_lock)
            {
                EnsureLoaded();
                return _entries!.TryGetValue(project, out var state) ? Copy(state) : null;
            }
        }

        /// <summary>
        /// Запоминает листы проекта. Пустой редактор без листов убирает запись
        /// проекта совсем. Повтор того же состояния файл не трогает.
        /// </summary>
        public static void Save(string project, State state)
        {
            if (string.IsNullOrWhiteSpace(project) || state == null) return;

            lock (_lock)
            {
                EnsureLoaded();

                if (state.Sheets.Count == 0)
                {
                    if (_entries!.Remove(project)) Write();
                    return;
                }

                if (_entries!.TryGetValue(project, out var known) && SameAs(known, state)) return;

                var copy = Copy(state);
                copy.UpdatedUtc = DateTime.UtcNow;
                _entries[project] = copy;

                Write();
            }
        }

        private static bool SameAs(State a, State b)
        {
            if (!string.Equals(a.ActiveId, b.ActiveId, StringComparison.Ordinal)) return false;
            if (a.Sheets.Count != b.Sheets.Count) return false;

            for (int i = 0; i < a.Sheets.Count; i++)
            {
                var x = a.Sheets[i];
                var y = b.Sheets[i];
                if (!string.Equals(x.AnketaId, y.AnketaId, StringComparison.Ordinal)) return false;
                if (x.IsNew != y.IsNew) return false;
                if (!string.Equals(x.Draft, y.Draft, StringComparison.Ordinal)) return false;
            }

            return true;
        }

        private static State Copy(State state) => new()
        {
            ActiveId = state.ActiveId,
            UpdatedUtc = state.UpdatedUtc,
            Sheets = state.Sheets.ConvertAll(s => new SheetEntry
            {
                AnketaId = s.AnketaId,
                IsNew = s.IsNew,
                Draft = s.Draft
            })
        };

        private static void EnsureLoaded()
        {
            if (_entries is not null) return;

            _entries = new Dictionary<string, State>(StringComparer.Ordinal);

            try
            {
                var path = FilePath;
                if (!File.Exists(path)) return;

                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return;

                var loaded = JsonSerializer.Deserialize<Dictionary<string, State>>(json, JsonOptions);
                if (loaded is null) return;

                _entries = new Dictionary<string, State>(loaded, StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Failed to read anketa drafts from {Path}", FilePath);
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
                // черновиков всех проектов пустой или недописанный файл.
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_entries, JsonOptions));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Failed to write anketa drafts to {Path}", FilePath);
            }
        }
    }
}
