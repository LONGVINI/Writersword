using System.Collections.Generic;
using Writersword.Core.Models.Rendering;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Models.Styles;

namespace Writersword.Modules.TextEditor.Rendering
{
    /// <summary>
    /// Вёрстка текста сносок: общая для листов редактора и для печати, чтобы сноска
    /// на бумаге стояла теми же строками, что на экране.
    /// </summary>
    internal static class NoteLayout
    {
        /// <summary>
        /// Раскладка абзаца сноски. Первый абзац верстается вместе с номером сноски в
        /// начале; у сноски со своим знаком номера нет — знак набран её текстом.
        /// </summary>
        public static SKTextLayout Build(
            SKTextRenderer renderer, NoteBlock note, int paragraphIndex, string mark,
            float widthPt, StyleResolver styles)
        {
            var paragraph = note.Paragraphs[paragraphIndex];
            var display = MarkFor(note, paragraphIndex, mark).Length > 0
                ? DisplayParagraph(note, paragraph, mark)
                : paragraph;

            return renderer.BuildLayout(display, widthPt, styles);
        }

        /// <summary>Номер, который вёрстка ставит в начало этого абзаца сноски; пусто — никакого.</summary>
        public static string MarkFor(NoteBlock note, int paragraphIndex, string mark)
            => paragraphIndex == 0 && string.IsNullOrEmpty(note.CustomMark) ? mark : string.Empty;

        /// <summary>
        /// Первый абзац сноски с номером в начале. Номер в тексте сноски не хранится: он
        /// считается по порядку знаков, и подставной абзац делит с настоящим текст и
        /// оформление — добавлен только номер верхним индексом.
        /// </summary>
        public static ParagraphBlock DisplayParagraph(NoteBlock note, ParagraphBlock first, string mark)
        {
            RunProperties? source = note.MarkProperties;
            if (source is null)
            {
                foreach (var chunk in first.Chunks)
                {
                    foreach (var run in chunk.Runs)
                        if (run.Properties is not null && !run.IsInlineObject) { source = run.Properties; break; }

                    if (source is not null) break;
                }
            }

            var markProperties = source?.WithoutRevisions() ?? new RunProperties();
            markProperties.IsSuperscript = true;
            markProperties.IsSubscript = false;
            markProperties.IsHidden = false;

            var runs = new List<RunModel>
            {
                new RunModel { Text = mark, Properties = markProperties }
            };

            foreach (var chunk in first.Chunks)
                runs.AddRange(chunk.Runs);

            var displayChunk = new TextChunk { Runs = runs };
            displayChunk.InvalidateLength();

            return new ParagraphBlock
            {
                Id = first.Id,
                Chunks = new List<TextChunk> { displayChunk },
                Properties = first.Properties,
                ListProperties = first.ListProperties,
                SuppressSpaceBefore = first.SuppressSpaceBefore,
                SuppressSpaceAfter = first.SuppressSpaceAfter
            };
        }

        /// <summary>
        /// Высота строки с чертой над сносками: строка обычного текста документа
        /// одинарным интервалом, без интервалов абзаца — так её пишет Word.
        /// </summary>
        public static float RuleBandPt(SKTextRenderer renderer, float widthPt, StyleResolver styles, float fallbackPt)
        {
            var probe = new ParagraphBlock();
            probe.SetPlainText(" ");
            probe.Properties.SpaceBefore = 0.0;
            probe.Properties.SpaceAfter = 0.0;
            probe.Properties.LineSpacingRule = LineSpacingRule.Auto;
            probe.Properties.LineSpacingValue = 1.0;

            var layout = renderer.BuildLayout(probe, widthPt, styles);
            return layout.Lines.Count > 0 && layout.Lines[0].Height > 0f
                ? layout.Lines[0].Height
                : fallbackPt;
        }
    }
}
