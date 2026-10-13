using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Writersword.Modules.TextEditor.Commands;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Models.Styles;
using Writersword.Modules.TextEditor.Views.Dialogs;

namespace Writersword.Modules.TextEditor.ViewModels
{
    /// <summary>
    /// Примечания рецензентов: заметки на полях к месту в тексте.
    ///
    /// Место отмечено на самих фрагментах текста (RunProperties.CommentIds), поэтому
    /// правка внутри него места не теряет. На полях листа у начала места стоит значок
    /// с инициалами автора; нажатие на него открывает обсуждение — примечание, ответы,
    /// поле нового ответа, «решено» и удаление.
    /// </summary>
    public sealed partial class DocumentViewModel
    {
        /// <summary>
        /// Примечания документа изменились: добавлено, удалено, закрыто, отвечено.
        /// Полотно перерисовывает подсветку мест и значки на полях.
        /// </summary>
        public event Action? CommentsChanged;

        /// <summary>Примечания документа.</summary>
        public IReadOnlyList<CommentModel> Comments => _document.Comments;

        /// <summary>
        /// Оставляет примечание к выделенному месту (без выделения — к слову под
        /// кареткой). Пустой текст — текст спрашивается в окне.
        /// </summary>
        public void InsertComment(string text) => _ = InsertCommentAsync(text, CommentKind.Note);

        /// <summary>Оставляет обсуждение нужного вида: примечание, предложение или требование.</summary>
        public void InsertComment(string text, CommentKind kind) => _ = InsertCommentAsync(text, kind);

        /// <summary>Подпись вида обсуждения: «Примечание», «Предложение», «Требование».</summary>
        public static string CommentKindName(CommentKind kind) => kind switch
        {
            CommentKind.Suggestion => "Предложение",
            CommentKind.Requirement => "Требование",
            _ => "Примечание"
        };

        /// <summary>Сменить вид обсуждения.</summary>
        public void SetCommentKind(CommentModel comment, CommentKind kind)
        {
            if (IsReadOnly) return;

            var root = ThreadRoot(comment);
            if (!_document.Comments.Contains(root) || root.Kind == kind) return;

            BeginEditDelegate?.Invoke("Вид обсуждения: " + CommentKindName(kind).ToLowerInvariant());
            root.Kind = kind;
            CommitEditDelegate?.Invoke();
            NotifyCommentMarkupChanged();
        }

        /// <summary>Открывает обсуждение примечания: по нажатию на его значок на полях.</summary>
        public void RequestCommentThread(CommentModel comment)
        {
            if (comment is null) return;
            _ = OpenCommentThreadAsync(comment);
        }

        private async Task InsertCommentAsync(string text, CommentKind kind)
        {
            try
            {
                if (IsReadOnly) return;

                // Место запоминается до окна: окно забирает фокус, и канвас после
                // него выделение уже не отдаст.
                var ranges = CommentTargetRanges();
                if (ranges.Count == 0) return;

                if (string.IsNullOrWhiteSpace(text))
                {
                    string what = kind switch
                    {
                        CommentKind.Suggestion => "Предложение к выделенному месту текста: вариант, который автор может принять или нет.",
                        CommentKind.Requirement => "Требование к выделенному месту текста: то, что исправить нужно обязательно.",
                        _ => "Комментарий к выделенному месту текста."
                    };

                    var result = await TextEditDialog.ShowAsync(
                        kind == CommentKind.Note ? "Комментарий" : CommentKindName(kind),
                        what + " Он встанет на полях, место в тексте подсветится.\n" +
                        "Enter — новая строка, Ctrl+Enter — готово.",
                        string.Empty);

                    if (result is null || result.Delete || string.IsNullOrWhiteSpace(result.Text)) return;
                    text = result.Text;
                }

                if (IsReadOnly) return;

                BeginEditDelegate?.Invoke("Комментарий");

                var comment = new CommentModel
                {
                    Author = ReviewerName,
                    Date = DateTime.UtcNow,
                    Kind = kind,
                    Paragraphs = BuildCommentParagraphs(text)
                };
                _document.Comments.Add(comment);

                foreach (var (para, from, to) in ranges)
                    ApplyCharPropertyToRange(para, from, to, p => AddCommentId(p, comment.Id), clearAll: false);

                CommitEditDelegate?.Invoke();
                NotifyCommentMarkupChanged();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[COMMENTS] Примечание оставить не удалось");
            }
        }

        // ── Обсуждение прямо из панели рецензии ──────────────────────────
        // Те же действия, что в окне обсуждения, без окна: панель показывает
        // обсуждение целиком и отвечает в нём сама.

        /// <summary>Ответить в обсуждении примечания.</summary>
        public void ReplyToComment(CommentModel comment, string text)
        {
            if (IsReadOnly || string.IsNullOrWhiteSpace(text)) return;

            var root = ThreadRoot(comment);
            if (!_document.Comments.Contains(root)) return;

            BeginEditDelegate?.Invoke("Ответ на комментарий");
            _document.Comments.Add(new CommentModel
            {
                Author = ReviewerName,
                Date = DateTime.UtcNow,
                ParentId = root.Id,
                Paragraphs = BuildCommentParagraphs(text.Trim())
            });
            CommitEditDelegate?.Invoke();
            CommentsChanged?.Invoke();
        }

        /// <summary>Закрыть обсуждение или открыть закрытое снова.</summary>
        public void ToggleCommentDone(CommentModel comment)
        {
            if (IsReadOnly) return;

            var root = ThreadRoot(comment);
            if (!_document.Comments.Contains(root)) return;

            var thread = ThreadOf(root);
            BeginEditDelegate?.Invoke(root.Done ? "Обсуждение открыто" : "Обсуждение закрыто");
            bool done = !root.Done;
            foreach (var item in thread) item.Done = done;
            CommitEditDelegate?.Invoke();
            NotifyCommentMarkupChanged();
        }

        /// <summary>Удалить обсуждение целиком: примечание, ответы и отметку места.</summary>
        public void DeleteCommentThread(CommentModel comment)
        {
            if (IsReadOnly) return;

            var root = ThreadRoot(comment);
            if (!_document.Comments.Contains(root)) return;

            BeginEditDelegate?.Invoke("Удаление комментария");
            RemoveThread(root);
            CommitEditDelegate?.Invoke();
            NotifyCommentMarkupChanged();
        }

        private async Task OpenCommentThreadAsync(CommentModel comment)
        {
            try
            {
                var root = ThreadRoot(comment);
                var thread = ThreadOf(root);

                var entries = thread
                    .Select(c => new CommentThreadEntry(
                        string.IsNullOrWhiteSpace(c.Author) ? "Без имени" : c.Author,
                        c.Date,
                        c.PlainText))
                    .ToList();

                var result = await CommentThreadDialog.ShowAsync(entries, root.Done, CommentedText(root.Id));
                if (result is null || IsReadOnly) return;

                // Обсуждение могло уйти из документа, пока окно было открыто.
                if (!_document.Comments.Contains(root)) return;

                switch (result.Action)
                {
                    case CommentThreadAction.Reply:
                        BeginEditDelegate?.Invoke("Ответ на комментарий");
                        _document.Comments.Add(new CommentModel
                        {
                            Author = ReviewerName,
                            Date = DateTime.UtcNow,
                            ParentId = root.Id,
                            Paragraphs = BuildCommentParagraphs(result.Text)
                        });
                        CommitEditDelegate?.Invoke();
                        CommentsChanged?.Invoke();
                        break;

                    case CommentThreadAction.ToggleDone:
                        BeginEditDelegate?.Invoke(root.Done ? "Обсуждение открыто" : "Обсуждение закрыто");
                        bool done = !root.Done;
                        foreach (var item in thread) item.Done = done;
                        CommitEditDelegate?.Invoke();
                        NotifyCommentMarkupChanged();
                        break;

                    case CommentThreadAction.Delete:
                        BeginEditDelegate?.Invoke("Удаление комментария");
                        RemoveThread(root);
                        CommitEditDelegate?.Invoke();
                        NotifyCommentMarkupChanged();
                        break;
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[COMMENTS] Обсуждение примечания не открылось");
            }
        }

        /// <summary>
        /// Убирает примечания, у которых в тексте не осталось ни одного отмеченного
        /// знака: их место удалено правкой. Вызывается перед сохранением — до этого
        /// отмена удаления должна возвращать место вместе с примечанием.
        /// Возвращает число убранных примечаний.
        /// </summary>
        public int PurgeOrphanComments()
        {
            if (_document.Comments.Count == 0) return 0;

            var anchored = new HashSet<Guid>();
            foreach (var para in EnumerateAllParagraphs())
                foreach (var chunk in para.Chunks)
                    foreach (var run in chunk.Runs)
                        if (run.Properties?.CommentIds is { Count: > 0 } ids)
                            foreach (var id in ids) anchored.Add(id);

            int removed = 0;
            foreach (var comment in _document.Comments.ToList())
            {
                var root = ThreadRoot(comment);
                if (anchored.Contains(root.Id)) continue;

                _document.Comments.Remove(comment);
                removed++;
            }

            return removed;
        }

        /// <summary>Исходное примечание обсуждения: ответ ведёт к нему по цепочке.</summary>
        private CommentModel ThreadRoot(CommentModel comment)
        {
            var current = comment;
            for (int guard = 0; guard < 64 && current.ParentId is Guid parentId; guard++)
            {
                var parent = _document.Comments.FirstOrDefault(c => c.Id == parentId);
                if (parent is null) break;
                current = parent;
            }
            return current;
        }

        /// <summary>Обсуждение: исходное примечание и все ответы по времени.</summary>
        private List<CommentModel> ThreadOf(CommentModel root)
        {
            var thread = new List<CommentModel> { root };
            var replies = _document.Comments
                .Where(c => !ReferenceEquals(c, root) && ReferenceEquals(ThreadRoot(c), root))
                .OrderBy(c => c.Date ?? DateTime.MinValue)
                .ToList();
            thread.AddRange(replies);
            return thread;
        }

        /// <summary>Убирает обсуждение и снимает отметки его места с текста.</summary>
        private void RemoveThread(CommentModel root)
        {
            var thread = ThreadOf(root);
            var ids = new HashSet<Guid>(thread.Select(c => c.Id));

            foreach (var item in thread)
                _document.Comments.Remove(item);

            foreach (var para in EnumerateAllParagraphs())
            {
                bool touched = false;
                foreach (var chunk in para.Chunks)
                    foreach (var run in chunk.Runs)
                        if (run.Properties?.CommentIds is { Count: > 0 } runIds && runIds.Any(ids.Contains))
                            touched = true;

                if (!touched) continue;

                var cells = para.ToCharCells();
                for (int i = 0; i < cells.Count; i++)
                {
                    if (cells[i].Props?.CommentIds is not { Count: > 0 } cellIds || !cellIds.Any(ids.Contains))
                        continue;

                    var props = cells[i].Props!.Clone();
                    props.CommentIds!.RemoveAll(ids.Contains);
                    if (props.CommentIds.Count == 0) props.CommentIds = null;

                    cells[i] = new ParagraphBlock.CharCell(
                        cells[i].Ch, props.IsDefault() ? null : props, cells[i].InlineImageId);
                }

                para.RebuildFromCharCells(cells);
            }
        }

        /// <summary>Текст места примечания одной строкой — для подписи в окне обсуждения.</summary>
        private string? CommentedText(Guid commentId)
        {
            var builder = new StringBuilder();

            foreach (var para in EnumerateAllParagraphs())
            {
                bool inPara = false;
                foreach (var chunk in para.Chunks)
                {
                    foreach (var run in chunk.Runs)
                    {
                        if (run.Properties?.CommentIds?.Contains(commentId) != true || run.IsInlineObject) continue;

                        if (!inPara && builder.Length > 0) builder.Append(' ');
                        inPara = true;
                        builder.Append(run.Text);
                        if (builder.Length > 200) return builder.ToString(0, 200) + "…";
                    }
                }
            }

            return builder.Length > 0 ? builder.ToString() : null;
        }

        private static void AddCommentId(RunProperties properties, Guid commentId)
        {
            properties.CommentIds ??= new List<Guid>();
            if (!properties.CommentIds.Contains(commentId))
                properties.CommentIds.Add(commentId);
        }

        /// <summary>Абзацы текста примечания: строка окна — абзац.</summary>
        private static List<ParagraphBlock> BuildCommentParagraphs(string text)
        {
            var lines = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n').Split('\n');
            var paragraphs = new List<ParagraphBlock>(lines.Length);

            foreach (var line in lines)
            {
                var paragraph = new ParagraphBlock
                {
                    Properties = new ParagraphProperties { SpaceBefore = 0.0, SpaceAfter = 0.0 }
                };
                paragraph.SetPlainText(line);
                paragraphs.Add(paragraph);
            }

            return paragraphs;
        }

        /// <summary>
        /// Места, к которым встанет новое примечание: выделение (в нескольких абзацах —
        /// по куску в каждом), без выделения — слово под кареткой, в пустом месте —
        /// знак перед кареткой.
        /// </summary>
        private List<(ParagraphBlock Para, int From, int To)> CommentTargetRanges()
        {
            var ranges = new List<(ParagraphBlock Para, int From, int To)>();

            if (TableActiveCellParagraph is { } cellParagraph)
            {
                var cellRanges = GetCellSelectionRangesDelegate?.Invoke();
                if (cellRanges is { Count: > 0 })
                {
                    foreach (var (paraId, from, to) in cellRanges)
                    {
                        if (to <= from) continue;
                        if (DocumentModelHelper.FindParagraph(_document, paraId) is { } found)
                            ranges.Add((found, from, to));
                    }
                }
                else if (_selectionEnd > _selectionStart)
                {
                    ranges.Add((cellParagraph, _selectionStart, _selectionEnd));
                }
            }
            else if (SelectionParagraphs.Count > 1)
            {
                for (int idx = 0; idx < SelectionParagraphs.Count; idx++)
                {
                    var pvm = SelectionParagraphs[idx];
                    int len = pvm.Model.TotalLength;
                    int s = idx == 0 ? pvm.SelectionStart : 0;
                    int e = idx == SelectionParagraphs.Count - 1 ? pvm.SelectionEnd : len;
                    s = Math.Clamp(s, 0, len);
                    e = Math.Clamp(e, 0, len);
                    if (e > s) ranges.Add((pvm.Model, s, e));
                }
            }
            else if (_activeParagraph is not null && _selectionEnd > _selectionStart)
            {
                int len = _activeParagraph.Model.TotalLength;
                int s = Math.Clamp(_selectionStart, 0, len);
                int e = Math.Clamp(_selectionEnd, 0, len);
                if (e > s) ranges.Add((_activeParagraph.Model, s, e));
            }

            if (ranges.Count > 0) return ranges;

            if (GetCaretWordRangeDelegate?.Invoke() is { } word && word.To > word.From && word.Pvm.Model is { } wordPara)
            {
                ranges.Add((wordPara, word.From, word.To));
                return ranges;
            }

            var caret = GetCaretTargetDelegate?.Invoke();
            var para = caret?.Para ?? _activeParagraph?.Model;
            if (para is null || para.TotalLength == 0) return ranges;

            int at = Math.Clamp(caret?.CharIndex ?? _selectionStart, 0, para.TotalLength);
            ranges.Add(at > 0 ? (para, at - 1, at) : (para, 0, 1));
            return ranges;
        }

        /// <summary>
        /// Отметки примечаний на тексте или подсветка мест изменились: раскладки
        /// абзацев с прежней подсветкой больше не годятся.
        /// </summary>
        private void NotifyCommentMarkupChanged()
        {
            ParagraphFormatChanged?.Invoke();
            CommentsChanged?.Invoke();
        }
    }
}
