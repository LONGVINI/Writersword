using System;
using System.Collections.Generic;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using W = DocumentFormat.OpenXml.Wordprocessing;
using W15 = DocumentFormat.OpenXml.Office2013.Word;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Примечания рецензентов из .docx.
    ///
    /// У Word текст примечаний лежит в части comments.xml, а в тексте документа стоят
    /// границы выделенного места (w:commentRangeStart … w:commentRangeEnd) и ссылка
    /// w:commentReference. Раньше всё это пропускалось, и примечания терялись. Теперь
    /// примечание становится записью документа, а место — отметкой на фрагментах
    /// текста между границами: отметка едет вместе с текстом при любой правке.
    /// Ответы и «обсуждение закрыто» Word хранит в commentsExtended.xml.
    /// </summary>
    public sealed partial class ImportService
    {
        // Примечания по номеру записи Word.
        private readonly Dictionary<string, CommentModel> _commentsByWordId = new(StringComparer.Ordinal);

        // Примечания, чьё выделенное место сейчас идёт: их Id ставятся на каждый
        // фрагмент текста, разобранный между границами.
        private readonly List<Guid> _openComments = new();

        // Примечания, получившие хоть один отмеченный фрагмент.
        private readonly HashSet<Guid> _markedComments = new();

        // Примечание без выделенного места: отметка встанет на следующий фрагмент.
        private readonly List<Guid> _pendingPointComments = new();

        // Отметки примечаний не ставятся: идёт разбор текста сноски или примечания.
        private bool _commentMarksSuspended;

        /// <summary>
        /// Разбирает записи примечаний. Вызывается до разбора тела документа: границы
        /// мест в тексте ссылаются на записи по их номерам.
        /// </summary>
        private void ImportComments(
            MainDocumentPart mainPart,
            DocumentModel doc,
            SectionModel section,
            DocxFormatResolver resolver,
            DocxNumberingMap numbering,
            Dictionary<int, Guid> listIdMap,
            Dictionary<string, byte[]> extractedImages,
            List<string> warnings)
        {
            _commentsByWordId.Clear();
            _openComments.Clear();
            _markedComments.Clear();
            _pendingPointComments.Clear();
            _commentMarksSuspended = false;

            var comments = mainPart.WordprocessingCommentsPart?.Comments;
            if (comments is null) return;

            // Примечание узнаётся в commentsExtended.xml по w14:paraId своего последнего абзаца.
            var byParagraphId = new Dictionary<string, CommentModel>(StringComparer.OrdinalIgnoreCase);

            bool suspended = _commentMarksSuspended;
            _commentMarksSuspended = true;
            try
            {
                foreach (var comment in comments.Elements<W.Comment>())
                {
                    string? wordId = comment.Id?.Value;
                    if (string.IsNullOrEmpty(wordId)) continue;

                    var model = new CommentModel
                    {
                        Author = comment.Author?.Value ?? string.Empty,
                        Initials = string.IsNullOrWhiteSpace(comment.Initials?.Value) ? null : comment.Initials!.Value,
                        Date = comment.Date?.Value is DateTime date
                            ? (date.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(date, DateTimeKind.Utc) : date.ToUniversalTime())
                            : null
                    };

                    string? lastParagraphId = null;
                    foreach (var paragraph in comment.Descendants<W.Paragraph>())
                    {
                        model.Paragraphs.Add(ImportCellParagraph(
                            paragraph, section, resolver, numbering, listIdMap,
                            mainPart, extractedImages, warnings));

                        if (paragraph.ParagraphId?.Value is { Length: > 0 } paragraphId)
                            lastParagraphId = paragraphId;
                    }

                    if (model.Paragraphs.Count == 0)
                        model.Paragraphs.Add(new ParagraphBlock());

                    // Вид обсуждения Writersword: приставка «Предложение:» или
                    // «Требование:» в начале текста. Сама приставка из текста уходит.
                    ReadCommentKind(model);

                    doc.Comments.Add(model);
                    _commentsByWordId[wordId!] = model;
                    if (lastParagraphId is not null) byParagraphId[lastParagraphId] = model;
                }
            }
            finally
            {
                _commentMarksSuspended = suspended;
            }

            var extended = mainPart.WordprocessingCommentsExPart?.CommentsEx;
            if (extended is null) return;

            foreach (var entry in extended.Elements<W15.CommentEx>())
            {
                if (entry.ParaId?.Value is not { } paragraphId) continue;
                if (!byParagraphId.TryGetValue(paragraphId, out var model)) continue;

                model.Done = entry.Done?.Value == true;

                if (entry.ParaIdParent?.Value is { } parentId
                    && byParagraphId.TryGetValue(parentId, out var parent)
                    && !ReferenceEquals(parent, model))
                    model.ParentId = parent.Id;
            }
        }

        private static void ReadCommentKind(CommentModel model)
        {
            var first = model.Paragraphs[0];
            string text = first.GetPlainText();

            foreach (var kind in new[] { CommentKind.Suggestion, CommentKind.Requirement })
            {
                if (CommentModel.KindPrefix(kind) is not { } prefix) continue;
                if (!text.StartsWith(prefix, StringComparison.Ordinal)) continue;

                int end = prefix.Length;
                while (end < text.Length && text[end] == ' ') end++;

                first.SpliceText(0, end, string.Empty);
                model.Kind = kind;
                return;
            }
        }

        /// <summary>
        /// Граница места примечания среди детей абзаца или тела документа. True —
        /// элемент разобран здесь.
        /// </summary>
        private bool TryHandleCommentBoundary(OpenXmlElement element)
        {
            switch (element)
            {
                case W.CommentRangeStart start:
                    if (start.Id?.Value is { } startId && _commentsByWordId.TryGetValue(startId, out var opened)
                        && !_openComments.Contains(opened.Id))
                        _openComments.Add(opened.Id);
                    return true;

                case W.CommentRangeEnd end:
                    if (end.Id?.Value is { } endId && _commentsByWordId.TryGetValue(endId, out var closed))
                        _openComments.Remove(closed.Id);
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Ссылка на примечание (w:commentReference). У примечания с выделенным местом
        /// она стоит сразу за ним и ничего не добавляет. Примечание без места
        /// привязывается к фрагменту перед ссылкой, а в начале абзаца — к следующему.
        /// </summary>
        private void AnchorCommentReference(W.CommentReference reference, TextChunk chunk)
        {
            if (_commentMarksSuspended) return;
            if (reference.Id?.Value is not { } wordId) return;
            if (!_commentsByWordId.TryGetValue(wordId, out var comment)) return;
            if (_markedComments.Contains(comment.Id)) return;

            for (int i = chunk.Runs.Count - 1; i >= 0; i--)
            {
                var run = chunk.Runs[i];
                if (string.IsNullOrEmpty(run.Text)) continue;

                MarkRunWithComments(run, new[] { comment.Id });
                return;
            }

            if (!_pendingPointComments.Contains(comment.Id))
                _pendingPointComments.Add(comment.Id);
        }

        /// <summary>
        /// Ставит отметки открытых примечаний на фрагменты, добавленные в абзац
        /// начиная с заданного.
        /// </summary>
        private void MarkOpenComments(TextChunk chunk, int firstRun)
        {
            if (_commentMarksSuspended) return;
            if (_openComments.Count == 0 && _pendingPointComments.Count == 0) return;

            for (int i = firstRun; i < chunk.Runs.Count; i++)
            {
                var run = chunk.Runs[i];
                if (string.IsNullOrEmpty(run.Text)) continue;

                if (_openComments.Count > 0)
                    MarkRunWithComments(run, _openComments);

                if (_pendingPointComments.Count > 0)
                {
                    MarkRunWithComments(run, _pendingPointComments);
                    _pendingPointComments.Clear();
                }
            }
        }

        private void MarkRunWithComments(RunModel run, IEnumerable<Guid> commentIds)
        {
            // Свойства бывают общими у нескольких ранов: отметка ставится на копию.
            var properties = run.Properties?.Clone() ?? new RunProperties();
            properties.CommentIds ??= new List<Guid>();

            foreach (var id in commentIds)
            {
                if (!properties.CommentIds.Contains(id)) properties.CommentIds.Add(id);
                _markedComments.Add(id);
            }

            run.Properties = properties;
        }
    }
}
