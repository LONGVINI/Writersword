using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Вычисляет SHA-256 хеши для чанков и аннотаций.
    /// Хеши используются в дельта-кеше для определения изменившихся элементов.
    /// Хеш пересчитывается только при сохранении, не при каждом нажатии клавиши.
    /// </summary>
    public sealed class DeltaHashService
    {
        /// <summary>
        /// Пересчитывает хеши всех чанков параграфа.
        /// Возвращает список Id чанков у которых хеш изменился.
        /// </summary>
        public IReadOnlyList<Guid> UpdateParagraphHashes(ParagraphBlock paragraph)
        {
            var changed = new List<Guid>();

            foreach (var chunk in paragraph.Chunks)
            {
                string newHash = ComputeChunkHash(chunk);
                if (chunk.Hash != newHash)
                {
                    chunk.Hash = newHash;
                    changed.Add(chunk.Id);
                }
            }

            return changed;
        }

        /// <summary>
        /// Пересчитывает хеш одного чанка.
        /// Возвращает true если хеш изменился.
        /// </summary>
        public bool UpdateChunkHash(TextChunk chunk)
        {
            string newHash = ComputeChunkHash(chunk);
            if (chunk.Hash == newHash) return false;
            chunk.Hash = newHash;
            return true;
        }

        /// <summary>
        /// Пересчитывает хеш аннотации.
        /// Возвращает true если хеш изменился.
        /// </summary>
        public bool UpdateAnnotationHash(InlineAnnotation annotation)
        {
            string newHash = ComputeAnnotationHash(annotation);
            if (annotation.Hash == newHash) return false;
            annotation.Hash = newHash;
            return true;
        }

        /// <summary>
        /// Вычисляет SHA-256 хеш чанка на основе его текстового содержимого
        /// и свойств форматирования каждого Run.
        /// </summary>
        public string ComputeChunkHash(TextChunk chunk)
        {
            // Сериализуем только данные влияющие на содержимое чанка.
            // Id чанка не включаем — он сам является ключом, а не содержимым.
            using var sha = SHA256.Create();
            using var stream = new System.IO.MemoryStream();
            using var writer = new Utf8JsonWriter(stream);

            writer.WriteStartArray();
            foreach (var run in chunk.Runs)
            {
                writer.WriteStartObject();
                writer.WriteString("t", run.Text);

                // Ссылка на встроенный объект — часть содержимого: у всех картинок
                // одинаковый символ-заполнитель, и без Id замена картинки не меняла бы
                // хеш чанка, а значит не попадала бы в дельту сохранения.
                if (run.InlineImageId is System.Guid inlineId)
                    writer.WriteString("io", inlineId);

                if (run.Properties is not null && !run.Properties.IsDefault())
                {
                    writer.WritePropertyName("p");
                    JsonSerializer.Serialize(writer, run.Properties);
                }

                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.Flush();

            stream.Position = 0;
            byte[] hash = sha.ComputeHash(stream);
            return Convert.ToHexString(hash);
        }

        /// <summary>
        /// Вычисляет SHA-256 хеш собственных свойств блока — всего, что не является
        /// текстом чанков. Чанковые хеши покрывают только текст и оформление Run,
        /// поэтому без этого хеша мимо дельты проходят: свойства абзаца и списка,
        /// геометрия и оформление картинок и фигур, структура и оформление таблиц,
        /// параметры надписей и тип разрыва. Порядок дочерних элементов включён
        /// в хеш идентификаторами — перестановка и удаление тоже становятся видны.
        /// </summary>
        public string ComputeBlockPropertiesHash(BlockModel block)
        {
            using var sha = SHA256.Create();
            using var stream = new System.IO.MemoryStream();
            using var writer = new Utf8JsonWriter(stream);

            writer.WriteStartObject();
            writer.WriteString("type", block.BlockType.ToString());

            switch (block)
            {
                case ParagraphBlock paragraph:
                    writer.WritePropertyName("props");
                    JsonSerializer.Serialize(writer, paragraph.Properties);
                    if (paragraph.ListProperties is not null)
                    {
                        writer.WritePropertyName("list");
                        JsonSerializer.Serialize(writer, paragraph.ListProperties);
                    }
                    WriteIdArray(writer, "chunks", paragraph.Chunks.Select(c => c.Id));
                    break;

                case TableBlock table:
                    writer.WriteNumber("rows", table.RowCount);
                    writer.WriteNumber("cols", table.ColumnCount);
                    writer.WriteNumber("widthPercent", table.WidthPercent);
                    writer.WriteNumber("leftIndent", table.LeftIndentPt);
                    writer.WriteBoolean("repeatHeader", table.RepeatHeader);
                    writer.WriteString("split", table.SplitMode.ToString());
                    writer.WriteString("style", table.StyleName ?? string.Empty);
                    writer.WriteString("breakLabel", table.BreakLabel ?? string.Empty);
                    writer.WriteString("continuationLabel", table.ContinuationLabel ?? string.Empty);

                    writer.WritePropertyName("columns");
                    JsonSerializer.Serialize(writer, table.Columns);

                    writer.WriteStartArray("cells");
                    foreach (var cell in table.Cells)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("id", cell.Id.ToString());
                        writer.WriteNumber("row", cell.Row);
                        writer.WriteNumber("col", cell.Column);
                        writer.WriteNumber("rowSpan", cell.RowSpan);
                        writer.WriteNumber("colSpan", cell.ColSpan);
                        writer.WriteString("bg", cell.BackgroundColor ?? string.Empty);
                        writer.WriteString("vAlign", cell.VerticalAlignment.ToString());
                        writer.WriteNumber("padT", cell.PaddingTopPt);
                        writer.WriteNumber("padB", cell.PaddingBottomPt);
                        writer.WriteNumber("padL", cell.PaddingLeftPt);
                        writer.WriteNumber("padR", cell.PaddingRightPt);
                        writer.WritePropertyName("borders");
                        JsonSerializer.Serialize(writer, cell.Borders);
                        WriteIdArray(writer, "paragraphs", cell.Paragraphs.Select(p => p.Id));
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    break;

                case FloatingTextBlock floatingText:
                    writer.WriteNumber("x", floatingText.XPt);
                    writer.WriteNumber("y", floatingText.YPt);
                    writer.WriteNumber("w", floatingText.WidthPt);
                    writer.WriteNumber("h", floatingText.HeightPt);
                    writer.WriteString("bg", floatingText.BackgroundColor ?? string.Empty);
                    writer.WriteString("border", floatingText.BorderColor ?? string.Empty);
                    writer.WriteNumber("borderThickness", floatingText.BorderThicknessPt);
                    writer.WriteString("anchor", floatingText.Anchor.ToString());
                    writer.WriteNumber("z", floatingText.ZOrder);
                    writer.WriteBoolean("grouped", floatingText.IsGrouped);
                    writer.WriteString("group", floatingText.GroupId ?? string.Empty);
                    WriteIdArray(writer, "paragraphs", floatingText.Paragraphs.Select(p => p.Id));
                    break;

                default:
                    // Картинки, фигуры, разрывы — весь блок целиком: текста в них нет,
                    // и все их свойства влияют на документ.
                    writer.WritePropertyName("block");
                    JsonSerializer.Serialize(writer, block, block.GetType());
                    break;
            }

            writer.WriteEndObject();
            writer.Flush();

            stream.Position = 0;
            return Convert.ToHexString(sha.ComputeHash(stream));
        }

        /// <summary>
        /// Вычисляет SHA-256 хеш свойств раздела: параметры страницы и колонок,
        /// колонтитулы, состав и порядок блоков и плавающих объектов.
        /// </summary>
        public string ComputeSectionPropertiesHash(SectionModel section)
        {
            using var sha = SHA256.Create();
            using var stream = new System.IO.MemoryStream();
            using var writer = new Utf8JsonWriter(stream);

            writer.WriteStartObject();
            if (section.PageSettings is not null)
            {
                writer.WritePropertyName("page");
                JsonSerializer.Serialize(writer, section.PageSettings);
            }
            if (section.ColumnSettings is not null)
            {
                writer.WritePropertyName("columns");
                JsonSerializer.Serialize(writer, section.ColumnSettings);
            }
            writer.WritePropertyName("header");
            JsonSerializer.Serialize(writer, section.Header);
            writer.WritePropertyName("footer");
            JsonSerializer.Serialize(writer, section.Footer);
            WriteIdArray(writer, "blocks", section.Blocks.Select(b => b.Id));
            WriteIdArray(writer, "floating", section.FloatingObjects.Select(b => b.Id));
            WriteIdArray(writer, "inline", section.InlineObjects.Select(b => b.Id));
            writer.WriteEndObject();
            writer.Flush();

            stream.Position = 0;
            return Convert.ToHexString(sha.ComputeHash(stream));
        }

        /// <summary>
        /// Вычисляет SHA-256 хеш свойств документа: заголовок, стили, параметры
        /// страницы и колонок, оформление листа, состав и порядок разделов.
        /// Масштаб и режим отображения намеренно исключены — это состояние окна,
        /// и от прокрутки колесом документ не должен считаться изменённым.
        /// Сохраняются они через сессионные данные модуля, которые пишутся всегда
        /// и не зависят от того, признан ли документ изменённым.
        /// </summary>
        public string ComputeDocumentPropertiesHash(DocumentModel document)
        {
            using var sha = SHA256.Create();
            using var stream = new System.IO.MemoryStream();
            using var writer = new Utf8JsonWriter(stream);

            writer.WriteStartObject();
            writer.WriteString("title", document.Title);
            writer.WriteNumber("schema", document.SchemaVersion);
            writer.WritePropertyName("page");
            JsonSerializer.Serialize(writer, document.PageSettings);
            writer.WritePropertyName("columns");
            JsonSerializer.Serialize(writer, document.ColumnSettings);
            writer.WritePropertyName("canvas");
            JsonSerializer.Serialize(writer, document.CanvasSettings);
            writer.WritePropertyName("styles");
            JsonSerializer.Serialize(writer, document.Styles);
            if (document.DocumentAutoReplaceRules is not null)
            {
                writer.WritePropertyName("autoReplace");
                JsonSerializer.Serialize(writer, document.DocumentAutoReplaceRules);
            }
            WriteIdArray(writer, "sections", document.Sections.Select(s => s.Id));
            writer.WriteEndObject();
            writer.Flush();

            stream.Position = 0;
            return Convert.ToHexString(sha.ComputeHash(stream));
        }

        // Пишет массив идентификаторов — так в хеш попадает состав и порядок детей.
        private static void WriteIdArray(Utf8JsonWriter writer, string name, IEnumerable<Guid> ids)
        {
            writer.WriteStartArray(name);
            foreach (var id in ids)
                writer.WriteStringValue(id.ToString());
            writer.WriteEndArray();
        }

        /// <summary>
        /// Вычисляет SHA-256 хеш аннотации.
        /// </summary>
        public string ComputeAnnotationHash(InlineAnnotation annotation)
        {
            using var sha = SHA256.Create();

            // Включаем в хеш все поля которые могут измениться.
            var sb = new StringBuilder();
            sb.Append(annotation.Type);
            sb.Append('|');
            sb.Append(annotation.Start.BlockId);
            sb.Append(':');
            sb.Append(annotation.Start.ChunkId);
            sb.Append(':');
            sb.Append(annotation.Start.Offset);
            sb.Append('|');
            sb.Append(annotation.End.BlockId);
            sb.Append(':');
            sb.Append(annotation.End.ChunkId);
            sb.Append(':');
            sb.Append(annotation.End.Offset);
            sb.Append('|');
            sb.Append(annotation.Color ?? string.Empty);
            sb.Append('|');
            sb.Append(annotation.LinkedEntityId ?? string.Empty);
            sb.Append('|');
            sb.Append(annotation.Content ?? string.Empty);
            sb.Append('|');
            sb.Append(annotation.DisplayLabel ?? string.Empty);

            byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
            byte[] hash = sha.ComputeHash(bytes);
            return Convert.ToHexString(hash);
        }
    }
}
