using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DocumentFormat.OpenXml;
using Writersword.Modules.TextEditor.Models.Document;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Оглавление в .docx.
    ///
    /// Раньше строки оглавления уходили в файл обычными абзацами: без контейнера
    /// оглавления и без поля TOC. Word видел в них просто текст, а обратный импорт —
    /// тоже текст, похожий на оглавление: оно переставало быть оглавлением, номера
    /// страниц застывали теми, какими были при выгрузке, и дальше не обновлялись.
    ///
    /// Теперь строки одного оглавления идут подряд в контейнере w:sdt с галереей
    /// «Table of Contents», как у Word, а сами строки — значение поля TOC с уровнями
    /// и ключами этого оглавления. Word обновляет такое оглавление по F9, обратный
    /// импорт узнаёт его по контейнеру и снова делает живым оглавлением Writersword.
    /// </summary>
    public sealed partial class ExportService
    {
        /// <summary>
        /// Запоминает абзац Word, собранный из строки оглавления: по нему строки
        /// одного оглавления потом собираются в контейнер.
        /// </summary>
        private static void RememberTocParagraph(ParagraphBlock para, W.Paragraph built, DocxWriteContext ctx)
        {
            if (para.Properties.TocOwnerId is Guid owner)
                ctx.TocParagraphs[built] = (owner, para.Properties.TocEntryLevel);
        }

        /// <summary>
        /// Собирает подряд идущие абзацы каждого оглавления в контейнер оглавления Word
        /// и ставит вокруг его строк поле TOC. Абзац с параметрами раздела в контейнер
        /// не уходит: разрыв раздела остаётся в теле документа.
        /// </summary>
        private static void WrapTablesOfContents(W.Body body, DocumentModel document, DocxWriteContext ctx)
        {
            if (ctx.TocParagraphs.Count == 0) return;

            var children = body.ChildElements.ToList();
            int i = 0;

            while (i < children.Count)
            {
                if (children[i] is not W.Paragraph first
                    || !ctx.TocParagraphs.TryGetValue(first, out var head)
                    || HoldsSectionProperties(first))
                {
                    i++;
                    continue;
                }

                var group = new List<W.Paragraph>();
                int j = i;

                while (j < children.Count
                       && children[j] is W.Paragraph paragraph
                       && ctx.TocParagraphs.TryGetValue(paragraph, out var line)
                       && line.Owner == head.Owner
                       && !HoldsSectionProperties(paragraph))
                {
                    group.Add(paragraph);
                    j++;
                }

                WrapToc(group, head.Owner, document, ctx);
                i = j;
            }
        }

        private static bool HoldsSectionProperties(W.Paragraph paragraph)
            => paragraph.ParagraphProperties?.SectionProperties is not null;

        /// <summary>Один контейнер оглавления с полем TOC вокруг его строк.</summary>
        private static void WrapToc(List<W.Paragraph> group, Guid owner, DocumentModel document, DocxWriteContext ctx)
        {
            if (group.Count == 0) return;

            var content = new W.SdtContentBlock();
            var sdt = new W.SdtBlock(
                new W.SdtProperties(
                    new W.SdtContentDocPartObject(
                        new W.DocPartGallery { Val = "Table of Contents" },
                        new W.DocPartUnique())),
                content);

            group[0].InsertBeforeSelf(sdt);

            foreach (var paragraph in group)
            {
                paragraph.Remove();
                content.AppendChild(paragraph);
            }

            // Поле TOC — вокруг строк: название над ними в значение поля не входит,
            // как у Word, у которого название стоит над полем.
            var entries = group.Where(p => ctx.TocParagraphs[p].Level > 0).ToList();
            if (entries.Count == 0) return;

            var firstEntry = entries[0];
            int at = firstEntry.GetFirstChild<W.ParagraphProperties>() is null ? 0 : 1;

            firstEntry.InsertAt(new W.Run(new W.FieldChar
            {
                FieldCharType = new EnumValue<W.FieldCharValues>(W.FieldCharValues.Begin)
            }), at++);

            firstEntry.InsertAt(new W.Run(new W.FieldCode(" " + TocFieldInstruction(document, owner) + " ")
            {
                Space = new EnumValue<SpaceProcessingModeValues>(SpaceProcessingModeValues.Preserve)
            }), at++);

            firstEntry.InsertAt(new W.Run(new W.FieldChar
            {
                FieldCharType = new EnumValue<W.FieldCharValues>(W.FieldCharValues.Separate)
            }), at);

            entries[entries.Count - 1].AppendChild(new W.Run(new W.FieldChar
            {
                FieldCharType = new EnumValue<W.FieldCharValues>(W.FieldCharValues.End)
            }));
        }

        /// <summary>
        /// Код поля TOC по настройкам оглавления: уровни заголовков, ссылки на главы,
        /// без номеров страниц — если они выключены.
        /// </summary>
        private static string TocFieldInstruction(DocumentModel document, Guid owner)
        {
            var settings = document.TableOfContents?.FirstOrDefault(t => t.Id == owner);

            int min = Math.Clamp(settings?.MinLevel ?? 1, 1, 9);
            int max = Math.Clamp(settings?.MaxLevel ?? 3, min, 9);

            string instruction = string.Format(
                CultureInfo.InvariantCulture, "TOC \\o \"{0}-{1}\" \\h \\z \\u", min, max);

            if (settings is { ShowPageNumbers: false })
                instruction += " \\n";

            return instruction;
        }
    }
}
