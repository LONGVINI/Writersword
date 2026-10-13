using System;
using System.Collections.Generic;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Сноски и концевые сноски в .docx — так же, как их пишет Word: в абзаце стоит
    /// ссылка w:footnoteReference / w:endnoteReference с номером записи, сам текст
    /// лежит в частях footnotes.xml и endnotes.xml. Первые две записи каждой части —
    /// черта над сносками и черта над продолжением сноски; без них Word считает
    /// файл повреждённым.
    ///
    /// Номер сноски в файл не пишется, как и у Word: его ставит редактор по порядку
    /// ссылок. Пишутся вид нумерации и номер первой сноски — в настройки документа.
    /// </summary>
    public sealed partial class ExportService
    {
        private static Dictionary<Guid, NoteBlock> BuildInlineNoteMap(SectionModel section)
        {
            var map = new Dictionary<Guid, NoteBlock>();
            foreach (var block in section.InlineObjects)
                if (block is NoteBlock note)
                    map[note.Id] = note;
            return map;
        }

        /// <summary>
        /// Ран со ссылкой на сноску. Оформление — от символа-заполнителя сноски, верхним
        /// индексом. null — сноска в тексте сноски: у Word такого не бывает.
        /// </summary>
        private W.Run? BuildNoteReferenceRun(RunModel run, NoteBlock note, DocxWriteContext ctx)
        {
            if (ctx.WritingNote) return null;

            var target = note.Kind == NoteKind.Endnote ? ctx.Endnotes : ctx.Footnotes;
            int index = target.IndexOf(note);
            if (index < 0)
            {
                target.Add(note);
                index = target.Count - 1;
            }

            // Номера записей — с единицы: ноль и минус единица заняты чертами.
            int id = index + 1;

            var wordRun = new W.Run();
            AppendNoteMarkProperties(wordRun, run.Properties, ctx);

            bool customMark = !string.IsNullOrEmpty(note.CustomMark);

            if (note.Kind == NoteKind.Endnote)
            {
                var reference = new W.EndnoteReference { Id = id };
                if (customMark) reference.CustomMarkFollows = true;
                wordRun.AppendChild(reference);
            }
            else
            {
                var reference = new W.FootnoteReference { Id = id };
                if (customMark) reference.CustomMarkFollows = true;
                wordRun.AppendChild(reference);
            }

            if (customMark)
            {
                wordRun.AppendChild(new W.Text(note.CustomMark!)
                {
                    Space = new EnumValue<SpaceProcessingModeValues>(SpaceProcessingModeValues.Preserve)
                });
            }

            return wordRun;
        }

        /// <summary>Свойства рана со знаком сноски: оформление источника и верхний индекс.</summary>
        private void AppendNoteMarkProperties(W.Run wordRun, RunProperties? source, DocxWriteContext ctx)
        {
            var properties = source?.Clone() ?? new RunProperties();
            properties.IsSuperscript = true;
            properties.IsSubscript = false;

            var rPr = new W.RunProperties();
            foreach (var element in BuildRunPropertyElements(properties, writeExplicitToggles: true))
                rPr.AppendChild(element);

            if (source?.FormatChange is { } formatChange)
                AppendRunFormatChange(rPr, formatChange, ctx);

            wordRun.AppendChild(rPr);
        }

        /// <summary>
        /// Пишет части со сносками и настройки нумерации. Вызывается после сборки тела
        /// документа: номера записей выдаются по ходу записи абзацев.
        /// </summary>
        private void WriteNoteParts(MainDocumentPart mainPart, DocumentModel document, DocxWriteContext ctx)
        {
            if (ctx.Footnotes.Count == 0 && ctx.Endnotes.Count == 0) return;

            ctx.WritingNote = true;
            try
            {
                if (ctx.Footnotes.Count > 0)
                {
                    var footnotes = new W.Footnotes();
                    DocxTextEffects.DeclareNamespaces(footnotes);
                    footnotes.AppendChild(new W.Footnote(BuildNoteRuleParagraph(new W.SeparatorMark()))
                    {
                        Type = W.FootnoteEndnoteValues.Separator,
                        Id = -1
                    });
                    footnotes.AppendChild(new W.Footnote(BuildNoteRuleParagraph(new W.ContinuationSeparatorMark()))
                    {
                        Type = W.FootnoteEndnoteValues.ContinuationSeparator,
                        Id = 0
                    });

                    for (int i = 0; i < ctx.Footnotes.Count; i++)
                    {
                        var element = new W.Footnote { Id = i + 1 };
                        foreach (var paragraph in BuildNoteParagraphs(ctx.Footnotes[i], ctx))
                            element.AppendChild(paragraph);
                        footnotes.AppendChild(element);
                    }

                    var part = mainPart.AddNewPart<FootnotesPart>();
                    part.Footnotes = footnotes;
                    part.Footnotes.Save();
                }

                if (ctx.Endnotes.Count > 0)
                {
                    var endnotes = new W.Endnotes();
                    DocxTextEffects.DeclareNamespaces(endnotes);
                    endnotes.AppendChild(new W.Endnote(BuildNoteRuleParagraph(new W.SeparatorMark()))
                    {
                        Type = W.FootnoteEndnoteValues.Separator,
                        Id = -1
                    });
                    endnotes.AppendChild(new W.Endnote(BuildNoteRuleParagraph(new W.ContinuationSeparatorMark()))
                    {
                        Type = W.FootnoteEndnoteValues.ContinuationSeparator,
                        Id = 0
                    });

                    for (int i = 0; i < ctx.Endnotes.Count; i++)
                    {
                        var element = new W.Endnote { Id = i + 1 };
                        foreach (var paragraph in BuildNoteParagraphs(ctx.Endnotes[i], ctx))
                            element.AppendChild(paragraph);
                        endnotes.AppendChild(element);
                    }

                    var part = mainPart.AddNewPart<EndnotesPart>();
                    part.Endnotes = endnotes;
                    part.Endnotes.Save();
                }
            }
            finally
            {
                ctx.WritingNote = false;
            }

            WriteNoteSettings(mainPart, document, ctx);
        }

        /// <summary>
        /// Абзац с чертой над сносками: одинарный интервал без отступа после абзаца,
        /// как его пишет Word.
        /// </summary>
        private static W.Paragraph BuildNoteRuleParagraph(OpenXmlElement mark)
        {
            return new W.Paragraph(
                new W.ParagraphProperties(
                    new W.SpacingBetweenLines
                    {
                        After = "0",
                        Line = "240",
                        LineRule = W.LineSpacingRuleValues.Auto
                    }),
                new W.Run(mark));
        }

        /// <summary>
        /// Абзацы текста сноски. В начало первого встаёт место номера (w:footnoteRef /
        /// w:endnoteRef) верхним индексом; у сноски со своим знаком его нет — знак
        /// набран текстом.
        /// </summary>
        private List<W.Paragraph> BuildNoteParagraphs(NoteBlock note, DocxWriteContext ctx)
        {
            var paragraphs = new List<W.Paragraph>(Math.Max(1, note.Paragraphs.Count));

            foreach (var paragraph in note.Paragraphs)
                paragraphs.Add(BuildParagraph(paragraph, ctx));

            if (paragraphs.Count == 0)
                paragraphs.Add(new W.Paragraph());

            if (string.IsNullOrEmpty(note.CustomMark))
            {
                RunProperties? markSource = note.MarkProperties;
                if (markSource is null && note.Paragraphs.Count > 0)
                {
                    foreach (var chunk in note.Paragraphs[0].Chunks)
                    {
                        foreach (var run in chunk.Runs)
                            if (run.Properties is not null && !run.IsInlineObject) { markSource = run.Properties; break; }

                        if (markSource is not null) break;
                    }
                }

                var markRun = new W.Run();
                AppendNoteMarkProperties(markRun, markSource?.WithoutRevisions(), ctx);
                OpenXmlElement referenceMark = note.Kind == NoteKind.Endnote
                    ? new W.EndnoteReferenceMark()
                    : new W.FootnoteReferenceMark();
                markRun.AppendChild(referenceMark);

                var first = paragraphs[0];
                int insertAt = first.GetFirstChild<W.ParagraphProperties>() is null ? 0 : 1;
                first.InsertAt(markRun, insertAt);
            }

            return paragraphs;
        }

        /// <summary>
        /// Настройки сносок документа: вид и начало нумерации, если они не словные по
        /// умолчанию, и ссылки на записи с чертами.
        /// </summary>
        private static void WriteNoteSettings(MainDocumentPart mainPart, DocumentModel document, DocxWriteContext ctx)
        {
            var part = mainPart.DocumentSettingsPart ?? mainPart.AddNewPart<DocumentSettingsPart>();
            part.Settings ??= new W.Settings();

            part.Settings.RemoveAllChildren<W.FootnoteDocumentWideProperties>();
            part.Settings.RemoveAllChildren<W.EndnoteDocumentWideProperties>();

            if (ctx.Footnotes.Count > 0)
            {
                var properties = new W.FootnoteDocumentWideProperties();

                if (document.FootnoteNumberFormat != NoteNumberFormat.Decimal)
                    properties.AppendChild(new W.NumberingFormat { Val = MapNoteNumberFormat(document.FootnoteNumberFormat) });
                if (document.FootnoteNumberStart > 1)
                    properties.AppendChild(new W.NumberingStart { Val = (UInt16Value)(ushort)Math.Min(document.FootnoteNumberStart, ushort.MaxValue) });

                properties.AppendChild(new W.FootnoteSpecialReference { Id = -1 });
                properties.AppendChild(new W.FootnoteSpecialReference { Id = 0 });
                part.Settings.AppendChild(properties);
            }

            if (ctx.Endnotes.Count > 0)
            {
                var properties = new W.EndnoteDocumentWideProperties();

                if (document.EndnoteNumberFormat != NoteNumberFormat.LowerRoman)
                    properties.AppendChild(new W.NumberingFormat { Val = MapNoteNumberFormat(document.EndnoteNumberFormat) });
                if (document.EndnoteNumberStart > 1)
                    properties.AppendChild(new W.NumberingStart { Val = (UInt16Value)(ushort)Math.Min(document.EndnoteNumberStart, ushort.MaxValue) });

                properties.AppendChild(new W.EndnoteSpecialReference { Id = -1 });
                properties.AppendChild(new W.EndnoteSpecialReference { Id = 0 });
                part.Settings.AppendChild(properties);
            }

            part.Settings.Save();
        }

        private static W.NumberFormatValues MapNoteNumberFormat(NoteNumberFormat format) => format switch
        {
            NoteNumberFormat.LowerRoman => W.NumberFormatValues.LowerRoman,
            NoteNumberFormat.UpperRoman => W.NumberFormatValues.UpperRoman,
            NoteNumberFormat.LowerLetter => W.NumberFormatValues.LowerLetter,
            NoteNumberFormat.UpperLetter => W.NumberFormatValues.UpperLetter,
            NoteNumberFormat.Symbols => W.NumberFormatValues.Chicago,
            _ => W.NumberFormatValues.Decimal
        };

        // ── Текстовые форматы ───────────────────────────────────────────────

        /// <summary>
        /// Текст абзаца для выгрузки в .txt: знак сноски — её номером в квадратных
        /// скобках. Остальной текст — как в абзаце.
        /// </summary>
        private static string PlainTextWithNoteMarks(
            ParagraphBlock paragraph, NoteNumbering numbering, IReadOnlyDictionary<Guid, FieldBlock> fields)
        {
            if (!numbering.HasNotes && fields.Count == 0) return paragraph.GetPlainText();

            var builder = new StringBuilder();
            foreach (var chunk in paragraph.Chunks)
            {
                foreach (var run in chunk.Runs)
                {
                    if (run.InlineImageId is Guid id && numbering.MarkOf(id) is { } mark)
                    {
                        builder.Append('[').Append(mark).Append(']');
                        continue;
                    }

                    // Живое поле — своим значением (ExportService.Fields).
                    if (run.InlineImageId is Guid fieldId && fields.TryGetValue(fieldId, out var field))
                    {
                        builder.Append(LiveFieldValue(field, run.Properties));
                        continue;
                    }

                    builder.Append(run.Text);
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// Тексты сносок документа для выгрузки в текстовый файл: сначала сноски,
        /// затем концевые, каждая под своим номером. Пустой список — сносок нет.
        /// </summary>
        private static List<(string Mark, string Text)> NoteTextsFor(NoteNumbering numbering)
        {
            var result = new List<(string, string)>();

            void Add(IReadOnlyList<NoteReference> references)
            {
                foreach (var reference in references)
                {
                    string text = reference.Note.PlainText
                        .Replace(RunModel.ObjectPlaceholder.ToString(), string.Empty)
                        .Trim();
                    result.Add((reference.Mark, text));
                }
            }

            Add(numbering.Footnotes);
            Add(numbering.Endnotes);
            return result;
        }
    }
}
