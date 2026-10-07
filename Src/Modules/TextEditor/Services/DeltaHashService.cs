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
            using var lease = HashWriter.Rent();
            var writer = lease.Writer;

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

            return lease.ComputeHash();
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
            using var lease = HashWriter.Rent();
            var writer = lease.Writer;

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
                    writer.WriteString("alignment", table.Alignment.ToString());
                    writer.WriteBoolean("bidiVisual", table.BidiVisual);

                    // Положение таблицы с обтеканием: без него перенос таблицы в поток
                    // и обратно не попадал бы в дельту сохранения.
                    if (table.FloatPosition is not null)
                    {
                        writer.WritePropertyName("float");
                        JsonSerializer.Serialize(writer, table.FloatPosition);
                    }
                    writer.WriteBoolean("repeatHeader", table.RepeatHeader);
                    writer.WriteString("split", table.SplitMode.ToString());
                    writer.WriteString("style", table.StyleName ?? string.Empty);
                    writer.WriteString("breakLabel", table.BreakLabel ?? string.Empty);
                    writer.WriteString("continuationLabel", table.ContinuationLabel ?? string.Empty);

                    writer.WritePropertyName("columns");
                    JsonSerializer.Serialize(writer, table.Columns);

                    // Заданные высоты строк и отметки точной высоты: без них правка
                    // высоты строки не попадала в дельту сохранения.
                    writer.WritePropertyName("rowHeights");
                    JsonSerializer.Serialize(writer, table.RowMinHeightsPt);
                    if (table.ExactHeightRows is not null)
                    {
                        writer.WritePropertyName("exactRows");
                        JsonSerializer.Serialize(writer, table.ExactHeightRows);
                    }

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
                        writer.WriteString("shdPattern", cell.ShadingPattern ?? string.Empty);
                        writer.WriteString("shdColor", cell.ShadingPatternColor ?? string.Empty);
                        writer.WriteString("textDir", cell.TextDirection.ToString());
                        writer.WriteNumber("padT", cell.PaddingTopPt);
                        writer.WriteNumber("padB", cell.PaddingBottomPt);
                        writer.WriteNumber("padL", cell.PaddingLeftPt);
                        writer.WriteNumber("padR", cell.PaddingRightPt);
                        writer.WritePropertyName("borders");
                        JsonSerializer.Serialize(writer, cell.Borders);
                        WriteIdArray(writer, "paragraphs", cell.Paragraphs.Select(p => p.Id));

                        // Вложенные таблицы: место в ячейке и хеш свойств каждой. Без них
                        // правка структуры или оформления вложенной таблицы не попадала
                        // бы в дельту сохранения.
                        if (cell.NestedTables is { Count: > 0 } nestedTables)
                        {
                            writer.WriteStartArray("nested");
                            foreach (var nested in nestedTables)
                            {
                                writer.WriteStartObject();
                                writer.WriteString("id", nested.Table.Id.ToString());
                                writer.WriteNumber("at", cell.NestedTablePosition(nested));
                                writer.WriteString("hash", ComputeBlockPropertiesHash(nested.Table));
                                writer.WriteEndObject();
                            }
                            writer.WriteEndArray();
                        }

                        // Плавающие объекты ячейки: абзац привязки и объект целиком.
                        if (cell.Floats is { Count: > 0 } cellFloats)
                        {
                            writer.WriteStartArray("floats");
                            foreach (var cellFloat in cellFloats)
                            {
                                writer.WriteStartObject();
                                writer.WriteString("anchor", cellFloat.AnchorParagraphId.ToString());
                                writer.WriteNumber("at", cellFloat.AnchorParagraphIndex);
                                writer.WriteString("hash", ComputeBlockPropertiesHash(cellFloat.Object));
                                writer.WriteEndObject();
                            }
                            writer.WriteEndArray();
                        }

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

            return lease.ComputeHash();
        }

        /// <summary>
        /// Вычисляет SHA-256 хеш свойств раздела: параметры страницы и колонок,
        /// колонтитулы, состав и порядок блоков и плавающих объектов.
        /// </summary>
        public string ComputeSectionPropertiesHash(SectionModel section)
        {
            using var lease = HashWriter.Rent();
            var writer = lease.Writer;

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

            return lease.ComputeHash();
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
            using var lease = HashWriter.Rent();
            var writer = lease.Writer;

            writer.WriteStartObject();
            writer.WriteString("title", document.Title);
            writer.WriteNumber("schema", document.SchemaVersion);
            // Запись исправлений — свойство документа: её включение должно сохраняться.
            writer.WriteBoolean("trackRevisions", document.TrackRevisions);
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
            // Виды чтения, приложенные к рукописи. Без них заведённый в документе вид
            // не менял ни одного чанка, снимок документа считался неизменным, и на
            // диск уходила прежняя базовая линия — вид пропадал при перезапуске.
            if (document.ReadingThemes is not null)
            {
                writer.WritePropertyName("readingThemes");
                JsonSerializer.Serialize(writer, document.ReadingThemes);
            }
            // Колонтитулы и правила страниц живут у документа и в чанки не попадают:
            // без них правка колонтитула не считалась изменением и не сохранялась.
            if (document.HeaderFooter is not null)
            {
                writer.WritePropertyName("headerFooter");
                JsonSerializer.Serialize(writer, document.HeaderFooter);
            }
            WriteIdArray(writer, "sections", document.Sections.Select(s => s.Id));
            writer.WriteEndObject();

            return lease.ComputeHash();
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
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        /// <summary>
        /// Буфер и писатель JSON для хешей, общие на поток.
        ///
        /// Хеши считаются при каждом снимке документа — тике кеша и сохранении, —
        /// причём для каждого чанка и каждого блока, на UI-потоке. Раньше на каждый
        /// хеш заводились свой поток в памяти, свой писатель и свой объект SHA-256:
        /// на рукописи в три тысячи абзацев это десятки мегабайт мусора и заметная
        /// доля времени снимка. Здесь буфер и писатель переиспользуются, а хеш
        /// считается разовым вызовом над записанными байтами.
        ///
        /// В хеш идут ровно те же байты, что и прежде: тот же JSON с теми же
        /// настройками писателя, — поэтому хеши, уже лежащие в чанках, остаются
        /// действительными, и после обновления документ не считается изменённым.
        ///
        /// Буфер свой у каждого потока: снимок снимается на UI-потоке, подготовка
        /// данных при загрузке идёт в пуле. Повторный вход на том же потоке во время
        /// записи получает отдельный буфер. Буфер, раздутый большим блоком, после
        /// использования отпускается, чтобы не держать память до конца сессии.
        /// </summary>
        private sealed class HashWriter
        {
            private const int InitialCapacity = 16 * 1024;
            private const int RetainedCapacityLimit = 1024 * 1024;

            [ThreadStatic] private static HashWriter? t_cached;

            private readonly System.Buffers.ArrayBufferWriter<byte> _buffer = new(InitialCapacity);
            private readonly Utf8JsonWriter _writer;
            private bool _inUse;

            private HashWriter()
            {
                _writer = new Utf8JsonWriter(_buffer);
            }

            public static Lease Rent()
            {
                var cached = t_cached;

                if (cached is null || cached._inUse)
                {
                    var fresh = new HashWriter();
                    if (cached is null) t_cached = fresh;
                    cached = fresh;
                }

                cached._inUse = true;
                cached._buffer.ResetWrittenCount();
                cached._writer.Reset(cached._buffer);
                return new Lease(cached);
            }

            public readonly struct Lease : IDisposable
            {
                private readonly HashWriter _owner;

                public Lease(HashWriter owner) => _owner = owner;

                public Utf8JsonWriter Writer => _owner._writer;

                /// <summary>SHA-256 записанного JSON в виде шестнадцатеричной строки.</summary>
                public string ComputeHash()
                {
                    _owner._writer.Flush();

                    Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
                    SHA256.HashData(_owner._buffer.WrittenSpan, hash);
                    return Convert.ToHexString(hash);
                }

                public void Dispose()
                {
                    _owner._inUse = false;

                    if (_owner._buffer.Capacity > RetainedCapacityLimit
                        && ReferenceEquals(t_cached, _owner))
                    {
                        t_cached = null;
                    }
                }
            }
        }
    }
}
