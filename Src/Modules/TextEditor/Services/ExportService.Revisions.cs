using System;
using System.Collections.Generic;
using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Models.Styles;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Правки рецензирования в .docx — теми же элементами, какими их пишет Word:
    ///   вставка — w:ins, удаление — w:del с текстом в w:delText, удаление вставленного —
    ///   w:del внутри w:ins, перемещение — w:moveFrom / w:moveTo между границами
    ///   w:moveFromRangeStart…End и w:moveToRangeStart…End;
    ///   смена оформления фрагмента — w:rPrChange с прежними свойствами;
    ///   вставленный или удалённый знак абзаца — w:ins / w:del в w:pPr/w:rPr;
    ///   смена оформления абзаца — w:pPrChange;
    ///   включённая запись исправлений — w:trackRevisions в настройках документа.
    ///
    /// Номера правок (w:id) выдаются заново по всему документу: Word требует, чтобы
    /// они не повторялись.
    /// </summary>
    public sealed partial class ExportService
    {
        private const string WordprocessingNs =
            "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        /// <summary>
        /// Ставит на элемент правки номер, автора и время. Время пишется в UTC без
        /// долей секунды, как у Word.
        /// </summary>
        private static T StampRevision<T>(T element, RevisionInfo info, DocxWriteContext ctx) where T : OpenXmlElement
        {
            int id = ctx.NextRevisionId++;
            element.SetAttribute(new OpenXmlAttribute("w", "id", WordprocessingNs,
                id.ToString(CultureInfo.InvariantCulture)));
            element.SetAttribute(new OpenXmlAttribute("w", "author", WordprocessingNs,
                string.IsNullOrEmpty(info.Author) ? "Writersword" : info.Author));

            if (info.Date is DateTime date)
            {
                var utc = date.Kind == DateTimeKind.Local ? date.ToUniversalTime() : date;
                element.SetAttribute(new OpenXmlAttribute("w", "date", WordprocessingNs,
                    utc.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture)));
            }

            return element;
        }

        /// <summary>
        /// Смена оформления рана: прежние свойства в w:rPrChange, последним элементом
        /// w:rPr, как требует схема.
        /// </summary>
        private void AppendRunFormatChange(W.RunProperties rPr, RunFormatChange change, DocxWriteContext ctx)
        {
            var previous = new W.PreviousRunProperties();
            foreach (var element in BuildRunPropertyElements(change.Previous, writeExplicitToggles: true))
                previous.AppendChild(element);

            var rPrChange = StampRevision(new W.RunPropertiesChange(), change.Info, ctx);
            rPrChange.AppendChild(previous);
            rPr.AppendChild(rPrChange);
        }

        /// <summary>
        /// Правки абзаца в его w:pPr: отметка знака абзаца в w:rPr и прежнее
        /// оформление в w:pPrChange — оба по схеме последние.
        /// </summary>
        private void AppendParagraphRevisions(
            W.ParagraphProperties pPr, ParagraphBlock para, DocxWriteContext ctx)
        {
            var props = para.Properties;

            if (props.MarkInserted is not null || props.MarkDeleted is not null)
            {
                var markRunProperties = new W.ParagraphMarkRunProperties();

                if (props.MarkInserted is { } inserted)
                {
                    OpenXmlElement mark = inserted.IsMove ? new W.MoveTo() : new W.Inserted();
                    markRunProperties.AppendChild(StampRevision(mark, inserted, ctx));
                }

                if (props.MarkDeleted is { } deleted)
                {
                    OpenXmlElement mark = deleted.IsMove ? new W.MoveFrom() : new W.Deleted();
                    markRunProperties.AppendChild(StampRevision(mark, deleted, ctx));
                }

                pPr.AppendChild(markRunProperties);
            }

            if (props.FormatChange is { } change)
            {
                var previous = new W.ParagraphPropertiesExtended();
                foreach (var element in BuildParagraphPropertyElements(
                             change.Previous, para.ListProperties, ctx.Numbering, null))
                    previous.AppendChild(element);

                var pPrChange = StampRevision(new W.ParagraphPropertiesChange(), change.Info, ctx);
                pPrChange.AppendChild(previous);
                pPr.AppendChild(pPrChange);
            }
        }

        /// <summary>
        /// Раны абзаца с их правками: подряд идущие раны одной правки уходят одним
        /// элементом правки, как у Word. Обычные раны идут как есть.
        /// </summary>
        private List<OpenXmlElement> WrapRunRevisions(
            List<(RunModel Run, List<OpenXmlElement> Elements)> runs, DocxWriteContext ctx)
        {
            var result = new List<OpenXmlElement>();
            int i = 0;

            while (i < runs.Count)
            {
                var props = runs[i].Run.Properties;
                var inserted = props?.Inserted;
                var deleted = props?.Deleted;

                if (inserted is null && deleted is null)
                {
                    result.AddRange(runs[i].Elements);
                    i++;
                    continue;
                }

                // Группа: подряд идущие раны с теми же отметками.
                var group = new List<OpenXmlElement>();
                int j = i;
                while (j < runs.Count
                       && RevisionInfo.Same(runs[j].Run.Properties?.Inserted, inserted)
                       && RevisionInfo.Same(runs[j].Run.Properties?.Deleted, deleted))
                {
                    group.AddRange(runs[j].Elements);
                    j++;
                }

                if (group.Count > 0)
                    result.AddRange(BuildRevisionGroup(group, inserted, deleted, ctx));

                i = j;
            }

            return result;
        }

        /// <summary>Одна группа ранов под своей правкой (или двумя — удаление вставленного).</summary>
        private static IEnumerable<OpenXmlElement> BuildRevisionGroup(
            List<OpenXmlElement> group, RevisionInfo? inserted, RevisionInfo? deleted, DocxWriteContext ctx)
        {
            var output = new List<OpenXmlElement>();

            // Удалённый текст пишется w:delText — так его читает Word.
            if (deleted is not null)
            {
                foreach (var element in group)
                    ConvertToDeletedText(element);
            }

            OpenXmlElement content;
            List<OpenXmlElement> rangeOpen = new();
            List<OpenXmlElement> rangeClose = new();

            if (deleted is not null)
            {
                OpenXmlElement wrapper = deleted.IsMove ? new W.MoveFromRun() : new W.DeletedRun();
                StampRevision(wrapper, deleted, ctx);
                foreach (var element in group) wrapper.AppendChild(element);
                content = wrapper;

                if (deleted.IsMove)
                {
                    int rangeId = ctx.NextRevisionId++;
                    var start = new W.MoveFromRangeStart
                    {
                        Id = rangeId.ToString(CultureInfo.InvariantCulture),
                        Name = deleted.MoveName ?? $"move{rangeId}"
                    };
                    StampRangeAuthor(start, deleted);
                    rangeOpen.Add(start);
                    rangeClose.Add(new W.MoveFromRangeEnd { Id = rangeId.ToString(CultureInfo.InvariantCulture) });
                }

                if (inserted is not null)
                {
                    OpenXmlElement outer = inserted.IsMove ? new W.MoveToRun() : new W.InsertedRun();
                    StampRevision(outer, inserted, ctx);
                    outer.AppendChild(content);
                    content = outer;
                }
            }
            else
            {
                OpenXmlElement wrapper = inserted!.IsMove ? new W.MoveToRun() : new W.InsertedRun();
                StampRevision(wrapper, inserted, ctx);
                foreach (var element in group) wrapper.AppendChild(element);
                content = wrapper;

                if (inserted.IsMove)
                {
                    int rangeId = ctx.NextRevisionId++;
                    var start = new W.MoveToRangeStart
                    {
                        Id = rangeId.ToString(CultureInfo.InvariantCulture),
                        Name = inserted.MoveName ?? $"move{rangeId}"
                    };
                    StampRangeAuthor(start, inserted);
                    rangeOpen.Add(start);
                    rangeClose.Add(new W.MoveToRangeEnd { Id = rangeId.ToString(CultureInfo.InvariantCulture) });
                }
            }

            output.AddRange(rangeOpen);
            output.Add(content);
            output.AddRange(rangeClose);
            return output;
        }

        /// <summary>Автор и время у границы перемещения.</summary>
        private static void StampRangeAuthor(OpenXmlElement rangeStart, RevisionInfo info)
        {
            rangeStart.SetAttribute(new OpenXmlAttribute("w", "author", WordprocessingNs,
                string.IsNullOrEmpty(info.Author) ? "Writersword" : info.Author));

            if (info.Date is DateTime date)
            {
                var utc = date.Kind == DateTimeKind.Local ? date.ToUniversalTime() : date;
                rangeStart.SetAttribute(new OpenXmlAttribute("w", "date", WordprocessingNs,
                    utc.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture)));
            }
        }

        /// <summary>Текст рана удалённой правки — в w:delText.</summary>
        private static void ConvertToDeletedText(OpenXmlElement element)
        {
            if (element is not W.Run run) return;

            var texts = new List<W.Text>();
            foreach (var child in run.ChildElements)
                if (child is W.Text text) texts.Add(text);

            foreach (var text in texts)
            {
                var deletedText = new W.DeletedText(text.Text)
                {
                    Space = new EnumValue<SpaceProcessingModeValues>(SpaceProcessingModeValues.Preserve)
                };
                run.ReplaceChild(deletedText, text);
            }
        }

        /// <summary>
        /// Включённая запись исправлений: w:trackRevisions в настройках документа. По
        /// схеме он стоит раньше всего, что пишет выгрузка в настройки, поэтому
        /// ставится в начало.
        /// </summary>
        private static void WriteTrackRevisionsSetting(MainDocumentPart mainPart, DocumentModel document)
        {
            if (!document.TrackRevisions) return;

            var part = mainPart.DocumentSettingsPart ?? mainPart.AddNewPart<DocumentSettingsPart>();
            part.Settings ??= new W.Settings();

            if (part.Settings.GetFirstChild<W.TrackRevisions>() is null)
                part.Settings.PrependChild(new W.TrackRevisions());

            part.Settings.Save();
        }
    }
}
