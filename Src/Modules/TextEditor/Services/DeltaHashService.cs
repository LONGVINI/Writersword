using System;
using System.Collections.Generic;
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
