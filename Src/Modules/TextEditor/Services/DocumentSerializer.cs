using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Полезная нагрузка дельта-кеша для одного сохранения.
    /// Содержит только изменившиеся чанки и аннотации.
    /// </summary>
    public sealed class DeltaCachePayload
    {
        /// <summary>Маркер для DataComparisonService.</summary>
        [JsonPropertyName("__deltaMode")]
        public bool DeltaMode { get; set; } = true;

        /// <summary>Версия схемы дельта-кеша.</summary>
        public int Version { get; set; } = 1;

        /// <summary>
        /// Изменившиеся чанки.
        /// Ключ — ChunkId.ToString().
        /// Значение — содержимое чанка в JSON (для восстановления).
        /// </summary>
        public Dictionary<string, ChunkCacheEntry> ChangedChunks { get; set; } = new();

        /// <summary>
        /// Удалённые чанки (Id → список удалённых ChunkId).
        /// </summary>
        public Dictionary<string, List<string>> RemovedChunks { get; set; } = new();

        /// <summary>
        /// Изменившиеся аннотации. Ключ — AnnotationId.ToString().
        /// </summary>
        public Dictionary<string, InlineAnnotation> ChangedAnnotations { get; set; } = new();

        /// <summary>Удалённые аннотации (список Id).</summary>
        public List<string> RemovedAnnotations { get; set; } = new();

        /// <summary>
        /// Порядок блоков в документе (List of BlockId в порядке следования).
        /// Сохраняется при каждом дельта-сохранении так как он маленький.
        /// </summary>
        public List<string> BlockOrder { get; set; } = new();

        public bool IsEmpty =>
            ChangedChunks.Count == 0
            && RemovedChunks.Count == 0
            && ChangedAnnotations.Count == 0
            && RemovedAnnotations.Count == 0;
    }

    /// <summary>
    /// Запись об одном изменившемся чанке в кеше.
    /// </summary>
    public sealed class ChunkCacheEntry
    {
        /// <summary>Id параграфа-владельца.</summary>
        public string BlockId { get; set; } = string.Empty;

        /// <summary>Id чанка.</summary>
        public string ChunkId { get; set; } = string.Empty;

        /// <summary>SHA-256 хеш содержимого (для быстрой проверки без десериализации).</summary>
        public string Hash { get; set; } = string.Empty;

        /// <summary>Содержимое чанка — список Run.</summary>
        public List<Models.Inline.RunModel> Runs { get; set; } = new();
    }

    /// <summary>
    /// Сериализует и десериализует <see cref="DocumentModel"/> в JSON.
    /// Также строит <see cref="DeltaCachePayload"/> для дельта-кеша.
    /// </summary>
    public sealed class DocumentSerializer
    {
        private readonly DeltaHashService _hashService;
        private readonly ChunkManager _chunkManager;

        private static readonly JsonSerializerOptions _options = new()
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };

        public DocumentSerializer(DeltaHashService hashService, ChunkManager chunkManager)
        {
            _hashService = hashService;
            _chunkManager = chunkManager;
        }

        /// <summary>
        /// Сериализует документ в JSON.
        /// </summary>
        public string Serialize(DocumentModel document)
        {
            return JsonSerializer.Serialize(document, _options);
        }

        /// <summary>
        /// Десериализует документ из JSON.
        /// </summary>
        public DocumentModel? Deserialize(string json)
        {
            return JsonSerializer.Deserialize<DocumentModel>(json, _options);
        }

        /// <summary>
        /// Сериализует документ в поток асинхронно.
        /// </summary>
        public async Task SerializeAsync(DocumentModel document, Stream stream)
        {
            await JsonSerializer.SerializeAsync(stream, document, _options);
        }

        /// <summary>
        /// Десериализует документ из потока асинхронно.
        /// </summary>
        public async Task<DocumentModel?> DeserializeAsync(Stream stream)
        {
            return await JsonSerializer.DeserializeAsync<DocumentModel>(stream, _options);
        }

        /// <summary>
        /// Нормализует чанки всего документа и строит полезную нагрузку дельта-кеша.
        /// Содержит только изменившиеся чанки (те у которых хеш изменился).
        /// Вызывать при каждом автосохранении.
        /// </summary>
        public DeltaCachePayload BuildDeltaPayload(DocumentModel document, DeltaCachePayload? previousPayload)
        {
            var payload = new DeltaCachePayload();

            // Собираем Id всех существующих чанков для определения удалённых.
            var existingChunkIds = new HashSet<string>();
            var existingAnnotationIds = new HashSet<string>();

            foreach (var section in document.Sections)
            {
                CollectBlocksForDelta(section.Blocks, payload, existingChunkIds);
                CollectBlocksForDelta(section.FloatingObjects, payload, existingChunkIds);
            }

            foreach (var annotation in document.Annotations)
            {
                existingAnnotationIds.Add(annotation.Id.ToString());
                if (_hashService.UpdateAnnotationHash(annotation))
                    payload.ChangedAnnotations[annotation.Id.ToString()] = annotation;
            }

            // Определяем удалённые чанки и аннотации на основе предыдущего payload.
            if (previousPayload is not null)
            {
                foreach (var prevChunkKey in previousPayload.ChangedChunks.Keys)
                {
                    if (!existingChunkIds.Contains(prevChunkKey))
                    {
                        var entry = previousPayload.ChangedChunks[prevChunkKey];
                        if (!payload.RemovedChunks.ContainsKey(entry.BlockId))
                            payload.RemovedChunks[entry.BlockId] = new List<string>();
                        payload.RemovedChunks[entry.BlockId].Add(prevChunkKey);
                    }
                }

                foreach (var prevAnnKey in previousPayload.ChangedAnnotations.Keys)
                {
                    if (!existingAnnotationIds.Contains(prevAnnKey))
                        payload.RemovedAnnotations.Add(prevAnnKey);
                }
            }

            // Сохраняем порядок всех блоков.
            foreach (var section in document.Sections)
                foreach (var block in section.Blocks)
                    payload.BlockOrder.Add(block.Id.ToString());

            return payload;
        }

        /// <summary>
        /// Обходит список блоков, нормализует чанки параграфов и добавляет изменившиеся в payload.
        /// </summary>
        private void CollectBlocksForDelta(
            System.Collections.Generic.List<BlockModel> blocks,
            DeltaCachePayload payload,
            HashSet<string> existingChunkIds)
        {
            foreach (var block in blocks)
            {
                if (block is ParagraphBlock paragraph)
                {
                    _chunkManager.NormalizeChunks(paragraph);

                    foreach (var chunk in paragraph.Chunks)
                    {
                        string chunkKey = chunk.Id.ToString();
                        existingChunkIds.Add(chunkKey);

                        if (_hashService.UpdateChunkHash(chunk))
                        {
                            payload.ChangedChunks[chunkKey] = new ChunkCacheEntry
                            {
                                BlockId = paragraph.Id.ToString(),
                                ChunkId = chunkKey,
                                Hash = chunk.Hash,
                                Runs = chunk.Runs
                            };
                        }
                    }
                }
                else if (block is TableBlock table)
                {
                    // Рекурсивно обходим ячейки таблицы.
                    foreach (var cell in table.Cells)
                    {
                        foreach (var cellPara in cell.Paragraphs)
                        {
                            _chunkManager.NormalizeChunks(cellPara);
                            foreach (var chunk in cellPara.Chunks)
                            {
                                string chunkKey = chunk.Id.ToString();
                                existingChunkIds.Add(chunkKey);

                                if (_hashService.UpdateChunkHash(chunk))
                                {
                                    payload.ChangedChunks[chunkKey] = new ChunkCacheEntry
                                    {
                                        BlockId = cellPara.Id.ToString(),
                                        ChunkId = chunkKey,
                                        Hash = chunk.Hash,
                                        Runs = chunk.Runs
                                    };
                                }
                            }
                        }
                    }
                }
                else if (block is FloatingTextBlock floatingText)
                {
                    foreach (var para in floatingText.Paragraphs)
                    {
                        _chunkManager.NormalizeChunks(para);
                        foreach (var chunk in para.Chunks)
                        {
                            string chunkKey = chunk.Id.ToString();
                            existingChunkIds.Add(chunkKey);

                            if (_hashService.UpdateChunkHash(chunk))
                            {
                                payload.ChangedChunks[chunkKey] = new ChunkCacheEntry
                                {
                                    BlockId = para.Id.ToString(),
                                    ChunkId = chunkKey,
                                    Hash = chunk.Hash,
                                    Runs = chunk.Runs
                                };
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Сериализует полезную нагрузку дельта-кеша в JSON.
        /// </summary>
        public string SerializeDeltaPayload(DeltaCachePayload payload)
        {
            return JsonSerializer.Serialize(payload, _options);
        }
    }
}
