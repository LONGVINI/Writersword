using System;
using System.Collections.Generic;
using SkiaSharp;
using Writersword.Core.Models.Project;
using Writersword.Core.Models.Rendering;
using Writersword.Modules.TextEditor.Rendering;
using Writersword.Modules.TextEditor.ViewModels.Blocks;

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
        private readonly record struct ColumnRule(int PageIndex, float XPt, float TopPt, float BottomPt, string? Color = null);

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

            // Авто — чёрная, а на листе с темой полотна — цвета текста темы, как буквы
            // без своего цвета. Свой цвет черты (SectionColumns.SeparatorColor) — как
            // задан, градиент — шейдером по длине черты.
            var autoColor = SKTextRenderer.DefaultTextColorOverride ?? SKColors.Black;

            // Черта в полпункта на экране — меньше пикселя. Сглаженная, она ложилась на
            // один пиксель или размазывалась на два — смотря где по дробной части
            // приходилась середина промежутка, — и черты на одном листе выглядели
            // разной толщины. Без поворота холста черта встаёт на целые пиксели:
            // толщина — целое число пикселей, не меньше одного (как у границ таблиц,
            // TableBorderPainter), левый край — на границе пикселя.
            var matrix = canvas.TotalMatrix;
            bool axisAligned = matrix.SkewX == 0f && matrix.SkewY == 0f && matrix.ScaleX > 0f;
            float scale = axisAligned ? matrix.ScaleX : 1f;
            float widthPx = Math.Max(1f, MathF.Floor(ColumnRuleWidthPt * scale + 0.001f));

            using var stroke = new SKPaint
            {
                Color = autoColor,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = ColumnRuleWidthPt
            };
            using var fill = new SKPaint
            {
                Color = autoColor,
                IsAntialias = false,
                Style = SKPaintStyle.Fill
            };

            foreach (var rule in rules)
            {
                if (rule.PageIndex < firstPage || rule.PageIndex > lastPage) continue;
                if (rule.PageIndex < 0 || rule.PageIndex >= pages.Count) continue;
                if (rule.BottomPt - rule.TopPt < 1f) continue;

                var page = pages[rule.PageIndex];
                float x = page.PadLeftPt + rule.XPt + page.GutterShiftPt;

                float left = x - ColumnRuleWidthPt / 2f;
                float width = ColumnRuleWidthPt;
                if (axisAligned)
                {
                    float leftPx = MathF.Round(x * scale + matrix.TransX - widthPx / 2f);
                    left = (leftPx - matrix.TransX) / scale;
                    width = widthPx / scale;
                }

                var ruleRect = new SKRect(left, rule.TopPt, left + width, rule.BottomPt);
                SKShader? shader = null;
                if (rule.Color is { } code)
                {
                    var solid = SKTextRenderer.GradientSolidColor(code, autoColor);
                    stroke.Color = solid;
                    fill.Color = solid;
                    if (SKTextRenderer.IsGradient(code))
                    {
                        try { shader = GradientShaderFactory.BuildShader(GradientSpec.Parse(code), ruleRect); }
                        catch { shader = null; }
                    }
                }
                else
                {
                    stroke.Color = autoColor;
                    fill.Color = autoColor;
                }

                stroke.Shader = shader;
                fill.Shader = shader;

                if (axisAligned)
                    canvas.DrawRect(ruleRect, fill);
                else
                    canvas.DrawLine(x, rule.TopPt, x, rule.BottomPt, stroke);

                stroke.Shader = null;
                fill.Shader = null;
                shader?.Dispose();
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
        private bool TryBalanceColumns(
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

            // Колонки разной ширины: абзац, перешедший в колонку другой ширины, верстается
            // по ней. Пробы высоты повторяют одни и те же переходы — перевёрстка одного
            // перехода делается один раз.
            var relaid = new Dictionary<(SKTextLayout Layout, int FromLine, int WidthKey), SKTextLayout>();
            SKTextLayout? Relayout(ColumnFrames.ColumnSlice slice, SKTextLayout layout, int fromLine, float widthPt)
            {
                if (slice.Key is not ParagraphViewModel pvm || pvm.Model is null) return null;

                var key = (layout, fromLine, (int)MathF.Round(widthPt * 100f));
                if (relaid.TryGetValue(key, out var ready)) return ready;

                var result = ContinueParagraphLayout(pvm, layout, fromLine, widthPt);
                relaid[key] = result;
                return result;
            }

            if (!ColumnFrames.TryBalance(
                    slices, topPt, limitPt, leftPt, offsets, widths,
                    out var balanced, out bottomPt, out usedColumns, Relayout))
                return false;

            layouts.RemoveRange(from, layouts.Count - from);
            foreach (var slice in balanced)
            {
                layouts.Add((ParaLayout)slice.Source with
                {
                    Layout = slice.Layout,
                    Ypt = slice.Ypt,
                    HeightPt = slice.HeightPt,
                    PageIndex = pageIdx,
                    LineFrom = slice.LineFrom,
                    LineTo = slice.LineTo,
                    AbsXPt = slice.XPt
                });
            }

            // Абзац, начатый на прошлом листе, мог получить новую раскладку: его куски
            // там смотрят в неё же (строки до перехода в ней прежние).
            if (from < layouts.Count && layouts[from].Layout is { } firstLayout)
            {
                var firstVm = layouts[from].Vm;
                for (int i = from - 1; i >= 0; i--)
                {
                    var earlier = layouts[i];
                    if (earlier.Vm != firstVm || earlier.Cell is not null) break;
                    if (!ReferenceEquals(earlier.Layout, firstLayout))
                        layouts[i] = earlier with { Layout = firstLayout };
                }
            }

            return true;
        }

        /// <summary>
        /// Абзац продолжается в колонке, ширина которой не та, по которой сверстаны его
        /// оставшиеся строки. Абзац с обтеканием так не переверстывается: его строки
        /// сверстаны вокруг объектов своего места.
        /// </summary>
        private static bool ContinuesInOtherWidth(
            ParagraphViewModel pvm, IReadOnlyList<SKWrapZone>? wrapZones, float columnWidthPt, float layoutWidthPt)
            => wrapZones is null
               && pvm.Model is not null
               && Math.Abs(columnWidthPt - layoutWidthPt) > 0.5f;

        /// <summary>
        /// Раскладка абзаца, строки которого с <paramref name="fromLine"/> ушли в колонку
        /// шириной <paramref name="widthPt"/> (SKTextRenderer.ContinueLayout). В кеш
        /// раскладок не попадает: она зависит от места абзаца на листе.
        /// </summary>
        private SKTextLayout ContinueParagraphLayout(ParagraphViewModel pvm, SKTextLayout layout, int fromLine, float widthPt)
        {
            if (pvm.Model is null || _styleResolver is null) return layout;
            return _renderer.ContinueLayout(layout, fromLine, pvm.Model, widthPt, _styleResolver);
        }

        /// <summary>
        /// Куски абзаца с записи <paramref name="from"/> смотрят в раскладку
        /// <paramref name="layout"/>: абзац перешёл в колонку другой ширины, и его
        /// итоговая раскладка заменяет те, что были у кусков до перехода.
        /// </summary>
        private static void ShareParagraphLayout(List<ParaLayout> layouts, int from, ParagraphViewModel pvm, SKTextLayout layout)
        {
            for (int i = Math.Max(from, 0); i < layouts.Count; i++)
            {
                var entry = layouts[i];
                if (entry.Vm != pvm || entry.Cell is not null || ReferenceEquals(entry.Layout, layout)) continue;
                layouts[i] = entry with { Layout = layout };
            }
        }
    }
}
