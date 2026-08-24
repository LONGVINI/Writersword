using System;
using System.Collections.Concurrent;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Writersword.Core.Models.Project;

namespace Writersword.Infrastructure.Converters
{
    // Строит Avalonia-кисть из универсального описания цвета. Одноцвет даёт
    // SolidColorBrush, многоцветный — линейную/радиальную/коническую кисть.
    // Применяется для карточек персонажа, превью-образцов и любого UI.
    public static class GradientBrushFactory
    {
        public static IBrush ToBrush(GradientSpec? spec)
        {
            if (spec == null)
                return new SolidColorBrush(Colors.Black);

            if (spec.IsSolid)
                return new SolidColorBrush(ParseColor(spec.SolidHex));

            var stops = BuildStops(spec);

            switch (spec.Kind)
            {
                case GradientKind.Radial:
                    return new RadialGradientBrush
                    {
                        Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                        GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                        RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
                        RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
                        GradientStops = stops
                    };

                case GradientKind.Conic:
                    return new ConicGradientBrush
                    {
                        Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                        Angle = spec.AngleDeg,
                        GradientStops = stops
                    };

                default:
                    var (start, end) = AngleToPoints(spec.AngleDeg);
                    return new LinearGradientBrush
                    {
                        StartPoint = start,
                        EndPoint = end,
                        GradientStops = stops
                    };
            }
        }

        public static IBrush FromCode(string? code) => ToBrush(GradientSpec.Parse(code));

        private static GradientStops BuildStops(GradientSpec spec)
        {
            var result = new GradientStops();
            foreach (var s in spec.SortedStops())
                result.Add(new Avalonia.Media.GradientStop(ParseColor(s.Hex), s.Position));
            return result;
        }

        private static Color ParseColor(string hex)
            => Color.TryParse(hex, out var c) ? c : Colors.Black;

        // Угол в точки на единичном квадрате: 0 — слева направо, 90 — снизу вверх
        // (ось Y направлена вниз, поэтому верх — это меньшее значение Y).
        private static (RelativePoint start, RelativePoint end) AngleToPoints(double deg)
        {
            var rad = deg * Math.PI / 180.0;
            var cos = Math.Cos(rad);
            var sin = Math.Sin(rad);

            var start = new RelativePoint(0.5 - 0.5 * cos, 0.5 + 0.5 * sin, RelativeUnit.Relative);
            var end = new RelativePoint(0.5 + 0.5 * cos, 0.5 - 0.5 * sin, RelativeUnit.Relative);
            return (start, end);
        }
    }

    // Конвертер для XAML: строка-код цвета/градиента -> IBrush. Удобно вешать на
    // Background/Fill образцов и карточек.
    public sealed class ColorCodeToBrushConverter : IValueConverter
    {
        public static readonly ColorCodeToBrushConverter Instance = new();

        // Карточки персонажей вешают этот конвертер сразу на несколько своих
        // свойств (рамка, закладка, аватар, кольцо), а сам список виртуализирован —
        // контейнеры пересоздаются и перепривязываются на каждый скролл и на
        // каждое перетаскивание карточки. Без кэша это был разбор строки
        // цвета/градиента и аллокация нового IBrush на каждую такую перепривязку,
        // на каждое из 7 свойств каждой видимой карточки. Кэш по строке кода
        // убирает повторный разбор для повторяющихся значений — сам разбор и
        // результат не меняются, один и тот же Brush просто переиспользуется.
        private static readonly ConcurrentDictionary<string, IBrush> Cache = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var code = value as string ?? string.Empty;
            return Cache.GetOrAdd(code, GradientBrushFactory.FromCode);
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Код цвета означает «без цвета»: пустая строка или полностью прозрачный цвет.
    /// Нужен кнопке пикера — прозрачную заливку на кружке не отличить от «ничего
    /// не выбрано», и поверх неё рисуется перечёркивание.
    /// </summary>
    public sealed class IsNoColorCodeConverter : IValueConverter
    {
        public static readonly IsNoColorCodeConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var code = value as string;
            if (string.IsNullOrWhiteSpace(code)) return true;

            code = code.Trim();

            // #00000000 и любой другой код с нулевой альфой: первые два разряда после
            // решётки — прозрачность, и ноль в них означает полностью прозрачный цвет.
            if (code.Length == 9 && code[0] == '#')
                return code[1] == '0' && code[2] == '0';

            return false;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
