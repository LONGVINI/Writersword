using System;
using System.Collections.Generic;
using SkiaSharp;
using Writersword.Modules.TextEditor.Rendering;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Колонки раздела на листах правки, как у Word.
    ///
    /// Раскладка листов (RebuildPageModePass) ведёт текст по колонкам: колонка — это
    /// полоса листа от верха области колонок до низа текста. Не поместившееся уходит в
    /// следующую колонку того же листа, из последней — на новый лист. Область колонок
    /// начинается с верха листа, а у раздела после непрерывного разрыва — там, где
    /// кончился предыдущий раздел.
    ///
    /// Перед непрерывным разрывом Word выравнивает колонки раздела по высоте: текст
    /// последнего листа раздела делится между колонками поровну, и следующий раздел идёт
    /// под самой длинной из них (<see cref="TryBalanceColumns"/>). Разрыв колонки ведёт
    /// в следующую колонку, в разделе из одной колонки — на следующий лист.
    ///
    /// Черта между колонками (w:sep) стоит посередине промежутка от верха области
    /// колонок до низа текста; на листе, где колонки кончились раньше, — до низа самой
    /// длинной из них.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        /// <summary>
        /// Черта между колонками: X — от левого края листа, верх и низ — в координатах
        /// документа.
        /// </summary>
        private readonly record struct ColumnRule(int PageIndex, float XPt, float TopPt, float BottomPt);

        private List<ColumnRule> _columnRules = new();
        private List<ColumnRule> _passColumnRules = new();

        /// <summary>Толщина черты между колонками, пт: у Word — полпункта.</summary>
        private const float ColumnRuleWidthPt = 0.5f;

        /// <summary>Черты между колонками на видимых листах.</summary>
        private void DrawColumnRules(SKCanvas canvas, List<PageRect> pages, int firstPage, int lastPage)
        {
            List<ColumnRule> rules;
            lock (_renderLock) rules = _columnRules;
            if (rules.Count == 0) return;

            using var paint = new SKPaint
            {
                Color = SKTextRenderer.DefaultTextColorOverride ?? SKColors.Black,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = ColumnRuleWidthPt
            };

            foreach (var rule in rules)
            {
                if (rule.PageIndex < firstPage || rule.PageIndex > lastPage) continue;
                if (rule.PageIndex < 0 || rule.PageIndex >= pages.Count) continue;
                if (rule.BottomPt - rule.TopPt < 1f) continue;

                var page = pages[rule.PageIndex];
                float x = page.PadLeftPt + rule.XPt + page.GutterShiftPt;
                canvas.DrawLine(x, rule.TopPt, x, rule.BottomPt, paint);
            }
        }

        /// <summary>
        /// Выравнивает колонки области по высоте, как Word перед непрерывным разрывом
        /// (<see cref="ColumnFrames.TryBalance"/>): абзацы области, начиная с записи
        /// <paramref name="from"/>, раскладываются по колонкам заново. Выравниваются
        /// только области из одних абзацев листа — без таблиц, картинок и ячеек; иначе
        /// раскладка остаётся как есть, и false.
        /// </summary>
        /// <param name="topPt">Верх области колонок.</param>
        /// <param name="limitPt">Низ места под текст на листе.</param>
        /// <param name="leftPt">Левый край текстовой области листа.</param>
        /// <param name="bottomPt">Низ самой длинной колонки после выравнивания.</param>
        /// <param name="usedColumns">Сколько колонок занято после выравнивания.</param>
        private static bool TryBalanceColumns(
            List<ParaLayout> layouts, int from, int pageIdx,
            float topPt, float limitPt, float leftPt,
            float[] offsets, float[] widths,
            out float bottomPt, out int usedColumns)
        {
            bottomPt = topPt;
            usedColumns = 1;
            if (from >= layouts.Count) return false;

            var slices = new List<ColumnFrames.ColumnSlice>(layouts.Count - from);
            for (int i = from; i < layouts.Count; i++)
            {
                var entry = layouts[i];
                if (entry.Cell is not null || entry.Layout is null || entry.PageIndex != pageIdx) return false;

                slices.Add(new ColumnFrames.ColumnSlice(
                    entry.Vm, entry, entry.Layout,
                    entry.Ypt, entry.HeightPt, entry.LineFrom, entry.LineTo, entry.AbsXPt, 0));
            }

            if (!ColumnFrames.TryBalance(
                    slices, topPt, limitPt, leftPt, offsets, widths,
                    out var balanced, out bottomPt, out usedColumns))
                return false;

            layouts.RemoveRange(from, layouts.Count - from);
            foreach (var slice in balanced)
            {
                layouts.Add((ParaLayout)slice.Source with
                {
                    Ypt = slice.Ypt,
                    HeightPt = slice.HeightPt,
                    PageIndex = pageIdx,
                    LineFrom = slice.LineFrom,
                    LineTo = slice.LineTo,
                    AbsXPt = slice.XPt
                });
            }

            return true;
        }
    }
}
