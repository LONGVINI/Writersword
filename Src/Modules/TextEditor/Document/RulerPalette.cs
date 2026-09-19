using System;
using SkiaSharp;
using Writersword.Modules.TextEditor.ViewModels.Components;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Цвета линейки. Без вида рабочей области это прежние серые тона, с видом —
    /// выведенные из бумаги и чернил.
    ///
    /// Линейка стоит вплотную к листу, и оставлять её светло-серой над ночной
    /// страницей нельзя: узкая яркая полоса у верхней кромки бьёт по глазам ровно
    /// тем, ради чего лист и перекрашивали. Собственных цветов у линейки поэтому
    /// нет — она берёт их у листа: делениям и цифрам достаётся цвет чернил
    /// разной плотности, полю вокруг страницы — заливка поля, самой странице —
    /// бумага.
    ///
    /// Маркеры отступов, столбцов и перетаскивания сюда не входят: их цвета
    /// смысловые — синий отступ, зелёный столбец, оранжевое перетаскивание, — и
    /// менять их вместе с бумагой значит терять смысл.
    /// </summary>
    internal readonly struct RulerPalette
    {
        /// <summary>Фон страницы: текстовая область.</summary>
        public SKColor Sheet { get; init; }

        /// <summary>Поле вокруг страницы.</summary>
        public SKColor OutsidePage { get; init; }

        /// <summary>Зона полей внутри страницы.</summary>
        public SKColor MarginZone { get; init; }

        public SKColor TickMajor { get; init; }
        public SKColor TickMinor { get; init; }
        public SKColor TickTiny { get; init; }

        /// <summary>Деления в зоне полей — приглушённые.</summary>
        public SKColor TickMajorMuted { get; init; }
        public SKColor TickMinorMuted { get; init; }
        public SKColor TickTinyMuted { get; init; }

        public SKColor Label { get; init; }

        /// <summary>Цифра в зоне полей.</summary>
        public SKColor LabelMuted { get; init; }

        /// <summary>Цифра отрицательной части шкалы — она за краем текста.</summary>
        public SKColor LabelNegative { get; init; }

        public SKColor Border { get; init; }
        public SKColor MarginHandle { get; init; }

        /// <summary>Позиция табуляции, поставленная человеком.</summary>
        public SKColor TabMarker { get; init; }

        /// <summary>Засечка шага табуляции по умолчанию — та же краска, но слабее.</summary>
        public SKColor TabMarkerFaint { get; init; }

        /// <summary>Прежние тона: ими линейка рисуется, пока вид не назначен.</summary>
        public static RulerPalette Default { get; } = new()
        {
            Sheet = new SKColor(0xF0, 0xF0, 0xF0),
            OutsidePage = new SKColor(0xD0, 0xD0, 0xD0),
            MarginZone = new SKColor(0xD8, 0xD8, 0xD8),
            TickMajor = new SKColor(0x60, 0x60, 0x60),
            TickMinor = new SKColor(0x99, 0x99, 0x99),
            TickTiny = new SKColor(0xBB, 0xBB, 0xBB),
            TickMajorMuted = new SKColor(0x99, 0x99, 0x99),
            TickMinorMuted = new SKColor(0xBB, 0xBB, 0xBB),
            TickTinyMuted = new SKColor(0xD0, 0xD0, 0xD0),
            Label = new SKColor(0x44, 0x44, 0x44),
            LabelMuted = new SKColor(0x88, 0x88, 0x88),
            LabelNegative = new SKColor(0x99, 0x44, 0x44),
            Border = new SKColor(0xCC, 0xCC, 0xCC),
            MarginHandle = new SKColor(0x88, 0x88, 0x88),
            TabMarker = DefaultTabMarker,
            TabMarkerFaint = DefaultTabMarker.WithAlpha(0x66)
        };

        /// <summary>Прежний бирюзовый: им табуляция рисуется, пока вид не задал свой.</summary>
        private static readonly SKColor DefaultTabMarker = new(0x0E, 0x7A, 0x7A);

        /// <summary>
        /// Палитра линейки для текущего вида. Вид не назначен — прежние тона.
        /// </summary>
        public static RulerPalette Resolve(RulerViewModel? vm)
        {
            if (vm is null || !vm.ThemeActive) return Default;

            var paper = Parse(vm.ThemeSheetHex, Default.Sheet);
            var ink = Parse(vm.ThemeInkHex, Default.Label);
            var field = Parse(vm.ThemeFieldHex, Default.OutsidePage);
            var tab = Parse(vm.ThemeTabHex, DefaultTabMarker);

            return FromTheme(paper, field, ink, tab);
        }

        private static RulerPalette FromTheme(SKColor paper, SKColor field, SKColor ink, SKColor tab)
        {
            // Зона полей отличается от текстовой заметно, но остаётся бумагой:
            // на линейке она показывает край листа, а не другой лист.
            var marginZone = Mix(paper, field, 0.42);

            return new RulerPalette
            {
                Sheet = paper,
                OutsidePage = field,
                MarginZone = marginZone,

                // Деления и цифры — чернила разной плотности. Тот же приём, что и
                // у служебных мелочей на странице: цвет один, различает их сила.
                TickMajor = ink.WithAlpha(0xC8),
                TickMinor = ink.WithAlpha(0x8C),
                TickTiny = ink.WithAlpha(0x5A),
                TickMajorMuted = ink.WithAlpha(0x8C),
                TickMinorMuted = ink.WithAlpha(0x5A),
                TickTinyMuted = ink.WithAlpha(0x3C),

                Label = ink.WithAlpha(0xDC),
                LabelMuted = ink.WithAlpha(0x8C),

                // Отрицательная часть шкалы остаётся красноватой, но берёт светлоту
                // у чернил: тёмно-красное по тёмной бумаге не читается.
                LabelNegative = Mix(ink, IsDark(paper)
                    ? new SKColor(0xE8, 0x8A, 0x8A)
                    : new SKColor(0x99, 0x44, 0x44), 0.75),

                Border = ink.WithAlpha(0x3C),
                MarginHandle = ink.WithAlpha(0x7A),

                TabMarker = tab,
                TabMarkerFaint = tab.WithAlpha(0x66)
            };
        }

        private static SKColor Parse(string? hex, SKColor fallback)
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            return SKColor.TryParse(hex, out var c) ? c : fallback;
        }

        /// <summary>Смешивает два цвета: 0 — первый, 1 — второй.</summary>
        private static SKColor Mix(SKColor a, SKColor b, double amount)
        {
            double t = Math.Clamp(amount, 0.0, 1.0);
            return new SKColor(
                (byte)Math.Round(a.Red + (b.Red - a.Red) * t),
                (byte)Math.Round(a.Green + (b.Green - a.Green) * t),
                (byte)Math.Round(a.Blue + (b.Blue - a.Blue) * t));
        }

        /// <summary>Тёмен ли цвет по воспринимаемой светлоте — порог тот же, что у видов.</summary>
        private static bool IsDark(SKColor c)
            => (0.2126 * c.Red + 0.7152 * c.Green + 0.0722 * c.Blue) / 255.0 < 0.45;
    }
}
