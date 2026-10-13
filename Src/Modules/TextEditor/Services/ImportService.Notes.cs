using System;
using System.Collections.Generic;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Сноски и концевые сноски из .docx.
    ///
    /// У Word текст сносок лежит в отдельных частях файла (footnotes.xml,
    /// endnotes.xml), а в абзаце стоит только ссылка w:footnoteReference с номером
    /// записи. Раньше ссылка пропускалась, и сноски пропадали из документа вместе с
    /// текстом. Теперь ссылка становится знаком сноски — объектом строки, — а текст
    /// сноски разбирается тем же разбором абзацев, что и ячейка таблицы.
    /// </summary>
    public sealed partial class ImportService
    {
        // Нумерация списков документа: абзацы сносок разбираются по ходу разбора
        // абзаца со ссылкой, а туда она не передаётся.
        private DocxNumberingMap? _noteNumbering;
        private Dictionary<int, Guid>? _noteListIdMap;

        // Идёт разбор текста сноски. Ссылка на сноску внутри сноски не разбирается:
        // у Word её быть не может, а повреждённый файл не должен зациклить импорт.
        private bool _importingNote;

        // Оформление цифры в начале текста разбираемой сноски (ран с w:footnoteRef).
        private RunProperties? _noteMarkProperties;

        /// <summary>Готовит разбор сносок: вызывается до разбора тела документа.</summary>
        private void BeginNoteImport(DocxNumberingMap numbering, Dictionary<int, Guid> listIdMap)
        {
            _noteNumbering = numbering;
            _noteListIdMap = listIdMap;
            _importingNote = false;
            _noteMarkProperties = null;
        }

        /// <summary>
        /// Вид и начало нумерации сносок: общие для документа (w:settings) и заданные
        /// последним разделом — тем же, чьи параметры страницы берёт импорт.
        /// </summary>
        private static void ApplyNoteSettings(MainDocumentPart mainPart, W.Body body, DocumentModel doc)
        {
            var settings = mainPart.DocumentSettingsPart?.Settings;

            ReadNoteNumbering(settings?.GetFirstChild<W.FootnoteDocumentWideProperties>(), doc, NoteKind.Footnote);
            ReadNoteNumbering(settings?.GetFirstChild<W.EndnoteDocumentWideProperties>(), doc, NoteKind.Endnote);

            var finalSection = body.Elements<W.SectionProperties>().LastOrDefault();
            ReadNoteNumbering(finalSection?.GetFirstChild<W.FootnoteProperties>(), doc, NoteKind.Footnote);
            ReadNoteNumbering(finalSection?.GetFirstChild<W.EndnoteProperties>(), doc, NoteKind.Endnote);
        }

        private static void ReadNoteNumbering(OpenXmlElement? properties, DocumentModel doc, NoteKind kind)
        {
            if (properties is null) return;

            if (properties.GetFirstChild<W.NumberingFormat>()?.Val?.Value is { } format)
            {
                var mapped = MapNoteNumberFormat(format);
                if (kind == NoteKind.Endnote) doc.EndnoteNumberFormat = mapped;
                else doc.FootnoteNumberFormat = mapped;
            }

            if (properties.GetFirstChild<W.NumberingStart>()?.Val?.Value is { } start && start >= 1)
            {
                if (kind == NoteKind.Endnote) doc.EndnoteNumberStart = start;
                else doc.FootnoteNumberStart = start;
            }
        }

        private static NoteNumberFormat MapNoteNumberFormat(W.NumberFormatValues format)
        {
            if (format == W.NumberFormatValues.LowerRoman) return NoteNumberFormat.LowerRoman;
            if (format == W.NumberFormatValues.UpperRoman) return NoteNumberFormat.UpperRoman;
            if (format == W.NumberFormatValues.LowerLetter) return NoteNumberFormat.LowerLetter;
            if (format == W.NumberFormatValues.UpperLetter) return NoteNumberFormat.UpperLetter;
            if (format == W.NumberFormatValues.Chicago) return NoteNumberFormat.Symbols;
            return NoteNumberFormat.Decimal;
        }

        /// <summary>
        /// Ссылка на сноску в ране абзаца: сноска разбирается из своей части файла и
        /// кладётся в объекты строки раздела, в текст абзаца встаёт её знак.
        /// Возвращает сноску или null, если записи с таким номером в файле нет.
        /// </summary>
        private NoteBlock? AppendNoteReference(
            long? noteId,
            NoteKind kind,
            TextChunk chunk,
            SectionModel section,
            DocxFormatResolver resolver,
            RunProperties runProps,
            MainDocumentPart mainPart,
            Dictionary<string, byte[]> extractedImages,
            List<string> warnings)
        {
            if (_importingNote || noteId is null || _noteNumbering is null || _noteListIdMap is null)
                return null;

            OpenXmlElement? source = kind == NoteKind.Endnote
                ? mainPart.EndnotesPart?.Endnotes?.Elements<W.Endnote>()
                    .FirstOrDefault(n => n.Id?.Value == noteId)
                : mainPart.FootnotesPart?.Footnotes?.Elements<W.Footnote>()
                    .FirstOrDefault(n => n.Id?.Value == noteId);

            if (source is null)
            {
                warnings.Add(kind == NoteKind.Endnote
                    ? "В документе есть ссылка на концевую сноску, текста которой в файле нет, — ссылка пропущена."
                    : "В документе есть ссылка на сноску, текста которой в файле нет, — ссылка пропущена.");
                return null;
            }

            var note = new NoteBlock { Kind = kind };

            // Разбор текста сноски идёт посреди разбора абзаца со ссылкой: состояние
            // этого абзаца (плавающие объекты, имена перемещений, выравнивание формул)
            // на время откладывается и возвращается.
            bool floatingAllowed = _floatingAllowed;
            var cellFloatCollector = _cellFloatCollector;
            string? moveFromName = _moveFromName;
            string? moveToName = _moveToName;
            var formulaAlignment = _formulaParagraphAlignment;

            _floatingAllowed = false;
            _cellFloatCollector = null;
            _formulaParagraphAlignment = null;
            _noteMarkProperties = null;
            _importingNote = true;

            // Текст сноски не входит в место примечания, внутри которого стоит её знак.
            bool commentMarksSuspended = _commentMarksSuspended;
            _commentMarksSuspended = true;

            try
            {
                // Таблица в сноске раскладывается по абзацам: таблиц в области сносок нет.
                foreach (var paragraph in source.Descendants<W.Paragraph>())
                {
                    note.Paragraphs.Add(ImportCellParagraph(
                        paragraph, section, resolver, _noteNumbering, _noteListIdMap,
                        mainPart, extractedImages, warnings));
                }
            }
            finally
            {
                _importingNote = false;
                _commentMarksSuspended = commentMarksSuspended;
                _floatingAllowed = floatingAllowed;
                _cellFloatCollector = cellFloatCollector;
                _moveFromName = moveFromName;
                _moveToName = moveToName;
                _formulaParagraphAlignment = formulaAlignment;
            }

            note.MarkProperties = _noteMarkProperties?.WithoutRevisions();
            _noteMarkProperties = null;

            if (note.Paragraphs.Count == 0)
                note.Paragraphs.Add(new ParagraphBlock());

            section.InlineObjects.Add(note);

            // Знак сноски несёт оформление своего места в тексте без верхнего индекса:
            // над строкой знак стоит всегда, а текст, набранный сразу за ним, индексом
            // стать не должен.
            var markProperties = runProps.Clone();
            markProperties.IsSuperscript = false;
            markProperties.IsSubscript = false;

            chunk.Runs.Add(new RunModel
            {
                Text = RunModel.ObjectPlaceholder.ToString(),
                InlineImageId = note.Id,
                Properties = markProperties
            });

            return note;
        }
    }
}
