using System;

namespace Writersword.Modules.Notes.Models
{
    /// <summary>
    /// Вид строки заметки.
    ///
    /// Вид отвечает только за оформление. Текст строки во всех видах хранится
    /// одинаково и не содержит ни маркера списка, ни номера, ни коробочки
    /// задачи — их рисует разметка. Прежний редактор держал маркеры прямо в
    /// тексте документа: скопированный кусок уезжал в буфер вместе с «• » и
    /// «☐ », а каретка вставала перед маркером.
    /// </summary>
    public enum NoteBlockType
    {
        /// <summary>Обычная строка.</summary>
        Paragraph,

        /// <summary>Заголовок первого уровня — «# ».</summary>
        Heading1,

        /// <summary>Заголовок второго уровня — «## ».</summary>
        Heading2,

        /// <summary>Заголовок третьего уровня — «### ».</summary>
        Heading3,

        /// <summary>Заголовок четвёртого уровня — «#### ».</summary>
        Heading4,

        /// <summary>Заголовок пятого уровня — «##### ».</summary>
        Heading5,

        /// <summary>Заголовок шестого уровня — «###### ».</summary>
        Heading6,

        /// <summary>Строка маркированного списка — «- », «* », «+ ».</summary>
        Bullet,

        /// <summary>Строка нумерованного списка — «1. ». Номер считается по порядку.</summary>
        Numbered,

        /// <summary>Задача с коробочкой — «- [ ] », «- [x] ».</summary>
        Checklist,

        /// <summary>Цитата — «&gt; ».</summary>
        Quote,

        /// <summary>Строка кода — «``` » или четыре пробела в начале.</summary>
        Code,

        /// <summary>Горизонтальная черта без текста — «---», «***», «___».</summary>
        Divider
    }

    /// <summary>
    /// Одна строка заметки: вид, текст и пометки.
    /// Единица хранения и единица отмены — страница состоит из списка таких строк.
    /// </summary>
    public sealed class NoteBlock
    {
        /// <summary>
        /// Устойчивый идентификатор строки. Нужен, чтобы отмена структурной
        /// операции вернула на место ту же строку, а не её копию: по нему
        /// восстанавливается выделение и место каретки.
        /// </summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Вид строки.</summary>
        public NoteBlockType Type { get; set; }

        /// <summary>Текст строки без маркеров вида, но с разметкой внутри текста.</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>Задача отмечена выполненной. Имеет смысл только для <see cref="NoteBlockType.Checklist"/>.</summary>
        public bool IsChecked { get; set; }

        /// <summary>Строка подсвечена целиком.</summary>
        public bool IsHighlighted { get; set; }

        /// <summary>Строка зачёркнута целиком.</summary>
        public bool IsStruckThrough { get; set; }

        /// <summary>
        /// Копия строки вместе с идентификатором.
        /// Идентификатор намеренно переносится: копия уходит в снимок для
        /// отмены и должна вернуться той же строкой.
        /// </summary>
        public NoteBlock Clone() => new()
        {
            Id = Id,
            Type = Type,
            Text = Text,
            IsChecked = IsChecked,
            IsHighlighted = IsHighlighted,
            IsStruckThrough = IsStruckThrough
        };
    }

    /// <summary>
    /// Оформление куска текста внутри строки.
    /// Набирается флагами: «**`код`**» — это и жирный, и код разом.
    /// </summary>
    [Flags]
    public enum NoteSpanStyle
    {
        None = 0,

        /// <summary>«**текст**» или «__текст__».</summary>
        Bold = 1,

        /// <summary>«*текст*» или «_текст_».</summary>
        Italic = 2,

        /// <summary>«~~текст~~».</summary>
        Strikethrough = 4,

        /// <summary>«`текст`».</summary>
        Code = 8,

        /// <summary>«==текст==».</summary>
        Mark = 16
    }

    /// <summary>
    /// Кусок строки с одинаковым оформлением. Разбор разметки внутри строки
    /// возвращает список таких кусков, а разметка рисует по ним надпись.
    /// </summary>
    /// <param name="Text">Текст куска без знаков разметки.</param>
    /// <param name="Style">Оформление куска.</param>
    public readonly record struct NoteSpan(string Text, NoteSpanStyle Style);
}
