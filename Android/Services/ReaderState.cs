using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using Writersword.Modules.TextEditor.Models.Settings;

namespace Writersword.Mobile.Services
{
    /// <summary>
    /// Что читалка помнит между запусками.
    ///
    /// Здесь два разных набора, и лежат они вместе только потому, что живут по
    /// одним правилам. Настройки чтения — подача, лист, вид, кегль, свет — это
    /// личное дело читателя: они одни на все книги и в проект не уезжают, иначе
    /// у получателя рукопись открылась бы с чужими глазами. Позиция чтения своя
    /// у каждой книги.
    ///
    /// Лежит в данных приложения, рядом с настройками подключения. Сбой чтения
    /// или записи ничего не роняет: в худшем случае книга откроется с начала и с
    /// настройками по умолчанию. Удобство не имеет права мешать читать.
    /// </summary>
    public static class ReaderState
    {
        private static readonly object _lock = new();

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "reader.json");

        private sealed class State
        {
            [JsonProperty("flow")] public ReadingFlow Flow { get; set; } = ReadingFlow.Column;
            [JsonProperty("format")] public ReadingSheetFormat Format { get; set; } = ReadingSheetFormat.Pocket;
            [JsonProperty("themeId")] public string ThemeId { get; set; } = ReadingTheme.CreamId;

            [JsonProperty("fontFamily")] public string? FontFamily { get; set; }
            [JsonProperty("fontStep")] public int FontStep { get; set; }
            [JsonProperty("zoom")] public double Zoom { get; set; } = 1.0;

            [JsonProperty("brightness")] public double Brightness { get; set; } = 1.0;
            [JsonProperty("contrast")] public double Contrast { get; set; } = 1.0;
            [JsonProperty("warmth")] public double Warmth { get; set; }

            [JsonProperty("showPageNumbers")] public bool ShowPageNumbers { get; set; } = true;
            [JsonProperty("scaleContent")] public bool ScaleContent { get; set; } = true;

            /// <summary>Имя книги — доля прочитанного, от нуля до единицы.</summary>
            [JsonProperty("positions")] public Dictionary<string, double> Positions { get; set; } = new();
        }

        private static State? _state;

        // ── Настройки чтения ──────────────────────────────────────────────

        /// <summary>
        /// Наложить запомненное на настройки открываемой книги.
        ///
        /// Зовётся до первой раскладки: смена подачи или кегля после неё стоит
        /// полного прохода пагинации, а книга при этом на мгновение показывается
        /// не такой, какой её оставили.
        /// </summary>
        public static void Apply(ReadingSettings settings)
        {
            if (settings is null) return;

            lock (_lock)
            {
                EnsureLoaded();
                var s = _state!;

                settings.Flow = s.Flow;
                settings.Format = s.Format;
                settings.FontStep = s.FontStep;
                settings.Zoom = s.Zoom;
                settings.ShowPageNumbers = s.ShowPageNumbers;
                settings.ScaleContent = s.ScaleContent;

                // Вид ставится целиком, а правки поверх него накладываются следом:
                // шрифт и свет живут в виде, и взять их без вида не из чего.
                settings.ApplyTheme(ReadingTheme.FindBuiltIn(s.ThemeId));

                if (settings.Active is { } active)
                {
                    active.FontFamily = s.FontFamily;
                    active.Brightness = s.Brightness;
                    active.Contrast = s.Contrast;
                    active.Warmth = s.Warmth;
                }
            }
        }

        /// <summary>Запомнить нынешние настройки чтения.</summary>
        public static void Save(ReadingSettings? settings)
        {
            if (settings is null) return;

            lock (_lock)
            {
                EnsureLoaded();
                var s = _state!;

                s.Flow = settings.Flow;
                s.Format = settings.Format;
                s.FontStep = settings.FontStep;
                s.Zoom = settings.Zoom;
                s.ShowPageNumbers = settings.ShowPageNumbers;
                s.ScaleContent = settings.ScaleContent;
                s.ThemeId = settings.ThemeId;

                if (settings.Active is { } active)
                {
                    s.FontFamily = active.FontFamily;
                    s.Brightness = active.Brightness;
                    s.Contrast = active.Contrast;
                    s.Warmth = active.Warmth;
                }

                Write();
            }
        }

        // ── Позиция чтения ────────────────────────────────────────────────

        /// <summary>
        /// Доля прочитанного, а не номер страницы.
        ///
        /// Номер страницы годился бы, не меняйся он от кегля и подачи: прибавил
        /// букв — и запомненная сто двадцатая страница указывает уже в другое
        /// место книги. Доля переживает и то, и другое, но точна лишь примерно —
        /// возвращает на разворот, а не на строку. Для «продолжить с того же
        /// места» этого достаточно, и честнее обещать именно столько.
        /// </summary>
        public static double PositionOf(string book)
        {
            if (string.IsNullOrWhiteSpace(book)) return 0.0;

            lock (_lock)
            {
                EnsureLoaded();
                return _state!.Positions.TryGetValue(book, out var value)
                    ? Math.Clamp(value, 0.0, 1.0)
                    : 0.0;
            }
        }

        public static void SavePosition(string book, double position)
        {
            if (string.IsNullOrWhiteSpace(book)) return;

            lock (_lock)
            {
                EnsureLoaded();

                double clamped = Math.Clamp(position, 0.0, 1.0);

                if (_state!.Positions.TryGetValue(book, out var known)
                    && Math.Abs(known - clamped) < 0.0005)
                {
                    return;
                }

                _state.Positions[book] = clamped;
                Write();
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

                var json = File.ReadAllText(FilePath);
                if (string.IsNullOrWhiteSpace(json)) return;

                var loaded = JsonConvert.DeserializeObject<State>(json);
                if (loaded is null) return;

                loaded.Positions ??= new Dictionary<string, double>();
                _state = loaded;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Failed to read reader state from {Path}", FilePath);
            }
        }

        private static void Write()
        {
            try
            {
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(_state, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Failed to write reader state to {Path}", FilePath);
            }
        }
    }
}
