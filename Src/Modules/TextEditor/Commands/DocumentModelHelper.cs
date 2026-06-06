using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;

namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Вспомогательные методы для безопасной мутации DocumentModel на уровне runs.
    /// Используется командами для применения и отката изменений.
    /// Все методы работают с плоской символьной позицией (charPos) —
    /// внутри они разворачивают её в координаты Chunk/Run.
    /// </summary>
    public static class DocumentModelHelper
    {
        // ── Поиск ────────────────────────────────────────────────────────

        /// <summary>
        /// Найти ParagraphBlock по Id во всём документе (включая ячейки таблиц).
        /// Возвращает null если параграф не найден.
        /// </summary>
        public static ParagraphBlock? FindParagraph(DocumentModel doc, Guid id)
        {
            foreach (var section in doc.Sections)
            {
                var found = FindInBlocks(section.Blocks, id);
                if (found != null) return found;
            }
            return null;
        }

        private static ParagraphBlock? FindInBlocks(IEnumerable<BlockModel> blocks, Guid id)
        {
            foreach (var block in blocks)
            {
                if (block is ParagraphBlock para && para.Id == id)
                    return para;

                if (block is TableBlock table)
                {
                    foreach (var cell in table.Cells)
                    {
                        var found = FindInBlocks(cell.Paragraphs.Cast<BlockModel>(), id);
                        if (found != null) return found;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Найти секцию и индекс блока по Id параграфа (только верхний уровень, не ячейки).
        /// Возвращает null если параграф в ячейке таблицы или не найден.
        /// </summary>
        public static (SectionModel section, int blockIndex)? FindBlockPosition(DocumentModel doc, Guid paraId)
        {
            foreach (var section in doc.Sections)
            {
                for (int i = 0; i < section.Blocks.Count; i++)
                {
                    if (section.Blocks[i] is ParagraphBlock p && p.Id == paraId)
                        return (section, i);
                }
            }
            return null;
        }

        // ── Вставка текста ────────────────────────────────────────────────

        /// <summary>
        /// Вставить текст в параграф в позиции charPos.
        /// Если explicitProperties == null — текст наследует форматирование
        /// run-а в точке вставки (стандартное поведение при наборе).
        /// </summary>
        public static void InsertText(ParagraphBlock para, int charPos, string text,
            RunProperties? explicitProperties = null)
        {
            if (string.IsNullOrEmpty(text)) return;

            EnsureOneChunk(para);
            var chunk = para.Chunks[0];

            var (runIdx, offsetInRun) = MapToRun(chunk, charPos);

            if (runIdx < 0)
            {
                // Параграф пустой — просто добавляем run.
                chunk.Runs.Add(new RunModel
                {
                    Text = text,
                    Properties = explicitProperties?.Clone()
                });
                chunk.InvalidateLength();
                return;
            }

            var targetRun = chunk.Runs[runIdx];
            var insertProps = explicitProperties ?? targetRun.Properties?.Clone();

            if (offsetInRun == 0)
            {
                // Вставляем перед targetRun.
                chunk.Runs.Insert(runIdx, new RunModel
                {
                    Text = text,
                    Properties = insertProps
                });
            }
            else if (offsetInRun == (targetRun.Text?.Length ?? 0))
            {
                // Вставляем после targetRun.
                chunk.Runs.Insert(runIdx + 1, new RunModel
                {
                    Text = text,
                    Properties = insertProps
                });
            }
            else
            {
                // Разбиваем targetRun на две части и вставляем между ними.
                var textBefore = targetRun.Text![..offsetInRun];
                var textAfter = targetRun.Text[offsetInRun..];

                targetRun.Text = textBefore;

                chunk.Runs.Insert(runIdx + 1, new RunModel
                {
                    Text = text,
                    Properties = insertProps
                });
                chunk.Runs.Insert(runIdx + 2, new RunModel
                {
                    Text = textAfter,
                    Properties = targetRun.Properties?.Clone()
                });
            }

            chunk.InvalidateLength();
            CollapseAdjacentRuns(chunk);
        }

        // ── Удаление текста ───────────────────────────────────────────────

        /// <summary>
        /// Удалить диапазон символов [charPos, charPos + length) из параграфа.
        /// Возвращает снапшоты удалённых runs для возможности точного восстановления.
        /// </summary>
        public static RunSnapshot[] DeleteRange(ParagraphBlock para, int charPos, int length)
        {
            if (length <= 0) return Array.Empty<RunSnapshot>();

            EnsureOneChunk(para);
            var chunk = para.Chunks[0];

            // Собираем снапшот удаляемого диапазона до модификации.
            var snapshot = GetRunsInRange(para, charPos, length);

            int remaining = length;
            int pos = charPos;

            while (remaining > 0 && chunk.Runs.Count > 0)
            {
                var (runIdx, offsetInRun) = MapToRun(chunk, pos);
                if (runIdx < 0) break;

                var run = chunk.Runs[runIdx];
                int runLen = run.Text?.Length ?? 0;
                int available = runLen - offsetInRun;
                int toDelete = Math.Min(available, remaining);

                if (toDelete == runLen)
                {
                    // Удаляем run целиком.
                    chunk.Runs.RemoveAt(runIdx);
                }
                else
                {
                    // Удаляем часть run.
                    run.Text = run.Text!.Remove(offsetInRun, toDelete);
                }

                remaining -= toDelete;
                // pos не меняется — удаление сдвигает всё влево.
            }

            // Если параграф остался пустым — оставляем один пустой run.
            if (chunk.Runs.Count == 0)
                chunk.Runs.Add(new RunModel { Text = string.Empty });

            chunk.InvalidateLength();
            CollapseAdjacentRuns(chunk);

            return snapshot;
        }

        // ── Восстановление runs ───────────────────────────────────────────

        /// <summary>
        /// Вставить runs из снапшота в позицию charPos.
        /// Используется для точного восстановления удалённого форматированного текста.
        /// </summary>
        public static void RestoreRuns(ParagraphBlock para, int charPos, IReadOnlyList<RunSnapshot> runs)
        {
            if (runs.Count == 0) return;

            int insertPos = charPos;
            foreach (var snap in runs)
            {
                InsertText(para, insertPos, snap.Text, snap.Properties);
                insertPos += snap.Text.Length;
            }
        }

        /// <summary>
        /// Перегрузка для массива снапшотов.
        /// </summary>
        public static void RestoreRuns(ParagraphBlock para, int charPos, RunSnapshot[] runs)
            => RestoreRuns(para, charPos, (IReadOnlyList<RunSnapshot>)runs);

        // ── Чтение диапазона ──────────────────────────────────────────────

        /// <summary>
        /// Получить снапшоты runs в диапазоне [charPos, charPos + length).
        /// Runs на границах диапазона разрезаются по нужной позиции.
        /// </summary>
        public static RunSnapshot[] GetRunsInRange(ParagraphBlock para, int charPos, int length)
        {
            if (length <= 0) return Array.Empty<RunSnapshot>();

            EnsureOneChunk(para);
            var chunk = para.Chunks[0];
            var result = new List<RunSnapshot>();

            int currentPos = 0;
            int endPos = charPos + length;

            foreach (var run in chunk.Runs)
            {
                int runLen = run.Text?.Length ?? 0;
                int runEnd = currentPos + runLen;

                if (runEnd <= charPos)
                {
                    currentPos += runLen;
                    continue;
                }

                if (currentPos >= endPos)
                    break;

                int overlapStart = Math.Max(charPos, currentPos) - currentPos;
                int overlapEnd = Math.Min(endPos, runEnd) - currentPos;
                string text = (run.Text ?? string.Empty).Substring(overlapStart, overlapEnd - overlapStart);

                if (text.Length > 0)
                    result.Add(new RunSnapshot(text, run.Properties));

                currentPos += runLen;
            }

            return result.ToArray();
        }

        // ── Форматирование runs ───────────────────────────────────────────

        /// <summary>
        /// Применить мутацию свойств ко всем runs в диапазоне [from, to).
        /// Runs на границах разрезаются так чтобы мутация применялась точно.
        /// </summary>
        public static void ApplyRunProperty(ParagraphBlock para, int from, int to,
            Action<RunProperties> mutate)
        {
            if (from >= to) return;

            EnsureOneChunk(para);
            var chunk = para.Chunks[0];

            // Сначала разрезаем runs на границах диапазона.
            SplitRunAt(chunk, to);
            SplitRunAt(chunk, from);

            int currentPos = 0;
            foreach (var run in chunk.Runs)
            {
                int runLen = run.Text?.Length ?? 0;
                int runEnd = currentPos + runLen;

                if (currentPos >= from && runEnd <= to)
                {
                    // Run полностью внутри диапазона — применяем мутацию.
                    run.Properties ??= new RunProperties();
                    mutate(run.Properties);
                }

                currentPos += runLen;
            }

            chunk.InvalidateLength();
            CollapseAdjacentRuns(chunk);
        }

        // ── Вспомогательные методы ────────────────────────────────────────

        /// <summary>
        /// Убедиться что у параграфа есть хотя бы один chunk с одним run.
        /// Нормализует структуру перед операциями вставки/удаления.
        /// Сворачивает несколько chunks в один для упрощения операций —
        /// ChunkManager восстановит нужное разбиение при следующем сохранении.
        /// </summary>
        private static void EnsureOneChunk(ParagraphBlock para)
        {
            if (para.Chunks.Count == 0)
            {
                var chunk = new TextChunk();
                chunk.Runs.Add(new RunModel { Text = string.Empty });
                para.Chunks.Add(chunk);
                return;
            }

            if (para.Chunks.Count > 1)
            {
                var merged = new TextChunk();
                foreach (var c in para.Chunks)
                    merged.Runs.AddRange(c.Runs);
                para.Chunks.Clear();
                para.Chunks.Add(merged);
            }

            if (para.Chunks[0].Runs.Count == 0)
                para.Chunks[0].Runs.Add(new RunModel { Text = string.Empty });
        }

        /// <summary>
        /// Найти run и смещение внутри него для заданной символьной позиции.
        /// Возвращает (-1, 0) если параграф пуст.
        /// </summary>
        private static (int runIdx, int offsetInRun) MapToRun(TextChunk chunk, int charPos)
        {
            if (chunk.Runs.Count == 0) return (-1, 0);

            int pos = 0;
            for (int i = 0; i < chunk.Runs.Count; i++)
            {
                int len = chunk.Runs[i].Text?.Length ?? 0;
                if (charPos <= pos + len)
                    return (i, charPos - pos);
                pos += len;
            }

            // Позиция за концом — возвращаем конец последнего run.
            int last = chunk.Runs.Count - 1;
            return (last, chunk.Runs[last].Text?.Length ?? 0);
        }

        /// <summary>
        /// Разрезать run в позиции charPos если разрез не совпадает с границей run.
        /// После этого charPos гарантированно является началом какого-то run.
        /// </summary>
        private static void SplitRunAt(TextChunk chunk, int charPos)
        {
            if (charPos <= 0) return;

            int pos = 0;
            for (int i = 0; i < chunk.Runs.Count; i++)
            {
                var run = chunk.Runs[i];
                int len = run.Text?.Length ?? 0;

                if (pos + len > charPos)
                {
                    int offset = charPos - pos;
                    var before = run.Text![..offset];
                    var after = run.Text[offset..];

                    run.Text = before;
                    chunk.Runs.Insert(i + 1, new RunModel
                    {
                        Text = after,
                        Properties = run.Properties?.Clone()
                    });
                    return;
                }

                pos += len;
                if (pos == charPos) return; // Уже на границе run.
            }
        }

        /// <summary>
        /// Объединить соседние runs с одинаковым форматированием.
        /// Уменьшает количество объектов и упрощает дальнейшие операции.
        /// </summary>
        private static void CollapseAdjacentRuns(TextChunk chunk)
        {
            for (int i = chunk.Runs.Count - 1; i > 0; i--)
            {
                var prev = chunk.Runs[i - 1];
                var curr = chunk.Runs[i];

                if (RunPropertiesEqual(prev.Properties, curr.Properties))
                {
                    prev.Text = (prev.Text ?? string.Empty) + (curr.Text ?? string.Empty);
                    chunk.Runs.RemoveAt(i);
                }
            }
        }

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
