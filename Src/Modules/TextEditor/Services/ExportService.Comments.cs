using System;
using System.Collections.Generic;
using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using W = DocumentFormat.OpenXml.Wordprocessing;
using W15 = DocumentFormat.OpenXml.Office2013.Word;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Примечания рецензентов в .docx — так же, как их пишет Word: в тексте границы
    /// места w:commentRangeStart / w:commentRangeEnd и ссылка w:commentReference,
    /// текст примечаний — в части comments.xml, ответы и «обсуждение закрыто» — в
    /// commentsExtended.xml.
    ///
    /// Место примечания в модели — отметки на фрагментах текста; граница встаёт перед
    /// первым отмеченным фрагментом и за последним. У ответа места своего нет — его
    /// границы стоят там же, где у примечания, на которое он отвечает, как у Word.
    /// </summary>
    public sealed partial class ExportService
    {
        /// <summary>
        /// Находит первый и последний отмеченный фрагмент каждого примечания в порядке
        /// записи документа и раздаёт примечаниям номера записей.
        /// </summary>
        private static void PlanComments(DocumentModel document, DocxWriteContext ctx)
        {
            ctx.CommentStarts.Clear();
            ctx.CommentEnds.Clear();
            ctx.CommentIds.Clear();
            ctx.CommentsToWrite.Clear();

            if (document.Comments.Count == 0) return;

            var byId = new Dictionary<Guid, CommentModel>();
            foreach (var comment in document.Comments) byId[comment.Id] = comment;

            var first = new Dictionary<Guid, RunModel>();
            var last = new Dictionary<Guid, RunModel>();

            void Visit(ParagraphBlock paragraph)
            {
                foreach (var chunk in paragraph.Chunks)
                {
                    foreach (var run in chunk.Runs)
                    {
                        if (run.Properties?.CommentIds is not { Count: > 0 } ids) continue;

                        foreach (var id in ids)
                        {
                            if (!byId.ContainsKey(id)) continue;
                            first.TryAdd(id, run);
                            last[id] = run;
                        }
                    }
                }
            }

            foreach (var section in document.Sections)
            {
                foreach (var block in section.Blocks)
                {
                    switch (block)
                    {
                        case ParagraphBlock paragraph:
                            Visit(paragraph);
                            break;

                        case TableBlock table:
                            foreach (var cell in table.Cells)
                                foreach (var cellParagraph in cell.ParagraphsDeep())
                                    Visit(cellParagraph);
                            break;
                    }
                }
            }

            // Корень обсуждения: ответ на ответ тоже ведёт к исходному примечанию.
            CommentModel RootOf(CommentModel comment)
            {
                var current = comment;
                for (int guard = 0; guard < 64 && current.ParentId is Guid parentId
                     && byId.TryGetValue(parentId, out var parent); guard++)
                    current = parent;
                return current;
            }

            int nextId = 0;
            foreach (var comment in document.Comments)
            {
                var anchor = comment.ParentId is null ? comment : RootOf(comment);
                if (!first.TryGetValue(anchor.Id, out var startRun)) continue;

                var endRun = last[anchor.Id];

                ctx.CommentIds[comment.Id] = nextId++;
                ctx.CommentsToWrite.Add(comment);

                if (!ctx.CommentStarts.TryGetValue(startRun, out var starts))
                    ctx.CommentStarts[startRun] = starts = new List<CommentModel>();
                starts.Add(comment);

                if (!ctx.CommentEnds.TryGetValue(endRun, out var ends))
                    ctx.CommentEnds[endRun] = ends = new List<CommentModel>();
                ends.Add(comment);
            }
        }

        /// <summary>
        /// Обрамляет элементы рана границами мест примечаний, начинающихся и
        /// кончающихся на нём. За концом места — ссылка на примечание.
        /// </summary>
        private static List<OpenXmlElement> WithCommentMarks(
            RunModel run, List<OpenXmlElement> elements, DocxWriteContext ctx)
        {
            if (ctx.WritingNote || ctx.CommentIds.Count == 0) return elements;

            bool starts = ctx.CommentStarts.TryGetValue(run, out var started);
            bool ends = ctx.CommentEnds.TryGetValue(run, out var ended);
            if (!starts && !ends) return elements;

            var result = new List<OpenXmlElement>(elements.Count + 4);

            if (starts)
            {
                foreach (var comment in started!)
                    result.Add(new W.CommentRangeStart { Id = WordCommentId(comment, ctx) });
            }

            result.AddRange(elements);

            if (ends)
            {
                foreach (var comment in ended!)
                {
                    string id = WordCommentId(comment, ctx);
                    result.Add(new W.CommentRangeEnd { Id = id });
                    result.Add(new W.Run(
                        new W.RunProperties(new W.FontSize { Val = "16" }),
                        new W.CommentReference { Id = id }));
                }
            }

            return result;
        }

        private static string WordCommentId(CommentModel comment, DocxWriteContext ctx)
            => ctx.CommentIds[comment.Id].ToString(CultureInfo.InvariantCulture);

        /// <summary>Пишет части с текстом примечаний, ответами и пометкой «закрыто».</summary>
        private void WriteCommentParts(MainDocumentPart mainPart, DocxWriteContext ctx)
        {
            if (ctx.CommentsToWrite.Count == 0) return;

            var comments = new W.Comments();

            // Номера абзацев (w14:paraId) и эффекты букв Word 2010+ в тексте примечаний.
            DocxTextEffects.DeclareNamespaces(comments);
            var extended = new W15.CommentsEx();
            var paragraphIds = new Dictionary<Guid, string>();
            int nextParagraphId = 0x10000001;

            ctx.WritingNote = true;
            try
            {
                foreach (var comment in ctx.CommentsToWrite)
                {
                    var element = new W.Comment
                    {
                        Id = WordCommentId(comment, ctx),
                        Author = string.IsNullOrWhiteSpace(comment.Author) ? "Writersword" : comment.Author,
                        Initials = comment.DisplayInitials
                    };
                    if (comment.Date is DateTime date)
                        element.Date = DateTime.SpecifyKind(date, DateTimeKind.Utc);

                    var paragraphs = new List<W.Paragraph>();
                    foreach (var paragraph in comment.Paragraphs)
                        paragraphs.Add(BuildParagraph(paragraph, ctx));
                    if (paragraphs.Count == 0)
                        paragraphs.Add(new W.Paragraph());

                    // Место значка примечания в начале его текста, как у Word.
                    var first = paragraphs[0];
                    int insertAt = first.GetFirstChild<W.ParagraphProperties>() is null ? 0 : 1;
                    first.InsertAt(new W.Run(
                        new W.RunProperties(new W.FontSize { Val = "16" }),
                        new W.AnnotationReferenceMark()), insertAt);

                    // Вид обсуждения Word хранить негде — он уходит приставкой к тексту
                    // («Требование: …») и по ней же узнаётся при открытии файла.
                    if (comment.ParentId is null && CommentModel.KindPrefix(comment.Kind) is { } prefix)
                    {
                        first.InsertAt(new W.Run(
                            new W.RunProperties(new W.Bold()),
                            new W.Text(prefix + " ") { Space = SpaceProcessingModeValues.Preserve }), insertAt + 1);
                    }

                    // По номеру последнего абзаца commentsExtended.xml находит примечание.
                    string paragraphId = (nextParagraphId++).ToString("X8", CultureInfo.InvariantCulture);
                    paragraphs[paragraphs.Count - 1].ParagraphId = paragraphId;
                    paragraphIds[comment.Id] = paragraphId;

                    foreach (var paragraph in paragraphs)
                        element.AppendChild(paragraph);

                    comments.AppendChild(element);
                }
            }
            finally
            {
                ctx.WritingNote = false;
            }

            var commentsPart = mainPart.AddNewPart<WordprocessingCommentsPart>();
            commentsPart.Comments = comments;
            commentsPart.Comments.Save();

            foreach (var comment in ctx.CommentsToWrite)
            {
                var entry = new W15.CommentEx
                {
                    ParaId = paragraphIds[comment.Id],
                    Done = comment.Done
                };

                if (comment.ParentId is Guid parentId && paragraphIds.TryGetValue(parentId, out var parentParagraphId))
                    entry.ParaIdParent = parentParagraphId;

                extended.AppendChild(entry);
            }

            var extendedPart = mainPart.AddNewPart<WordprocessingCommentsExPart>();
            extendedPart.CommentsEx = extended;
            extendedPart.CommentsEx.Save();
        }
    }
}
