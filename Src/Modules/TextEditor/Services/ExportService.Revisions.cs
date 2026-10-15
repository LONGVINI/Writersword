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
        /// <summary>
        /// Ран блока, вставленного или удалённого правкой целиком (плавающая картинка,
        /// фигура, разрыв), под его правкой: удалённый — в w:del, вставленный — в
        /// w:ins, удалённая вставка — w:del внутри w:ins. Без правки — ран как есть.
        /// </summary>
        private static OpenXmlElement WrapObjectRevision(W.Run run, Models.Document.BlockModel block, DocxWriteContext ctx)
        {
            if (block is not Models.Document.IRevisionMarked marked) return run;

            OpenXmlElement content = run;

            if (marked.Deleted is { } deleted)
            {
                var deletedRun = StampRevision(new W.DeletedRun(), deleted, ctx);
                deletedRun.AppendChild(content);
                content = deletedRun;
            }

            if (marked.Inserted is { } inserted)
            {
                var insertedRun = StampRevision(new W.InsertedRun(), inserted, ctx);
                insertedRun.AppendChild(content);
                content = insertedRun;
            }

            return content;
        }

        /// <summary>
        /// Отметка знака абзаца, который несёт только разрыв, вставленный или удалённый
        /// правкой: без неё Word при отклонении вставки оставил бы пустой абзац.
        /// </summary>
        private static void MarkBreakParagraphRevision(W.Paragraph paragraph, Models.Document.BreakBlock block, DocxWriteContext ctx)
        {
            if (block.Inserted is null && block.Deleted is null) return;

            var markRunProperties = new W.ParagraphMarkRunProperties();
            if (block.Inserted is { } inserted)
                markRunProperties.AppendChild(StampRevision(new W.Inserted(), inserted, ctx));
            if (block.Deleted is { } deleted)
                markRunProperties.AppendChild(StampRevision(new W.Deleted(), deleted, ctx));

            var pPr = paragraph.GetFirstChild<W.ParagraphProperties>();
            if (pPr is null)
            {
                pPr = new W.ParagraphProperties();
                paragraph.PrependChild(pPr);
            }
            pPr.AppendChild(markRunProperties);
        }

        /// <summary>
        /// Убирает ран вместе с обёртками правки, в которых он лежит: пустой w:ins или
        /// w:del в абзаце Word не нужен.
        /// </summary>
        private static void RemoveRunWithRevision(W.Run run)
        {
            OpenXmlElement top = run;
            while (top.Parent is W.InsertedRun or W.DeletedRun)
                top = top.Parent;
            top.Remove();
        }

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
                // Прежний список — свой, если правка сменила и список.
                var previousList = change.ListChanged ? change.PreviousList : para.ListProperties;

                var previous = new W.ParagraphPropertiesExtended();
                foreach (var element in BuildParagraphPropertyElements(
                             change.Previous, previousList, ctx.Numbering, null))
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
            if (!document.TrackRevisions && document.RevisionProtection is null) return;

            var part = mainPart.DocumentSettingsPart ?? mainPart.AddNewPart<DocumentSettingsPart>();
            part.Settings ??= new W.Settings();

            var track = part.Settings.GetFirstChild<W.TrackRevisions>();
            if (track is null)
            {
                track = new W.TrackRevisions();
                part.Settings.PrependChild(track);
            }

            if (document.RevisionProtection is { } protection)
                WriteRevisionProtection(part.Settings, track, protection);

            part.Settings.Save();
        }

        /// <summary>
        /// Защита записи исправлений: w:documentProtection w:edit="trackedChanges",
        /// по схеме — сразу за w:trackRevisions. Отпечаток пароля пишется тем видом
        /// атрибутов, каким он получен: свой — как пишет Word (w:cryptAlgorithmSid,
        /// w:hash, w:salt), пришедший из файла в строгом виде — так же строго.
        /// </summary>
        private static void WriteRevisionProtection(W.Settings settings, W.TrackRevisions track, RevisionProtection protection)
        {
            settings.GetFirstChild<W.DocumentProtection>()?.Remove();

            var element = new W.DocumentProtection
            {
                Edit = W.DocumentProtectionValues.TrackedChanges,
                Enforcement = new OnOffValue(true)
            };

            if (protection.HasPassword)
            {
                if (protection.Format == RevisionProtectionFormat.AlgorithmName)
                {
                    element.AlgorithmName = protection.AlgorithmName;
                    element.HashValue = protection.HashValue;
                    element.SaltValue = protection.SaltValue;
                    element.SpinCount = protection.SpinCount;
                }
                else
                {
                    string? algorithm = RevisionLock.NormalizeAlgorithm(protection.AlgorithmName);
                    bool sha1 = algorithm is null or "SHA-1";

                    element.CryptographicProviderType = sha1 ? W.CryptProviderValues.RsaFull : W.CryptProviderValues.RsaAdvancedEncryptionStandard;
                    element.CryptographicAlgorithmClass = W.CryptAlgorithmClassValues.Hash;
                    element.CryptographicAlgorithmType = W.CryptAlgorithmValues.TypeAny;
                    element.CryptographicAlgorithmSid = RevisionLock.AlgorithmSid(algorithm);
                    element.CryptographicSpinCount = (uint)Math.Max(0, protection.SpinCount);
                    element.Hash = protection.HashValue;
                    element.Salt = protection.SaltValue;
                }
            }

            track.InsertAfterSelf(element);
        }
    }
}
