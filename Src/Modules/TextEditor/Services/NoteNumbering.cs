using System;
using System.Collections.Generic;
using System.Text;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Rendering;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>Знак сноски в тексте: сама сноска, абзац со знаком и номер.</summary>
    /// <param name="Note">Сноска.</param>
    /// <param name="Owner">Абзац, в котором стоит знак сноски.</param>
    /// <param name="Mark">Текст знака: номер или свой знак сноски.</param>
    /// <param name="InTable">Знак стоит в ячейке таблицы.</param>
    /// <param name="Ordinal">Порядковое место знака среди сносок своего вида, с нуля.</param>
    public readonly record struct NoteReference(
        NoteBlock Note, ParagraphBlock Owner, string Mark, bool InTable, int Ordinal);

    /// <summary>
    /// Номера сносок документа. Номер в сноске не хранится: сноски нумеруются по
    /// порядку знаков в тексте — сверху вниз, ячейки таблицы по строкам слева
    /// направо, — отдельным счётом для сносок и для концевых сносок. Сноска со своим
    /// знаком (звёздочкой) в счёте не участвует.
    ///
    /// Знак, спрятанный правкой рецензирования при выбранном виде, сноской не
    /// считается: её нет ни в счёте, ни внизу страницы — как у Word.
    /// </summary>
    public sealed class NoteNumbering
    {
        /// <summary>Нумерация документа без сносок.</summary>
        public static readonly NoteNumbering Empty = new();

        private readonly Dictionary<Guid, NoteReference> _byId = new();
        private readonly List<NoteReference> _footnotes = new();
        private readonly List<NoteReference> _endnotes = new();
        private readonly HashSet<ParagraphBlock> _footnoteOwners = new();

        private NoteNumbering() { }

        /// <summary>Сноски в порядке их знаков в тексте.</summary>
        public IReadOnlyList<NoteReference> Footnotes => _footnotes;

        /// <summary>Концевые сноски в порядке их знаков в тексте.</summary>
        public IReadOnlyList<NoteReference> Endnotes => _endnotes;

        /// <summary>В документе есть хоть одна сноска или концевая сноска.</summary>
        public bool HasNotes => _byId.Count > 0;

        /// <summary>Текст знака сноски по её Id или null, если объект с этим Id не сноска.</summary>
        public string? MarkOf(Guid noteId)
            => _byId.TryGetValue(noteId, out var reference) ? reference.Mark : null;

        /// <summary>В абзаце стоит знак хотя бы одной сноски (не концевой).</summary>
        public bool HasFootnoteMarksIn(ParagraphBlock paragraph) => _footnoteOwners.Contains(paragraph);

        /// <summary>Знак сноски по её Id.</summary>
        public bool TryGet(Guid noteId, out NoteReference reference)
            => _byId.TryGetValue(noteId, out reference);

        /// <summary>Считает номера сносок документа при выбранном виде исправлений.</summary>
        public static NoteNumbering Compute(DocumentModel? document, RevisionView view = RevisionView.AllMarkup)
        {
            if (document is null) return Empty;

            Dictionary<Guid, NoteBlock>? notes = null;
            foreach (var section in document.Sections)
            {
                foreach (var block in section.InlineObjects)
                {
                    if (block is not NoteBlock note) continue;
                    notes ??= new Dictionary<Guid, NoteBlock>();
                    notes[note.Id] = note;
                }
            }

            if (notes is null) return Empty;

            var numbering = new NoteNumbering();
            int footnoteNumber = Math.Max(1, document.FootnoteNumberStart);
            int endnoteNumber = Math.Max(1, document.EndnoteNumberStart);

            void Visit(ParagraphBlock paragraph, bool inTable)
            {
                if (RevisionDisplay.IsParagraphHidden(paragraph, view)) return;

                foreach (var chunk in paragraph.Chunks)
                {
                    foreach (var run in chunk.Runs)
                    {
                        if (run.InlineImageId is not Guid id) continue;
                        if (!notes.TryGetValue(id, out var note)) continue;
                        if (numbering._byId.ContainsKey(id)) continue;
                        if (RevisionDisplay.IsRunHidden(run.Properties, view)) continue;

                        string mark;
                        if (!string.IsNullOrEmpty(note.CustomMark))
                        {
                            mark = note.CustomMark!;
                        }
                        else if (note.Kind == NoteKind.Endnote)
                        {
                            mark = Format(endnoteNumber++, document.EndnoteNumberFormat);
                        }
                        else
                        {
                            mark = Format(footnoteNumber++, document.FootnoteNumberFormat);
                        }

                        var target = note.Kind == NoteKind.Endnote ? numbering._endnotes : numbering._footnotes;
                        var reference = new NoteReference(note, paragraph, mark, inTable, target.Count);
                        numbering._byId[id] = reference;
                        target.Add(reference);

                        if (note.Kind == NoteKind.Footnote)
                            numbering._footnoteOwners.Add(paragraph);
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
                            Visit(paragraph, inTable: false);
                            break;

                        case TableBlock table:
                            foreach (var cell in CellsInReadingOrder(table))
                                foreach (var cellParagraph in cell.ParagraphsDeep())
                                    Visit(cellParagraph, inTable: true);
                            break;
                    }
                }
            }

            return numbering;
        }

        /// <summary>Ячейки таблицы по строкам сверху вниз, в строке — слева направо.</summary>
        private static List<TableCell> CellsInReadingOrder(TableBlock table)
        {
            var cells = new List<TableCell>(table.Cells);
            cells.Sort((a, b) => a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.Column.CompareTo(b.Column));
            return cells;
        }

        /// <summary>Id сносок, чьи знаки стоят в абзаце, в порядке знаков.</summary>
        public static IEnumerable<Guid> NoteIdsOf(ParagraphBlock paragraph, NoteNumbering numbering)
        {
            if (!numbering.HasNotes) yield break;

            foreach (var chunk in paragraph.Chunks)
                foreach (var run in chunk.Runs)
                    if (run.InlineImageId is Guid id && numbering._byId.ContainsKey(id))
                        yield return id;
        }

        /// <summary>Номер сноски в заданном виде нумерации.</summary>
        public static string Format(int number, NoteNumberFormat format)
        {
            if (number < 1) number = 1;

            return format switch
            {
                NoteNumberFormat.LowerRoman => Roman(number).ToLowerInvariant(),
                NoteNumberFormat.UpperRoman => Roman(number),
                NoteNumberFormat.LowerLetter => Letters(number, 'a'),
                NoteNumberFormat.UpperLetter => Letters(number, 'A'),
                NoteNumberFormat.Symbols => Symbols(number),
                _ => number.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
        }

        private static readonly (int Value, string Text)[] RomanParts =
        {
            (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"),
            (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")
        };

        private static string Roman(int number)
        {
            // Римскими цифрами Word пишет до 32767; дальше счёт идёт арабскими.
            if (number > 32767) return number.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var builder = new StringBuilder();
            foreach (var (value, text) in RomanParts)
            {
                while (number >= value)
                {
                    builder.Append(text);
                    number -= value;
                }
            }
            return builder.ToString();
        }

        /// <summary>
        /// Буквами, как у Word: a…z, затем aa, bb…zz, затем aaa — буква повторяется,
        /// а не идёт счёт по разрядам.
        /// </summary>
        private static string Letters(int number, char first)
        {
            int index = (number - 1) % 26;
            int repeat = (number - 1) / 26 + 1;
            return new string((char)(first + index), repeat);
        }

        private static readonly string[] SymbolSet = { "*", "†", "‡", "§" };

        /// <summary>
        /// Знаками, как у Word: звёздочка, крестик, двойной крестик, параграф, затем
        /// те же знаки по два, по три и так далее.
        /// </summary>
        private static string Symbols(int number)
        {
            string symbol = SymbolSet[(number - 1) % SymbolSet.Length];
            int repeat = (number - 1) / SymbolSet.Length + 1;

            var builder = new StringBuilder(symbol.Length * repeat);
            for (int i = 0; i < repeat; i++) builder.Append(symbol);
            return builder.ToString();
        }
    }
}
