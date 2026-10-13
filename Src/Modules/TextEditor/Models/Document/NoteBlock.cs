using System.Collections.Generic;
using System.Text.Json.Serialization;
using Writersword.Modules.TextEditor.Models.Inline;

namespace Writersword.Modules.TextEditor.Models.Document
{
    /// <summary>Вид сноски.</summary>
    public enum NoteKind
    {
        /// <summary>Сноска: её текст стоит внизу той страницы, где стоит знак сноски.</summary>
        Footnote = 0,

        /// <summary>Концевая сноска: её текст стоит в конце документа.</summary>
        Endnote = 1
    }

    /// <summary>Чем нумеруются знаки сносок.</summary>
    public enum NoteNumberFormat
    {
        /// <summary>1, 2, 3.</summary>
        Decimal = 0,

        /// <summary>i, ii, iii.</summary>
        LowerRoman = 1,

        /// <summary>I, II, III.</summary>
        UpperRoman = 2,

        /// <summary>a, b, c.</summary>
        LowerLetter = 3,

        /// <summary>A, B, C.</summary>
        UpperLetter = 4,

        /// <summary>Знаки: звёздочка, крестик, двойной крестик, параграф.</summary>
        Symbols = 5
    }

    /// <summary>
    /// Сноска или концевая сноска.
    ///
    /// В тексте от сноски остаётся знак — маленькая цифра над строкой. Знак стоит
    /// среди букв своего абзаца, как объект строки: сама сноска лежит в
    /// <see cref="SectionModel.InlineObjects"/>, а run абзаца хранит ссылку на неё и
    /// один символ-заполнитель. Для каретки, выделения и отмены знак сноски — один
    /// символ; вместе с ним удаляется и сноска.
    ///
    /// Номер в знаке не хранится: сноски нумеруются по порядку знаков в тексте, и
    /// вставка новой сноски выше сдвигает номера остальных сама.
    ///
    /// Текст сноски — абзацы <see cref="Paragraphs"/>. Цифра в начале текста сноски
    /// тоже не хранится: её ставит вёрстка перед первым абзацем. Пробел между цифрой
    /// и текстом — обычный знак текста сноски, как у Word.
    /// </summary>
    public sealed class NoteBlock : BlockModel
    {
        public override BlockType BlockType => BlockType.Note;

        /// <summary>Сноска внизу страницы или концевая сноска.</summary>
        public NoteKind Kind { get; set; }

        /// <summary>Текст сноски: один или несколько абзацев.</summary>
        public List<ParagraphBlock> Paragraphs { get; set; } = new();

        /// <summary>
        /// Оформление цифры в начале текста сноски: гарнитура, кегль, цвет. Null —
        /// шрифтом текста сноски. Верхним индексом цифра стоит всегда. Знак сноски в
        /// основном тексте оформлен своим символом-заполнителем, как любой знак абзаца.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public RunProperties? MarkProperties { get; set; }

        /// <summary>
        /// Свой знак сноски вместо номера (звёздочка, буква). Такая сноска в счёте
        /// номеров не участвует, а её знак в начале текста сноски набран самим
        /// текстом. Null — сноска нумеруется по порядку.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CustomMark { get; set; }

        /// <summary>Текст сноски одной строкой: абзацы через перевод строки.</summary>
        [JsonIgnore]
        public string PlainText
        {
            get
            {
                var builder = new System.Text.StringBuilder();
                for (int i = 0; i < Paragraphs.Count; i++)
                {
                    if (i > 0) builder.Append('\n');
                    builder.Append(Paragraphs[i].GetPlainText());
                }
                return builder.ToString();
            }
        }
    }
}
