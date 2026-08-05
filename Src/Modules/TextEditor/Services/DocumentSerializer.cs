using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        /// <summary>
        /// Хеши блоков без текстовых чанков (картинки, фигуры): BlockId → хеш свойств.
        /// Поворот, размер, обрезка, прозрачность, рамка, обтекание и позиция живут
        /// только здесь — в чанках их нет.
        /// </summary>
        public Dictionary<string, string> ObjectHashes { get; set; } = new();

        /// <summary>
        /// Изменились свойства объектов или порядок блоков по сравнению с прошлой
        /// дельтой. Отдельный флаг, потому что такие правки не создают ни
        /// изменившихся, ни удалённых чанков.
        /// </summary>
        public bool StructureChanged { get; set; }

        public bool IsEmpty =>
            ChangedChunks.Count == 0
            && RemovedChunks.Count == 0
            && ChangedAnnotations.Count == 0
            && RemovedAnnotations.Count == 0
            && !StructureChanged;
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
                CollectBlocksForDelta(section.InlineObjects, payload, existingChunkIds);

                // Параметры страницы и колонок, колонтитулы, состав и порядок
                // блоков раздела — всё это тоже не отражается в чанках.
                var currentSection = section;
                payload.ObjectHashes["section:" + section.Id] = SafeHash(
                    payload,
                    () => _hashService.ComputeSectionPropertiesHash(currentSection),
                    $"section {currentSection.Id}");
            }

            // Заголовок, стили документа, параметры страницы и оформление листа.
            payload.ObjectHashes["document"] = SafeHash(
                payload,
                () => _hashService.ComputeDocumentPropertiesHash(document),
                "document");

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

            // Правки картинок и фигур, а также перестановка блоков, не создают
            // изменившихся чанков. Без этого сравнения снимок документа считался
            // неизменным: поворот, обрезка и прочие свойства не попадали ни в кеш,
            // ни в сохранение — на диск уходила прежняя базовая линия.
            if (previousPayload is not null)
            {
                if (previousPayload.ObjectHashes.Count != payload.ObjectHashes.Count)
                {
                    payload.StructureChanged = true;
                }
                else
                {
                    foreach (var kv in payload.ObjectHashes)
                    {
                        if (previousPayload.ObjectHashes.TryGetValue(kv.Key, out var previousHash)
                            && previousHash == kv.Value)
                            continue;

                        payload.StructureChanged = true;
                        break;
                    }
                }

                if (!payload.StructureChanged
                    && !previousPayload.BlockOrder.SequenceEqual(payload.BlockOrder))
                    payload.StructureChanged = true;
            }

            return payload;
        }

        /// <summary>
        /// Хеш свойств с защитой от исключений. Сбой хеширования не имеет права
        /// ронять снимок документа: выше по стеку это означало бы возврат null из
        /// TakeStateSnapshot, то есть отсутствие и кеша, и сохранения. При ошибке
        /// блок считается изменённым — данные уйдут в файл в любом случае.
        /// </summary>
        private string SafeHash(DeltaCachePayload payload, Func<string> compute, string key)
        {
            try
            {
                return compute();
            }
            catch (Exception ex)
            {
                payload.StructureChanged = true;
                System.Diagnostics.Debug.WriteLine(
                    $"[DELTA] Hash failed for {key}: {ex.Message}");
                return "hash-error";
            }
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

                            // Оформление абзаца внутри ячейки живёт вне чанков.
                            // Хеш считается после нормализации: она может разбить
                            // или склеить чанки, а их состав входит в хеш.
                            payload.ObjectHashes[cellPara.Id.ToString()] = SafeHash(
                                payload,
                                () => _hashService.ComputeBlockPropertiesHash(cellPara),
                                $"cell paragraph {cellPara.Id}");

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

                        // Оформление абзаца внутри надписи живёт вне чанков.
                        payload.ObjectHashes[para.Id.ToString()] = SafeHash(
                            payload,
                            () => _hashService.ComputeBlockPropertiesHash(para),
                            $"floating paragraph {para.Id}");

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

                // Собственные свойства блока — всё, чего нет в чанках: оформление
                // абзаца и списка, геометрия картинок и фигур, структура и оформление
                // таблицы, параметры надписи, тип разрыва, состав дочерних элементов.
                // Считается последним: нормализация чанков выше могла изменить их состав.
                payload.ObjectHashes[block.Id.ToString()] = SafeHash(
                    payload,
                    () => _hashService.ComputeBlockPropertiesHash(block),
                    $"block {block.BlockType} {block.Id}");
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
