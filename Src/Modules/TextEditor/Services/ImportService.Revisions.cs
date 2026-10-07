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
    /// Правки рецензирования из .docx.
    ///
    /// Раньше импорт принимал все правки молча: вставленное становилось обычным
    /// текстом, удалённое выбрасывалось, смена оформления забывалась. Документ с
    /// правками терял их при первом же открытии, и обратно в Word уходил уже
    /// «чистым». Теперь каждая правка живёт на своём фрагменте (отметки в
    /// <see cref="RunProperties"/> и <see cref="ParagraphProperties"/>): удалённый
    /// текст остаётся в абзаце до принятия правки, вставленный помнит автора и время,
    /// смена оформления — прежнее оформление. Показывает их лист по выбранному виду,
    /// а выгрузка пишет обратно теми же элементами Word.
    /// </summary>
    public sealed partial class ImportService
    {
        // Имя перемещения, открытого w:moveFromRangeStart / w:moveToRangeStart: им
        // источник перемещения связан с приёмником.
        private string? _moveFromName;
        private string? _moveToName;

        /// <summary>Читает автора, время и номер правки из её элемента.</summary>
        private static RevisionInfo ReadRevisionInfo(OpenXmlElement element, bool isMove = false, string? moveName = null)
        {
            var info = new RevisionInfo { IsMove = isMove, MoveName = isMove ? moveName : null };

            foreach (var attribute in element.GetAttributes())
            {
                if (attribute.NamespaceUri != WordprocessingNamespace) continue;

                switch (attribute.LocalName)
                {
                    case "author":
                        info.Author = attribute.Value ?? string.Empty;
                        break;

                    case "date":
                        if (DateTime.TryParse(attribute.Value, CultureInfo.InvariantCulture,
                                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date))
                            info.Date = DateTime.SpecifyKind(date, DateTimeKind.Utc);
                        break;

                    case "id":
                        if (int.TryParse(attribute.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                            info.Id = id;
                        break;
                }
            }

            return info;
        }

        private const string WordprocessingNamespace =
            "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        /// <summary>
        /// Содержимое правки (w:ins, w:del, w:moveFrom, w:moveTo) — раны, гиперссылки,
        /// вложенные правки — разбирается как обычно, а получившиеся раны получают
        /// отметку правки. Удаление вставленного (w:ins с w:del внутри) несёт обе.
        /// </summary>
        private void AppendRevisedContainer(
            OpenXmlElement container,
            RevisionInfo? inserted,
            RevisionInfo? deleted,
            TextChunk chunk,
            SectionModel section,
            DocxFormatResolver resolver,
            EffectiveParagraph effPara,
            MainDocumentPart mainPart,
            Dictionary<string, byte[]> extractedImages,
            List<string> warnings)
        {
            int first = chunk.Runs.Count;

            foreach (var child in container.ChildElements)
                AppendRunOrDrawing(child, chunk, section, resolver, effPara, mainPart, extractedImages, warnings);

            for (int i = first; i < chunk.Runs.Count; i++)
            {
                var run = chunk.Runs[i];

                // Свойства рана бывают общими у нескольких ранов (один ран Word делится
                // по шрифтам письменностей) — отметка ставится на копию.
                var props = run.Properties?.Clone() ?? new RunProperties();
                if (inserted is not null && props.Inserted is null) props.Inserted = inserted.Clone();
                if (deleted is not null && props.Deleted is null) props.Deleted = deleted.Clone();
                run.Properties = props;
            }
        }

        /// <summary>
        /// Смена оформления рана (w:rPr/w:rPrChange): прежнее оформление разбирается
        /// тем же каскадом стилей, что и нынешнее, — от стиля абзаца и символьного
        /// стиля, записанного в прежних свойствах.
        /// </summary>
        private static RunFormatChange? ReadRunFormatChange(
            W.Run run, DocxFormatResolver resolver, EffectiveParagraph effPara)
        {
            var change = run.RunProperties?.GetFirstChild<W.RunPropertiesChange>();
            if (change is null) return null;

            var previousRun = new W.Run();
            var previousProperties = new W.RunProperties();
            var previous = change.GetFirstChild<W.PreviousRunProperties>();
            if (previous is not null)
            {
                foreach (var element in previous.ChildElements)
                    previousProperties.AppendChild(element.CloneNode(true));
            }
            previousRun.AppendChild(previousProperties);

            var previousFormat = resolver.ResolveEffectiveRun(previousRun, effPara).ToRunProperties();

            return new RunFormatChange
            {
                Info = ReadRevisionInfo(change),
                Previous = previousFormat.WithoutRevisions()
            };
        }

        /// <summary>
        /// Правки абзаца: вставленный или удалённый знак абзаца (w:pPr/w:rPr/w:ins|w:del,
        /// у перемещения — w:moveTo|w:moveFrom) и смена оформления абзаца (w:pPrChange).
        /// </summary>
        private static void ApplyParagraphRevisions(W.Paragraph p, ParagraphBlock para, DocxFormatResolver resolver)
        {
            var pPr = p.ParagraphProperties;
            if (pPr is null) return;

            var markProperties = pPr.ParagraphMarkRunProperties;
            if (markProperties is not null)
            {
                foreach (var element in markProperties.ChildElements)
                {
                    switch (element)
                    {
                        case W.Inserted inserted:
                            para.Properties.MarkInserted = ReadRevisionInfo(inserted);
                            break;
                        case W.Deleted deleted:
                            para.Properties.MarkDeleted = ReadRevisionInfo(deleted);
                            break;
                        case W.MoveTo moveTo:
                            para.Properties.MarkInserted = ReadRevisionInfo(moveTo, isMove: true);
                            break;
                        case W.MoveFrom moveFrom:
                            para.Properties.MarkDeleted = ReadRevisionInfo(moveFrom, isMove: true);
                            break;
                    }
                }
            }

            var change = pPr.GetFirstChild<W.ParagraphPropertiesChange>();
            if (change is not null)
            {
                // Прежнее оформление — тем же каскадом стилей: временный абзац с прежними
                // свойствами разбирается так же, как настоящий.
                var previousProperties = new W.ParagraphProperties();
                var previous = change.GetFirstChild<W.ParagraphPropertiesExtended>();
                if (previous is not null)
                {
                    foreach (var element in previous.ChildElements)
                        previousProperties.AppendChild(element.CloneNode(true));
                }

                var previousParagraph = new W.Paragraph(previousProperties);
                var previousEffective = resolver.ResolveEffectiveParagraph(previousParagraph);
                string? previousStyle = resolver.MapStyleName(previousParagraph, previousEffective);

                para.Properties.FormatChange = new ParagraphFormatChange
                {
                    Info = ReadRevisionInfo(change),
                    Previous = previousEffective.ToParagraphProperties(previousStyle).WithoutRevisions()
                };
            }
        }

        /// <summary>
        /// Запись исправлений, включённая в документе (w:settings/w:trackRevisions):
        /// документ открывается с ней, как в Word.
        /// </summary>
        private static void ApplyTrackRevisionsSetting(MainDocumentPart mainPart, DocumentModel doc)
        {
            var track = mainPart.DocumentSettingsPart?.Settings?.GetFirstChild<W.TrackRevisions>();
            doc.TrackRevisions = track is not null && (track.Val is null || track.Val.Value);
        }

        /// <summary>
        /// Обрабатывает правки и границы перемещений среди детей абзаца. True —
        /// элемент разобран здесь.
        /// </summary>
        private bool TryAppendRevisionElement(
            OpenXmlElement element,
            TextChunk chunk,
            SectionModel section,
            DocxFormatResolver resolver,
            EffectiveParagraph effPara,
            MainDocumentPart mainPart,
            Dictionary<string, byte[]> extractedImages,
            List<string> warnings)
        {
            switch (element)
            {
                case W.InsertedRun ins:
                    AppendRevisedContainer(ins, ReadRevisionInfo(ins), null,
                        chunk, section, resolver, effPara, mainPart, extractedImages, warnings);
                    return true;

                case W.DeletedRun del:
                    AppendRevisedContainer(del, null, ReadRevisionInfo(del),
                        chunk, section, resolver, effPara, mainPart, extractedImages, warnings);
                    return true;

                case W.MoveToRun moveTo:
                    AppendRevisedContainer(moveTo, ReadRevisionInfo(moveTo, isMove: true, _moveToName), null,
                        chunk, section, resolver, effPara, mainPart, extractedImages, warnings);
                    return true;

                case W.MoveFromRun moveFrom:
                    AppendRevisedContainer(moveFrom, null, ReadRevisionInfo(moveFrom, isMove: true, _moveFromName),
                        chunk, section, resolver, effPara, mainPart, extractedImages, warnings);
                    return true;

                case W.MoveFromRangeStart moveFromStart:
                    _moveFromName = moveFromStart.Name?.Value;
                    return true;

                case W.MoveToRangeStart moveToStart:
                    _moveToName = moveToStart.Name?.Value;
                    return true;

                case W.MoveFromRangeEnd:
                case W.MoveToRangeEnd:
                    return true;
            }

            return false;
        }
    }
}
