using System;
using System.Collections.Generic;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Управляет жизненным циклом чанков параграфа.
    /// Отвечает за сплит при превышении порога и мерж при деградации.
    /// Операции выполняются при сохранении, не в реальном времени.
    /// </summary>
    public sealed class ChunkManager
    {
        private readonly DeltaHashService _hashService;

        public ChunkManager(DeltaHashService hashService)
        {
            _hashService = hashService;
        }

        /// <summary>
        /// Нормализует чанки параграфа: выполняет сплит и мерж там где нужно.
        /// Возвращает true если структура чанков изменилась (появились новые или удалились старые Id).
        /// </summary>
        public bool NormalizeChunks(ParagraphBlock paragraph)
        {
            bool structureChanged = false;

            // Сначала мержим слишком маленькие соседние чанки.
            structureChanged |= MergeSmallChunks(paragraph);

            // Затем разбиваем аномально большие.
            structureChanged |= SplitOversizedChunks(paragraph);

            return structureChanged;
        }

        /// <summary>
        /// Объединяет соседние чанки если оба меньше <see cref="TextChunk.MergeThreshold"/>.
        /// Проходит по списку до тех пор пока мержи возможны.
        /// </summary>
        private bool MergeSmallChunks(ParagraphBlock paragraph)
        {
            bool anyMerged = false;

            int i = 0;
            while (i < paragraph.Chunks.Count - 1)
            {
                var current = paragraph.Chunks[i];
                var next = paragraph.Chunks[i + 1];

                if (current.Length < TextChunk.MergeThreshold
                    && next.Length < TextChunk.MergeThreshold)
                {
                    MergeChunks(paragraph, i, i + 1);
                    anyMerged = true;
                    // Не увеличиваем i — новый чанк может снова оказаться маленьким.
                }
                else
                {
                    i++;
                }
            }

            return anyMerged;
        }

        /// <summary>
        /// Разбивает чанки превышающие <see cref="TextChunk.SplitThreshold"/>.
        /// </summary>
        private bool SplitOversizedChunks(ParagraphBlock paragraph)
        {
            bool anySplit = false;

            int i = 0;
            while (i < paragraph.Chunks.Count)
            {
                var chunk = paragraph.Chunks[i];

                if (chunk.Length > TextChunk.SplitThreshold)
                {
                    int insertedCount = SplitChunk(paragraph, i);
                    // После сплита пропускаем все вставленные чанки — они уже нормального размера.
                    i += insertedCount;
                    anySplit = true;
                }
                else
                {
                    i++;
                }
            }

            return anySplit;
        }

        /// <summary>
        /// Объединяет два соседних чанка по индексам <paramref name="firstIndex"/> и <paramref name="firstIndex"/>+1.
        /// Первый чанк заменяется объединённым, второй удаляется.
        /// </summary>
        private void MergeChunks(ParagraphBlock paragraph, int firstIndex, int secondIndex)
        {
            var first = paragraph.Chunks[firstIndex];
            var second = paragraph.Chunks[secondIndex];

            var merged = new TextChunk();
            merged.Runs.AddRange(first.Runs);
            merged.Runs.AddRange(second.Runs);
            merged.InvalidateLength();

            // Пытаемся объединить соседние Run с одинаковыми свойствами.
            CollapseAdjacentRuns(merged);

            paragraph.Chunks[firstIndex] = merged;
            paragraph.Chunks.RemoveAt(secondIndex);
        }

        /// <summary>
        /// Разбивает чанк по индексу <paramref name="chunkIndex"/> на несколько чанков
        /// по <see cref="TextChunk.NormalChunkSize"/> символов каждый.
        /// Старый чанк заменяется новыми. Граница сплита выбирается по пробелу.
        /// Возвращает количество вставленных чанков.
        /// </summary>
        private int SplitChunk(ParagraphBlock paragraph, int chunkIndex)
        {
            var source = paragraph.Chunks[chunkIndex];
            string fullText = source.GetPlainText();

            // Определяем позиции разбиения.
            var splitPositions = new List<int>();
            int pos = 0;
            while (pos + TextChunk.NormalChunkSize < fullText.Length)
            {
                int splitAt = pos + TextChunk.NormalChunkSize;
                // Ищем ближайший пробел или перенос строки назад от splitAt.
                int boundary = FindWordBoundary(fullText, splitAt);
                splitPositions.Add(boundary);
                pos = boundary;
            }

            if (splitPositions.Count == 0) return 1;

            // Разбиваем Run-ы по позициям.
            var newChunks = SplitRunsByPositions(source, splitPositions);

            // Заменяем исходный чанк новыми.
            paragraph.Chunks.RemoveAt(chunkIndex);
            for (int i = 0; i < newChunks.Count; i++)
                paragraph.Chunks.Insert(chunkIndex + i, newChunks[i]);

            return newChunks.Count;
        }

        /// <summary>
        /// Ищет ближайшую позицию пробела/переноса строки не дальше чем <paramref name="desiredPos"/>.
        /// Если пробел не найден в пределах 200 символов — возвращает исходную позицию.
        /// </summary>
        private static int FindWordBoundary(string text, int desiredPos)
        {
            const int searchWindow = 200;
            int start = Math.Max(0, desiredPos - searchWindow);

            for (int i = desiredPos; i >= start; i--)
            {
                char c = text[i];
                if (c == ' ' || c == '\n' || c == '\t')
                    return i + 1;
            }

            return desiredPos;
        }

        /// <summary>
        /// Разбивает список Run исходного чанка на несколько чанков
        /// по заданным символьным позициям.
        /// </summary>
        private static List<TextChunk> SplitRunsByPositions(TextChunk source, List<int> positions)
        {
            var result = new List<TextChunk>();
            var allPositions = new List<int>(positions) { int.MaxValue };

            int globalOffset = 0;
            int posIndex = 0;
            var currentChunk = new TextChunk();

            foreach (var run in source.Runs)
            {
                int runOffset = 0;

                while (runOffset < run.Text.Length)
                {
                    int splitGlobal = allPositions[posIndex];
                    int runEnd = globalOffset + run.Text.Length;
                    int copyEnd = Math.Min(splitGlobal - globalOffset, run.Text.Length - runOffset);

                    if (copyEnd <= 0)
                    {
                        // Граница сплита точно на начале этого Run — закрываем текущий чанк.
                        result.Add(currentChunk);
                        currentChunk = new TextChunk();
                        posIndex++;
                        continue;
                    }

                    string piece = run.Text.Substring(runOffset, copyEnd);
                    currentChunk.Runs.Add(new RunModel
                    {
                        Text = piece,
                        Properties = run.Properties?.Clone(),
                        // Объектный run — один символ-заполнитель, разрезать его нельзя,
                        // а ссылку на картинку обязан унести кусок целиком.
                        InlineImageId = run.InlineImageId
                    });
                    currentChunk.InvalidateLength();

                    runOffset += copyEnd;
                    globalOffset += copyEnd;

                    if (globalOffset >= splitGlobal && posIndex < allPositions.Count - 1)
                    {
                        result.Add(currentChunk);
                        currentChunk = new TextChunk();
                        posIndex++;
                    }
                }

                globalOffset += 0; // уже накоплено выше
            }

            // Последний чанк.
            if (currentChunk.Runs.Count > 0)
                result.Add(currentChunk);

            return result;
        }

        /// <summary>
        /// Объединяет соседние Run с идентичными свойствами форматирования.
        /// Уменьшает количество объектов в памяти.
        /// </summary>
        private static void CollapseAdjacentRuns(TextChunk chunk)
        {
            if (chunk.Runs.Count <= 1) return;

            for (int i = chunk.Runs.Count - 1; i > 0; i--)
            {
                var prev = chunk.Runs[i - 1];
                var curr = chunk.Runs[i];

                if (RunPropertiesEqual(prev.Properties, curr.Properties))
                {
                    prev.Text += curr.Text;
                    chunk.Runs.RemoveAt(i);
                }
            }

            chunk.InvalidateLength();
        }

        /// <summary>
        /// Сравнивает свойства двух Run для определения возможности слияния.
        /// </summary>
        private static bool RunPropertiesEqual(RunProperties? a, RunProperties? b)
        {
            if (a is null && b is null) return true;
            if (a is null || b is null) return false;

            return a.FontFamily == b.FontFamily
                && a.FontSize == b.FontSize
                && a.IsBold == b.IsBold
                && a.IsItalic == b.IsItalic
                && a.IsUnderline == b.IsUnderline
                && a.IsStrikethrough == b.IsStrikethrough
                && a.IsSuperscript == b.IsSuperscript
                && a.IsSubscript == b.IsSubscript
                && a.IsAllCaps == b.IsAllCaps
                && a.IsSmallCaps == b.IsSmallCaps
                && a.TextColor == b.TextColor
                && a.HighlightColor == b.HighlightColor
                && a.Language == b.Language;
        }
    }
}
