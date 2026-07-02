using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Writersword.Core.Models.Project
{
    // Тип градиента. Линейный идёт вдоль линии под заданным углом, радиальный
    // расходится из центра кругами, конический поворачивается вокруг центра.
    public enum GradientKind
    {
        Linear,
        Radial,
        Conic
    }

    // Режим заливки именно букв текста градиентом. Block тянет один градиент на
    // прямоугольник всего абзаца, PerLine перезапускает градиент на каждой строке.
    // Для задников и карточек значения не имеет.
    public enum GradientTextFill
    {
        Block,
        PerLine
    }

    // Одна точка цвета на полосе градиента: позиция вдоль полосы (0..1) и цвет.
    public readonly struct GradientColorStop
    {
        public double Position { get; }
        public string Hex { get; }

        public GradientColorStop(double position, string hex)
        {
            Position = GradientSpec.Clamp01(position);
            Hex = GradientSpec.NormalizeHex(hex);
        }

        public GradientColorStop WithPosition(double position) => new GradientColorStop(position, Hex);
        public GradientColorStop WithHex(string hex) => new GradientColorStop(Position, hex);
    }

    // Универсальное описание цвета: даже одиночный цвет — это градиент из одного
    // стопа. Сериализуется в строку, совместимую со старым форматом: одноцвет
    // пишется как обычный hex, многоцветный — кодом с префиксом "grad|".
    public sealed class GradientSpec
    {
        private const string Prefix = "grad|";

        public GradientKind Kind { get; set; } = GradientKind.Linear;

        // Для линейного — направление полосы в градусах (0 — слева направо,
        // 90 — снизу вверх). Для конического — стартовый угол. Радиальный игнорирует.
        public double AngleDeg { get; set; } = 0;

        public GradientTextFill TextFill { get; set; } = GradientTextFill.Block;

        public List<GradientColorStop> Stops { get; set; } = new();

        // Считается одноцветным, если стопов нет/один, либо все цвета совпадают.
        // Такой спек сериализуется обратно в чистый hex для совместимости.
        public bool IsSolid
        {
            get
            {
                if (Stops.Count <= 1) return true;
                var first = Stops[0].Hex;
                return Stops.All(s => string.Equals(s.Hex, first, StringComparison.OrdinalIgnoreCase));
            }
        }

        public string SolidHex => Stops.Count > 0 ? Stops[0].Hex : "#000000";

        // Стопы по возрастанию позиции — в таком порядке их ждут шейдеры и кисти.
        public IReadOnlyList<GradientColorStop> SortedStops()
        {
            var list = new List<GradientColorStop>(Stops);
            list.Sort((a, b) => a.Position.CompareTo(b.Position));
            return list;
        }

        public static GradientSpec Solid(string hex) => new GradientSpec
        {
            Kind = GradientKind.Linear,
            AngleDeg = 0,
            TextFill = GradientTextFill.Block,
            Stops = new List<GradientColorStop> { new GradientColorStop(0, hex) }
        };

        // Разбор строки из конфига. Пустая строка и любой обычный hex дают
        // одноцветный спек, код "grad|..." — полноценный градиент.
        public static GradientSpec Parse(string? code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return Solid("#000000");

            var text = code.Trim();
            if (!text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                return Solid(text);

            var body = text.Substring(Prefix.Length);
            var parts = body.Split('|');
            if (parts.Length < 4)
                return Solid("#000000");

            var spec = new GradientSpec
            {
                Kind = ParseKind(parts[0]),
                AngleDeg = ParseDouble(parts[1], 0),
                TextFill = ParseFill(parts[2])
            };

            foreach (var token in parts[3].Split(';'))
            {
                if (string.IsNullOrWhiteSpace(token)) continue;
                var sep = token.IndexOf(':');
                if (sep <= 0) continue;
                var pos = ParseDouble(token.Substring(0, sep), 0);
                var hex = token.Substring(sep + 1);
                spec.Stops.Add(new GradientColorStop(pos, hex));
            }

            if (spec.Stops.Count == 0)
                spec.Stops.Add(new GradientColorStop(0, "#000000"));

            return spec;
        }

        // Обратная сериализация. Одноцвет пишется как hex (чтобы конфиг оставался
        // читаемым и совместимым), иначе — компактный код.
        public string ToCode()
        {
            if (IsSolid)
                return SolidHex;

            var sb = new StringBuilder();
            sb.Append(Prefix);
            sb.Append(KindToken(Kind)).Append('|');
            sb.Append(AngleDeg.ToString("0.###", CultureInfo.InvariantCulture)).Append('|');
            sb.Append(FillToken(TextFill)).Append('|');

            var sorted = SortedStops();
            for (int i = 0; i < sorted.Count; i++)
            {
                if (i > 0) sb.Append(';');
                sb.Append(sorted[i].Position.ToString("0.###", CultureInfo.InvariantCulture));
                sb.Append(':');
                sb.Append(sorted[i].Hex);
            }

            return sb.ToString();
        }

        public override string ToString() => ToCode();

        internal static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        // Приводит цвет к виду с ведущей решёткой, не теряя альфу. Пустое — чёрный.
        internal static string NormalizeHex(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return "#000000";
            var h = hex.Trim();
            if (!h.StartsWith("#")) h = "#" + h;
            return h;
        }

        private static GradientKind ParseKind(string s) => s.Trim().ToLowerInvariant() switch
        {
            "rad" => GradientKind.Radial,
            "con" => GradientKind.Conic,
            _ => GradientKind.Linear
        };

        private static string KindToken(GradientKind k) => k switch
        {
            GradientKind.Radial => "rad",
            GradientKind.Conic => "con",
            _ => "lin"
        };

        private static GradientTextFill ParseFill(string s) =>
            s.Trim().ToLowerInvariant() == "line" ? GradientTextFill.PerLine : GradientTextFill.Block;

        private static string FillToken(GradientTextFill f) =>
            f == GradientTextFill.PerLine ? "line" : "blk";

        private static double ParseDouble(string s, double fallback) =>
            double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }
}
