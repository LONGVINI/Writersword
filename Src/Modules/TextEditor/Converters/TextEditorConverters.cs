using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Writersword.Core.Models.Print;
using Writersword.Modules.TextEditor.Models.Page;

// TextAlignment намеренно не импортируется через using — конфликт с Avalonia.Media.TextAlignment.
// Используем полные имена: Models.Styles.TextAlignment и Avalonia.Media.TextAlignment.

namespace Writersword.Modules.TextEditor.Converters
{
    /// <summary>
    /// Преобразует строку цвета в формате #RRGGBB или #AARRGGBB
    /// в <see cref="SolidColorBrush"/> для привязки к Background/Foreground.
    /// Возвращает прозрачную кисть при некорректной строке.
    /// </summary>
    public sealed class HexColorConverter : IValueConverter
    {
        public static readonly HexColorConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is string hex && !string.IsNullOrWhiteSpace(hex))
            {
                try { return new SolidColorBrush(Color.Parse(hex)); }
                catch { }
            }
            return new SolidColorBrush(Colors.Transparent);
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is SolidColorBrush brush) return brush.Color.ToString();
            return "#00000000";
        }
    }

    /// <summary>
    /// Преобразует <see cref="TextEditorPageSettings"/> в ширину листа в пикселях.
    /// 1 мм = 96/25.4 пикселей (96 dpi).
    /// </summary>
    public sealed class PageWidthConverter : IValueConverter
    {
        public static readonly PageWidthConverter Instance = new();

        private const double MmToPx = 96.0 / 25.4;

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is TextEditorPageSettings settings)
            {
                double widthMm = settings.Orientation == PageOrientation.Landscape
                    ? settings.HeightMm
                    : settings.WidthMm;
                return widthMm * MmToPx;
            }
            return 210 * MmToPx;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => AvaloniaProperty.UnsetValue;
    }

    /// <summary>
    /// Преобразует <see cref="TextEditorPageSettings"/> в <see cref="Thickness"/> полей листа в пикселях.
    /// </summary>
    public sealed class PageMarginsConverter : IValueConverter
    {
        public static readonly PageMarginsConverter Instance = new();

        private const double MmToPx = 96.0 / 25.4;

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is TextEditorPageSettings settings)
            {
                double left = (settings.MarginLeftMm + settings.MarginGutterMm) * MmToPx;
                double right = settings.MarginRightMm * MmToPx;
                double top = settings.MarginTopMm * MmToPx;
                double bottom = settings.MarginBottomMm * MmToPx;
                return new Thickness(left, top, right, bottom);
            }
            return new Thickness(30 * MmToPx, 25 * MmToPx, 15 * MmToPx, 25 * MmToPx);
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => AvaloniaProperty.UnsetValue;
    }

    /// <summary>
    /// Преобразует <see cref="Models.Document.EditorViewMode"/> в bool.
    /// True если режим Page — используется для IsVisible листа.
    /// </summary>
    public sealed class ViewModePageConverter : IValueConverter
    {
        public static readonly ViewModePageConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is Models.Document.EditorViewMode mode && mode == Models.Document.EditorViewMode.Page;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is true ? Models.Document.EditorViewMode.Page : Models.Document.EditorViewMode.Draft;
    }

    /// <summary>
    /// Преобразует <see cref="Models.Document.EditorViewMode"/> в bool.
    /// True если режим Draft — используется для IsVisible черновика.
    /// </summary>
    public sealed class ViewModeDraftConverter : IValueConverter
    {
        public static readonly ViewModeDraftConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is Models.Document.EditorViewMode mode && mode == Models.Document.EditorViewMode.Draft;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is true ? Models.Document.EditorViewMode.Draft : Models.Document.EditorViewMode.Page;
    }

    /// <summary>
    /// Преобразует <see cref="Models.Styles.ParagraphProperties"/> в <see cref="Thickness"/> отступов абзаца.
    /// Учитывает левый, правый отступ и межабзацные интервалы.
    /// </summary>
    public sealed class ParagraphPaddingConverter : IValueConverter
    {
        public static readonly ParagraphPaddingConverter Instance = new();

        private const double PtToPx = 96.0 / 72.0;

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is Models.Styles.ParagraphProperties props)
            {
                double left = (props.LeftIndent ?? 0) * PtToPx;
                double right = (props.RightIndent ?? 0) * PtToPx;
                double top = (props.SpaceBefore ?? 0) * PtToPx;
                double bottom = (props.SpaceAfter ?? 8) * PtToPx;
                return new Thickness(left, top, right, bottom);
            }
            return new Thickness(0, 0, 0, 8);
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => AvaloniaProperty.UnsetValue;
    }

    /// <summary>
    /// Преобразует <see cref="Models.Styles.TextAlignment"/> (модельный enum)
    /// в <see cref="Avalonia.Media.TextAlignment"/> для TextBlock.
    /// Полные имена обязательны — оба пространства имён содержат TextAlignment.
    /// </summary>
    public sealed class TextAlignmentConverter : IValueConverter
    {
        public static readonly TextAlignmentConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is Models.Styles.TextAlignment alignment)
            {
                return alignment switch
                {
                    Models.Styles.TextAlignment.Left => TextAlignment.Left,
                    Models.Styles.TextAlignment.Center => TextAlignment.Center,
                    Models.Styles.TextAlignment.Right => TextAlignment.Right,
                    Models.Styles.TextAlignment.Justify => TextAlignment.Justify,
                    _ => TextAlignment.Left
                };
            }
            return Avalonia.Media.TextAlignment.Left;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is TextAlignment alignment)
            {
                return alignment switch
                {
                    TextAlignment.Left => Models.Styles.TextAlignment.Left,
                    TextAlignment.Center => Models.Styles.TextAlignment.Center,
                    TextAlignment.Right => Models.Styles.TextAlignment.Right,
                    TextAlignment.Justify => Models.Styles.TextAlignment.Justify,
                    _ => Models.Styles.TextAlignment.Left
                };
            }
            return Models.Styles.TextAlignment.Left;
        }
    }

    /// <summary>
    /// Преобразует масштаб (0.25–5.0) в проценты для слайдера статус-бара (25–500).
    /// </summary>
    public sealed class ZoomPercentConverter : IValueConverter
    {
        public static readonly ZoomPercentConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is double zoom) return zoom * 100.0;
            return 100.0;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is double percent) return percent / 100.0;
            return 1.0;
        }
    }

    /// <summary>
    /// Преобразует nullable bool в <see cref="FontStyle"/>.
    /// True → Italic, остальное → Normal.
    /// </summary>
    public sealed class BoolToFontStyleConverter : IValueConverter
    {
        public static readonly BoolToFontStyleConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is true ? FontStyle.Italic : FontStyle.Normal;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is FontStyle.Italic;
    }

    /// <summary>
    /// Преобразует nullable bool в <see cref="FontWeight"/>.
    /// True → Bold, остальное → Normal.
    /// </summary>
    public sealed class BoolToFontWeightConverter : IValueConverter
    {
        public static readonly BoolToFontWeightConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is true ? FontWeight.Bold : FontWeight.Normal;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is FontWeight.Bold;
    }

    /// <summary>
    /// Преобразует nullable bool в <see cref="TextDecorationCollection"/>.
    /// True → Underline, false → null.
    /// </summary>
    public sealed class BoolToUnderlineConverter : IValueConverter
    {
        public static readonly BoolToUnderlineConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is true ? TextDecorations.Underline : null;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is not null;
    }

    /// <summary>
    /// Универсальный конвертор равенства.
    /// Возвращает true если value.ToString() == parameter.ToString().
    /// Используется для IsChecked у ToggleButton привязанного к enum/string.
    /// Аналог BoolConverters.IsEqual из WPF — в Avalonia отсутствует.
    /// </summary>
    public sealed class EqualityConverter : IValueConverter
    {
        public static readonly EqualityConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is null || parameter is null) return false;
            return value.ToString() == parameter.ToString();
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => AvaloniaProperty.UnsetValue;
    }

    /// <summary>
    /// Возвращает размер шрифта для превью карточки стиля в галерее.
    /// </summary>
    public sealed class StylePreviewSizeConverter : IValueConverter
    {
        public static readonly StylePreviewSizeConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value is string name ? name switch
            {
                "Heading 1" => 16.0,
                "Heading 2" => 14.0,
                "Heading 3" => 13.0,
                "Heading 4" => 12.0,
                "Heading 5" => 11.0,
                "Heading 6" => 10.0,
                _ => 11.0
            } : 11.0;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => AvaloniaProperty.UnsetValue;
    }

    /// <summary>
    /// Возвращает FontWeight для превью карточки стиля в галерее.
    /// </summary>
    public sealed class StylePreviewWeightConverter : IValueConverter
    {
        public static readonly StylePreviewWeightConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value is string name && name.StartsWith("Heading")
                ? FontWeight.Bold
                : FontWeight.Normal;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => AvaloniaProperty.UnsetValue;
    }

    /// <summary>
    /// Возвращает FontStyle для превью карточки стиля в галерее.
    /// </summary>
    public sealed class StylePreviewStyleConverter : IValueConverter
    {
        public static readonly StylePreviewStyleConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value is "Quote" ? FontStyle.Italic : FontStyle.Normal;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => AvaloniaProperty.UnsetValue;
    }
}