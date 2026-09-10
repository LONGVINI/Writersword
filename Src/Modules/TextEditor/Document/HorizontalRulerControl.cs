using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Infrastructure.Behaviours;
using Writersword.Modules.TextEditor.ViewModels.Components;
// Псевдоним, а не using: у контрола есть своё свойство Resources, и без
// него имя Resources в этом классе означает словарь ресурсов Avalonia.
using Strings = Writersword.Modules.TextEditor.Resources.TextEditorStrings;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Горизонтальная линейка редактора.
    /// В режиме таблицы отображает маркеры колонок, левый край таблицы (drag сдвигает всю таблицу)
    /// и маркеры отступов абзаца в пределах активной ячейки.
    /// </summary>
    public sealed class HorizontalRulerControl : Control
    {
        private const double RulerHeightPx = 24.0;
        private const double MarkerSizePx = 8.0;
        private const double MarkerHitRadiusPx = 7.0;
        private const double MajorTickHeightPx = 10.0;
        private const double MinorTickHeightPx = 6.0;
        private const double TinyTickHeightPx = 3.0;

        // Значок позиции табуляции: невысокий, у самого низа линейки — там же, где
        // его ищут по привычке из Word, и там, где он не спорит со шкалой.
        private const double TabGlyphHeightPx = 7.0;
        private const double TabGlyphArmPx = 5.0;
        private const double TabHitRadiusPx = 5.0;

        // Насколько ниже линейки надо увести маркер, чтобы отпускание его сняло.
        private const double TabDiscardDistancePx = 8.0;

        // Маркеры отступов, списка, столбцов и перетаскивания цвета не меняют:
        // он у них смысловой — синий отступ, фиолетовый край списка, зелёный
        // столбец, оранжевое перетаскивание. Перекрасить их вместе с бумагой
        // значит потерять то единственное, что они и различают.
        private static readonly SKColor ColorMarkerIndent = new(0x33, 0x66, 0xCC);
        private static readonly SKColor ColorMarkerList = new(0x8A, 0x3F, 0xD0); // фиолетовый — маркер края списка
        private static readonly SKColor ColorMarkerColumn = new(0x22, 0x99, 0x55);
        private static readonly SKColor ColorMarkerLeftEdge = new(0x22, 0x99, 0x55); // такой же зелёный — перетаскивает всю таблицу
        private static readonly SKColor ColorMarkerDragging = new(0xFF, 0x66, 0x00);
        private static readonly SKColor ColorGuideLine = new(0xFF, 0x66, 0x00, 0xAA);
        private static readonly SKColor ColorMarkerTab = new(0x0E, 0x7A, 0x7A); // бирюзовый — позиция табуляции
        private static readonly SKColor ColorMarkerTabDefault = new(0x0E, 0x7A, 0x7A, 0x66);

        // Палитра фона, делений и цифр. Пересобирается перед каждой отрисовкой:
        // вид листа меняется на ходу, и линейка обязана меняться вместе с ним.
        // Без вида здесь стоят прежние серые тона.
        private RulerPalette _palette = RulerPalette.Default;

        private SKColor ColorBackground => _palette.Sheet;
        private SKColor ColorOutsidePage => _palette.OutsidePage;
        private SKColor ColorTickMajor => _palette.TickMajor;
        private SKColor ColorTickMinor => _palette.TickMinor;
        private SKColor ColorTickTiny => _palette.TickTiny;
        private SKColor ColorLabel => _palette.Label;
        private SKColor ColorLabelNegative => _palette.LabelNegative;
        private SKColor ColorBorder => _palette.Border;
        private SKColor ColorMarginZone => _palette.MarginZone;
        private SKColor ColorMarginHandle => _palette.MarginHandle;

        private RulerViewModel? _vm;
        private bool _isDragging;
        private bool _isDraggingMargin;
        private bool _draggingLeftMargin;
        private bool _isDraggingIndentInTable; // true = drag indent маркера в режиме таблицы
        private bool _isDraggingTab;

        public HorizontalRulerControl()
        {
            Height = RulerHeightPx;
            Cursor = new Cursor(StandardCursorType.Arrow);
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (_vm is not null)
                _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = DataContext as RulerViewModel;
            if (_vm is not null)
                _vm.PropertyChanged += OnVmPropertyChanged;
            InvalidateVisual();
        }

        private void OnVmPropertyChanged(object? sender,
            System.ComponentModel.PropertyChangedEventArgs e)
            => InvalidateVisual();

        public override void Render(DrawingContext ctx)
        {
            ctx.Custom(new RulerSKDrawOperation(
                this, new Rect(0, 0, Bounds.Width, Bounds.Height)));
        }

        internal void RenderWithSKCanvas(SKCanvas canvas)
        {
            if (_vm is null) return;

            // Цвета берутся у листа перед каждым кадром: вид меняется на ходу.
            _palette = RulerPalette.Resolve(_vm);

            float w = (float)Bounds.Width;
            float h = (float)RulerHeightPx;
            double zoom = _vm.Zoom;

            using var bgPaint = new SKPaint { Color = ColorBackground };
            canvas.DrawRect(0, 0, w, h, bgPaint);

            double pageOffsetXPx = _vm.PageOffsetXPx;
            double pageWidthPx = MmToPx(_vm.PageWidthMm, zoom);
            double marginLeftPx = MmToPx(_vm.MarginLeftMm, zoom);
            double marginRightPx = MmToPx(_vm.MarginRightMm, zoom);
            double textAreaStartPx = pageOffsetXPx + marginLeftPx;
            double textAreaEndPx = pageOffsetXPx + pageWidthPx - marginRightPx;

            // Область вне страницы.
            using var outerPaint = new SKPaint { Color = ColorOutsidePage };
            if (pageOffsetXPx > 0)
                canvas.DrawRect(0, 0, (float)pageOffsetXPx, h, outerPaint);
            double pageRightPx = pageOffsetXPx + pageWidthPx;
            if (pageRightPx < w)
                canvas.DrawRect((float)pageRightPx, 0, w - (float)pageRightPx, h, outerPaint);

            // Серые зоны полей.
            using var marginPaint = new SKPaint { Color = ColorMarginZone };
            canvas.DrawRect((float)pageOffsetXPx, 0,
                (float)(textAreaStartPx - pageOffsetXPx), h, marginPaint);
            canvas.DrawRect((float)textAreaEndPx, 0,
                (float)(pageRightPx - textAreaEndPx), h, marginPaint);

            // Граница поля и текста.
            using var handlePaint = new SKPaint
            { Color = ColorMarginHandle, StrokeWidth = 1f, IsStroke = true };
            canvas.DrawLine((float)textAreaStartPx, 0, (float)textAreaStartPx, h, handlePaint);
            canvas.DrawLine((float)textAreaEndPx, 0, (float)textAreaEndPx, h, handlePaint);

            DrawScale(canvas, pageOffsetXPx, pageWidthPx,
                textAreaStartPx, textAreaEndPx, h, zoom);

            var (zoneStartPx, zoneEndPx) = ZoneBounds(textAreaStartPx, textAreaEndPx, zoom);

            // Табуляция рисуется до маркеров отступа: значки стоят у самого низа, а
            // треугольники отступов заходят на ту же полосу и должны лежать поверх —
            // ими пользуются чаще, и перекрывать их мелочью нельзя.
            DrawTabMarkers(canvas, zoneStartPx, zoneEndPx, h, zoom);

            if (_vm.Mode == RulerMode.Paragraph)
            {
                DrawIndentMarkers(canvas, textAreaStartPx, textAreaEndPx, h, zoom);
            }
            else
            {
                double unitSizePx = UnitSizePx(zoom);
                double cellStartPx = textAreaStartPx + _vm.ActiveCellLeftUnits * unitSizePx;
                double cellEndPx = textAreaStartPx + _vm.ActiveCellRightUnits * unitSizePx;
                DrawColumnMarkers(canvas, textAreaStartPx, cellStartPx, cellEndPx, h, zoom);
            }

            using var borderPaint = new SKPaint
            { Color = ColorBorder, StrokeWidth = 1f, IsStroke = true };
            canvas.DrawLine(0, h - 0.5f, w, h - 0.5f, borderPaint);
        }

        // ── Шкала ─────────────────────────────────────────────────────────

        private void DrawScale(
            SKCanvas canvas,
            double pageOffsetXPx, double pageWidthPx,
            double textAreaStartPx, double textAreaEndPx,
            float h, double zoom)
        {
            if (_vm is null) return;

            double unitSizePx = UnitSizePx(zoom);
            double majorInterval = _vm.MajorTickInterval;
            double minorInterval = _vm.MinorTickInterval;
            double tinyInterval = _vm.TinyTickInterval;

            using var majorPaint = new SKPaint
            { Color = ColorTickMajor, StrokeWidth = 1f, IsStroke = true, IsAntialias = false };
            using var minorPaint = new SKPaint
            { Color = ColorTickMinor, StrokeWidth = 1f, IsStroke = true, IsAntialias = false };
            using var tinyPaint = new SKPaint
            { Color = ColorTickTiny, StrokeWidth = 1f, IsStroke = true, IsAntialias = false };
            using var labelPaint = new SKPaint { Color = ColorLabel, IsAntialias = true };
            using var labelNegPaint = new SKPaint { Color = ColorLabelNegative, IsAntialias = true };

            using var typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal)
                ?? SKTypeface.Default;
            using var font = new SKFont(typeface, 8f);

            double pageStartXPx = pageOffsetXPx;
            double pageEndXPx = pageOffsetXPx + pageWidthPx;

            int stepsLeft = (int)Math.Ceiling((textAreaStartPx - pageStartXPx) / (unitSizePx * tinyInterval)) + 2;
            int stepsRight = (int)Math.Ceiling((pageEndXPx - textAreaStartPx) / (unitSizePx * tinyInterval)) + 2;

            int tinyPerMajor = (int)Math.Round(majorInterval / tinyInterval);
            int tinyPerMinor = (int)Math.Round(minorInterval / tinyInterval);

            for (int i = -stepsLeft; i <= stepsRight; i++)
            {
                double unitValue = i * tinyInterval;
                double xPx = textAreaStartPx + unitValue * unitSizePx;

                if (xPx < 0 || xPx > Bounds.Width) continue;

                bool isMajor = (i % tinyPerMajor) == 0;
                bool isMinor = !isMajor && (i % tinyPerMinor) == 0;

                float tickH = isMajor ? (float)MajorTickHeightPx
                            : isMinor ? (float)MinorTickHeightPx
                            : (float)TinyTickHeightPx;

                var paint = isMajor ? majorPaint : isMinor ? minorPaint : tinyPaint;
                canvas.DrawLine((float)xPx, h - tickH, (float)xPx, h, paint);

                if (isMajor)
                {
                    if (Math.Abs(unitValue) < majorInterval * 0.1) continue;

                    bool isNeg = unitValue < 0;
                    string label = _vm.Units == Models.Settings.RulerUnits.Inches
                        ? Math.Abs(unitValue).ToString("0.##")
                        : ((int)Math.Round(Math.Abs(unitValue) * 10)).ToString();
                    if (isNeg) label = "-" + label;

                    float textW = font.MeasureText(label);
                    canvas.DrawText(label,
                        (float)xPx - textW / 2f, h - (float)MajorTickHeightPx - 2f,
                        font, isNeg ? labelNegPaint : labelPaint);
                }
            }
        }

        // ── Маркеры отступов ──────────────────────────────────────────────

        private void DrawIndentMarkers(
            SKCanvas canvas,
            double textAreaStartPx, double textAreaEndPx,
            float h, double zoom)
        {
            if (_vm is null) return;

            double unitSizePx = UnitSizePx(zoom);
            float ms = (float)MarkerSizePx;
            float markerY = h - ms;

            var drawOrder = new[]
            {
                RulerIndentMarkerType.LeftIndent,
                RulerIndentMarkerType.RightIndent,
                RulerIndentMarkerType.FirstLineIndent
            };

            foreach (var type in drawOrder)
            {
                var marker = GetIndentMarker(type);
                if (marker is null) continue;

                bool isDragging = _vm.DraggingIndentMarker == type;
                var color = isDragging ? ColorMarkerDragging : ColorMarkerIndent;

                using var fillPaint = new SKPaint { Color = color, IsAntialias = true };
                using var strokePaint = new SKPaint
                {
                    Color = SKColors.White,
                    StrokeWidth = 1f,
                    IsStroke = true,
                    IsAntialias = true
                };

                double xPx;
                if (type == RulerIndentMarkerType.RightIndent)
                {
                    xPx = textAreaEndPx - marker.Position * unitSizePx;
                    DrawTriangleDown(canvas, (float)xPx, markerY, ms, fillPaint, strokePaint);
                }
                else if (type == RulerIndentMarkerType.FirstLineIndent)
                {
                    xPx = textAreaStartPx + marker.Position * unitSizePx;
                    DrawTriangleUp(canvas, (float)xPx, 0, ms, fillPaint, strokePaint);
                }
                else // LeftIndent
                {
                    xPx = textAreaStartPx + marker.Position * unitSizePx;
                    DrawTriangleDown(canvas, (float)xPx, markerY, ms, fillPaint, strokePaint);
                }

                if (isDragging)
                {
                    using var guidePaint = new SKPaint
                    {
                        Color = ColorGuideLine,
                        StrokeWidth = 1f,
                        IsStroke = true,
                        PathEffect = SKPathEffect.CreateDash(new[] { 4f, 4f }, 0)
                    };
                    canvas.DrawLine((float)xPx, 0, (float)xPx, h, guidePaint);
                }
            }

            // Дополнительная стрелка «край списка» (hanging): показывается только для абзацев-списков.
            // Рисуется верхним треугольником фиолетового цвета на позиции маркера от левого поля.
            if (_vm.ShowListMarker)
            {
                var listMarker = GetIndentMarker(RulerIndentMarkerType.ListMarker);
                if (listMarker is not null)
                {
                    bool isDragging = _vm.DraggingIndentMarker == RulerIndentMarkerType.ListMarker;
                    var color = isDragging ? ColorMarkerDragging : ColorMarkerList;
                    using var fillPaint = new SKPaint { Color = color, IsAntialias = true };
                    using var strokePaint = new SKPaint
                    {
                        Color = SKColors.White,
                        StrokeWidth = 1f,
                        IsStroke = true,
                        IsAntialias = true
                    };
                    double xPx = textAreaStartPx + listMarker.Position * unitSizePx;
                    DrawTriangleUp(canvas, (float)xPx, 0, ms, fillPaint, strokePaint);

                    if (isDragging)
                    {
                        using var guidePaint = new SKPaint
                        {
                            Color = ColorGuideLine,
                            StrokeWidth = 1f,
                            IsStroke = true,
                            PathEffect = SKPathEffect.CreateDash(new[] { 4f, 4f }, 0)
                        };
                        canvas.DrawLine((float)xPx, 0, (float)xPx, h, guidePaint);
                    }
                }
            }
        }

        private RulerIndentMarker? GetIndentMarker(RulerIndentMarkerType type)
        {
            if (_vm is null) return null;
            foreach (var m in _vm.IndentMarkers)
                if (m.Type == type) return m;
            return null;
        }

        // ── Позиции табуляции ─────────────────────────────────────────────

        /// <summary>
        /// Границы зоны абзаца в пикселях: вне таблицы это текстовая область страницы,
        /// внутри — контентный бокс активной ячейки. Позиции табуляции хранятся от левого
        /// края зоны, как и отступы, поэтому и рисуются от него.
        /// </summary>
        private (double Start, double End) ZoneBounds(
            double textAreaStartPx, double textAreaEndPx, double zoom)
        {
            if (_vm is null || _vm.Mode == RulerMode.Paragraph)
                return (textAreaStartPx, textAreaEndPx);

            double unitSizePx = UnitSizePx(zoom);
            return (textAreaStartPx + _vm.ActiveCellLeftUnits * unitSizePx,
                    textAreaStartPx + _vm.ActiveCellRightUnits * unitSizePx);
        }

        private void DrawTabMarkers(
            SKCanvas canvas,
            double zoneStartPx, double zoneEndPx,
            float h, double zoom)
        {
            if (_vm is null) return;

            double unitSizePx = UnitSizePx(zoom);

            // Снимок списка: жест идёт в UI-потоке, а рисует поток отрисовки.
            var markers = _vm.TabMarkers.ToList();
            int draggingIdx = _vm.DraggingTabIndex;
            bool discarding = _vm.IsTabDragDiscarding;

            // Засечки шага по умолчанию. Они начинаются за последней своей позицией:
            // раскладка ищет ближайшую заданную, и только не найдя её берёт шаг. Рисовать
            // их раньше значило бы обещать остановку там, где текст не остановится.
            double lastExplicitUnits = 0;
            foreach (var m in markers)
                if (m.Position > lastExplicitUnits) lastExplicitUnits = m.Position;

            double stepUnits = _vm.MmToUnits(_vm.DefaultTabStopMm);
            if (stepUnits > 0.01)
            {
                using var defPaint = new SKPaint
                { Color = ColorMarkerTabDefault, StrokeWidth = 1f, IsStroke = true, IsAntialias = false };

                // Верхняя граница числа засечек нужна не для красоты: шаг приходит из
                // документа и после неудачного импорта может оказаться крошечным, а
                // цикл по нему рисует до правого края зоны.
                const int MaxDefaultTicks = 400;

                int first = (int)Math.Floor(lastExplicitUnits / stepUnits) + 1;
                for (int i = first; i < first + MaxDefaultTicks; i++)
                {
                    double units = i * stepUnits;
                    double xPx = zoneStartPx + units * unitSizePx;
                    if (xPx > zoneEndPx) break;
                    if (xPx >= 0 && xPx <= Bounds.Width)
                        canvas.DrawLine((float)xPx, h - 3f, (float)xPx, h - 1f, defPaint);
                }
            }

            for (int i = 0; i < markers.Count; i++)
            {
                var marker = markers[i];
                double xPx = zoneStartPx + marker.Position * unitSizePx;
                if (xPx < -TabGlyphArmPx || xPx > Bounds.Width + TabGlyphArmPx) continue;

                bool isDragging = draggingIdx == i;
                var color = isDragging
                    ? (discarding ? new SKColor(0x99, 0x99, 0x99, 0x99) : ColorMarkerDragging)
                    : ColorMarkerTab;

                DrawTabGlyph(canvas, (float)xPx, h, marker.Alignment, marker.Leader, color);

                if (isDragging && !discarding)
                {
                    using var guidePaint = new SKPaint
                    {
                        Color = ColorGuideLine,
                        StrokeWidth = 1f,
                        IsStroke = true,
                        PathEffect = SKPathEffect.CreateDash(new[] { 4f, 4f }, 0)
                    };
                    canvas.DrawLine((float)xPx, 0, (float)xPx, h, guidePaint);
                }
            }
        }

        /// <summary>
        /// Рисует значок одного типа выравнивания. Форма читается сама: ножка стоит на самой
        /// позиции, а полка показывает, в какую сторону от неё пойдёт текст.
        /// </summary>
        private static void DrawTabGlyph(
            SKCanvas canvas, float xPx, float h,
            Models.Styles.TabAlignment alignment,
            Models.Styles.TabLeaderStyle leader,
            SKColor color)
        {
            float bottom = h - 1.5f;
            float top = bottom - (float)TabGlyphHeightPx;
            float arm = (float)TabGlyphArmPx;

            using var paint = new SKPaint
            {
                Color = color,
                StrokeWidth = 1.6f,
                IsStroke = true,
                IsAntialias = true,
                StrokeCap = SKStrokeCap.Round
            };

            canvas.DrawLine(xPx, top, xPx, bottom, paint);

            switch (alignment)
            {
                case Models.Styles.TabAlignment.Left:
                    canvas.DrawLine(xPx, bottom, xPx + arm, bottom, paint);
                    break;
                case Models.Styles.TabAlignment.Right:
                    canvas.DrawLine(xPx - arm, bottom, xPx, bottom, paint);
                    break;
                default:
                    canvas.DrawLine(xPx - arm + 1f, bottom, xPx + arm - 1f, bottom, paint);
                    break;
            }

            if (alignment == Models.Styles.TabAlignment.Decimal)
            {
                using var dotPaint = new SKPaint { Color = color, IsAntialias = true };
                canvas.DrawCircle(xPx + 3f, bottom - 2.5f, 1.3f, dotPaint);
            }

            if (leader != Models.Styles.TabLeaderStyle.None)
            {
                using var leaderPaint = new SKPaint
                { Color = color, StrokeWidth = 1f, IsStroke = true, IsAntialias = true };
                float y = top - 1.5f;
                canvas.DrawLine(xPx - arm, y, xPx - arm + 2f, y, leaderPaint);
                canvas.DrawLine(xPx - 1.5f, y, xPx + 0.5f, y, leaderPaint);
            }
        }

        // ── Подсказки о линейке ───────────────────────────────────────────

        /// <summary>
        /// Ведёт подсказку под указателем. Линейка рисует своё содержимое сама, и обычный
        /// путь — одна подсказка на весь контрол — здесь не годится: под указателем может
        /// оказаться стрелка отступа, значок табуляции или пустая полоса, и говорить надо
        /// про то, на что человек смотрит.
        ///
        /// Зовётся на каждое движение мыши. Повторные вызовы про то же место подсказку не
        /// пересобирают — за этим следит опознаватель места.
        /// </summary>
        private void UpdateTabHint(
            double xPx, double yPx,
            double zoneStartPx, double zoneEndPx,
            double unitSizePx)
        {
            if (_vm is null) return;

            // Стрелки отступов проверяются первыми — тем же порядком, каким разбираются
            // щелчки. Иначе подсказка рассказывала бы про табуляцию там, где нажатие
            // возьмётся за отступ.
            var indent = HitTestIndentMarkerPriority(xPx, yPx, zoneStartPx, zoneEndPx, unitSizePx);
            if (indent.HasValue)
            {
                double indentXPx = indent.Value == RulerIndentMarkerType.RightIndent
                    ? zoneEndPx - GetMarkerPosition(indent.Value) * unitSizePx
                    : zoneStartPx + GetMarkerPosition(indent.Value) * unitSizePx;

                TooltipBehavior.ShowSpot(
                    this,
                    "indent:" + indent.Value,
                    indentXPx,
                    IndentTitle(indent.Value),
                    string.Format(Strings.Tab_Hint_IndentBody, IndentMeaning(indent.Value)));
                return;
            }

            int hitTab = HitTestTabMarker(xPx, yPx, zoneStartPx, unitSizePx);
            if (hitTab >= 0)
            {
                var marker = _vm.TabMarkers[hitTab];
                double markerXPx = zoneStartPx + marker.Position * unitSizePx;

                TooltipBehavior.ShowSpot(
                    this,
                    "tabstop:" + hitTab + ":" + marker.Alignment + ":" + marker.Leader,
                    markerXPx,
                    string.Format(Strings.Tab_Hint_MarkerTitle, FormatPosition(marker.Position)),
                    string.Format(Strings.Tab_Hint_MarkerBody,
                        TabIconMarkup(marker.Alignment) + AlignmentMeaning(marker.Alignment),
                        LeaderName(marker.Leader)));
                return;
            }

            bool onStrip = yPx >= RulerHeightPx - TabGlyphHeightPx - 2
                           && xPx >= zoneStartPx && xPx <= zoneEndPx;

            if (onStrip)
            {
                TooltipBehavior.ShowSpot(
                    this, "tabstrip", xPx,
                    Strings.Tab_Hint_StripTitle,
                    Strings.Tab_Hint_StripBody,
                    HintPreview("tab-ruler"));
                return;
            }

            TooltipBehavior.HideSpot(this);
        }

        private static string IndentTitle(RulerIndentMarkerType type)
            => type switch
            {
                RulerIndentMarkerType.FirstLineIndent => Strings.Tab_Hint_IndentFirstTitle,
                RulerIndentMarkerType.RightIndent => Strings.Tab_Hint_IndentRightTitle,
                RulerIndentMarkerType.ListMarker => Strings.Tab_Hint_IndentListTitle,
                _ => Strings.Tab_Hint_IndentLeftTitle
            };

        private static string IndentMeaning(RulerIndentMarkerType type)
            => type switch
            {
                RulerIndentMarkerType.FirstLineIndent => Strings.Tab_Hint_IndentFirstWhat,
                RulerIndentMarkerType.RightIndent => Strings.Tab_Hint_IndentRightWhat,
                RulerIndentMarkerType.ListMarker => Strings.Tab_Hint_IndentListWhat,
                _ => Strings.Tab_Hint_IndentLeftWhat
            };

        private static string AlignmentMeaning(Models.Styles.TabAlignment alignment)
            => alignment switch
            {
                Models.Styles.TabAlignment.Center => Strings.Tab_Hint_WhatCenter,
                Models.Styles.TabAlignment.Right => Strings.Tab_Hint_WhatRight,
                Models.Styles.TabAlignment.Decimal => Strings.Tab_Hint_WhatDecimal,
                _ => Strings.Tab_Hint_WhatLeft
            };

        private static string LeaderName(Models.Styles.TabLeaderStyle leader)
            => leader switch
            {
                Models.Styles.TabLeaderStyle.Dots => Strings.Tab_LeaderDots,
                Models.Styles.TabLeaderStyle.Dashes => Strings.Tab_LeaderDashes,
                Models.Styles.TabLeaderStyle.Line => Strings.Tab_LeaderLine,
                _ => Strings.Tab_LeaderNone
            };

        private static string AlignmentImage(Models.Styles.TabAlignment alignment)
            => alignment switch
            {
                Models.Styles.TabAlignment.Center => "tab-center",
                Models.Styles.TabAlignment.Right => "tab-right",
                Models.Styles.TabAlignment.Decimal => "tab-decimal",
                _ => "tab-left"
            };

        /// <summary>
        /// Метка значка типа для строки подсказки. Подсказка понимает разметку
        /// [img:путь] и ставит по ней картинку ростом со строку.
        ///
        /// Живёт здесь, а не у переключателя типа, потому что значки эти — тема линейки:
        /// она рисует их у поставленных позиций, и картинка в подсказке обязана совпадать
        /// с тем, что человек видит на самой линейке.
        /// </summary>
        internal static string TabIconMarkup(Models.Styles.TabAlignment alignment)
            => "[img:" + TabIconsFolder + AlignmentImage(alignment) + ".png] ";

        private const string TabIconsFolder = "avares://Writersword/Resources/Images/Tabs/";

        /// <summary>Позиция маркера строкой в единицах линейки, с их обозначением.</summary>
        private string FormatPosition(double units)
        {
            string value = units.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
            return _vm is not null && _vm.Units == Models.Settings.RulerUnits.Inches
                ? value + "\""
                : value + " " + Strings.Tab_Unit_Cm;
        }

        /// <summary>
        /// Путь к картинке подсказки, если она в сборке есть. Нет — подсказка обойдётся
        /// текстом: рисунок здесь поясняет, а не несёт смысл, и ждать его появления,
        /// пряча объяснение, было бы хуже, чем показать одно объяснение.
        ///
        /// Картинки лежат в Resources/Images/Tabs корневого проекта и подхватываются
        /// сборкой сами: там стоит AvaloniaResource на всю папку Resources.
        /// </summary>
        private static string? HintPreview(string fileName)
        {
            string path = TabIconsFolder + fileName + ".png";

            try
            {
                return Avalonia.Platform.AssetLoader.Exists(new Uri(path)) ? path : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Позиция табуляции под курсором. -1 — мимо.</summary>
        private int HitTestTabMarker(
            double xPx, double yPx, double zoneStartPx, double unitSizePx)
        {
            if (_vm is null) return -1;
            if (yPx < RulerHeightPx - TabGlyphHeightPx - 2) return -1;

            int bestIdx = -1;
            double bestD = double.MaxValue;

            for (int i = 0; i < _vm.TabMarkers.Count; i++)
            {
                double markerX = zoneStartPx + _vm.TabMarkers[i].Position * unitSizePx;
                double d = Math.Abs(xPx - markerX);
                if (d <= TabHitRadiusPx && d < bestD) { bestD = d; bestIdx = i; }
            }

            return bestIdx;
        }

        // ── Маркеры колонок (режим таблицы) ──────────────────────────────

        private void DrawColumnMarkers(
            SKCanvas canvas,
            double textAreaStartPx,
            double cellStartPx, double cellEndPx,
            float h, double zoom)
        {
            if (_vm is null) return;

            double unitSizePx = UnitSizePx(zoom);

            // Снимаем снимок списка перед итерацией — ColumnMarkers может изменяться
            // из UI-потока (drag, UpdateTableColumns) пока render-поток рисует.
            var markers = _vm.ColumnMarkers.ToList();
            int draggingIdx = _vm.DraggingColumnIndex;

            foreach (var marker in markers)
            {
                double xPx = textAreaStartPx + marker.RightEdge * unitSizePx;
                bool isLeftEdge = marker.ColumnIndex < 0;

                bool isDragging = draggingIdx >= 0
                    && draggingIdx < markers.Count
                    && markers[draggingIdx].ColumnIndex == marker.ColumnIndex;

                var color = isDragging ? ColorMarkerDragging
                          : isLeftEdge ? ColorMarkerLeftEdge
                          : ColorMarkerColumn;
                float strokeW = isDragging ? 2f : 1f;

                bool skipLine = isLeftEdge && marker.RightEdge < _vm.MmToUnits(0.5);

                // Линия на всю высоту (визуально)
                if (!skipLine)
                {
                    using var linePaint = new SKPaint
                    {
                        Color = color,
                        StrokeWidth = strokeW,
                        IsStroke = true,
                        IsAntialias = false
                    };
                    canvas.DrawLine((float)xPx, 0, (float)xPx, h, linePaint);
                }

                // Треугольник-стрелочка вверху — только она имеет коллайдер
                float triSize = (float)(MarkerSizePx * 0.7);
                using var fillPaint = new SKPaint { Color = color, IsAntialias = true };
                using var strokePaint2 = new SKPaint
                { Color = SKColors.White, StrokeWidth = 1f, IsStroke = true, IsAntialias = true };
                DrawTriangleDown(canvas, (float)xPx, 0, triSize, fillPaint, strokePaint2);

                if (isDragging)
                {
                    using var guidePaint = new SKPaint
                    {
                        Color = ColorGuideLine,
                        StrokeWidth = 1f,
                        IsStroke = true,
                        PathEffect = SKPathEffect.CreateDash(new[] { 4f, 3f }, 0)
                    };
                    canvas.DrawLine((float)xPx, 0, (float)xPx, h, guidePaint);
                }
            }

            // Маркеры отступа абзаца внутри активной ячейки.
            DrawIndentMarkers(canvas, cellStartPx, cellEndPx, h, zoom);
        }

        // ── Pointer events ────────────────────────────────────────────────

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (_vm is null) return;

            // Начался жест — подсказку убираем: она стоит ровно там, куда человек целится.
            TooltipBehavior.HideSpot(this);

            // Правая кнопка, нажатая посреди жеста, отпускает привязку и ничего больше:
            // меню в этот момент открывать нельзя — оно перехватит указатель и бросит
            // маркер на полпути.
            if ((_isDragging || _isDraggingMargin)
                && e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            {
                _vm.IsSnapEnabled = false;
                e.Handled = true;
                return;
            }

            // Режим сравнения: линейка только отображает — никакие drag
            // (отступы, колонки, поля страницы) не начинаются.
            if (_vm.IsReadOnly) return;

            var pos = e.GetPosition(this);
            double zoom = _vm.Zoom;
            double unitSizePx = UnitSizePx(zoom);
            double textAreaStartPx = _vm.PageOffsetXPx + MmToPx(_vm.MarginLeftMm, zoom);
            double textAreaEndPx = _vm.PageOffsetXPx
                + MmToPx(_vm.PageWidthMm, zoom)
                - MmToPx(_vm.MarginRightMm, zoom);

            var (zoneStartPx, zoneEndPx) = ZoneBounds(textAreaStartPx, textAreaEndPx, zoom);

            // Правая кнопка собирает меню под то, на что нажали: у позиции табуляции свой
            // набор, у пустого места — общий. Само меню откроет Avalonia, поэтому здесь
            // только состав.
            var props = e.GetCurrentPoint(this).Properties;
            if (props.IsRightButtonPressed)
            {
                int hitTab = HitTestTabMarker(pos.X, pos.Y, zoneStartPx, unitSizePx);
                ContextMenu = BuildTabContextMenu(hitTab);
                return;
            }

            // Меню снимается на каждом нажатии левой кнопки: правая кнопка во время жеста
            // включает привязку и меню не собирает, а оставшееся от прошлого щелчка
            // всплыло бы само и бросило маркер.
            ContextMenu = null;

            if (_vm.Mode == RulerMode.Paragraph)
            {
                var hitMarker = HitTestIndentMarkerPriority(
                    pos.X, pos.Y, textAreaStartPx, textAreaEndPx, unitSizePx);

                if (hitMarker.HasValue)
                {
                    _isDragging = true;
                    _isDraggingIndentInTable = false;
                    _vm.BeginIndentDrag(hitMarker.Value);
                    e.Pointer.Capture(this);
                    Cursor = new Cursor(StandardCursorType.SizeWestEast);
                    e.Handled = true;
                    return;
                }

                int hitTab = HitTestTabMarker(pos.X, pos.Y, zoneStartPx, unitSizePx);
                if (hitTab >= 0)
                {
                    _isDragging = true;
                    _isDraggingTab = true;
                    _isDraggingIndentInTable = false;
                    _vm.BeginTabDrag(hitTab);
                    e.Pointer.Capture(this);
                    Cursor = new Cursor(StandardCursorType.SizeWestEast);
                    e.Handled = true;
                    return;
                }
            }
            else // Table mode
            {
                double cellStartPx = textAreaStartPx + _vm.ActiveCellLeftUnits * unitSizePx;
                double cellEndPx = textAreaStartPx + _vm.ActiveCellRightUnits * unitSizePx;

                // Сначала проверяем маркеры отступа параграфа ячейки.
                var hitMarker = HitTestIndentMarkerPriority(
                    pos.X, pos.Y, cellStartPx, cellEndPx, unitSizePx);

                if (hitMarker.HasValue)
                {
                    _isDragging = true;
                    _isDraggingIndentInTable = true;
                    _vm.BeginIndentDrag(hitMarker.Value);
                    e.Pointer.Capture(this);
                    Cursor = new Cursor(StandardCursorType.SizeWestEast);
                    e.Handled = true;
                    return;
                }

                // Позиция табуляции стоит в нижней полосе, маркер колонки занимает всю
                // высоту — без проверки в этом порядке колонка перехватывала бы щелчки
                // по значкам табуляции, оказавшимся под её линией.
                int hitTabInCell = HitTestTabMarker(pos.X, pos.Y, cellStartPx, unitSizePx);
                if (hitTabInCell >= 0)
                {
                    _isDragging = true;
                    _isDraggingTab = true;
                    _isDraggingIndentInTable = false;
                    _vm.BeginTabDrag(hitTabInCell);
                    e.Pointer.Capture(this);
                    Cursor = new Cursor(StandardCursorType.SizeWestEast);
                    e.Handled = true;
                    return;
                }

                // Маркеры колонок — коллайдер только на треугольнике (верхние MarkerSizePx px).
                int hitCol = HitTestColumnMarker(pos.X, pos.Y, textAreaStartPx, unitSizePx);
                if (hitCol >= 0)
                {
                    _isDragging = true;
                    _isDraggingIndentInTable = false;
                    _vm.BeginColumnDrag(hitCol);
                    e.Pointer.Capture(this);
                    Cursor = new Cursor(StandardCursorType.SizeWestEast);
                    e.Handled = true;
                    return;
                }
            }

            // Drag границы поля.
            const double MarginHitPx = 5.0;
            if (Math.Abs(pos.X - textAreaStartPx) <= MarginHitPx)
            {
                _isDraggingMargin = true; _draggingLeftMargin = true;
                _vm.BeginMarginDrag();
                e.Pointer.Capture(this);
                Cursor = new Cursor(StandardCursorType.SizeWestEast);
                e.Handled = true;
            }
            else if (Math.Abs(pos.X - textAreaEndPx) <= MarginHitPx)
            {
                _isDraggingMargin = true; _draggingLeftMargin = false;
                _vm.BeginMarginDrag();
                e.Pointer.Capture(this);
                Cursor = new Cursor(StandardCursorType.SizeWestEast);
                e.Handled = true;
            }
            else if (pos.Y >= RulerHeightPx - TabGlyphHeightPx - 2
                     && pos.X >= zoneStartPx && pos.X <= zoneEndPx)
            {
                // Пустое место нижней полосы — новая позиция табуляции. Проверка идёт
                // последней: всё, у чего на линейке уже есть смысл, свой щелчок забрало.
                _vm.AddTabStopAt((pos.X - zoneStartPx) / unitSizePx);
                InvalidateVisual();
                e.Handled = true;
            }
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (_vm is null) return;

            var pos = e.GetPosition(this);
            double zoom = _vm.Zoom;
            double unitSizePx = UnitSizePx(zoom);
            double textAreaStartPx = _vm.PageOffsetXPx + MmToPx(_vm.MarginLeftMm, zoom);
            double textAreaEndPx = _vm.PageOffsetXPx
                + MmToPx(_vm.PageWidthMm, zoom)
                - MmToPx(_vm.MarginRightMm, zoom);

            // Привязка отпущена ровно столько, сколько зажата правая кнопка. Читаем её
            // на каждом движении, а не только на нажатии: кнопку отпускают и посреди
            // жеста, и тогда маркер обязан снова пойти по делениям.
            if (_isDragging || _isDraggingMargin)
                _vm.IsSnapEnabled = !e.GetCurrentPoint(this).Properties.IsRightButtonPressed;

            if (_isDraggingMargin)
            {
                double pageOffsetXPx2 = _vm.PageOffsetXPx;
                double pageWidthPx2 = MmToPx(_vm.PageWidthMm, zoom);
                double clampedX = Math.Max(pageOffsetXPx2,
                    Math.Min(pos.X, pageOffsetXPx2 + pageWidthPx2));

                if (_draggingLeftMargin)
                {
                    double newMarginMm = PxToMm(clampedX - pageOffsetXPx2, zoom);
                    if (_vm.IsSnapEnabled)
                    { double s = _vm.UnitsToMm(_vm.SnapStep); newMarginMm = Math.Round(newMarginMm / s) * s; }
                    newMarginMm = Math.Max(0, Math.Min(newMarginMm, _vm.PageWidthMm - _vm.MarginRightMm - 5));
                    _vm.MarginLeftMm = newMarginMm;
                }
                else
                {
                    double newMarginMm = PxToMm(pageOffsetXPx2 + pageWidthPx2 - clampedX, zoom);
                    if (_vm.IsSnapEnabled)
                    { double s = _vm.UnitsToMm(_vm.SnapStep); newMarginMm = Math.Round(newMarginMm / s) * s; }
                    newMarginMm = Math.Max(0, Math.Min(newMarginMm, _vm.PageWidthMm - _vm.MarginLeftMm - 5));
                    _vm.MarginRightMm = newMarginMm;
                }

                _vm.NotifyMarginChanged();
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            if (_isDragging && _isDraggingTab)
            {
                var (zoneStartPx, zoneEndPx) = ZoneBounds(textAreaStartPx, textAreaEndPx, zoom);
                double clampedX = Math.Max(zoneStartPx, Math.Min(pos.X, zoneEndPx));

                // Указатель ушёл ниже линейки — жест снимает позицию. Порог небольшой:
                // линейка стоит вплотную к листу, и «увести вниз» здесь означает пару
                // пикселей, а не размашистое движение.
                bool discarding = pos.Y > RulerHeightPx + TabDiscardDistancePx;

                _vm.UpdateTabDrag((clampedX - zoneStartPx) / unitSizePx, discarding);
                Cursor = new Cursor(discarding
                    ? StandardCursorType.No
                    : StandardCursorType.SizeWestEast);

                InvalidateVisual();
                e.Handled = true;
                return;
            }

            if (_isDragging)
            {
                if (_isDraggingIndentInTable && _vm.DraggingIndentMarker.HasValue)
                {
                    // Drag отступа внутри ячейки — позиция относительно левого края ячейки.
                    double cellStartPx = textAreaStartPx + _vm.ActiveCellLeftUnits * unitSizePx;
                    double cellEndPx = textAreaStartPx + _vm.ActiveCellRightUnits * unitSizePx;
                    double clampedX = Math.Max(cellStartPx, Math.Min(pos.X, cellEndPx));

                    double posUnits;
                    if (_vm.DraggingIndentMarker == RulerIndentMarkerType.RightIndent)
                        posUnits = (cellEndPx - clampedX) / unitSizePx;
                    else
                        posUnits = (clampedX - cellStartPx) / unitSizePx;

                    _vm.UpdateTableIndentDragUnclamped(posUnits);
                }
                else if (!_isDraggingIndentInTable && _vm.Mode == RulerMode.Paragraph
                    && _vm.DraggingIndentMarker.HasValue)
                {
                    double pageStartPx = _vm.PageOffsetXPx;
                    double pageEndPx = _vm.PageOffsetXPx + MmToPx(_vm.PageWidthMm, zoom);

                    double posUnits;
                    if (_vm.DraggingIndentMarker == RulerIndentMarkerType.RightIndent)
                    {
                        double pageEndPx2 = _vm.PageOffsetXPx + MmToPx(_vm.PageWidthMm, zoom);
                        double clampedX = Math.Max(_vm.PageOffsetXPx, Math.Min(pos.X, pageEndPx2));
                        posUnits = (textAreaEndPx - clampedX) / unitSizePx;
                    }
                    else
                    {
                        double clampedX = Math.Max(pageStartPx, Math.Min(pos.X, pageEndPx));
                        posUnits = (clampedX - textAreaStartPx) / unitSizePx;
                        double minUnits = -(textAreaStartPx - pageStartPx) / unitSizePx;
                        posUnits = Math.Max(posUnits, minUnits);
                    }

                    _vm.UpdateIndentDragUnclamped(posUnits);
                }
                else if (!_isDraggingIndentInTable && _vm.Mode == RulerMode.Table
                    && _vm.DraggingColumnIndex >= 0)
                {
                    double posUnits = (pos.X - textAreaStartPx) / unitSizePx;
                    _vm.UpdateColumnDrag(posUnits);
                }

                InvalidateVisual();
                e.Handled = true;
                return;
            }

            // Курсор и подсказка при наведении.
            UpdateHoverCursor(pos.X, pos.Y, textAreaStartPx, textAreaEndPx, unitSizePx);

            var (hintStartPx, hintEndPx) = ZoneBounds(textAreaStartPx, textAreaEndPx, zoom);
            UpdateTabHint(pos.X, pos.Y, hintStartPx, hintEndPx, unitSizePx);
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            TooltipBehavior.HideSpot(this);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);

            // Отпустили правую кнопку — жест продолжается, привязка возвращается.
            // Без этой проверки маркер бросало бы на месте, где человек всего лишь
            // перестал держать точную подгонку.
            if (e.InitialPressMouseButton != MouseButton.Left)
            {
                if (_vm is not null && (_isDragging || _isDraggingMargin))
                {
                    _vm.IsSnapEnabled = true;
                    InvalidateVisual();
                    e.Handled = true;
                }
                return;
            }

            if (_isDraggingMargin)
            {
                _isDraggingMargin = false;
                e.Pointer.Capture(null);
                Cursor = new Cursor(StandardCursorType.Arrow);
                _vm?.CommitMarginChange();
                if (_vm is not null) _vm.IsSnapEnabled = true;
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            if (!_isDragging || _vm is null) return;

            if (_isDraggingTab)
            {
                _isDragging = false;
                _isDraggingTab = false;
                e.Pointer.Capture(null);
                Cursor = new Cursor(StandardCursorType.Arrow);
                _vm.EndTabDrag();
                _vm.IsSnapEnabled = true;
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            _isDragging = false;
            bool wasIndentInTable = _isDraggingIndentInTable;
            _isDraggingIndentInTable = false;
            e.Pointer.Capture(null);
            Cursor = new Cursor(StandardCursorType.Arrow);

            if (wasIndentInTable || (_vm.Mode == RulerMode.Paragraph && _vm.DraggingIndentMarker.HasValue))
                _vm.EndIndentDrag();
            else if (_vm.Mode == RulerMode.Table && !wasIndentInTable)
                _vm.EndColumnDrag();

            // Точная подгонка живёт ровно один жест: следующий снова идёт по делениям.
            _vm.IsSnapEnabled = true;

            InvalidateVisual();
            e.Handled = true;
        }

        // ── HitTest ───────────────────────────────────────────────────────

        private RulerIndentMarkerType? HitTestIndentMarkerPriority(
            double xPx, double yPx,
            double textAreaStartPx, double textAreaEndPx,
            double unitSizePx)
        {
            if (_vm is null) return null;

            double r = MarkerHitRadiusPx;
            double h = RulerHeightPx;

            double xLeft = textAreaStartPx + GetMarkerPosition(RulerIndentMarkerType.LeftIndent) * unitSizePx;
            double xFirst = textAreaStartPx + GetMarkerPosition(RulerIndentMarkerType.FirstLineIndent) * unitSizePx;
            double xRight = textAreaEndPx - GetMarkerPosition(RulerIndentMarkerType.RightIndent) * unitSizePx;
            double xList = textAreaStartPx + GetMarkerPosition(RulerIndentMarkerType.ListMarker) * unitSizePx;

            bool hitLeft = Math.Abs(xPx - xLeft) <= r;
            bool hitFirst = Math.Abs(xPx - xFirst) <= r;
            bool hitRight = Math.Abs(xPx - xRight) <= r;
            bool hitList = _vm.ShowListMarker && Math.Abs(xPx - xList) <= r;

            // Каждый треугольник кликабелен ТОЛЬКО в своей Y-зоне:
            //   FirstLineIndent / ListMarker → верхние MarkerSizePx пикселей (DrawTriangleUp вверху)
            //   LeftIndent      → нижние MarkerSizePx пикселей  (DrawTriangleDown внизу)
            //   RightIndent     → нижние MarkerSizePx пикселей
            // Между треугольниками (средняя зона) — ни один из них не перехватывает клик.
            bool inTopZone = yPx <= MarkerSizePx;
            bool inBottomZone = yPx >= h - MarkerSizePx;

            // Маркер списка имеет приоритет над отступом первой строки в верхней зоне.
            if (inTopZone && hitList) return RulerIndentMarkerType.ListMarker;
            if (inTopZone && hitFirst) return RulerIndentMarkerType.FirstLineIndent;
            if (inBottomZone)
            {
                if (hitLeft) return RulerIndentMarkerType.LeftIndent;
                if (hitRight) return RulerIndentMarkerType.RightIndent;
            }

            return null;
        }

        private double GetMarkerPosition(RulerIndentMarkerType type)
        {
            if (_vm is null) return 0;
            foreach (var m in _vm.IndentMarkers)
                if (m.Type == type) return m.Position;
            return 0;
        }

        private int HitTestColumnMarker(double xPx, double yPx, double textAreaStartPx, double unitSizePx)
        {
            if (_vm is null) return -1;
            double r = MarkerHitRadiusPx;
            int bestIdx = -1;
            double bestD = double.MaxValue;
            for (int i = 0; i < _vm.ColumnMarkers.Count; i++)
            {
                double markerX = textAreaStartPx + _vm.ColumnMarkers[i].RightEdge * unitSizePx;
                double d = Math.Abs(xPx - markerX);
                if (d <= r && d < bestD) { bestD = d; bestIdx = i; }
            }
            return bestIdx;
        }

        private void UpdateHoverCursor(
            double xPx, double yPx,
            double textAreaStartPx, double textAreaEndPx,
            double unitSizePx)
        {
            if (_vm is null) return;

            // Режим сравнения: перетаскивание запрещено — курсор ресайза не показываем.
            if (_vm.IsReadOnly)
            {
                Cursor = new Cursor(StandardCursorType.Arrow);
                return;
            }


            if (_vm.Mode == RulerMode.Paragraph)
            {
                var hit = HitTestIndentMarkerPriority(
                    xPx, yPx, textAreaStartPx, textAreaEndPx, unitSizePx);
                if (hit.HasValue) { Cursor = new Cursor(StandardCursorType.SizeWestEast); return; }

                int hitTab = HitTestTabMarker(xPx, yPx, textAreaStartPx, unitSizePx);
                if (hitTab >= 0) { Cursor = new Cursor(StandardCursorType.SizeWestEast); return; }
            }
            else
            {
                double cellStartPx = textAreaStartPx + _vm.ActiveCellLeftUnits * unitSizePx;
                double cellEndPx = textAreaStartPx + _vm.ActiveCellRightUnits * unitSizePx;

                // Треугольники отступа имеют приоритет в своих зонах
                var hit = HitTestIndentMarkerPriority(xPx, yPx, cellStartPx, cellEndPx, unitSizePx);
                if (hit.HasValue) { Cursor = new Cursor(StandardCursorType.SizeWestEast); return; }

                int hitTab = HitTestTabMarker(xPx, yPx, cellStartPx, unitSizePx);
                if (hitTab >= 0) { Cursor = new Cursor(StandardCursorType.SizeWestEast); return; }

                // Маркеры колонок — HitTestColumnMarker сам ограничивает по Y
                int hitCol = HitTestColumnMarker(xPx, yPx, textAreaStartPx, unitSizePx);
                if (hitCol >= 0) { Cursor = new Cursor(StandardCursorType.SizeWestEast); return; }
            }

            const double MarginHoverPx = 5.0;
            if (Math.Abs(xPx - textAreaStartPx) <= MarginHoverPx
                || Math.Abs(xPx - textAreaEndPx) <= MarginHoverPx)
            { Cursor = new Cursor(StandardCursorType.SizeWestEast); return; }

            Cursor = new Cursor(StandardCursorType.Arrow);
        }

        // ── Меню линейки ──────────────────────────────────────────────────

        /// <summary>
        /// Собирает меню под правую кнопку. Когда нажали на позицию табуляции, сверху идут
        /// действия над ней самой; ниже — то, что относится к абзацу целиком.
        ///
        /// Меню собирается заново на каждый вызов, а не хранится: отметки в нём показывают
        /// состояние конкретной позиции, и переиспользовать один набор пунктов для разных
        /// маркеров нельзя.
        /// </summary>
        private ContextMenu BuildTabContextMenu(int tabIndex)
        {
            var vm = _vm!;
            var items = new List<Control>();

            bool onMarker = tabIndex >= 0 && tabIndex < vm.TabMarkers.Count;

            if (onMarker)
            {
                var marker = vm.TabMarkers[tabIndex];

                var align = new MenuItem { Header = Strings.Tab_MenuAlignment };
                align.ItemsSource = new[]
                {
                    RadioItem(Strings.Tab_AlignLeft, marker.Alignment == Models.Styles.TabAlignment.Left,
                        () => vm.SetTabAlignmentAt(tabIndex, Models.Styles.TabAlignment.Left)),
                    RadioItem(Strings.Tab_AlignCenter, marker.Alignment == Models.Styles.TabAlignment.Center,
                        () => vm.SetTabAlignmentAt(tabIndex, Models.Styles.TabAlignment.Center)),
                    RadioItem(Strings.Tab_AlignRight, marker.Alignment == Models.Styles.TabAlignment.Right,
                        () => vm.SetTabAlignmentAt(tabIndex, Models.Styles.TabAlignment.Right)),
                    RadioItem(Strings.Tab_AlignDecimal, marker.Alignment == Models.Styles.TabAlignment.Decimal,
                        () => vm.SetTabAlignmentAt(tabIndex, Models.Styles.TabAlignment.Decimal))
                };
                items.Add(align);

                var leader = new MenuItem { Header = Strings.Tab_MenuLeader };
                leader.ItemsSource = new[]
                {
                    RadioItem(Strings.Tab_LeaderNone, marker.Leader == Models.Styles.TabLeaderStyle.None,
                        () => vm.SetTabLeaderAt(tabIndex, Models.Styles.TabLeaderStyle.None)),
                    RadioItem(Strings.Tab_LeaderDots, marker.Leader == Models.Styles.TabLeaderStyle.Dots,
                        () => vm.SetTabLeaderAt(tabIndex, Models.Styles.TabLeaderStyle.Dots)),
                    RadioItem(Strings.Tab_LeaderDashes, marker.Leader == Models.Styles.TabLeaderStyle.Dashes,
                        () => vm.SetTabLeaderAt(tabIndex, Models.Styles.TabLeaderStyle.Dashes)),
                    RadioItem(Strings.Tab_LeaderLine, marker.Leader == Models.Styles.TabLeaderStyle.Line,
                        () => vm.SetTabLeaderAt(tabIndex, Models.Styles.TabLeaderStyle.Line))
                };
                items.Add(leader);

                var remove = new MenuItem { Header = Strings.Tab_MenuRemove };
                remove.Click += (_, _) => { vm.RemoveTabStopAt(tabIndex); InvalidateVisual(); };
                items.Add(remove);

                items.Add(new Separator());
            }

            var newAlign = new MenuItem { Header = Strings.Tab_MenuNewAlignment };
            newAlign.ItemsSource = new[]
            {
                RadioItem(Strings.Tab_AlignLeft, vm.NextTabAlignment == Models.Styles.TabAlignment.Left,
                    () => vm.NextTabAlignment = Models.Styles.TabAlignment.Left),
                RadioItem(Strings.Tab_AlignCenter, vm.NextTabAlignment == Models.Styles.TabAlignment.Center,
                    () => vm.NextTabAlignment = Models.Styles.TabAlignment.Center),
                RadioItem(Strings.Tab_AlignRight, vm.NextTabAlignment == Models.Styles.TabAlignment.Right,
                    () => vm.NextTabAlignment = Models.Styles.TabAlignment.Right),
                RadioItem(Strings.Tab_AlignDecimal, vm.NextTabAlignment == Models.Styles.TabAlignment.Decimal,
                    () => vm.NextTabAlignment = Models.Styles.TabAlignment.Decimal)
            };
            items.Add(newAlign);

            var newLeader = new MenuItem { Header = Strings.Tab_MenuNewLeader };
            newLeader.ItemsSource = new[]
            {
                RadioItem(Strings.Tab_LeaderNone, vm.NextTabLeader == Models.Styles.TabLeaderStyle.None,
                    () => vm.NextTabLeader = Models.Styles.TabLeaderStyle.None),
                RadioItem(Strings.Tab_LeaderDots, vm.NextTabLeader == Models.Styles.TabLeaderStyle.Dots,
                    () => vm.NextTabLeader = Models.Styles.TabLeaderStyle.Dots),
                RadioItem(Strings.Tab_LeaderDashes, vm.NextTabLeader == Models.Styles.TabLeaderStyle.Dashes,
                    () => vm.NextTabLeader = Models.Styles.TabLeaderStyle.Dashes),
                RadioItem(Strings.Tab_LeaderLine, vm.NextTabLeader == Models.Styles.TabLeaderStyle.Line,
                    () => vm.NextTabLeader = Models.Styles.TabLeaderStyle.Line)
            };
            items.Add(newLeader);

            var clear = new MenuItem
            {
                Header = Strings.Tab_MenuClear,
                IsEnabled = vm.TabMarkers.Count > 0
            };
            clear.Click += (_, _) => { vm.ClearTabStops(); InvalidateVisual(); };
            items.Add(clear);

            items.Add(new Separator());

            var settings = new MenuItem { Header = Strings.Tab_MenuSettings };
            settings.Click += (_, _) => vm.RequestTabSettings();
            items.Add(settings);

            return new ContextMenu { ItemsSource = items };
        }

        private static MenuItem RadioItem(string header, bool isChecked, Action apply)
        {
            var item = new MenuItem
            {
                Header = header,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = isChecked
            };
            item.Click += (_, _) => apply();
            return item;
        }

        // ── Геометрические примитивы ──────────────────────────────────────

        private static void DrawTriangleDown(SKCanvas canvas,
            float cx, float y, float size,
            SKPaint fillPaint, SKPaint strokePaint)
        {
            using var path = new SKPath();
            path.MoveTo(cx - size / 2f, y);
            path.LineTo(cx + size / 2f, y);
            path.LineTo(cx, y + size);
            path.Close();
            canvas.DrawPath(path, fillPaint);
            canvas.DrawPath(path, strokePaint);
        }

        private static void DrawTriangleUp(SKCanvas canvas,
            float cx, float y, float size,
            SKPaint fillPaint, SKPaint strokePaint)
        {
            using var path = new SKPath();
            path.MoveTo(cx - size / 2f, y + size);
            path.LineTo(cx + size / 2f, y + size);
            path.LineTo(cx, y);
            path.Close();
            canvas.DrawPath(path, fillPaint);
            canvas.DrawPath(path, strokePaint);
        }

        // ── Вспомогательные ───────────────────────────────────────────────

        private double UnitSizePx(double zoom)
        {
            if (_vm is null) return 96.0 * zoom;
            double unitMm = _vm.Units == Models.Settings.RulerUnits.Inches ? 25.4 : 10.0;
            return unitMm * (96.0 / 25.4) * zoom;
        }

        private static double MmToPx(double mm, double zoom)
            => mm * (96.0 / 25.4) * zoom;

        private static double PxToMm(double px, double zoom)
            => px / (96.0 / 25.4) / zoom;

        // ── ICustomDrawOperation ──────────────────────────────────────────

        private sealed class RulerSKDrawOperation : ICustomDrawOperation
        {
            private readonly HorizontalRulerControl _ruler;
            public Rect Bounds { get; }

            public RulerSKDrawOperation(HorizontalRulerControl ruler, Rect bounds)
            {
                _ruler = ruler;
                Bounds = bounds;
            }

            public void Dispose() { }
            public bool Equals(ICustomDrawOperation? other) => false;
            public bool HitTest(Point p) => true;

            public void Render(ImmediateDrawingContext context)
            {
                var feature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature))
                    as ISkiaSharpApiLeaseFeature;
                if (feature is null) return;
                using var lease = feature.Lease();
                _ruler.RenderWithSKCanvas(lease.SkCanvas);
            }
        }
    }
}