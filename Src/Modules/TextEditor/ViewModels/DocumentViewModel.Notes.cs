using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Models.Styles;
using Writersword.Modules.TextEditor.Services;
using Writersword.Modules.TextEditor.Views.Dialogs;

namespace Writersword.Modules.TextEditor.ViewModels
{
    /// <summary>
    /// Сноски: вставка, правка текста и удаление.
    ///
    /// Сноска — пояснение к месту в тексте: в тексте стоит маленькая цифра, а само
    /// пояснение под той же цифрой — внизу страницы (концевая сноска — в конце
    /// документа). Знак сноски живёт в абзаце одним символом, как картинка в строке,
    /// поэтому каретка, выделение, удаление и отмена работают с ним без особых правил.
    /// Текст сноски правится в отдельном окне: каретки в области сносок нет.
    /// </summary>
    public sealed partial class DocumentViewModel
    {
        /// <summary>Кегль текста сноски у Word по умолчанию.</summary>
        private const double DefaultNoteFontSizePt = 10.0;

        public void InsertFootnote() => _ = InsertNoteAsync(NoteKind.Footnote);

        public void InsertEndnote() => _ = InsertNoteAsync(NoteKind.Endnote);

        /// <summary>
        /// Открывает окно правки текста сноски: по двойному щелчку на знаке сноски или
        /// на её тексте внизу страницы.
        /// </summary>
        public void RequestNoteEdit(NoteBlock note)
        {
            if (note is null) return;
            _ = EditNoteAsync(note);
        }

        /// <summary>Сноска, стоящая в строке текста, по Id.</summary>
        private NoteBlock? FindInlineNoteById(Guid id)
        {
            foreach (var section in _document.Sections)
                foreach (var block in section.InlineObjects)
                    if (block is NoteBlock note && note.Id == id)
                        return note;
            return null;
        }

        private static string NoteTitle(NoteKind kind)
            => kind == NoteKind.Endnote ? "Концевая сноска" : "Сноска";

        private static string NotePrompt(NoteKind kind)
            => (kind == NoteKind.Endnote
                    ? "Текст пояснения. Он встанет в конце документа под номером, который появится в тексте."
                    : "Текст пояснения. Он встанет внизу страницы под номером, который появится в тексте.")
               + "\nEnter — новая строка, Ctrl+Enter — готово.";

        private async Task InsertNoteAsync(NoteKind kind)
        {
            try
            {
                if (IsReadOnly || _document.Sections.Count == 0) return;

                // Место знака запоминается до окна: окно забирает фокус, и канвас
                // после него каретку уже не отдаст.
                var target = GetCaretTargetDelegate?.Invoke();
                ParagraphBlock? para = target?.Para;
                int at = target?.CharIndex ?? 0;

                if (para is null && _activeParagraph is not null)
                {
                    para = _activeParagraph.Model;
                    at = _activeParagraph.SelectionStart;
                }

                if (para is null) return;

                var result = await TextEditDialog.ShowAsync(NoteTitle(kind), NotePrompt(kind), string.Empty);
                if (result is null || result.Delete) return;

                // Пока окно было открыто, абзац мог уйти из документа (автосохранение
                // с пересборкой, отмена из другого окна): знак ставить некуда.
                if (IsReadOnly || !EnumerateAllParagraphs().Any(p => ReferenceEquals(p, para))) return;
                at = Math.Max(0, Math.Min(at, para.TotalLength));

                BeginEditDelegate?.Invoke(kind == NoteKind.Endnote ? "Вставка концевой сноски" : "Вставка сноски");

                var (paragraphProperties, runProperties, markProperties) = NoteTextTemplate(kind, null);
                var note = new NoteBlock
                {
                    Kind = kind,
                    MarkProperties = markProperties,
                    Paragraphs = BuildNoteParagraphs(result.Text, paragraphProperties, runProperties, leadingSpace: true)
                };

                _document.Sections[0].InlineObjects.Add(note);
                para.InsertInlineObject(at, note.Id);
                InlineImageInserted?.Invoke(para, at);

                CommitEditDelegate?.Invoke();
                StructureChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[NOTES] Сноску вставить не удалось");
            }
        }

        private async Task EditNoteAsync(NoteBlock note)
        {
            try
            {
                if (IsReadOnly) return;

                bool leadingSpace = string.IsNullOrEmpty(note.CustomMark);
                string current = NoteEditableText(note, leadingSpace);

                var result = await TextEditDialog.ShowAsync(
                    NoteTitle(note.Kind),
                    "Enter — новая строка, Ctrl+Enter — готово.",
                    current,
                    deleteLabel: "Удалить сноску");

                if (result is null || IsReadOnly) return;

                // Сноска могла уйти из документа, пока окно было открыто.
                if (!ReferenceEquals(FindInlineNoteById(note.Id), note)) return;

                if (result.Delete)
                {
                    RemoveNote(note);
                    return;
                }

                if (string.Equals(NormalizeLineBreaks(result.Text), current, StringComparison.Ordinal)) return;

                BeginEditDelegate?.Invoke("Правка сноски");

                var (paragraphProperties, runProperties, _) = NoteTextTemplate(note.Kind, note);
                string newText = FullNoteText(note, result.Text, leadingSpace);
                note.Paragraphs = RichTextEdit.Apply(note.Paragraphs, newText, runProperties, paragraphProperties);

                CommitEditDelegate?.Invoke();
                StructureChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[NOTES] Текст сноски изменить не удалось");
            }
        }

        /// <summary>Убирает сноску вместе с её знаком в тексте.</summary>
        private void RemoveNote(NoteBlock note)
        {
            ParagraphBlock? owner = null;
            int index = -1;

            foreach (var para in EnumerateAllParagraphs())
            {
                index = para.IndexOfInlineObject(note.Id);
                if (index < 0) continue;

                owner = para;
                break;
            }

            BeginEditDelegate?.Invoke("Удаление сноски");

            owner?.SpliceText(index, index + 1, string.Empty);

            foreach (var section in _document.Sections)
                section.InlineObjects.Remove(note);

            if (owner is not null) InlineObjectsChanged?.Invoke(owner);

            CommitEditDelegate?.Invoke();
            StructureChanged?.Invoke();
        }

        private static string NormalizeLineBreaks(string text)
            => (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');

        /// <summary>
        /// Текст сноски для окна правки: абзацы через перевод строки. Пробел, которым
        /// текст отделён от номера, в окно не идёт — его ставит сама сноска.
        /// </summary>
        private static string NoteEditableText(NoteBlock note, bool leadingSpace)
        {
            string text = note.PlainText;

            if (leadingSpace && text.Length > 0 && (text[0] == ' ' || text[0] == ' ' || text[0] == '\t'))
                text = text.Substring(1);

            return text;
        }

        /// <summary>
        /// Полный текст сноски из текста окна: пробел между номером и текстом, который
        /// окно не показывает, возвращается на место. Меняется только набранное в окне —
        /// остальное оформление сноски сохраняет RichTextEdit.
        /// </summary>
        private static string FullNoteText(NoteBlock note, string text, bool leadingSpace)
        {
            text = NormalizeLineBreaks(text);
            if (!leadingSpace) return text;

            string old = note.PlainText;
            char separator = old.Length > 0 && (old[0] == ' ' || old[0] == '\u00A0' || old[0] == '\t') ? old[0] : ' ';
            return separator + text.TrimStart(' ', '\u00A0', '\t');
        }

        /// <summary>
        /// Оформление текста новой сноски: абзац, знаки и цифра в начале. Берётся у
        /// самой сноски, если она правится; иначе — у первой сноски того же вида, чтобы
        /// все сноски документа выглядели одинаково; в документе без сносок — как у
        /// Word: шрифт обычного текста кеглем 10, одинарный интервал, без отступов.
        /// </summary>
        private (ParagraphProperties Paragraph, RunProperties Run, RunProperties? Mark) NoteTextTemplate(
            NoteKind kind, NoteBlock? own)
        {
            NoteBlock? source = own is { Paragraphs.Count: > 0 } ? own : null;

            if (source is null)
            {
                foreach (var section in _document.Sections)
                {
                    foreach (var block in section.InlineObjects)
                    {
                        if (block is NoteBlock existing && existing.Kind == kind && existing.Paragraphs.Count > 0)
                        {
                            source = existing;
                            break;
                        }
                    }

                    if (source is not null) break;
                }
            }

            if (source is null)
            {
                return (
                    new ParagraphProperties
                    {
                        Alignment = TextAlignment.Left,
                        FirstLineIndent = 0.0,
                        LeftIndent = 0.0,
                        RightIndent = 0.0,
                        SpaceBefore = 0.0,
                        SpaceAfter = 0.0,
                        LineSpacingRule = LineSpacingRule.Auto,
                        LineSpacingValue = 1.0
                    },
                    new RunProperties { FontSize = DefaultNoteFontSizePt },
                    null);
            }

            var first = source.Paragraphs[0];

            var paragraphProperties = first.Properties.Clone();
            paragraphProperties.MarkInserted = null;
            paragraphProperties.MarkDeleted = null;
            paragraphProperties.FormatChange = null;

            RunProperties? runProperties = null;
            foreach (var chunk in first.Chunks)
            {
                foreach (var run in chunk.Runs)
                {
                    if (run.Properties is null || run.IsInlineObject) continue;
                    runProperties = run.Properties.WithoutRevisions();
                    break;
                }

                if (runProperties is not null) break;
            }

            return (
                paragraphProperties,
                runProperties ?? new RunProperties { FontSize = DefaultNoteFontSizePt },
                source.MarkProperties?.WithoutRevisions());
        }

        /// <summary>
        /// Абзацы сноски из текста окна правки: строка окна — абзац. Перед текстом
        /// первого абзаца ставится пробел, отделяющий его от номера, как у Word.
        /// </summary>
        private static List<ParagraphBlock> BuildNoteParagraphs(
            string text, ParagraphProperties paragraphProperties, RunProperties runProperties, bool leadingSpace)
        {
            string[] lines = NormalizeLineBreaks(text).Split('\n');
            var paragraphs = new List<ParagraphBlock>(lines.Length);

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (i == 0 && leadingSpace) line = " " + line.TrimStart(' ', ' ', '\t');

                var paragraph = new ParagraphBlock { Properties = paragraphProperties.Clone() };
                paragraph.SetPlainText(line);

                foreach (var chunk in paragraph.Chunks)
                    foreach (var run in chunk.Runs)
                        run.Properties = runProperties.Clone();

                paragraphs.Add(paragraph);
            }

            return paragraphs;
        }

        /// <summary>
        /// Копия сноски для вставленного абзаца: своя сноска со своими абзацами, чтобы
        /// правка текста одной не меняла другую.
        /// </summary>
        private NoteBlock CloneNoteForPaste(NoteBlock source)
        {
            var copy = (NoteBlock)DocumentCloner.CloneBlock(source);
            copy.Id = Guid.NewGuid();

            foreach (var paragraph in copy.Paragraphs)
            {
                paragraph.Id = Guid.NewGuid();
                foreach (var chunk in paragraph.Chunks)
                    chunk.Id = Guid.NewGuid();
            }

            return copy;
        }
    }
}
