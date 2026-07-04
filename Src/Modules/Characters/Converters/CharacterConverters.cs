using Avalonia.Data.Converters;
using System;
using System.Globalization;
using Writersword.Modules.Characters.Models.Enums;
using Writersword.Src.Modules.Characters.Resources;

namespace Writersword.Modules.Characters.Converters
{
    public class IsNumericConverter : IValueConverter
    {
        public static readonly IsNumericConverter Instance = new();
        public object? Convert(object? value, Type t, object? p, CultureInfo c)
            => value is CharacterParameterType v && v == CharacterParameterType.Numeric;
        public object? ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
    }

    public class IsTextConverter : IValueConverter
    {
        public static readonly IsTextConverter Instance = new();
        public object? Convert(object? value, Type t, object? p, CultureInfo c)
            => value is CharacterParameterType v && v == CharacterParameterType.Text;
        public object? ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
    }

    public class IsStateListConverter : IValueConverter
    {
        public static readonly IsStateListConverter Instance = new();
        public object? Convert(object? value, Type t, object? p, CultureInfo c)
            => value is CharacterParameterType v && v == CharacterParameterType.StateList;
        public object? ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
    }

    public class IsBooleanConverter : IValueConverter
    {
        public static readonly IsBooleanConverter Instance = new();
        public object? Convert(object? value, Type t, object? p, CultureInfo c)
            => value is CharacterParameterType v && v == CharacterParameterType.Boolean;
        public object? ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
    }

    public class IsNotStateListConverter : IValueConverter
    {
        public static readonly IsNotStateListConverter Instance = new();
        public object? Convert(object? value, Type t, object? p, CultureInfo c)
            => value is CharacterParameterType v && v != CharacterParameterType.StateList;
        public object? ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
    }

    public class IntIsZeroConverter : IValueConverter
    {
        public static readonly IntIsZeroConverter Instance = new();
        public object? Convert(object? value, Type t, object? p, CultureInfo c) => value is int i && i == 0;
        public object? ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
    }

    public class IntIsNotZeroConverter : IValueConverter
    {
        public static readonly IntIsNotZeroConverter Instance = new();
        public object? Convert(object? value, Type t, object? p, CultureInfo c) => value is int i && i != 0;
        public object? ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
    }

    public class IntIsOneConverter : IValueConverter
    {
        public static readonly IntIsOneConverter Instance = new();
        public object? Convert(object? value, Type t, object? p, CultureInfo c) => value is int i && i == 1;
        public object? ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
    }

    public class IntIsTwoConverter : IValueConverter
    {
        public static readonly IntIsTwoConverter Instance = new();
        public object? Convert(object? value, Type t, object? p, CultureInfo c) => value is int i && i == 2;
        public object? ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
    }

    public class IntIsThreeConverter : IValueConverter
    {
        public static readonly IntIsThreeConverter Instance = new();
        public object? Convert(object? value, Type t, object? p, CultureInfo c) => value is int i && i == 3;
        public object? ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
    }

    public class BoolToLabelConverter : IValueConverter
    {
        public static readonly BoolToLabelConverter Instance = new();
        public object? Convert(object? value, Type t, object? p, CultureInfo c)
        {
            var isTrue = value is bool b && b;
            if (p is string labels)
            {
                var parts = labels.Split('|');
                if (parts.Length == 2) return isTrue ? parts[0] : parts[1];
            }
            return isTrue ? CharactersStrings.Common_Yes : CharactersStrings.Common_No;
        }
        public object? ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
    }

    public class BoolToAccentConverter : IValueConverter
    {
        public static readonly BoolToAccentConverter Instance = new();
        public object? Convert(object? value, Type t, object? p, CultureInfo c)
            => value is bool b && b ? "AccentDefault" : "Secondary";
        public object? ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
    }

    public class IsCustomImportanceConverter : IValueConverter
    {
        public static readonly IsCustomImportanceConverter Instance = new();
        public object? Convert(object? value, Type t, object? p, CultureInfo c)
            => value is CharacterImportanceLevel level && level == CharacterImportanceLevel.Custom;
        public object? ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
    }

    public class BoolToActiveBrushConverter : IValueConverter
    {
        public static readonly BoolToActiveBrushConverter Instance = new();
        public object? Convert(object? value, Type t, object? p, CultureInfo c)
        {
            if (value is bool b && b)
                return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E07B39"));
            return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#3A3A3A"));
        }
        public object? ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
    }

    /// <summary>
    /// Конвертер для показа вкладки по конкретному индексу.
    /// Используется как: &lt;conv:TabIndexConverter x:Key="TabIndex1" TabIndex="1"/&gt;
    /// </summary>
    public class TabIndexConverter : IValueConverter
    {
        public int TabIndex { get; set; } = 0;

        public object? Convert(object? value, Type t, object? p, CultureInfo c)
            => value is int i && i == TabIndex;

        public object? ConvertBack(object? value, Type t, object? p, CultureInfo c)
            => throw new NotImplementedException();
    }

    public class BoolToActiveTemplateConverter : IValueConverter
    {
        public static readonly BoolToActiveTemplateConverter Instance = new();
        public object? Convert(object? value, Type t, object? p, CultureInfo c)
            => value is bool b && b ? CharactersStrings.Template_Applied : CharactersStrings.Template_Apply;
        public object? ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
    }

    /// <summary>true → ▼, false → ▶</summary>
    public class BoolToCollapseArrowConverter : IValueConverter
    {
        public static readonly BoolToCollapseArrowConverter Instance = new();
        public object? Convert(object? value, Type t, object? p, CultureInfo c)
            => value is bool b && b ? "▼" : "▶";
        public object? ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
    }

}