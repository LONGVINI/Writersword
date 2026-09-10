using System.Text.Json.Serialization;

namespace Writersword.Modules.TextEditor.Models.Styles
{
    /// <summary>
    /// К какому краю позиции табуляции прижимается текст, идущий за ней.
    /// </summary>
    public enum TabAlignment
    {
        /// <summary>Текст начинается от позиции и идёт вправо.</summary>
        Left = 0,
        /// <summary>Текст стоит серединой на позиции.</summary>
        Center = 1,
        /// <summary>Текст заканчивается на позиции. Ею прижимают номера страниц.</summary>
        Right = 2,
        /// <summary>
        /// Текст выравнивается по десятичному разделителю: числа встают запятая
        /// под запятой независимо от количества знаков до неё.
        /// </summary>
        Decimal = 3
    }

    /// <summary>
    /// Чем заполняется пустое место, которое отвела табуляция.
    /// </summary>
    public enum TabLeaderStyle
    {
        /// <summary>Ничем.</summary>
        None = 0,
        /// <summary>Точками — так набирают оглавления.</summary>
        Dots = 1,
        /// <summary>Чёрточками.</summary>
        Dashes = 2,
        /// <summary>Сплошной линией по базовой линии текста.</summary>
        Line = 3
    }

    /// <summary>
    /// Позиция табуляции в абзаце.
    ///
    /// Табуляция — это не «несколько пробелов», а именованная точка на строке: символ
    /// табуляции в тексте отдаёт следующему куску начало ровно в этом месте листа,
    /// сколько бы текста ни стояло до него. На ней держится всё, что должно стоять
    /// столбиком без таблицы: номера страниц в оглавлении, даты справа от строк,
    /// столбцы цифр по запятой.
    /// </summary>
    public sealed class TabStop
    {
        /// <summary>Расстояние от левого края текстовой области абзаца в пунктах.</summary>
        public double PositionPt { get; set; }

        /// <summary>Как прижимается текст за табуляцией.</summary>
        public TabAlignment Alignment { get; set; } = TabAlignment.Left;

        /// <summary>Заполнитель пустого места перед позицией.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public TabLeaderStyle Leader { get; set; } = TabLeaderStyle.None;

        /// <summary>
        /// Разделитель для табуляции по десятичному знаку. Пусто — взять разделитель
        /// текущего языка: в русской книге числа пишут через запятую, в английской
        /// через точку, и жёстко зашитый символ подвёл бы одну из них.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DecimalSeparator { get; set; }

        /// <summary>Создаёт копию позиции.</summary>
        public TabStop Clone() => (TabStop)MemberwiseClone();
    }
}
