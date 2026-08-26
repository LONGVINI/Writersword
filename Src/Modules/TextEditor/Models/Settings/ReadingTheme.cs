using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Writersword.Modules.TextEditor.Models.Settings
{
    /// <summary>Как картинка поля ложится на экран.</summary>
    public enum ReadingBackdropFit
    {
        /// <summary>Закрыть поле целиком, лишнее обрезать. Пропорции сохраняются.</summary>
        Cover = 0,
        /// <summary>Уместить целиком, по краям останется цвет. Пропорции сохраняются.</summary>
        Contain = 1,
        /// <summary>Растянуть на всё поле, не считаясь с пропорциями.</summary>
        Stretch = 2,
        /// <summary>Замостить в исходном размере.</summary>
        Tile = 3
    }

    /// <summary>
    /// Именованный вид чтения: как выглядит книга на экране. Цвет листа и текста,
    /// картинка бумаги, шрифт, свет — всё вместе и под своим именем.
    ///
    /// Вид живёт в двух местах сразу, и это разные вещи:
    ///   в документе — уезжает вместе с рукописью, и у того, кто её откроет,
    ///                 книга будет выглядеть так же;
    ///   везде       — лежит в настройках программы и доступен во всех проектах.
    /// Одному и тому же виду можно назначить обе области: тогда он и уедет с
    /// документом, и останется под рукой в других проектах.
    ///
    /// На печать, экспорт и содержание рукописи вид не влияет никогда.
    /// </summary>
    public sealed class ReadingTheme
    {
        /// <summary>
        /// Опознаватель вида. У встроенных — устойчивая строка, у своих — Guid.
        /// По нему настройки чтения помнят, какой вид выбран.
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>Имя, которое видно в списке. Своё у каждого вида.</summary>
        public string Name { get; set; } = "Без имени";

        /// <summary>Встроенный вид: его нельзя ни переименовать, ни удалить.</summary>
        public bool IsBuiltIn { get; set; }

        // ── Бумага ────────────────────────────────────────────────────────

        /// <summary>Цвет листа (HEX).</summary>
        public string SheetColor { get; set; } = "#FBF6EC";

        /// <summary>Цвет текста, у которого нет своего (HEX).</summary>
        public string InkColor { get; set; } = "#2E2A24";

        /// <summary>Картинка бумаги. Пусто — лист заливается цветом.</summary>
        public string? ImagePath { get; set; }

        /// <summary>Плотность картинки поверх цвета листа: 0 — не видна, 1 — целиком.</summary>
        public double ImageOpacity { get; set; } = 1.0;

        /// <summary>Замостить картинку вместо растягивания на весь лист.</summary>
        public bool ImageTile { get; set; }

        // ── Поле вокруг книги ─────────────────────────────────────────────
        // Поле занимает больше места на экране, чем сама книга, и от него зависит,
        // устают ли от чтения глаза. Поэтому оно часть вида, а не общая настройка.

        /// <summary>
        /// Заливка поля. Пусто — выводится из бумаги: под светлой книгой поле темнее,
        /// под тёмной чуть светлее.
        ///
        /// Значение здесь то же самое, что и у любого другого цвета в программе: либо
        /// HEX, либо код градиента. Своих видов заливки у поля нет намеренно —
        /// градиенты уже есть у цветов, и поле просто принимает их как есть.
        /// </summary>
        public string? BackdropColor { get; set; }

        /// <summary>Класть на поле картинку поверх заливки.</summary>
        public bool UseBackdropImage { get; set; }

        /// <summary>Картинка поля.</summary>
        public string? BackdropImagePath { get; set; }

        /// <summary>Как картинка поля ложится на экран.</summary>
        public ReadingBackdropFit BackdropImageFit { get; set; } = ReadingBackdropFit.Cover;

        /// <summary>Плотность картинки поля: 0 — не видна, 1 — целиком.</summary>
        public double BackdropImageOpacity { get; set; } = 1.0;

        // ── Текст ─────────────────────────────────────────────────────────

        /// <summary>
        /// Шрифт вида. Пусто — как в документе. Задавать не обязательно: вид
        /// может менять только цвета, а начертание оставлять авторским.
        /// </summary>
        public string? FontFamily { get; set; }

        // ── Свет ──────────────────────────────────────────────────────────

        /// <summary>Яркость листа: 1 — как есть, меньше — приглушённее.</summary>
        public double Brightness { get; set; } = 1.0;

        /// <summary>Контрастность: насколько текст расходится с бумагой.</summary>
        public double Contrast { get; set; } = 1.0;

        /// <summary>Тёплота: доля янтарной вуали поверх страницы.</summary>
        public double Warmth { get; set; }

        // ── Область хранения ──────────────────────────────────────────────

        /// <summary>
        /// Вид лежит в документе и уедет вместе с ним. Поле служебное: где вид
        /// хранится, определяет не оно, а то, в каком списке он оказался. Здесь
        /// оно нужно окну настройки, чтобы показать состояние переключателя.
        /// </summary>
        [JsonIgnore]
        public bool InDocument { get; set; }

        /// <summary>Вид лежит в настройках программы и доступен во всех проектах.</summary>
        [JsonIgnore]
        public bool IsGlobal { get; set; }

        /// <summary>Тёмный ли лист. По нему решается вид служебных мелочей.</summary>
        [JsonIgnore]
        public bool IsDark => IsDarkHex(SheetColor);

        public ReadingTheme Clone() => new()
        {
            Id = Id,
            Name = Name,
            IsBuiltIn = IsBuiltIn,
            SheetColor = SheetColor,
            InkColor = InkColor,
            ImagePath = ImagePath,
            ImageOpacity = ImageOpacity,
            ImageTile = ImageTile,
            BackdropColor = BackdropColor,
            UseBackdropImage = UseBackdropImage,
            BackdropImagePath = BackdropImagePath,
            BackdropImageFit = BackdropImageFit,
            BackdropImageOpacity = BackdropImageOpacity,
            FontFamily = FontFamily,
            Brightness = Brightness,
            Contrast = Contrast,
            Warmth = Warmth,
            InDocument = InDocument,
            IsGlobal = IsGlobal
        };

        /// <summary>
        /// Выглядят ли два вида одинаково. Сравнивается всё, что видно на экране, и
        /// ничего сверх того: имя, опознаватель и область хранения к внешности вида
        /// отношения не имеют.
        ///
        /// По этому и решается, показывать ли в списке имя вида или «Кастомное»:
        /// рабочая копия правится лентой на ходу, и стоит ей разойтись с сохранённым
        /// видом — на экране уже не он.
        /// </summary>
        public static bool SameLook(ReadingTheme? a, ReadingTheme? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a is null || b is null) return false;

            const StringComparison Ci = StringComparison.OrdinalIgnoreCase;
            const StringComparison Cs = StringComparison.Ordinal;

            static bool Same(string? x, string? y, StringComparison how)
                => string.Equals(x ?? string.Empty, y ?? string.Empty, how);

            static bool Near(double x, double y) => Math.Abs(x - y) < 0.0005;

            return Same(a.SheetColor, b.SheetColor, Ci)
                && Same(a.InkColor, b.InkColor, Ci)
                && Same(a.ImagePath, b.ImagePath, Cs)
                && Near(a.ImageOpacity, b.ImageOpacity)
                && a.ImageTile == b.ImageTile
                && Same(a.BackdropColor, b.BackdropColor, Ci)
                && a.UseBackdropImage == b.UseBackdropImage
                && Same(a.BackdropImagePath, b.BackdropImagePath, Cs)
                && a.BackdropImageFit == b.BackdropImageFit
                && Near(a.BackdropImageOpacity, b.BackdropImageOpacity)
                && Same(a.FontFamily, b.FontFamily, Cs)
                && Near(a.Brightness, b.Brightness)
                && Near(a.Contrast, b.Contrast)
                && Near(a.Warmth, b.Warmth);
        }

        /// <summary>Копия под новым именем и с новым опознавателем.</summary>
        public ReadingTheme CopyAs(string name)
        {
            var copy = Clone();
            copy.Id = Guid.NewGuid().ToString("N");
            copy.Name = name;
            copy.IsBuiltIn = false;
            return copy;
        }

        /// <summary>Тёмный ли цвет по HEX. Порог по воспринимаемой светлоте.</summary>
        public static bool IsDarkHex(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return false;
            string s = hex.TrimStart('#');
            if (s.Length == 8) s = s.Substring(2);
            if (s.Length != 6) return false;

            const System.Globalization.NumberStyles Hex = System.Globalization.NumberStyles.HexNumber;
            if (!int.TryParse(s.Substring(0, 2), Hex, null, out int r)) return false;
            if (!int.TryParse(s.Substring(2, 2), Hex, null, out int g)) return false;
            if (!int.TryParse(s.Substring(4, 2), Hex, null, out int b)) return false;

            return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0 < 0.45;
        }

        // ── Встроенные виды ───────────────────────────────────────────────

        public const string WhiteId = "builtin.white";
        public const string CreamId = "builtin.cream";
        public const string SepiaId = "builtin.sepia";
        public const string MistId = "builtin.mist";
        public const string NightId = "builtin.night";
        public const string InkId = "builtin.ink";

        /// <summary>Виды, которые есть всегда и у всех.</summary>
        public static IReadOnlyList<ReadingTheme> BuiltIn { get; } = new[]
        {
            Make(WhiteId, "Белая",     "#FFFFFF", "#1A1A1A"),
            Make(CreamId, "Кремовая",  "#FBF6EC", "#2E2A24"),
            Make(SepiaId, "Сепия",     "#F1E7D3", "#4A3B2A"),
            Make(MistId,  "Пасмурная", "#DEE2E6", "#23282C"),
            Make(NightId, "Ночная",    "#292C31", "#DCD8D0"),
            Make(InkId,   "Чёрная",    "#141619", "#E4E1DA")
        };

        private static ReadingTheme Make(string id, string name, string sheet, string ink) => new()
        {
            Id = id,
            Name = name,
            IsBuiltIn = true,
            SheetColor = sheet,
            InkColor = ink
        };

        /// <summary>Встроенный вид по опознавателю. Не нашёлся — кремовая.</summary>
        public static ReadingTheme FindBuiltIn(string? id)
        {
            foreach (var t in BuiltIn)
                if (string.Equals(t.Id, id, StringComparison.Ordinal)) return t;

            foreach (var t in BuiltIn)
                if (t.Id == CreamId) return t;

            return BuiltIn[0];
        }

        public static bool IsBuiltInId(string? id)
            => !string.IsNullOrEmpty(id) && id!.StartsWith("builtin.", StringComparison.Ordinal);
    }
}
