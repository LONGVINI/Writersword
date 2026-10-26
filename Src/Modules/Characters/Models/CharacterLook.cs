namespace Writersword.Modules.Characters.Models
{
    /// <summary>
    /// Выравнивание поля по высоте внутри строки анкеты, когда соседнее поле
    /// выше: подпись и значение — сверху, по центру или снизу строки.
    /// </summary>
    public static class CharacterLookAlign
    {
        public const string Top = "top";
        public const string Center = "center";
        public const string Bottom = "bottom";

        /// <summary>Значение из файла — одно из известных или null.</summary>
        public static string? Normalize(string? value) => value switch
        {
            Top or Center or Bottom => value,
            _ => null
        };
    }

    /// <summary>
    /// Особый вид одного поля анкеты — то, что обычно не трогают: выравнивание
    /// в строке, шрифт и размеры подписи и значения. Пустое значение — как у
    /// анкеты (CharacterAnketaLook), а там пустое — как у программы.
    /// </summary>
    public class CharacterFieldLook
    {
        /// <summary>Выравнивание в строке (CharacterLookAlign). Пусто — как у анкеты.</summary>
        public string? VerticalAlign { get; set; }

        /// <summary>Шрифт подписи и значения. Пусто — как у анкеты.</summary>
        public string? FontFamily { get; set; }

        /// <summary>Размер подписи, пт. Пусто — как у анкеты.</summary>
        public double? LabelFontSize { get; set; }

        /// <summary>Размер значения (текст, описание, число), пт. Пусто — как у анкеты.</summary>
        public double? ValueFontSize { get; set; }

        /// <summary>Ничего не задано — в анкету такой вид не пишется.</summary>
        public bool IsEmpty =>
            string.IsNullOrWhiteSpace(VerticalAlign) &&
            string.IsNullOrWhiteSpace(FontFamily) &&
            LabelFontSize is null &&
            ValueFontSize is null;

        public CharacterFieldLook Clone() => new()
        {
            VerticalAlign = VerticalAlign,
            FontFamily = FontFamily,
            LabelFontSize = LabelFontSize,
            ValueFontSize = ValueFontSize
        };
    }

    /// <summary>
    /// Особый вид всей анкеты: расстояние между строками, выравнивание полей
    /// в строках, шрифт и размеры, высота описаний. Поле может задать своё
    /// (CharacterFieldLook, CharacterAnketaField.InputHeight), строка — свой
    /// отступ (CharacterAnketaRow.SpacingAfter). Пустое — как у программы.
    /// </summary>
    public class CharacterAnketaLook
    {
        /// <summary>Добавочное расстояние под каждой строкой полей, пт. Пусто — без добавки.</summary>
        public double? RowSpacing { get; set; }

        /// <summary>Выравнивание полей в строках (CharacterLookAlign). Пусто — сверху.</summary>
        public string? FieldAlign { get; set; }

        /// <summary>Шрифт подписей и значений. Пусто — шрифт программы.</summary>
        public string? FontFamily { get; set; }

        /// <summary>Размер подписей, пт. Пусто — как в программе.</summary>
        public double? LabelFontSize { get; set; }

        /// <summary>Размер значений, пт. Пусто — как в программе.</summary>
        public double? ValueFontSize { get; set; }

        /// <summary>Высота полей описания, пт. Пусто — по тексту, не ниже обычной.</summary>
        public double? LongTextHeight { get; set; }

        /// <summary>Ничего не задано — в анкету такой вид не пишется.</summary>
        public bool IsEmpty =>
            RowSpacing is null &&
            string.IsNullOrWhiteSpace(FieldAlign) &&
            string.IsNullOrWhiteSpace(FontFamily) &&
            LabelFontSize is null &&
            ValueFontSize is null &&
            LongTextHeight is null;

        public CharacterAnketaLook Clone() => new()
        {
            RowSpacing = RowSpacing,
            FieldAlign = FieldAlign,
            FontFamily = FontFamily,
            LabelFontSize = LabelFontSize,
            ValueFontSize = ValueFontSize,
            LongTextHeight = LongTextHeight
        };
    }
}
