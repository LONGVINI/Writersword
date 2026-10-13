using System.Text.Json.Serialization;

namespace Writersword.Modules.TextEditor.Models.Document
{
    /// <summary>
    /// Формула Word в строке текста.
    ///
    /// Стоит среди букв своего абзаца, как картинка в строке: лежит в
    /// <see cref="SectionModel.InlineObjects"/>, а run абзаца хранит ссылку на неё и
    /// один символ-заполнитель. Для каретки, выделения и отмены формула — один знак.
    ///
    /// Сама формула хранится тем же XML, каким её пишет Word (OMML, элемент m:oMath):
    /// в .docx она возвращается без потерь, а вёрстка и рисование разбирают этот XML
    /// (Rendering.Formulas).
    /// </summary>
    public sealed class FormulaBlock : BlockModel
    {
        public override BlockType BlockType => BlockType.Formula;

        /// <summary>Формула в виде XML Word: элемент m:oMath со своими пространствами имён.</summary>
        public string Omml { get; set; } = string.Empty;

        /// <summary>Гарнитура формулы. У Word это Cambria Math.</summary>
        public string FontFamily { get; set; } = DefaultFontFamily;

        /// <summary>Кегль формулы в пунктах: кегль текста, в котором она стоит.</summary>
        public double FontSizePt { get; set; } = 11.0;

        /// <summary>
        /// Формула отдельной строкой (m:oMathPara): знаки сумм крупнее, пределы над и
        /// под ними, дроби в полный размер. В строке текста формула ужата, чтобы не
        /// раздвигать строки.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsDisplay { get; set; }

        /// <summary>Гарнитура формул Word.</summary>
        public const string DefaultFontFamily = "Cambria Math";
    }
}
