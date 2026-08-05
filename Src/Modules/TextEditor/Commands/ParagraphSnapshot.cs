using System.Collections.Generic;
using System.Linq;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Styles;

namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Полный снапшот одного параграфа — все runs и свойства.
    /// Используется командами требующими полного восстановления параграфа:
    /// MergeParagraph (хранит поглощённый параграф), DeleteParagraph и т.д.
    /// </summary>
    public sealed class ParagraphSnapshot
    {
        /// <summary>Снапшоты всех runs параграфа в порядке следования.</summary>
        public IReadOnlyList<RunSnapshot> Runs { get; }

        /// <summary>Свойства параграфа (отступы, выравнивание и т.д.).</summary>
        public ParagraphProperties Properties { get; }

        public ParagraphSnapshot(IReadOnlyList<RunSnapshot> runs, ParagraphProperties properties)
        {
            Runs = runs;
            Properties = properties;
        }

        /// <summary>
        /// Создать снапшот из существующего ParagraphBlock.
        /// </summary>
        public static ParagraphSnapshot From(ParagraphBlock para)
        {
            var runs = new List<RunSnapshot>();
            foreach (var chunk in para.Chunks)
                foreach (var run in chunk.Runs)
                    runs.Add(new RunSnapshot(
                        run.Text ?? string.Empty, run.Properties, run.InlineImageId));

            return new ParagraphSnapshot(runs, CloneProperties(para.Properties));
        }

        /// <summary>Клонировать ParagraphProperties.</summary>
        private static ParagraphProperties CloneProperties(ParagraphProperties src)
        {
            return new ParagraphProperties
            {
                StyleName = src.StyleName,
                Alignment = src.Alignment,
                LeftIndent = src.LeftIndent,
                RightIndent = src.RightIndent,
                FirstLineIndent = src.FirstLineIndent,
                SpaceBefore = src.SpaceBefore,
                SpaceAfter = src.SpaceAfter,
                LineSpacingValue = src.LineSpacingValue
            };
        }
    }
}
