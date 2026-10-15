using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
        private static void ApplyParagraphRevisions(
            W.Paragraph p,
            ParagraphBlock para,
            DocxFormatResolver resolver,
            DocxNumberingMap numbering,
            Dictionary<int, Guid> listIdMap)
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

                var previousProps = previousEffective.ToParagraphProperties(previousStyle);

                // Список до правки: Word пишет в w:pPrChange прежние w:numPr. Есть они
                // хоть с одной стороны — список прежний разбирается тем же путём, что
                // настоящий, и сверяется с нынешним.
                bool listChanged = false;
                ListProperties? previousList = null;
                bool previousHasNumbering = previous?.GetFirstChild<W.NumberingProperties>() is not null;
                bool currentHasNumbering = pPr.GetFirstChild<W.NumberingProperties>() is not null;
                if (previousHasNumbering || currentHasNumbering)
                {
                    previousList = numbering.Resolve(previousEffective, previousProps, listIdMap);
                    listChanged = !ParagraphFormatChange.SameList(previousList, para.ListProperties);
                }

                para.Properties.FormatChange = new ParagraphFormatChange
                {
                    Info = ReadRevisionInfo(change),
                    Previous = previousProps.WithoutRevisions(),
                    ListChanged = listChanged,
                    PreviousList = listChanged ? previousList : null
                };
            }
        }

        /// <summary>
        /// Запись исправлений, включённая в документе (w:settings/w:trackRevisions):
        /// документ открывается с ней, как в Word.
        /// </summary>
        private static void ApplyTrackRevisionsSetting(MainDocumentPart mainPart, DocumentModel doc)
        {
            var settings = mainPart.DocumentSettingsPart?.Settings;
            var track = settings?.GetFirstChild<W.TrackRevisions>();
            doc.TrackRevisions = track is not null && (track.Val is null || track.Val.Value);

            doc.RevisionProtection = ReadRevisionProtection(settings);

            // Под защитой записи Word пишет правками, даже если w:trackRevisions нет.
            if (doc.RevisionProtection is not null)
                doc.TrackRevisions = true;
        }

        /// <summary>
        /// Защита записи исправлений (w:documentProtection w:edit="trackedChanges"
        /// w:enforcement="1"). Другие виды защиты Word (только чтение, примечания,
        /// формы) здесь не читаются. Отпечаток пароля в неизвестном виде даёт защиту,
        /// которую можно снять только в Word: пароль, который нельзя проверить, не
        /// снимает ничего.
        /// </summary>
        private static RevisionProtection? ReadRevisionProtection(W.Settings? settings)
        {
            var protection = settings?.GetFirstChild<W.DocumentProtection>();
            if (protection is null) return null;

            if (protection.Edit?.Value != W.DocumentProtectionValues.TrackedChanges) return null;
            if (protection.Enforcement is null || !protection.Enforcement.Value) return null;

            if (protection.HashValue?.Value is { Length: > 0 } hashValue)
            {
                return new RevisionProtection
                {
                    Format = RevisionProtectionFormat.AlgorithmName,
                    AlgorithmName = RevisionLock.NormalizeAlgorithm(protection.AlgorithmName?.Value)
                                    ?? protection.AlgorithmName?.Value,
                    HashValue = hashValue,
                    SaltValue = protection.SaltValue?.Value,
                    SpinCount = protection.SpinCount?.Value ?? 0
                };
            }

            if (protection.Hash?.Value is { Length: > 0 } hash)
            {
                int sid = protection.CryptographicAlgorithmSid?.Value ?? 4;
                return new RevisionProtection
                {
                    Format = RevisionProtectionFormat.WordLegacyKey,
                    AlgorithmName = RevisionLock.AlgorithmFromSid(sid) ?? "SID-" + sid.ToString(CultureInfo.InvariantCulture),
                    HashValue = hash,
                    SaltValue = protection.Salt?.Value,
                    SpinCount = (int)(protection.CryptographicSpinCount?.Value ?? 0)
                };
            }

            return new RevisionProtection();
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

        // ── Таблицы ──────────────────────────────────────────────────────

        /// <summary>
        /// Строки и ячейки таблицы, вставленные или удалённые правкой: строка — w:ins или
        /// w:del у её w:trPr (отметка ложится на все ячейки, которые в ней начинаются),
        /// ячейка — w:cellIns или w:cellDel у её w:tcPr.
        /// </summary>
        private static void ReadTableStructureRevisions(List<W.TableRow> rows, TableBlock block)
        {
            var starts = new Dictionary<(int Row, int Column), Models.Document.TableCell>();
            foreach (var cell in block.Cells) starts[(cell.Row, cell.Column)] = cell;

            for (int r = 0; r < rows.Count; r++)
            {
                var rowPr = rows[r].TableRowProperties;
                RevisionInfo? rowInserted = rowPr?.GetFirstChild<W.Inserted>() is { } inserted ? ReadRevisionInfo(inserted) : null;
                RevisionInfo? rowDeleted = rowPr?.GetFirstChild<W.Deleted>() is { } deleted ? ReadRevisionInfo(deleted) : null;

                int column = 0;
                foreach (var wCell in rows[r].Elements<W.TableCell>())
                {
                    var cellPr = wCell.TableCellProperties;
                    int span = Math.Max(cellPr?.GridSpan?.Val?.Value ?? 1, 1);

                    if (starts.TryGetValue((r, column), out var cell))
                    {
                        if (rowDeleted is not null)
                            cell.Deleted = rowDeleted.Clone();
                        else if (cellPr?.GetFirstChild<W.CellDeletion>() is { } cellDeleted)
                            cell.Deleted = ReadRevisionInfo(cellDeleted);

                        if (rowInserted is not null)
                            cell.Inserted = rowInserted.Clone();
                        else if (cellPr?.GetFirstChild<W.CellInsertion>() is { } cellInserted)
                            cell.Inserted = ReadRevisionInfo(cellInserted);
                    }

                    column += span;
                }
            }
        }

        /// <summary>
        /// Смена оформления таблицы из w:tblPrChange, w:tblGridChange, w:trPrChange и
        /// w:tcPrChange. Прежний вид собирается копией таблицы, в которой свойства
        /// таблицы, сетки, строк и ячеек заменены прежними, а содержимое ячеек снято;
        /// копия читается тем же разбором, и её оформление
        /// (<see cref="TableFrame"/>) становится прежним видом смены. Ячейки копии
        /// узнаются по месту — состав таблицы оформление не меняет.
        /// </summary>
        private void ReadTableFormatChange(
            W.Table table, TableBlock block,
            SectionModel section,
            DocxFormatResolver resolver,
            DocxNumberingMap numbering,
            Dictionary<int, Guid> listIdMap,
            MainDocumentPart mainPart,
            Dictionary<string, byte[]> extractedImages,
            int depth)
        {
            OpenXmlElement? author = table.GetFirstChild<W.TableProperties>()?.GetFirstChild<W.TablePropertiesChange>();
            bool any = author is not null
                || table.GetFirstChild<W.TableGrid>()?.GetFirstChild<W.TableGridChange>() is not null;

            foreach (var row in table.Elements<W.TableRow>())
            {
                if (row.TableRowProperties?.GetFirstChild<W.TableRowPropertiesChange>() is { } rowChange)
                {
                    any = true;
                    author ??= rowChange;
                }

                foreach (var cell in row.Elements<W.TableCell>())
                {
                    if (cell.TableCellProperties?.GetFirstChild<W.TableCellPropertiesChange>() is { } cellChange)
                    {
                        any = true;
                        author ??= cellChange;
                    }
                }
            }

            if (!any) return;

            static void ReplaceWithPrevious(OpenXmlElement properties, OpenXmlElement? previous)
            {
                properties.RemoveAllChildren();
                if (previous is null) return;
                foreach (var child in previous.ChildElements)
                    properties.AppendChild(child.CloneNode(true));
            }

            var copy = (W.Table)table.CloneNode(true);

            if (copy.GetFirstChild<W.TableProperties>() is { } copyTablePr
                && copyTablePr.GetFirstChild<W.TablePropertiesChange>() is { } tableChange)
                ReplaceWithPrevious(copyTablePr, tableChange.GetFirstChild<W.PreviousTableProperties>());

            if (copy.GetFirstChild<W.TableGrid>() is { } copyGrid
                && copyGrid.GetFirstChild<W.TableGridChange>() is { } gridChange)
                ReplaceWithPrevious(copyGrid, gridChange.GetFirstChild<W.PreviousTableGrid>());

            foreach (var row in copy.Elements<W.TableRow>())
            {
                if (row.TableRowProperties is { } rowPr
                    && rowPr.GetFirstChild<W.TableRowPropertiesChange>() is { } rowChange)
                    ReplaceWithPrevious(rowPr, rowChange.GetFirstChild<W.PreviousTableRowProperties>());

                foreach (var cell in row.Elements<W.TableCell>())
                {
                    if (cell.TableCellProperties is { } cellPr
                        && cellPr.GetFirstChild<W.TableCellPropertiesChange>() is { } cellChange)
                        ReplaceWithPrevious(cellPr, cellChange.GetFirstChild<W.PreviousTableCellProperties>());

                    // Содержимое прежнему виду не нужно: ячейка — свойства и пустой абзац.
                    foreach (var child in cell.ChildElements.ToList())
                        if (child is not W.TableCellProperties) child.Remove();
                    cell.AppendChild(new W.Paragraph());
                }
            }

            var previous = ImportTable(copy, section, resolver, numbering, listIdMap, mainPart,
                extractedImages, new List<string>(), depth);
            if (previous is null) return;

            var byPlace = new Dictionary<(int Row, int Column), Guid>();
            foreach (var cell in block.Cells) byPlace[(cell.Row, cell.Column)] = cell.Id;
            foreach (var cell in previous.Cells)
                if (byPlace.TryGetValue((cell.Row, cell.Column), out var id)) cell.Id = id;

            string previousLook = TableFrame.Capture(previous);
            if (string.Equals(previousLook, TableFrame.Capture(block), StringComparison.Ordinal)) return;

            block.FormatChange = new ObjectChange
            {
                Info = author is not null ? ReadRevisionInfo(author) : new RevisionInfo(),
                Previous = previousLook
            };
        }
    }
}
