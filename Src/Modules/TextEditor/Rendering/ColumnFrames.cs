using System;
using System.Collections.Generic;
using Writersword.Core.Models.Rendering;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Page;

namespace Writersword.Modules.TextEditor.Rendering
{
    /// <summary>
    /// Колонки раздела на листе: где по ширине текстовой области стоит каждая и какой она
    /// ширины. Общий счёт для листов редактора (DocumentCanvas.Columns) и печати
    /// (SKTextRenderer.BuildPageLayout), чтобы колонки там и там совпадали.
    /// </summary>
    public static class ColumnFrames
    {
        private static float MmToPt(double mm) => (float)(mm * 72.0 / 25.4);

        /// <summary>
        /// Колонки последнего раздела: колонки разной ширины — из его параметров, иначе
        /// колонки документа (их же меняет команда «Колонки»).
        /// </summary>
        public static SectionColumns FinalColumns(DocumentModel document)
        {
            if (document.FinalSection?.Columns is { Columns: not null } own)
                return own;

            var settings = (document.Sections.Count > 0 ? document.Sections[0].ColumnSettings : null)
                ?? document.ColumnSettings;

            return new SectionColumns
            {
                Count = Math.Max(1, settings.ColumnCount),
                GapMm = settings.GapMm,
                Separator = settings.ShowSeparator,
                SeparatorColor = settings.SeparatorColor
            };
        }

        /// <summary>Колонки раздела, который кончается этим разрывом; null — последнего раздела.</summary>
        public static SectionColumns ColumnsOf(DocumentModel document, BreakBlock? sectionEnd)
            => sectionEnd?.Section?.Columns ?? FinalColumns(document);

        /// <summary>
        /// Левые края колонок от левого края текстовой области и их ширина, пт. Равные
        /// колонки делят ширину поровну за вычетом промежутков. Колонки разной ширины
        /// стоят так, как их записал Word; если вместе с промежутками они шире текстовой
        /// области, они сжимаются в неё пропорционально.
        /// </summary>
        public static (float[] Offsets, float[] Widths) Compute(SectionColumns? columns, float textWidthPt)
        {
            textWidthPt = Math.Max(textWidthPt, 1f);

            if (columns is null || columns.Count <= 1 && columns.Columns is not { Count: > 1 })
                return (new[] { 0f }, new[] { textWidthPt });

            if (columns.Columns is { Count: > 1 } own)
            {
                int n = own.Count;
                var widths = new float[n];
                var spaces = new float[n];
                float total = 0f;
                for (int i = 0; i < n; i++)
                {
                    widths[i] = Math.Max(MmToPt(own[i].WidthMm), 1f);
                    spaces[i] = i < n - 1 ? Math.Max(MmToPt(own[i].SpaceAfterMm), 0f) : 0f;
                    total += widths[i] + spaces[i];
                }

                float scale = total > textWidthPt ? textWidthPt / total : 1f;
                var offsets = new float[n];
                float x = 0f;
                for (int i = 0; i < n; i++)
                {
                    offsets[i] = x;
                    widths[i] *= scale;
                    x += widths[i] + spaces[i] * scale;
                }

                return (offsets, widths);
            }

            int count = columns.Count;
            float gap = Math.Max(MmToPt(columns.GapMm), 0f);

            // Промежутки не съедают колонки целиком: на каждую остаётся хотя бы пункт.
            if (gap * (count - 1) > textWidthPt - count)
                gap = Math.Max((textWidthPt - count) / Math.Max(count - 1, 1), 0f);

            float width = Math.Max((textWidthPt - gap * (count - 1)) / count, 1f);
            var equalOffsets = new float[count];
            var equalWidths = new float[count];
            for (int i = 0; i < count; i++)
            {
                equalOffsets[i] = i * (width + gap);
                equalWidths[i] = width;
            }

            return (equalOffsets, equalWidths);
        }

        /// <summary>
        /// Середина промежутка перед колонкой с номером <paramref name="column"/> от левого
        /// края текстовой области — там Word ставит черту между колонками.
        /// </summary>
        public static float SeparatorX(float[] offsets, float[] widths, int column)
            => (offsets[column - 1] + widths[column - 1] + offsets[column]) / 2f;

        /// <summary>
        /// Кусок абзаца в области колонок для выравнивания по высоте: строки
        /// [LineFrom, LineTo) раскладки, верх первой строки, высота (с интервалом после,
        /// если кусок кончает абзац) и левый край. Key — какому абзацу кусок принадлежит,
        /// Source — запись раскладки, из которой он взят.
        /// </summary>
        public readonly record struct ColumnSlice(
            object Key, object Source, SKTextLayout Layout,
            float Ypt, float HeightPt, int LineFrom, int LineTo, float XPt, int Column);

        private sealed class BalanceRun
        {
            public ColumnSlice First;
            public int LineFrom;
            public int LineTo;
            public bool Empty;
            public float EmptyHeightPt;
            public float GapPt;
            public float TrailingPt;
            public float LastX;
            public float LastBottomPt;
        }

        /// <summary>
        /// Выравнивает колонки области по высоте, как Word перед непрерывным разрывом:
        /// куски абзацев области раскладываются по колонкам заново с наименьшей высотой
        /// колонки, при которой всё помещается. Строки абзаца переходят в следующую
        /// колонку по одной, промежуток над абзацем вверху колонки снимается.
        ///
        /// Выравниваются куски без обтекания, стоящие у левого края своей колонки. Колонки
        /// разной ширины — только с <paramref name="relayout"/>: строка, перешедшая в
        /// колонку другой ширины, верстается по ней заново, и число строк абзаца меняется
        /// с каждой пробной высотой. Иначе — false, и раскладка остаётся как есть.
        /// </summary>
        /// <param name="slices">Куски области в порядке потока.</param>
        /// <param name="topPt">Верх области колонок.</param>
        /// <param name="limitPt">Низ места под текст.</param>
        /// <param name="leftPt">Левый край текстовой области.</param>
        /// <param name="balanced">Куски после выравнивания: колонка, место и строки каждого.</param>
        /// <param name="bottomPt">Низ самой длинной колонки после выравнивания.</param>
        /// <param name="usedColumns">Сколько колонок занято после выравнивания.</param>
        /// <param name="relayout">
        /// Перевёрстка абзаца для колонки другой ширины: первый кусок абзаца в области,
        /// его раскладка, первая строка, уходящая в колонку, и ширина колонки. Вернуть
        /// нужно раскладку, в которой строки до этой остались прежними, а с неё
        /// сверстаны по ширине колонки (SKTextRenderer.ContinueLayout); null — нельзя.
        /// </param>
        public static bool TryBalance(
            IReadOnlyList<ColumnSlice> slices,
            float topPt, float limitPt, float leftPt,
            float[] offsets, float[] widths,
            out List<ColumnSlice> balanced, out float bottomPt, out int usedColumns,
            Func<ColumnSlice, SKTextLayout, int, float, SKTextLayout?>? relayout = null)
        {
            balanced = new List<ColumnSlice>();
            bottomPt = topPt;
            usedColumns = 1;

            int columns = offsets.Length;
            if (columns < 2 || slices.Count == 0) return false;

            bool unequal = false;
            for (int c = 1; c < columns; c++)
                if (Math.Abs(widths[c] - widths[0]) > 0.5f) { unequal = true; break; }
            if (unequal && relayout is null) return false;

            var runs = new List<BalanceRun>();

            foreach (var entry in slices)
            {
                // Кусок стоит не у края своей колонки: его сдвинуло обтекание.
                bool atColumn = false;
                for (int c = 0; c < columns; c++)
                    if (Math.Abs(entry.XPt - (leftPt + offsets[c])) < 0.5f) { atColumn = true; break; }
                if (!atColumn) return false;

                var layout = entry.Layout;
                bool empty = layout.Lines.Count == 0;
                float linesPt = 0f;

                if (!empty)
                {
                    if (entry.LineFrom < 0 || entry.LineTo > layout.Lines.Count || entry.LineFrom >= entry.LineTo)
                        return false;

                    for (int li = entry.LineFrom; li < entry.LineTo; li++)
                    {
                        var line = layout.Lines[li];
                        if (line.WrapExtraTopPt > 0.01f || line.HasWrapFragments) return false;
                        linesPt += line.Height;
                    }
                }

                var last = runs.Count > 0 ? runs[^1] : null;

                // Продолжение того же абзаца из предыдущей колонки.
                if (!empty && last is not null && !last.Empty
                    && Equals(last.First.Key, entry.Key)
                    && last.LineTo == entry.LineFrom)
                {
                    last.LineTo = entry.LineTo;
                    last.TrailingPt = entry.LineTo == layout.Lines.Count
                        ? Math.Max(entry.HeightPt - linesPt, 0f)
                        : 0f;
                    last.LastX = entry.XPt;
                    last.LastBottomPt = entry.Ypt + linesPt;
                    continue;
                }

                float gapPt;
                if (last is null)
                {
                    gapPt = entry.Ypt - topPt;
                }
                else if (Math.Abs(entry.XPt - last.LastX) < 0.5f && entry.Ypt >= last.LastBottomPt - 0.5f)
                {
                    gapPt = entry.Ypt - last.LastBottomPt;
                }
                else
                {
                    // Абзац начинал новую колонку: промежуток — интервал после прошлого
                    // абзаца и интервал перед этим.
                    gapPt = last.TrailingPt + (!empty && entry.LineFrom == 0 ? layout.SpaceBeforePt : 0f);
                }

                runs.Add(new BalanceRun
                {
                    First = entry,
                    LineFrom = entry.LineFrom,
                    LineTo = entry.LineTo,
                    Empty = empty,
                    EmptyHeightPt = entry.HeightPt,
                    GapPt = Math.Max(gapPt, 0f),
                    TrailingPt = !empty && entry.LineTo == layout.Lines.Count
                        ? Math.Max(entry.HeightPt - linesPt, 0f)
                        : 0f,
                    LastX = entry.XPt,
                    LastBottomPt = entry.Ypt + (empty ? entry.HeightPt : linesPt)
                });
            }

            if (unequal)
                return TryBalanceUnequal(
                    runs, topPt, limitPt, leftPt, offsets, widths, relayout!,
                    balanced, out bottomPt, out usedColumns);

            float UnitHeight(BalanceRun run, int unit)
                => run.Empty ? run.EmptyHeightPt : run.First.Layout.Lines[run.LineFrom + unit].Height;

            int UnitCount(BalanceRun run) => run.Empty ? 1 : run.LineTo - run.LineFrom;

            // Раскладка по колонкам высотой heightPt; place — колонка и верх каждой единицы.
            // Висячие строки — как у Word: первая строка абзаца не остаётся одна внизу
            // колонки, последняя не уходит одна в следующую.
            bool Fit(float heightPt, List<(int Column, float Top)[]>? place)
            {
                int column = 0;
                float y = 0f;

                for (int r = 0; r < runs.Count; r++)
                {
                    var run = runs[r];
                    if (r == 0) y = run.GapPt;
                    else if (y > 0.01f) y += run.GapPt;

                    int units = UnitCount(run);
                    var slots = new (int Column, float Top)[units];
                    float runStartY = y;
                    int firstInColumn = 0;

                    int u = 0;
                    while (u < units)
                    {
                        float h = UnitHeight(run, u);
                        if (y + h > heightPt + 0.01f && y > 0.01f)
                        {
                            int breakAt = u;
                            if (!run.Empty && units >= 2 && firstInColumn == 0 && u == 1 && runStartY > 0.01f)
                                breakAt = 0;
                            else if (!run.Empty && units >= 3 && units - u == 1 && u - firstInColumn >= 2)
                                breakAt = u - 1;

                            column++;
                            y = 0f;
                            if (column >= columns) return false;

                            firstInColumn = breakAt;
                            u = breakAt;
                            continue;
                        }

                        slots[u] = (column, y);
                        y += h;
                        u++;
                    }

                    place?.Add(slots);
                }

                return true;
            }

            float totalPt = 0f;
            foreach (var run in runs)
            {
                totalPt += run.GapPt;
                for (int u = 0; u < UnitCount(run); u++) totalPt += UnitHeight(run, u);
            }

            float high = limitPt - topPt;
            if (high <= 0f || !Fit(high, null)) return false;

            float low = Math.Min(totalPt / columns, high);
            for (int step = 0; step < 32 && high - low > 0.05f; step++)
            {
                float mid = (low + high) / 2f;
                if (Fit(mid, null)) high = mid;
                else low = mid;
            }

            var placement = new List<(int Column, float Top)[]>(runs.Count);
            if (!Fit(high, placement)) return false;

            float bottom = topPt;
            int used = 1;

            for (int r = 0; r < runs.Count; r++)
            {
                var run = runs[r];
                var slots = placement[r];

                if (run.Empty)
                {
                    var (column, top) = slots[0];
                    used = Math.Max(used, column + 1);
                    var slice = run.First with
                    {
                        Ypt = topPt + top,
                        HeightPt = run.EmptyHeightPt,
                        XPt = leftPt + offsets[column],
                        Column = column
                    };
                    balanced.Add(slice);
                    bottom = Math.Max(bottom, slice.Ypt + slice.HeightPt);
                    continue;
                }

                int lines = run.First.Layout.Lines.Count;
                int start = 0;
                while (start < slots.Length)
                {
                    int column = slots[start].Column;
                    used = Math.Max(used, column + 1);

                    int end = start;
                    float heightPt = 0f;
                    while (end < slots.Length && slots[end].Column == column)
                    {
                        heightPt += UnitHeight(run, end);
                        end++;
                    }

                    int lineTo = run.LineFrom + end;
                    if (lineTo == lines) heightPt += run.TrailingPt;

                    var slice = run.First with
                    {
                        Ypt = topPt + slots[start].Top,
                        HeightPt = heightPt,
                        LineFrom = run.LineFrom + start,
                        LineTo = lineTo,
                        XPt = leftPt + offsets[column],
                        Column = column
                    };
                    balanced.Add(slice);
                    bottom = Math.Max(bottom, slice.Ypt + slice.HeightPt);

                    start = end;
                }
            }

            bottomPt = bottom;
            usedColumns = used;
            return true;
        }

        /// <summary>
        /// Выравнивание колонок разной ширины. Счёт тот же, что у равных колонок, но
        /// строки абзаца не переносятся из колонки в колонку как есть: перешедшие в
        /// колонку другой ширины верстаются по ней заново (<paramref name="relayout"/>),
        /// и сколько строк у абзаца, зависит от того, где он разорван. Поэтому каждая
        /// пробная высота раскладывает абзацы заново, а место строк ищется по номеру
        /// строки в той раскладке, что получилась у абзаца к концу пробы.
        /// </summary>
        private static bool TryBalanceUnequal(
            List<BalanceRun> runs,
            float topPt, float limitPt, float leftPt,
            float[] offsets, float[] widths,
            Func<ColumnSlice, SKTextLayout, int, float, SKTextLayout?> relayout,
            List<ColumnSlice> balanced, out float bottomPt, out int usedColumns)
        {
            bottomPt = topPt;
            usedColumns = 1;
            int columns = offsets.Length;

            // Раскладка, строки которой с fromLine стоят по ширине колонки column.
            SKTextLayout? Fitted(BalanceRun run, SKTextLayout layout, int fromLine, int column)
                => SKTextRenderer.LinesFitWidth(layout, fromLine, widths[column])
                    ? layout
                    : relayout(run.First, layout, fromLine, widths[column]);

            // Раскладка по колонкам высотой heightPt. place — строки каждого абзаца с
            // колонкой и верхом, finals — раскладка абзаца после пробы.
            bool Fit(float heightPt, List<List<(int Line, int Column, float Top)>>? place, SKTextLayout?[]? finals)
            {
                int column = 0;
                float y = 0f;

                for (int r = 0; r < runs.Count; r++)
                {
                    var run = runs[r];
                    if (r == 0) y = run.GapPt;
                    else if (y > 0.01f) y += run.GapPt;

                    if (run.Empty)
                    {
                        if (y + run.EmptyHeightPt > heightPt + 0.01f && y > 0.01f)
                        {
                            column++;
                            y = 0f;
                            if (column >= columns) return false;
                        }

                        place?.Add(new List<(int, int, float)> { (-1, column, y) });
                        if (finals is not null) finals[r] = null;
                        y += run.EmptyHeightPt;
                        continue;
                    }

                    var layout = Fitted(run, run.First.Layout, run.LineFrom, column);
                    if (layout is null) return false;

                    var slots = place is null ? null : new List<(int Line, int Column, float Top)>();
                    float runStartY = y;
                    int firstInColumn = run.LineFrom;
                    int u = run.LineFrom;

                    while (u < layout.Lines.Count)
                    {
                        float h = layout.Lines[u].Height;
                        if (y + h > heightPt + 0.01f && y > 0.01f)
                        {
                            // Висячие строки — как у равных колонок: первая строка абзаца
                            // не остаётся одна внизу колонки, последняя не уходит одна.
                            int units = layout.Lines.Count - run.LineFrom;
                            int breakAt = u;
                            if (units >= 2 && firstInColumn == run.LineFrom && u == run.LineFrom + 1 && runStartY > 0.01f)
                                breakAt = run.LineFrom;
                            else if (units >= 3 && layout.Lines.Count - u == 1 && u - firstInColumn >= 2)
                                breakAt = u - 1;

                            column++;
                            y = 0f;
                            if (column >= columns) return false;

                            slots?.RemoveAll(slot => slot.Line >= breakAt);
                            firstInColumn = breakAt;
                            u = breakAt;

                            layout = Fitted(run, layout, u, column);
                            if (layout is null) return false;
                            continue;
                        }

                        slots?.Add((u, column, y));
                        y += h;
                        u++;
                    }

                    if (slots is not null) place!.Add(slots);
                    if (finals is not null) finals[r] = layout;
                }

                return true;
            }

            float high = limitPt - topPt;
            if (high <= 0f || !Fit(high, null, null)) return false;

            // Нижняя граница — не доля нынешней высоты строк: в широкой колонке абзац
            // займёт меньше строк, чем сейчас, и столбцы могут выйти ниже этой доли.
            float low = 0f;
            for (int step = 0; step < 32 && high - low > 0.05f; step++)
            {
                float mid = (low + high) / 2f;
                if (Fit(mid, null, null)) high = mid;
                else low = mid;
            }

            var placement = new List<List<(int Line, int Column, float Top)>>(runs.Count);
            var finals = new SKTextLayout?[runs.Count];
            if (!Fit(high, placement, finals)) return false;

            float bottom = topPt;
            int used = 1;

            for (int r = 0; r < runs.Count; r++)
            {
                var run = runs[r];
                var slots = placement[r];

                if (run.Empty)
                {
                    var (_, column, top) = slots[0];
                    used = Math.Max(used, column + 1);
                    var slice = run.First with
                    {
                        Ypt = topPt + top,
                        HeightPt = run.EmptyHeightPt,
                        XPt = leftPt + offsets[column],
                        Column = column
                    };
                    balanced.Add(slice);
                    bottom = Math.Max(bottom, slice.Ypt + slice.HeightPt);
                    continue;
                }

                var layout = finals[r]!;
                int lines = layout.Lines.Count;
                int start = 0;
                while (start < slots.Count)
                {
                    int column = slots[start].Column;
                    used = Math.Max(used, column + 1);

                    int end = start;
                    float heightPt = 0f;
                    while (end < slots.Count && slots[end].Column == column)
                    {
                        heightPt += layout.Lines[slots[end].Line].Height;
                        end++;
                    }

                    int lineTo = slots[end - 1].Line + 1;
                    if (lineTo == lines) heightPt += run.TrailingPt;

                    var slice = run.First with
                    {
                        Layout = layout,
                        Ypt = topPt + slots[start].Top,
                        HeightPt = heightPt,
                        LineFrom = slots[start].Line,
                        LineTo = lineTo,
                        XPt = leftPt + offsets[column],
                        Column = column
                    };
                    balanced.Add(slice);
                    bottom = Math.Max(bottom, slice.Ypt + slice.HeightPt);

                    start = end;
                }
            }

            bottomPt = bottom;
            usedColumns = used;
            return true;
        }
    }
}
