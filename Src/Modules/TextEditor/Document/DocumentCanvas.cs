using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using Serilog;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Writersword.Core.Interfaces.Services.Input;
using Writersword.Core.Models.Print;
using Writersword.Core.Models.Rendering;
using System.Text.Json;
using Writersword.Modules.TextEditor.Rendering;
using Writersword.Modules.Common;
using Writersword.Modules.TextEditor.Commands;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Models.Page;
using Writersword.Modules.TextEditor.ViewModels;
using Writersword.Modules.TextEditor.ViewModels.Blocks;

namespace Writersword.Modules.TextEditor.Document
{
    public sealed partial class DocumentCanvas : Control
    {
        // ── Конвертация единиц ────────────────────────────────────────────
        private const float PtToPx = 96f / 72f;
        private const float PxToPt = 72f / 96f;

        // ── Константы геометрии ───────────────────────────────────────────
        private const float PageGapPt = 15f;
        private const float DraftPadHPt = 9f;
        private const float DraftPadWPt = 0f;
        private const float ReadingMaxPt = 510f;
        private const float FallbackLinePt = 16.5f;

        // Отступ каретки якоря от границы таблицы — чтобы не перекрывалась рамкой.
        private const float AnchorMarginPt = 4f;

        // Дополнительный отступ сверху для строк параграфа, продолжающегося на новой странице.
        // Добавляется к lineGroupYPt при переносе — чтобы первая строка не прилипала к полю.
        private const float PageContinuationTopPadPt = 4f;

        // ── CellInfo: metadata для параграфа ячейки таблицы ──────────────
        // Таблица — это просто "параграфы в тюрьме": параграфы ячеек
        // добавляются в _layouts рядом с обычными параграфами. Каретка,
        // выделение и навигация работают через единый _layouts без
        // отдельного "режима таблицы".
        private sealed class CellInfo
        {
            public TableBlock Table { get; }
            public TableCell Cell { get; }
            public ParagraphBlock ParaBlock { get; }
            public int CellParaIndex { get; }  // индекс внутри cell.Paragraphs
            public int TableEntryIdx { get; }  // индекс в _tables
            public float ContentXPt { get; }  // абсолютный X начала содержимого
            public float ContentYPt { get; }  // абсолютный Y начала содержимого
            public float ClipX { get; }  // clip rect для рендера
            public float ClipY { get; }
            public float ClipW { get; }
            public float ClipH { get; }

            public CellInfo(TableBlock table, TableCell cell, ParagraphBlock paraBlock,
                int cellParaIndex, int tableEntryIdx,
                float contentXPt, float contentYPt,
                float clipX, float clipY, float clipW, float clipH)
            {
                Table = table; Cell = cell; ParaBlock = paraBlock;
                CellParaIndex = cellParaIndex; TableEntryIdx = tableEntryIdx;
                ContentXPt = contentXPt; ContentYPt = contentYPt;
                ClipX = clipX; ClipY = clipY; ClipW = clipW; ClipH = clipH;
            }
        }

        // ── Layout параграфов ─────────────────────────────────────────────
        private record ParaLayout(
            ParagraphViewModel Vm,
            SKTextLayout? Layout,      // null для параграфов за пределами viewport-буфера
            float Ypt,
            float HeightPt,
            int PageIndex,
            int LineFrom,
            int LineTo,
            float AbsXPt = 0,          // абсолютный X левого края текстовой зоны
            CellInfo? Cell = null,     // null = обычный параграф
            Rendering.ListMarkerInfo? Marker = null);  // маркер списка (null = не элемент списка)

        private record PageRect(
            float Ypt,
            float WidthPt,
            float HeightPt,
            float PadLeftPt,
            float PadTopPt,
            float MarginLeftPt,
            float PadBottomPt = 0f);

        // ── Layout таблиц (только для рендера рамок/фона) ─────────────────
        // Одна запись = один слайс таблицы на одной странице.
        // При разбивке таблицы по строкам создаётся несколько записей с одним Layout.
        private record TableEntry(
            TableBlock Table,
            SKTableLayout Layout,
            float Ypt,
            float XPt,
            int PageIndex,
            int RowFrom = 0,
            int RowTo = -1,
            float LastRowVisibleHeightPt = -1f,
            float FirstRowContentOffsetPt = 0f,
            bool IsContinuation = false);

        // ── Layout изображений ────────────────────────────────────────────
        // Одна запись = одно изображение-блок на своей странице.
        private record ImageEntry(
            ImageBlock Block,
            float Ypt,
            float XPt,
            float WidthPt,
            float HeightPt,
            int PageIndex,
            // Картинка встроена в строку текста: рисует её рендер текста, а запись нужна
            // для выделения, маркеров размера, поворота и обрезки — они работают по
            // единому списку картинок.
            bool InLine = false);

        // ── Атомарный снимок для render-потока ────────────────────────────
        private readonly object _renderLock = new();
        private List<ParaLayout> _layouts = new();
        private List<PageRect> _pages = new();
        private List<TableEntry> _tables = new();
        private List<ImageEntry> _images = new();

        // Выделенная картинка (для рамки и удаления). null — ничего не выделено.
        private ImageBlock? _selectedImage;

        // Картинки, попавшие в ТЕКСТОВОЕ выделение (оно прошло через них по потоку).
        // Пересчитывается на UI-потоке в RefreshImagesInTextSelection, рендер только читает —
        // поэтому набор всегда подменяется целиком, а не правится на месте.
        private HashSet<ImageBlock> _imagesInTextSelection = new();
        private readonly SKPaint _paintImageSelection = new()
        {
            Style = SKPaintStyle.Stroke,
            Color = new SKColor(0xE0, 0x7B, 0x39),
            StrokeWidth = 1.5f,
            IsAntialias = true
        };

        // Перетаскивание картинки «в тексте». У неё нет свободных координат — она символ
        // абзаца, поэтому drag переносит её в точку текста под курсором, а не в точку листа.
        private bool _inlineImageDragging;
        private bool _inlineImageDragMoved;
        private ImageBlock? _inlineDragImage;

        // Перетаскивание плавающей картинки.
        private bool _imageDragging;
        private bool _imageDragMoved;
        private float _imgDragStartXPt;
        private float _imgDragStartYPt;
        private double _imgDragStartOffX;
        private double _imgDragStartOffY;

        // Изменение размера выделенной картинки за маркер.
        // Индексы: 0 — верх-лево, 1 — верх-право, 2 — низ-право, 3 — низ-лево,
        // 4 — верх-центр, 5 — право-центр, 6 — низ-центр, 7 — лево-центр, 8 — поворот.
        private bool _imageResizing;
        private bool _imageResizeMoved;
        private int _imageResizeCorner = -1;
        private float _imgResizeStartXPt;
        private float _imgResizeStartYPt;
        private double _imgResizeStartW;
        private double _imgResizeStartH;
        private double _imgResizeStartOffX;
        private double _imgResizeStartOffY;
        private double _imgResizeStartRotDeg;

        // Поворот выделенной картинки за круглый маркер над верхней гранью.
        private bool _imageRotating;
        private bool _imageRotateMoved;
        private double _imgRotStartDeg;
        private float _imgRotPointerStartDeg;
        private float _imgRotCenterXPt;
        private float _imgRotCenterYPt;

        // Полупрозрачная заливка угловых маркеров размера.
        private readonly SKPaint _paintImageHandleFill = new()
        {
            Style = SKPaintStyle.Fill,
            Color = new SKColor(0xFF, 0xFF, 0xFF),
            IsAntialias = true
        };

        // Сглаживание края и билинейная фильтрация при отрисовке картинок:
        // без него повёрнутая картинка рисуется с рваным ступенчатым краем.
        private readonly SKPaint _paintImageDraw = new()
        {
            IsAntialias = true
        };

        // Качество ресемплинга картинок: заменяет устаревший SKPaint.FilterQuality.High.
        // Кубический фильтр Митчелла даёт мягкий край на повёрнутых/масштабированных картинках.
        // Передаётся в canvas.DrawImage вместо свойства paint-а.
        private static readonly SKSamplingOptions _imageSampling = new(SKCubicResampler.Mitchell);

        // Режим предпросмотра переполнения страницы: во время драга (поворот/ресайз)
        // страница инлайн-картинки заморожена как на момент нажатия. Если картинка
        // перестаёт влезать — остаётся на месте, выходит за нижний край листа и
        // рисуется серой полупрозрачной. Если была на следующей странице и снова
        // влезает — остаётся на следующей. Реальный перенос в обе стороны
        // выполняется финальным пересбором после отпускания кнопки мыши.
        private bool _imageOverflowPreviewMode;
        private ImageBlock? _imageOverflowPreviewBlock;

        // Была ли выделенная картинка на момент старта драга перенесена
        // на следующую страницу из-за нехватки места.
        private bool _imagePreviewStartTransferred;

        // Инлайн-картинки, перенесённые последним пересбором на следующую страницу
        // из-за нехватки места. Читается при старте драга для заморозки страницы.
        private HashSet<ImageBlock> _inlineTransferredImages = new();

        // Рамка картинки: цвет и толщина выставляются перед отрисовкой каждой картинки.
        private readonly SKPaint _paintImageBorderDraw = new()
        {
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };

        // Обесцвеченная полупрозрачная отрисовка картинки в предпросмотре переполнения.
        private readonly SKPaint _paintImageDrawOverflow = new()
        {
            IsAntialias = true,
            ColorFilter = SKColorFilter.CreateColorMatrix(new float[]
            {
                0.21f, 0.72f, 0.07f, 0f,    0f,
                0.21f, 0.72f, 0.07f, 0f,    0f,
                0.21f, 0.72f, 0.07f, 0f,    0f,
                0f,    0f,    0f,    0.55f, 0f
            })
        };

        // Половина стороны квадратного маркера и радиус попадания по нему, в пунктах.
        private const float ImageHandleHalfPt = 3.5f;
        private const float ImageHandleHitPt = 6f;

        // ── Страницы рядом (view-трансформ) ──────────────────────────────
        // Число страниц в ряду. Раскладка остаётся «логической» (страницы столбиком),
        // рядом-стоящесть — только отображение: рендер переносит контент каждой
        // страницы на её визуальную позицию, ввод выполняет обратное преобразование.
        // При 1 все дельты нулевые — поведение идентично прежнему.
        private int _pagesPerRow = 1;

        // Настройка числа страниц в ряду: 0 — авто, иначе фиксированное число.
        // _pagesPerRow — уже вычисленное действующее значение.
        private int _pagesPerRowSetting = 1;

        /// <summary>
        /// Пересчитывает действующее число страниц в ряду. В авто-режиме берётся столько,
        /// сколько влезает в ширину канваса при текущем масштабе: отдалили — страниц в
        /// ряду больше, приблизили — меньше. Ширина канваса уже поделена на зум, поэтому
        /// отдельно следить за масштабом не нужно.
        /// </summary>
        private void UpdateEffectivePagesPerRow()
        {
            if (_pagesPerRowSetting > 0)
            {
                _pagesPerRow = _pagesPerRowSetting;
                return;
            }

            float pageWidthPt = GetPageWidthPt();
            if (pageWidthPt <= 1f) { _pagesPerRow = 1; return; }

            float availablePt = (float)(_canvasWidth * PxToPt);
            float stepPt = pageWidthPt + PageGapPt * 2f;

            // Последней странице ряда зазор справа не нужен — добавляем его к доступной
            // ширине, иначе ряд из ровно N страниц не считался бы влезающим.
            int fit = (int)Math.Floor((availablePt + PageGapPt * 2f) / stepPt);
            _pagesPerRow = Math.Clamp(fit, 1, 12);
        }

        /// <summary>
        /// Дельта визуальной позиции страницы относительно логической (в пунктах).
        /// Колонки раскладываются слева направо, ряды сверху вниз, блок рядов
        /// центрируется по живой ширине канваса.
        /// </summary>
        private (float DxPt, float DyPt) PageVisualDelta(int pageIdx, List<PageRect> pages)
        {
            if (SpreadMode && pages.Count > 0 && pageIdx >= 0 && pageIdx < pages.Count)
                return SpreadVisualDelta(pageIdx, pages);

            if (_pagesPerRow <= 1 || pages.Count == 0) return (0f, 0f);
            if (pageIdx < 0 || pageIdx >= pages.Count) return (0f, 0f);

            int cols = _pagesPerRow;
            int row = pageIdx / cols;
            int col = pageIdx % cols;
            var pg = pages[pageIdx];

            float canvasWPt = (float)(_canvasWidth * PxToPt);
            float gapX = PageGapPt * 2f;

            // Центрируем КАЖДЫЙ ряд по числу страниц именно в нём. По ширине полного ряда
            // неполный последний прижимался бы влево: две страницы из трёх — и они уже
            // не по центру, а одна-единственная висит слева.
            int inThisRow = Math.Max(1, Math.Min(cols, pages.Count - row * cols));
            float totalW = inThisRow * pg.WidthPt + (inThisRow - 1) * gapX;
            float marginX = Math.Max((canvasWPt - totalW) / 2f, 0f);

            float visX = marginX + col * (pg.WidthPt + gapX);
            float visY = PageGapPt + row * (pg.HeightPt + PageGapPt);

            return (visX - pg.PadLeftPt, visY - pg.Ypt);
        }

        /// <summary>
        /// Визуальная позиция страницы в книжном развороте. Две страницы разворота
        /// встают вплотную по центру вьюпорта, остальные уводятся далеко вниз — их
        /// отбрасывают те же проверки видимости, что работают в остальных режимах.
        /// </summary>
        private (float DxPt, float DyPt) SpreadVisualDelta(int pageIdx, List<PageRect> pages)
        {
            var pg = pages[pageIdx];

            // Отрисовка страницы в отдельный битмап идёт в логических координатах.
            if (pageIdx == SpreadOffscreenPage) return (0f, 0f);

            // Летящий лист рисуется отдельным проходом с перспективой — обычному
            // проходу эти страницы отдавать нельзя, иначе они будут видны дважды.
            // Половины под листом остаются за обычным проходом: они неподвижны, и
            // рисовать их снимком означало бы подменить векторный текст растровым —
            // на глаз это читается как рывок шрифта и утолщение линий в таблицах.
            if (_spreadFlipDir != 0 && (pageIdx == _spreadFlyFront || pageIdx == _spreadFlyBack))
                return (0f, SpreadHiddenOffsetPt);

            // Какая страница в какой половине. В покое это пара разворота; во время
            // переворота под листом уже открывается то, куда он ложится. Считает это
            // SpreadUnderPages — там же, где им пользуется сам переворот: два места со
            // своей копией правила однажды разошлись бы, и переход по номеру страницы
            // показал бы под листом не тот разворот.
            var (leftSlotPage, rightSlotPage) = SpreadUnderPages();

            bool isLeft = pageIdx == leftSlotPage;
            if (!isLeft && pageIdx != rightSlotPage)
                return (0f, SpreadHiddenOffsetPt);

            var (visX, visY) = SpreadPlacement(pageIdx, isLeft);
            return (visX - pg.PadLeftPt, visY - pg.Ypt);
        }

        // Страница, на которой начался текущий жест указателя. Во время жеста
        // (драг/ресайз/поворот картинки, драг таблицы) маппинг визуальных координат
        // в логические выполняется через ЭТУ страницу: иначе заход указателя на
        // соседнюю страницу перескакивал бы маппинг, логическая точка прыгала бы
        // на высоту страницы и смещения объектов получали мусорные значения.
        private int _gesturePage = -1;

        /// <summary>Индекс страницы, чей визуальный прямоугольник ближе всего к точке.</summary>
        private int NearestVisualPage(float xPt, float yPt, List<PageRect> pages)
        {
            int best = 0;
            float bestDist = float.MaxValue;
            for (int i = 0; i < pages.Count; i++)
            {
                var (dx, dy) = PageVisualDelta(i, pages);
                float l = pages[i].PadLeftPt + dx;
                float t = pages[i].Ypt + dy;
                float r = l + pages[i].WidthPt;
                float b = t + pages[i].HeightPt;
                float ddx = xPt < l ? l - xPt : xPt > r ? xPt - r : 0f;
                float ddy = yPt < t ? t - yPt : yPt > b ? yPt - b : 0f;
                float d = ddx * ddx + ddy * ddy;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = i;
                    if (d <= 0f) break;
                }
            }
            return best;
        }

        /// <summary>
        /// Переводит точку указателя (в пунктах, визуальные координаты канваса)
        /// в логические координаты раскладки: находит страницу, чей визуальный
        /// прямоугольник ближе всего к точке, и снимает её дельту.
        /// </summary>
        private (float XPt, float YPt) VisualToLogicalPt(float xPt, float yPt)
        {
            if (_pagesPerRow <= 1) return (xPt, yPt);

            List<PageRect> pages;
            lock (_renderLock) { pages = _pages; }
            if (pages.Count == 0) return (xPt, yPt);

            int best = NearestVisualPage(xPt, yPt, pages);
            var (bdx, bdy) = PageVisualDelta(best, pages);
            return (xPt - bdx, yPt - bdy);
        }

        /// <summary>
        /// Тот же перевод, но через фиксированную страницу жеста — маппинг не
        /// перескакивает на соседнюю страницу, пока кнопка мыши не отпущена.
        /// </summary>
        private (float XPt, float YPt) VisualToLogicalPt(float xPt, float yPt, int fixedPageIdx)
        {
            if (_pagesPerRow <= 1) return (xPt, yPt);
            if (fixedPageIdx < 0) return VisualToLogicalPt(xPt, yPt);

            List<PageRect> pages;
            lock (_renderLock) { pages = _pages; }
            if (pages.Count == 0 || fixedPageIdx >= pages.Count)
                return VisualToLogicalPt(xPt, yPt);

            var (dx, dy) = PageVisualDelta(fixedPageIdx, pages);
            return (xPt - dx, yPt - dy);
        }

        /// <summary>
        /// Точка указателя в системе координат страницы ВЫДЕЛЕННОЙ картинки. Нужна там,
        /// где проверяется попадание по её маркерам: картинка может лежать мимо своего
        /// листа, на территории соседнего, и перевод через ближайшую страницу дал бы
        /// координаты чужой системы — маркеры не ловились бы. Если картинки нет или
        /// режим одностраничный, возвращается точка как есть.
        /// </summary>
        private (float XPt, float YPt) LogicalPointForSelectedImage(
            float rawXPt, float rawYPt, float fallbackXPt, float fallbackYPt)
        {
            if (_pagesPerRow <= 1 || _selectedImage is null)
                return (fallbackXPt, fallbackYPt);

            foreach (var ie in _images)
            {
                if (!ReferenceEquals(ie.Block, _selectedImage)) continue;
                if (ie.PageIndex < 0) break;
                return VisualToLogicalPt(rawXPt, rawYPt, ie.PageIndex);
            }

            return (fallbackXPt, fallbackYPt);
        }

        /// <summary>Индекс страницы выделенной картинки, либо -1.</summary>
        private int SelectedImagePageIndex()
        {
            if (_selectedImage is null) return -1;
            foreach (var ie in _images)
                if (ReferenceEquals(ie.Block, _selectedImage))
                    return ie.PageIndex;
            return -1;
        }

        /// <summary>
        /// Живой сдвиг центрирования листа для одностраничного режима (компенсация
        /// между запечённым _layoutPageXPt и текущей шириной канваса). В режиме
        /// страниц рядом центрирование выполняет PageVisualDelta — сдвиг нулевой.
        /// </summary>
        private float GetPageShiftXPt()
        {
            if (_pagesPerRow > 1) return 0f;
            float canvasWPt = (float)(_canvasWidth * PxToPt);
            return Math.Max((canvasWPt - GetPageWidthPt()) / 2f, 0f) - _layoutPageXPt;
        }

        // Режим обрезки выделенной картинки: маркеры двигают границы кадрирования,
        // а не размер. Сбрасывается при снятии выделения.
        private bool _imageCropMode;

        // Картинка, для которой включён режим обрезки, и отложенные доли среза.
        // Пока режим активен, сама картинка не меняется: на канвасе показывается
        // исходное изображение целиком с затемнением срезаемых краёв. Границы
        // применяются одним действием при выходе из режима (кнопка «Обрезка» или
        // Enter — применить, Esc — отменить). Изменение размера в этом режиме
        // заблокировано: маркеры двигают только рамку кадрирования.
        private ImageBlock? _cropImage;
        private double _cropPendLeft;
        private double _cropPendTop;
        private double _cropPendRight;
        private double _cropPendBottom;

        // Полный (несрезанный) размер картинки в пунктах на момент входа в режим.
        private double _cropFullWPt;
        private double _cropFullHPt;

        // Минимальная сторона рамки кадрирования, пункты.
        private const double CropMinSidePt = 8.0;

        // Акцентная заливка маркеров в режиме обрезки.
        private readonly SKPaint _paintImageHandleCropFill = new()
        {
            Style = SKPaintStyle.Fill,
            Color = new SKColor(0xE0, 0x7B, 0x39),
            IsAntialias = true
        };

        // Затемнение срезаемой части картинки в режиме обрезки.
        private readonly SKPaint _paintCropDim = new()
        {
            Style = SKPaintStyle.Fill,
            Color = new SKColor(0x1A, 0x1A, 0x1A, 0x99),
            IsAntialias = true
        };

        // Пунктирный контур исходных границ картинки в режиме обрезки.
        private readonly SKPaint _paintCropOutline = new()
        {
            Style = SKPaintStyle.Stroke,
            Color = new SKColor(0xE0, 0x7B, 0x39, 0xB0),
            StrokeWidth = 1f,
            PathEffect = SKPathEffect.CreateDash(new[] { 4f, 3f }, 0f),
            IsAntialias = true
        };

        // Обрезка драгом маркера: стартовые доли кадрирования.
        private bool _imageCropDragging;
        private double _imgCropStartL;
        private double _imgCropStartT;
        private double _imgCropStartR;
        private double _imgCropStartB;

        // Расстояние маркера поворота от верхней грани и его радиус, в пунктах.
        private const float ImageRotateHandleOffsetPt = 20f;
        private const float ImageRotateHandleRadiusPt = 6f;

        // Круговая стрелка внутри маркера поворота.
        private readonly SKPaint _paintRotateArrowStroke = new()
        {
            Style = SKPaintStyle.Stroke,
            Color = new SKColor(0xE0, 0x7B, 0x39),
            StrokeWidth = 1.1f,
            IsAntialias = true
        };
        private readonly SKPaint _paintRotateArrowFill = new()
        {
            Style = SKPaintStyle.Fill,
            Color = new SKColor(0xE0, 0x7B, 0x39),
            IsAntialias = true
        };
        private double _canvasWidth;
        private double _canvasHeight;
        private float _canvasHeightPt;

        // ── Кеш лейаутов обычных параграфов ──────────────────────────────
        private readonly Dictionary<ParagraphViewModel,
            (string Text, float Width, SKTextLayout Layout)> _layoutCache = new();

        // Кеш декодированных изображений по имени файла внутри проекта.
        private readonly Dictionary<string, SKImage?> _imageCache = new();

        // Растры, с которых сняты эти образы. Держатся живыми ровно столько же:
        // SKImage.FromBitmap не обязан копировать пиксели — он вправе взять их у
        // растра как есть, и освобождённый растр оставил бы образ с чужой памятью.
        private readonly Dictionary<string, SKBitmap> _imageBitmaps = new();

        // Имена файлов, для которых декодирование уже запущено в фоне — чтобы один и тот же
        // файл не ставился в очередь на каждом кадре, пока предыдущая загрузка не завершилась.
        private readonly HashSet<string> _imageLoadsInFlight = new();

        // Имена файлов, которых в проекте не нашлось или которые не читаются.
        //
        // Нужны для двух вещей сразу. Во-первых, на месте такой картинки рисуется
        // видимая отметка вместо пустоты: раньше документ, приехавший без файла,
        // просто показывал дырку — ни имени, ни намёка на то, что там что-то было.
        // Во-вторых, повторные попытки прекращаются: без этого списка каждый кадр
        // заводил новую фоновую задачу на файл, которого нет, — шестьдесят задач
        // в секунду на одну ненайденную картинку.
        //
        // Список сбрасывается при смене документа: файл мог появиться.
        private readonly HashSet<string> _imageMissing = new(StringComparer.OrdinalIgnoreCase);

        // Синхронизирует доступ к _imageCache и _imageLoadsInFlight между render-потоком
        // (чтение) и фоновыми задачами декодирования (запись).
        private readonly object _imageCacheLock = new();

        // Поля live-preview шрифта вынесены в DocumentCanvas.FontPreview.cs.

        // Хранит лямбды подписанные в WirePvm чтобы точно отписать в UnwirePvm.
        // Анонимные лямбды нельзя отписать через -= без сохранения ссылки.
        private readonly Dictionary<ParagraphViewModel, Action> _pvmFocusHandlers = new();

        // ── Кеш VM-обёрток и лейаутов для параграфов ячеек ───────────────
        // Ключ — ParagraphBlock (живёт в TableCell.Paragraphs).
        // VM-обёртки переиспользуются между rebuild'ами → SnapCaretToCorrectSlice
        // находит нужный слайс через Vm == targetVm (ссылка стабильна).
        private readonly Dictionary<ParagraphBlock, ParagraphViewModel> _cellVmCache = new();
        private readonly Dictionary<ParagraphBlock,
            (string Text, float Width, SKTextLayout Layout)> _cellLayoutCache = new();

        // Превью шрифта для абзацев ячеек: оригинальный абзац -> preview-абзац (построен по
        // выделенному диапазону). BuildTableLayout строит раскладку из него. Пусто вне превью.
        private readonly Dictionary<ParagraphBlock, ParagraphBlock> _cellFontPreview = new();

        // Кеш раскладок таблиц. BuildTableLayout перевёрстывает параграфы всех ячеек таблицы,
        // и без кеша это выполнялось при каждом пересборе раскладки — то есть на каждый
        // введённый символ. Инвалидируется вместе с _cellLayoutCache.
        private readonly Dictionary<TableBlock, (float Width, SKTableLayout Layout)> _tableLayoutCache = new();

        // Общая точка сброса кешей содержимого ячеек: поабзацного и табличного.
        // Все операции, меняющие содержимое или структуру таблиц, вызывают этот метод.
        private void InvalidateCellLayoutCaches()
        {
            _cellLayoutCache.Clear();
            _tableLayoutCache.Clear();
        }

        // Возвращает раскладку таблицы из кеша либо строит и кеширует её.
        // Во время live-preview шрифта кеш не используется: раскладка зависит от
        // временной карты _cellFontPreview и не должна переживать предпросмотр.
        private SKTableLayout GetOrBuildTableLayout(TableBlock table, float textWidthPt)
        {
            bool previewActive = _cellFontPreview.Count > 0;
            if (!previewActive
                && _tableLayoutCache.TryGetValue(table, out var cached)
                && Math.Abs(cached.Width - textWidthPt) < 0.1f)
                return cached.Layout;

            var layout = _renderer.BuildTableLayout(table, textWidthPt, _styleResolver!, _cellFontPreview);
            if (!previewActive)
                _tableLayoutCache[table] = (textWidthPt, layout);
            return layout;
        }

        // ── Дебаунс пересчёта ─────────────────────────────────────────────
        private System.Threading.CancellationTokenSource _rebuildCts = new();

        // ── Виртуализация ─────────────────────────────────────────────────
        private ScrollViewer? _parentScrollViewer;
        private double _scrollOffsetY = 0;
        private double _viewportHeight = 600;

        // ── Каретка ───────────────────────────────────────────────────────
        // Единая для всего документа включая ячейки таблицы.
        private int _caretPara = 0;
        private int _caretChar = 0;
        private int _caretLineHint = -1;
        private bool _caretVisible = true;
        private float _preferredCaretXPt = 0f;

        // Индекс каретки (_caretPara) и список раскладок (_layouts) обновляются двумя
        // отдельными шагами: пересборка сначала подменяет раскладку под _renderLock, и лишь
        // потом вызывающий ищет новый индекс слайса. Рендер идёт на своём потоке и берёт
        // снимок раскладки под тем же замком, а _caretPara читает как есть — в промежутке
        // между этими шагами пара оказывается несогласованной, и кадр рисует каретку по
        // старому номеру в новой раскладке: при серии Enter в ячейке она мелькала в соседней
        // ячейке или за таблицей. Пока флаг взведён, каретка не рисуется — один-два кадра
        // без неё незаметны, а промаха по чужому абзацу больше нет.
        private bool _caretIndexPending;

        // Активна ли серия вертикальных перемещений (Up/Down подряд). В начале серии столбец
        // (_preferredCaretXPt) захватывается из ЖИВОЙ геометрии каретки и держится до любого
        // горизонтального перемещения/клика/правки (там вызывается UpdatePreferredX, который
        // сбрасывает флаг). Так столбец не «уезжает» при многократном Down на короткие строки.
        private bool _vNavActive;
        private readonly DispatcherTimer _caretTimer;

        // ── Анимация скролла ──────────────────────────────────────────────
        private DispatcherTimer? _scrollAnimTimer;
        private double _scrollAnimFrom;
        private double _scrollAnimTo;
        private double _scrollAnimElapsedMs;
        private const double ScrollAnimDurationMs = 130.0;
        private const double ScrollAnimTickMs = 8.0;

        // ── Активная таблица (для структурных операций AddRow и т.д.) ────
        private TableBlock? _activeTableBlock;
        private int _activeCellRow = 0;
        private int _activeCellCol = 0;
        private int _activeCellTableEntryIdx = -1;

        // ── Drag ручек таблицы (без использования линейки) ───────────────
        private enum TableDragMode { None, ColResize, TableMove, RowResize }
        private TableDragMode _tableDragMode = TableDragMode.None;
        private int _tableDragColIndex = -1;    // индекс колонки при ColResize
        private int _tableDragEntryIdx = -1;    // индекс TableEntry
        private float _tableDragStartXPt = 0f;    // X мыши при начале drag в pt
        private float _tableDragStartVal = 0f;    // исходная ширина колонки или LeftIndentPt в pt

        // Размер hit-зоны ручки в pt (~5px при 100% zoom)
        private const float TableHandleHitPt = 5f * PxToPt;

        // ── Выделение ─────────────────────────────────────────────────────
        private int _selStartPara = 0;
        private int _selStartChar = 0;
        private int _selEndPara = 0;
        private int _selEndChar = 0;
        private bool _isSelecting;

        // ── Авто-скролл при выделении у края вьюпорта ─────────────────────
        // Пока идёт выделение и указатель у верхней/нижней границы видимой области, таймер
        // прокручивает документ со скоростью, растущей по мере приближения к краю, и продолжает
        // расширять выделение под текущим указателем. _autoScrollViewportPoint хранит позицию
        // указателя ОТНОСИТЕЛЬНО вьюпорта (указатель физически не двигается во время авто-скролла,
        // поэтому его координаты в канвасе пересчитываются из вьюпорт-позиции и текущего offset).
        private Avalonia.Threading.DispatcherTimer? _autoScrollTimer;
        private double _autoScrollVelocity;
        private Point _autoScrollViewportPoint;

        // ── Выделение нескольких ячеек ────────────────────────────────────
        // Единый словарь: TableBlock → (startRow, startCol, endRow, endCol).
        // Обновляется при движении курсора, очищается при новом клике.
        private bool _isCellRangeSelecting = false;

        // Ячейка, в которой было нажатие мыши (якорь cell-range выделения).
        // Хранится отдельно, т.к. для пустых ячеек без layout-записи HitTest
        // возвращает неправильный pi (ближайший по Y параграф другой строки).
        private TableBlock? _pressCellTable;
        private int _pressCellRow = -1;
        private int _pressCellCol = -1;

        private readonly Dictionary<TableBlock, (int sr, int sc, int er, int ec)> _tableSelections = new();

        // Потоковое выделение ячеек: абзац ячейки -> выделенный диапазон [from, to].
        // Частичная стартовая ячейка, целые промежуточные по порядку чтения, частичная конечная.
        // Пусто, когда потокового выделения нет (тогда работает прямоугольное _tableSelections).
        private readonly Dictionary<ParagraphBlock, (int from, int to)> _cellFlowRanges = new();

        // Полностью попавшие в поток ячейки (table, row, col) — заливаются целиком прямоугольником
        // ячейки, как обычное табличное выделение (а не по тексту, иначе пустые/узкие дают полоски).
        private readonly HashSet<(TableBlock table, int row, int col)> _cellFlowFull = new();

        private sealed record FrozenTableSelection(
            TableBlock Table,
            int StartRow, int StartCol,
            int EndRow, int EndCol);

        // ── Bitmap-кеш для мигания каретки и скролла ──────────────────────
        //
        // render-bitmap — CPU-буфер, в который выполняется офскрин-растеризация текста.
        // После рендера с него снимается иммутабельный SKImage (_displayImage): DrawImage
        // такого снимка не копирует пиксели при каждом кадре, а GPU кэширует текстуру
        // по uniqueID изображения. Повторные кадры (мигание каретки, чужие инвалидации,
        // скролл в пределах overscan) стоят один блит закэшированной текстуры.
        private readonly object _bitmapLock = new();
        private SKBitmap? _renderBitmap;   // офскрин-цель, пишем на render-треде
        private SKImage? _displayImage;    // иммутабельный снимок, читает compositor
        private int _bitmapW;
        private int _bitmapH;
        private float _lastFullRenderScrollY;
        // Очередь для освобождения битмапов старого размера.
        private readonly System.Collections.Concurrent.ConcurrentQueue<SKBitmap> _bitmapDisposeQueue = new();

        // Очередь для освобождения списанных снимков (_displayImage).
        // SKImage освобождается ТОЛЬКО на render-потоке (в начале следующего рендера)
        // либо при повторном прикреплении канваса: освобождение с UI-потока по таймеру
        // диспетчера гонялось с уже поставленной в очередь композитора отрисовкой —
        // DrawImage читал освобождённый нативный объект и падал с access violation.
        private readonly System.Collections.Concurrent.ConcurrentQueue<SKImage> _imageDisposeQueue = new();

        private bool _caretOnlyRedraw = false;
        // Контент изменился и требует полного рендера. Пока флаг поднят, быстрый путь
        // (_caretOnlyRedraw — мигание каретки, скролл по кэш-снимку) не имеет права
        // подменить полный рендер: иначе скролл-событие, пришедшее в одном батче с
        // правкой текста (ScrollToCaret при печати у края страницы), перетирало запрос
        // полного рендера, и на экране вечно оставался старый снимок.
        private bool _contentDirty = true;
        private volatile bool _isTransitioning;

        // Текущий сфокусированный канвас. Хоткеи редактора приходят через глобальный
        // _hotKeyService и исполняются в TextEditorModule.ExecuteHotKey, которому нужен
        // именно активный документ. Раньше он брал _lastCreatedView, но при переключении
        // воркмодов/вкладок это уже другой экземпляр (или его PageCanvas отвязан) — и
        // Enter/Copy/навигация уходили в чужой канвас. Ссылка обновляется на фокусе и
        // сбрасывается при откреплении из дерева.
        internal static DocumentCanvas? FocusedInstance;

        // ── Буфер обмена ─────────────────────────────────────────────────
        private string? _clipboardCache;

        // Внутренний буфер: JSON-массив ClipboardBlock (параграфы + таблицы в порядке документа).
        // Заполняется при Copy, используется при Paste для точного воспроизведения структуры.
        private string? _internalClipboardJson;

        // Внутренний буфер для скопированной картинки. СТАТИЧЕСКИЙ — общий для всех
        // вкладок/проектов в процессе, поэтому копия переживает переключение проекта.
        // Хранит полную копию блока (свойства: размер, кроп, поворот, рамка) И байты
        // файла картинки: при вставке байты пишутся в ZIP ЦЕЛЕВОГО проекта новым файлом,
        // поэтому картинка не ломается при вставке в другой проект.
        private static ImageBlock? _clipboardImage;
        private static byte[]? _clipboardImageBytes;

        /// <summary>
        /// Имя файла скопированной, но ещё не вставленной картинки — или null.
        /// Читает очистка неиспользуемых картинок: файл скопированного изображения
        /// удалять нельзя, пока копия жива в буфере.
        /// </summary>
        internal static string? ClipboardImageFileName
            => string.IsNullOrEmpty(_clipboardImage?.ImageFileName)
                ? null
                : _clipboardImage!.ImageFileName;

        // Форматы картинки в системном буфере (Avalonia 12: DataFormat/DataTransfer).
        // "PNG" — читают современные приложения (браузеры, новый Office); CF_DIB
        // ("DeviceIndependentBitmap") — классический формат Windows, читают Word, Paint.
        private static readonly Avalonia.Input.DataFormat<byte[]> ClipboardImagePngFormat =
            Avalonia.Input.DataFormat.CreateBytesPlatformFormat("PNG");
        private static readonly Avalonia.Input.DataFormat<byte[]> ClipboardImageDibFormat =
            Avalonia.Input.DataFormat.CreateBytesPlatformFormat("DeviceIndependentBitmap");

        private enum ClipboardBlockKind { Paragraph, Table, Image }
        private sealed class ClipboardBlock
        {
            public ClipboardBlockKind Kind { get; set; }
            public string? Text { get; set; }           // plain-text для Paragraph (fallback)
            public ParagraphBlock? Block { get; set; }  // полная модель параграфа (стили + runs)
            public TableBlock? Table { get; set; }      // для Table (уже слайснутая)

            // Плавающая картинка, через которую прошло выделение. Хранится вместе
            // с порядком блоков, поэтому при вставке встаёт между теми же абзацами.
            public ImageBlock? Image { get; set; }
        }

        // ── Рендеринг ─────────────────────────────────────────────────────
        private readonly SKTextRenderer _renderer = new();

        // Вёрстка строки спрашивает габарит встроенной картинки у канваса:
        // сам рендер текста документа не видит.
        private void WireInlineImageSizeResolver()
            => _renderer.InlineImageSize = GetInlineImageSize;
        private StyleResolver? _styleResolver;

        /// <summary>
        /// Карта "скрипт → шрифт" из настроек редактора.
        /// Пробрасывается в StyleResolver и используется SKTextRenderer для фолбэка символов.
        /// Обновляется из TextEditorModule при изменении настроек.
        /// </summary>
        public IReadOnlyDictionary<string, string>? ScriptFontMap
        {
            get => _scriptFontMap;
            set
            {
                _scriptFontMap = value;
                if (DocVm is not null)
                    _styleResolver = CreateStyleResolver();
            }
        }
        private IReadOnlyDictionary<string, string>? _scriptFontMap;

        /// <summary>
        /// Подставлять ли шрифт вместо знаков, которых нет в выбранной гарнитуре.
        /// Обновляется из TextEditorModule при изменении настроек.
        /// </summary>
        public bool SubstituteMissingGlyphs
        {
            get => _substituteMissingGlyphs;
            set
            {
                _substituteMissingGlyphs = value;
                if (DocVm is not null)
                    _styleResolver = CreateStyleResolver();
            }
        }
        private bool _substituteMissingGlyphs;

        /// <summary>
        /// Шрифт подстановки для знаков, письмо которых в ScriptFontMap не описано.
        /// Обновляется из TextEditorModule при изменении настроек.
        /// </summary>
        public string? SubstituteFontFamily
        {
            get => _substituteFontFamily;
            set
            {
                _substituteFontFamily = value;
                if (DocVm is not null)
                    _styleResolver = CreateStyleResolver();
            }
        }
        private string? _substituteFontFamily;

        /// <summary>
        /// Собирает резолвер стилей с текущими настройками шрифтов.
        ///
        /// Резолвер пересоздаётся из девяти мест — при смене документа, подачи
        /// чтения, настроек, при холодном пересчёте раскладки и в превью шрифта.
        /// Настройки в нём неизменяемы, поэтому каждое такое место обязано
        /// передать их заново; собраны они здесь, чтобы добавление следующей
        /// настройки не требовало помнить про все девять.
        ///
        /// Вызывается только когда DocVm уже есть — все места это проверяют.
        /// </summary>
        private StyleResolver CreateStyleResolver()
            => new StyleResolver(
                DocVm!.Document.Styles,
                _scriptFontMap,
                _substituteMissingGlyphs,
                _substituteFontFamily);

        // ── Логирование ───────────────────────────────────────────────────
        private static readonly ILogger _logger = Log.ForContext<DocumentCanvas>();

        // ── HotKey ───────────────────────────────────────────────────────
        private IHotKeyService? _hotKeyService;

        // ── Undo ─────────────────────────────────────────────────────────
        public UndoRedoStack? UndoStack { get; set; }

        /// <summary>
        /// Лёгкий стек операционных команд для набора текста.
        /// Каждая запись хранит несколько байт вместо полного JSON документа.
        /// Устанавливается TextEditorModule при создании View.
        /// </summary>
        public Writersword.Modules.TextEditor.Commands.TextUndoRedoStack? TextUndoStack { get; set; }

        // Единый хронологический порядок отмены между снапшотным (UndoStack) и операционным
        // (TextUndoStack) стеками. Без него ExecuteUndo сначала вычерпывал бы весь операционный
        // стек, и Ctrl+Z откатывал бы не последнее действие, а сначала весь набор текста.
        private enum UndoSource { Text, Snapshot }
        private readonly LinkedList<UndoSource> _undoOrder = new();
        private readonly Stack<UndoSource> _redoOrder = new();

        // Кладёт операционную команду в стек и фиксирует её в общем порядке отмены.
        // Если команда слилась с предыдущей (TryMerge вернул, что добавления нет) — отдельной
        // записи порядка не создаём.
        private void PushTextCommand(Writersword.Modules.TextEditor.Commands.ITextCommand cmd)
        {
            if (TextUndoStack is null) return;
            if (TextUndoStack.Push(cmd))
            {
                _undoOrder.AddLast(UndoSource.Text);
                _redoOrder.Clear();
            }
        }

        // Фиксирует снапшотную команду в общем порядке отмены (вызывается из CommitEdit).
        private void RecordSnapshotInOrder()
        {
            _undoOrder.AddLast(UndoSource.Snapshot);
            _redoOrder.Clear();
        }

        // Сброс порядка отмены — при смене документа, когда стеки очищаются.
        private void ResetUndoOrder()
        {
            _undoOrder.Clear();
            _redoOrder.Clear();
        }

        private double _monitorSizeInches = 0;
        private double _cachedDpi = 96.0;
        private DocumentSnapshotCommand? _pendingSnapshot;

        // ── Цвета ─────────────────────────────────────────────────────────
        private static readonly SKColor SelectionColor = new(0x33, 0x90, 0xFF, 0x60);
        private static readonly SKColor CanvasBgColor = new(0xE8, 0xE8, 0xE8);
        private static readonly SKColor PageShadowColor = new(0x00, 0x00, 0x00, 0x28);

        // Кешированные паинты — создаются один раз, живут всё время жизни канваса.
        // Вместо 13+ аллокаций на каждый рендер-кадр — ноль.
        // Все паинты используются только на compositor-треде, поэтому thread-safe.
        private readonly SKPaint _paintCanvasBg = new() { Color = new SKColor(0xE8, 0xE8, 0xE8) };
        private readonly SKPaint _paintPageShadow = new() { Color = new SKColor(0x00, 0x00, 0x00, 0x28) };
        private readonly SKPaint _paintPageWhite = new() { Color = SKColors.White };
        private readonly SKPaint _paintTransparent = new() { Color = SKColors.Transparent };
        // Обычное выделение — мягкое полупрозрачное голубое.
        private readonly SKPaint _paintSelection = new() { Color = new SKColor(0x33, 0x90, 0xFF, 0x60) };
        // Выделение поверх голубой/циановой заливки: голубое по голубому сливается, поэтому
        // для таких заливок берём мягкий тёплый (янтарный) полупрозрачный — он контрастен синему.
        private readonly SKPaint _paintSelectionAlt = new() { Color = new SKColor(0xFF, 0x8F, 0x00, 0x66) };
        private readonly SKPaint _paintCaret = new() { Color = SKColors.Black, StrokeWidth = 1.1f, IsAntialias = false, IsStroke = true };
        private readonly SKPaint _paintHandleFill = new() { Color = new SKColor(0x22, 0x99, 0xFF, 0xCC), IsAntialias = true };
        private readonly SKPaint _paintHandleStroke = new() { Color = new SKColor(0xFF, 0xFF, 0xFF, 0xCC), StrokeWidth = 1f, IsStroke = true, IsAntialias = true };
        private readonly SKPaint _paintHandleArrow = new() { Color = SKColors.White, StrokeWidth = 1f, IsStroke = true, IsAntialias = true };
        // Паинт для фона ячейки — Color мутируется перед каждым DrawRect (compositor-тред).
        private readonly SKPaint _paintCellBg = new();

        private DocumentViewModel? _docVm;
        private DocumentViewModel? DocVm => _docVm;
        // Масштаб, в котором рисуется канвас. В чтении им распоряжается книга: лист
        // имеет постоянный размер, а окно решает лишь то, с каким увеличением его
        // показать. Приближение читателя умножается сверху.
        private double Zoom => ReadingActive
            ? ReadingFitScale * ReadingViewZoom
            : (DocVm?.Zoom ?? 1.0);

        // Блок-якорь на который нужно переместить каретку после ближайшего rebuild.
        // Устанавливается при вставке разрыва страницы, потребляется в ScheduleRebuild.
        private ParagraphBlock? _pendingFocusBlock;

        // ── Callbacks ────────────────────────────────────────────────────
        public Action<double>? RecommendedZoomChanged { get; set; }

        private double _lastPageOffsetXPx = 0;

        // Горизонтальный центр страницы (pageXPt), запечённый в раскладку при последнем пересчёте.
        // Рендер сравнивает его с центром по живому _canvasWidth и доводит страницу сдвигом, не
        // пересобирая раскладку. Во время зум-жеста это центрирует лист без тяжёлой пагинации, а
        // когда пересчёт уже прошёл — сдвиг нулевой (бесшовно).
        private float _layoutPageXPt;
        private Action<double>? _pageOffsetXChanged;
        public Action<double>? PageOffsetXChanged
        {
            get => _pageOffsetXChanged;
            set { _pageOffsetXChanged = value; value?.Invoke(_lastPageOffsetXPx); }
        }

        public Action<IReadOnlyList<double>, IReadOnlyList<double>, double, int>? CaretEnteredTable { get; set; }
        public Action? CaretLeftTable { get; set; }

        /// <summary>
        /// Фактическая геометрия абзаца под кареткой для линейки: границы его зоны и реальные
        /// позиции отступов, снятые с построенной раскладки. По ней линейка и ставит стрелки,
        /// ничего не пересчитывая — раскладка единственная знает, где текст оказался на самом
        /// деле после всех её ограничителей, полей ячейки и переноса первой строки.
        /// </summary>
        public Action<ViewModels.Components.RulerParagraphGeometry>? RulerGeometryChanged { get; set; }

        /// <summary>Выделена (true) или снята с выделения (false) картинка — для контекстной вкладки.</summary>
        public Action<bool>? ImageSelectionChanged { get; set; }

        /// <summary>
        /// Вызывается когда каретка перемещается на другую страницу.
        /// Вертикальная линейка отображает шкалу только для этой страницы.
        /// </summary>
        public Action<int>? CaretPageChanged { get; set; }

        public Action<int, int, double>? CaretStateChanged { get; set; }

        public double MonitorSizeInches
        {
            get => _monitorSizeInches;
            set
            {
                if (Math.Abs(_monitorSizeInches - value) < 0.01) return;
                _monitorSizeInches = value;
                RebuildDpiCache();
                InvalidateMeasure();
            }
        }

        public DocumentCanvas()
        {
            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Ibeam);

            _caretTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
            _caretTimer.Tick += (_, _) =>
            {
                _caretVisible = !_caretVisible;
                _caretOnlyRedraw = true;
                InvalidateVisual();
            };
            GotFocus += OnGotFocusHandler;
            // Каретка мигает только пока редактор в фокусе: без фокуса таймер остановлен
            // и редактор не генерирует кадры вообще — окно не перерисовывается в покое.
            LostFocus += OnLostFocusHandler;
        }

        // ── HotKey ───────────────────────────────────────────────────────
        public void SetHotKeyService(IHotKeyService service) => _hotKeyService = service;

        // ── DPI ───────────────────────────────────────────────────────────
        private void RebuildDpiCache()
        {
            if (_monitorSizeInches <= 0)
            {
                _cachedDpi = 96.0;
                Dispatcher.UIThread.Post(() => RecommendedZoomChanged?.Invoke(RecommendedZoom));
                return;
            }
            var topLevel = TopLevel.GetTopLevel(this);
            var screen = topLevel?.Screens?.ScreenFromVisual(this);
            if (screen is null) return;
            double physW = screen.Bounds.Width * screen.Scaling;
            double physH = screen.Bounds.Height * screen.Scaling;
            double diagPx = Math.Sqrt(physW * physW + physH * physH);
            _cachedDpi = diagPx / _monitorSizeInches;
            Dispatcher.UIThread.Post(() => RecommendedZoomChanged?.Invoke(RecommendedZoom));
        }

        public double RecommendedZoom => _cachedDpi > 0 ? _cachedDpi / 96.0 : 1.0;

        private static float MmToPt(double mm) => (float)(mm * 72.0 / 25.4);
        private static double PtToMm(float pt) => pt * 25.4 / 72.0;

        private float GetPageWidthPt()
        {
            if (SpreadMode)
            {
                if (_spreadPageWidthPt <= 1f) ComputeSpreadPageSize();
                return _spreadPageWidthPt;
            }

            var ps = DocVm?.Document.PageSettings;
            if (ps is null) return MmToPt(210);
            return ps.Orientation == PageOrientation.Landscape ? MmToPt(ps.HeightMm) : MmToPt(ps.WidthMm);
        }
        private float GetPageHeightPt()
        {
            if (SpreadMode)
            {
                if (_spreadPageHeightPt <= 1f) ComputeSpreadPageSize();
                return _spreadPageHeightPt;
            }

            var ps = DocVm?.Document.PageSettings;
            if (ps is null) return MmToPt(297);
            return ps.Orientation == PageOrientation.Landscape ? MmToPt(ps.WidthMm) : MmToPt(ps.HeightMm);
        }
        private (float left, float top, float right, float bottom) GetPagePaddingPt()
        {
            var ps = DocVm?.Document.PageSettings;
            var (l, t, r, b) = ps is null
                ? (MmToPt(20), MmToPt(20), MmToPt(20), MmToPt(20))
                : (MmToPt(ps.MarginLeftMm + ps.MarginGutterMm), MmToPt(ps.MarginTopMm),
                   MmToPt(ps.MarginRightMm), MmToPt(ps.MarginBottomMm));

            // Разворот: поля ужимаются в той же пропорции, что и сам лист. Иначе на
            // странице вдвое меньше бумажной поля остаются бумажными и съедают текст —
            // при узком окне колонка вырождается в несколько символов.
            if (SpreadMode && _spreadPadScale > 0f && _spreadPadScale < 1f)
                return (l * _spreadPadScale, t * _spreadPadScale,
                        r * _spreadPadScale, b * _spreadPadScale);

            return (l, t, r, b);
        }

        // ── Книжный разворот ──────────────────────────────────────────────
        // Виртуальный лист: страница считается не по формату бумаги, а по половине
        // вьюпорта с сохранением пропорций документа. Раскладка при этом остаётся
        // прежней — пагинация, таблицы и переносы работают как в режиме страниц,
        // просто лист другого размера.
        //
        // Признак читается прямо из вью-модели, а не хранится полем: поле пришлось бы
        // выставлять в начале пересчёта, а размер листа спрашивают раньше — прогрев
        // кеша раскладки. Кеш шейпился под одну ширину, пересчёт просил другую, и
        // условие готовности кеша не выполнялось никогда: книга не открывалась вовсе.
        private bool SpreadMode => DocVm?.IsSpreadReading == true;

        private float _spreadPageWidthPt;
        private float _spreadPageHeightPt;
        private float _spreadPadScale = 1f;

        // Левая страница текущего разворота. Развороты идут парами: (0,1), (2,3)…
        private int _spreadLeftPage;

        // Страница, которая прямо сейчас снимается в отдельный битмап: её визуальная
        // дельта обнуляется, потому что внутри снимка координаты логические и лист
        // кладётся в начало.
        //
        // Значение потоковое, и это принципиально. Съёмка идёт на потоке интерфейса,
        // а кадры рисуются на потоке отрисовки. Общим полем чужой поток видел бы
        // страницу «снимаемой» и уводил её с разворота на всё время съёмки — лист
        // или его содержимое пропадали с экрана и оставались пропавшими до следующей
        // перерисовки. Хранится со сдвигом на единицу: поле потока начинается с нуля,
        // а нулём должна быть именно «страница не снимается».
        [ThreadStatic]
        private static int _spreadOffscreenPagePlusOne;

        /// <summary>Индекс снимаемой сейчас страницы на этом потоке; -1 — никакой.</summary>
        private static int SpreadOffscreenPage => _spreadOffscreenPagePlusOne - 1;

        // Куда уводятся страницы, не попавшие в разворот. Проверки видимости в
        // рендере отбрасывают их по этой координате, отдельных условий не нужно.
        private const float SpreadHiddenOffsetPt = 1_000_000f;

        /// <summary>
        /// Размер листа чтения. Величина постоянная и от окна НЕ зависит.
        ///
        /// Раньше лист считался по вьюпорту: свернул ленту — окно стало выше, лист
        /// вырос, на него влезло больше текста, вся книга пересчиталась заново. Читать
        /// такое нельзя: страница под рукой перестаёт быть той же страницей, а каждое
        /// движение интерфейса стоит полной пагинации документа.
        ///
        /// Теперь лист — это лист: его размер задаёт выбранный формат, а окно решает
        /// только то, с каким масштабом книгу показать (см. ReadingFitScale).
        /// </summary>
        private void ComputeSpreadPageSize()
        {
            var ps = DocVm?.Document.PageSettings;
            float paperW = ps is null
                ? MmToPt(210)
                : (ps.Orientation == PageOrientation.Landscape ? MmToPt(ps.HeightMm) : MmToPt(ps.WidthMm));
            float paperH = ps is null
                ? MmToPt(297)
                : (ps.Orientation == PageOrientation.Landscape ? MmToPt(ps.WidthMm) : MmToPt(ps.HeightMm));

            if (paperW < 1f || paperH < 1f) { paperW = MmToPt(210); paperH = MmToPt(297); }

            // Формат задаёт настоящий размер листа, а не одни пропорции: от него
            // зависит, сколько текста помещается на странице, и величина эта должна
            // быть постоянной. Размеры взяты у бумажных книг соответствующего вида.
            switch (DocVm?.Reading.Format ?? Models.Settings.ReadingSheetFormat.Document)
            {
                case Models.Settings.ReadingSheetFormat.Pocket:
                    paperW = MmToPt(110); paperH = MmToPt(178);
                    break;
                case Models.Settings.ReadingSheetFormat.Square:
                    paperW = MmToPt(165); paperH = MmToPt(165);
                    break;
                case Models.Settings.ReadingSheetFormat.Wide:
                    paperW = MmToPt(200); paperH = MmToPt(170);
                    break;
            }

            _spreadPageWidthPt = paperW;
            _spreadPageHeightPt = paperH;

            // Поля ужимаются по отношению к бумажному листу документа, а не к
            // пропорциям выбранного формата: у карманного формата своей бумаги нет.
            float paperRefW = ps is null
                ? MmToPt(210)
                : (ps.Orientation == PageOrientation.Landscape ? MmToPt(ps.HeightMm) : MmToPt(ps.WidthMm));
            _spreadPadScale = Math.Clamp(_spreadPageWidthPt / Math.Max(paperRefW, 1f), 0.25f, 1f);
        }

        /// <summary>Читать по одному листу вместо разворота.</summary>
        private bool SpreadSinglePage => DocVm?.Reading.IsSinglePage == true;

        /// <summary>
        /// Индекс левой страницы разворота, которому принадлежит страница. При чтении
        /// по одному листу выравнивать нечего — страница сама себе разворот.
        /// </summary>
        private int SpreadLeftOf(int pageIdx)
            => SpreadSinglePage ? pageIdx : pageIdx - (pageIdx & 1);

        /// <summary>
        /// Держит текущий разворот в пределах документа. Нужно после пересчёта: смена
        /// размера окна или шрифта меняет число страниц, и прежний разворот может уйти
        /// за конец книги.
        /// </summary>
        private void ClampSpreadPage()
        {
            int last = Math.Max(0, _pages.Count - 1);

            // Вход в книгу открывает её там, где стоит каретка: читать с начала,
            // когда работал над серединой рукописи, никто не станет.
            if (_spreadNeedsCaretSync)
            {
                _spreadNeedsCaretSync = false;
                if (_caretPara >= 0 && _caretPara < _layouts.Count)
                    _spreadLeftPage = _layouts[_caretPara].PageIndex;
            }

            _spreadLeftPage = SpreadLeftOf(Clamp(_spreadLeftPage, 0, last));

            if (_spreadLeftPage != _spreadLabelPage || _pages.Count != _spreadLabelCount)
            {
                _spreadLabelPage = _spreadLeftPage;
                _spreadLabelCount = _pages.Count;
                SpreadPageChanged?.Invoke();
            }

            // Снимки, снятые под прежний разворот, дальше не нужны; те, что нужны
            // следующему перевороту, готовятся в простое.
            TrimSpreadCache();
            SchedulePrefetchSpreadNeighbours();
        }

        /// <summary>
        /// В развороте канвас равен вьюпорту: книга не прокручивается, страницы
        /// переворачиваются. Иначе ScrollViewer растянулся бы на всю высоту документа
        /// и рядом с книгой болтался бы бесконечный ползунок.
        /// </summary>
        private void FitCanvasToViewport()
        {
            double zoom = Math.Max(Zoom, 0.01);
            float viewHPt = (float)(Math.Max(_viewportHeight, 200) / zoom * PxToPt);

            // Холст книги равен вьюпорту и НИКОГДА его не перерастает. Иначе выходит
            // петля: холст выше окна поднимает полосу прокрутки, полоса сужает вьюпорт,
            // под суженный вьюпорт пересчитывается лист, лист становится ниже, полоса
            // пропадает — и всё повторяется по кругу, десятки пересборок в секунду.
            // Приближённая книга не помещается в окно по построению, и водит её не
            // прокрутка, а панорамирование (см. UpdateReadingEdgePan).

            lock (_renderLock)
            {
                _canvasHeightPt = viewHPt;
                _canvasHeight = viewHPt * PtToPx;
            }
        }

        // ── DataContext / ScrollViewer ────────────────────────────────────
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _isTransitioning = false;

            // Дренируем битмапы и снимки, накопившиеся пока view была detached.
            // RenderWithSKCanvas тоже дренирует, но он вызывается только когда
            // контрол видим. При смене воркмода TextEditor может долго не рендериться.
            // К моменту повторного прикрепления старые отрисовки композитора давно
            // завершены — освобождать здесь безопасно.
            while (_bitmapDisposeQueue.TryDequeue(out var stale))
                stale?.Dispose();
            while (_imageDisposeQueue.TryDequeue(out var staleImage))
                staleImage?.Dispose();

            base.OnAttachedToVisualTree(e);

            // Возвращаем подписки, снятые в OnDetachedFromVisualTree. Раньше они жили
            // только в конструкторе: при переиспользовании кэшированной вьюхи (detach →
            // reattach) конструктор не вызывается, и после переприцепки фокусная логика
            // и мигание каретки оставались мёртвыми. Пара -=/+= защищает от двойной
            // подписки при первом attach после конструктора.
            GotFocus -= OnGotFocusHandler;
            GotFocus += OnGotFocusHandler;
            LostFocus -= OnLostFocusHandler;
            LostFocus += OnLostFocusHandler;

            if (IsFocused)
            {
                _caretVisible = true;
                _caretTimer.Stop();
                _caretTimer.Start();
            }

            // Восстанавливаем подписки на DocumentViewModel и параграфы, снятые при
            // detach: у переиспользуемой вьюхи DataContext не меняется, и без этого
            // цепочка «ввод → PlainText → перерисовка» оставалась мёртвой навсегда.
            WireDocVmSubscriptions();

            RebuildDpiCache();
            SubscribeToScrollViewer();
            _ = PrefetchClipboardAsync();
            InvalidateFull();
            Dispatcher.UIThread.Post(
                InvalidateMeasure,
                DispatcherPriority.Loaded);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _isTransitioning = true;
            if (ReferenceEquals(FocusedInstance, this)) FocusedInstance = null;
            base.OnDetachedFromVisualTree(e);

            // Останавливаем таймер — он держит ссылку на this через замыкание и мешает GC.
            _caretTimer.Stop();

            // То же и с чтением: таймер подвода книги держит канвас, а картинка
            // бумаги — нативную память.
            ReleaseReadingResources();
            GotFocus -= OnGotFocusHandler;
            LostFocus -= OnLostFocusHandler;

            // Отписываемся от DocumentViewModel и всех ParagraphViewModel.
            if (_docVm is not null)
            {
                _docVm.ReadingSettingsChanged -= ApplyReadingSettings;
                _docVm.ReadingVisualChanged -= ApplyReadingVisualSettings;
                _docVm.Paragraphs.CollectionChanged -= OnParagraphsChanged;
                _docVm.PropertyChanged -= OnDocVmPropertyChanged;
                _docVm.ParagraphFormatChanged -= OnParagraphFormatChanged;
                _docVm.StructureChanged -= OnStructureChanged;
                _docVm.BeginFontPreviewDelegate = null;
                _docVm.PreviewFontFamilyDelegate = null;
                _docVm.EndFontPreviewDelegate = null;
                _docVm.FocusEditorDelegate = null;
                _docVm.OnPageBreakInserted = null;
                _docVm.UndoDelegate = null;
                _docVm.RedoDelegate = null;
                _docVm.CutDelegate = null;
                _docVm.CopyDelegate = null;
                _docVm.PasteDelegate = null;
                _docVm.BeginEditDelegate = null;
                _docVm.CommitEditDelegate = null;
                _docVm.CommitRunPropertyGranularDelegate = null;
                _docVm.CommitTextEditsDelegate = null;
                _docVm.CommitParagraphPropertyGranularDelegate = null;
                _docVm.GetCaretWordRangeDelegate = null;

                // Снимаем делегаты с каждого параграфа — иначе замыкания удерживают canvas.
                foreach (var pvm in _docVm.Paragraphs)
                    UnwirePvm(pvm);
            }

            // Отменяем фоновый rebuild.
            _rebuildCts.Cancel();

            // Останавливаем прогрев кеша раскладки: его проходы перепланируют себя
            // через диспетчер и без остановки продолжали бы шейпить отцепленную вьюху.
            // При повторном прикреплении measure перезапустит прогрев с того же места —
            // уже зашейпленные абзацы лежат в кеше и повторно не обрабатываются.
            SetWarmupActive(false);

            // Не диспозим bitmap и снимок напрямую — render-тред (compositor) может
            // держать локальную ссылку на тот же объект и рисовать его прямо сейчас:
            // уже поставленная в очередь композитора отрисовка выполняется ПОСЛЕ
            // detach, и освобождение с UI-потока (даже отложенным постом) гонялось
            // с DrawImage на render-потоке — приложение падало с access violation
            // внутри SkiaSharp. Оба объекта уходят в очереди и освобождаются только
            // там, где гонка исключена: в начале следующего рендера (render-поток,
            // рендеры сериализованы) либо при повторном прикреплении канваса
            // (к этому моменту старые отрисовки давно завершены). Если канвас больше
            // никогда не рендерится — нативную память вернёт финализатор SkiaSharp.
            lock (_bitmapLock)
            {
                if (_renderBitmap is not null)
                {
                    _bitmapDisposeQueue.Enqueue(_renderBitmap);
                    _renderBitmap = null;
                }
                if (_displayImage is not null)
                {
                    _imageDisposeQueue.Enqueue(_displayImage);
                    _displayImage = null;
                }
            }

            // SKPaint не диспозим здесь: DockFactory переиспользует DocumentCanvas
            // (detach → reattach при переключении вкладок). Если диспозить паинты
            // на detach, при повторном reattach рендер упадёт с disposed-объектами.
            // SKPaint — крошечные нативные объекты (~200 байт), GC соберёт при финализации.

            // Списки раскладки (_layouts, _pages, _tables) и кеши вёрстки СОХРАНЯЕМ:
            // DockFactory переиспользует канвас при переключении вкладок и воркмодов
            // (detach → reattach), и с живой раскладкой MeasureOverride после reattach
            // пропускает полный проход пагинации по отпечатку (LayoutsMatchCurrentState) —
            // на больших документах это разница между мгновенно и секундами.
            // Актуальность гарантирует отпечаток: смена документа создаёт новый
            // DocumentViewModel (ловится по ссылке), изменение ширины/режима/стилей
            // ловится по остальным полям отпечатка. При закрытии вкладки канвас
            // умирает целиком и нативную память соберёт GC.

            UnsubscribeFromScrollViewer();
        }

        private void OnGotFocusHandler(object? sender, Avalonia.Input.FocusChangedEventArgs e)
        {
            FocusedInstance = this;
            _ = PrefetchClipboardAsync();

            // Возобновляем мигание каретки.
            _caretVisible = true;
            _caretTimer.Stop();
            _caretTimer.Start();
            _caretOnlyRedraw = true;
            InvalidateVisual();
        }

        private void OnLostFocusHandler(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            // Без фокуса каретка не мигает и не показывается — редактор перестаёт
            // генерировать кадры, композитор окна спит, пока его не разбудит кто-то другой.
            _caretTimer.Stop();
            if (_caretVisible)
            {
                _caretVisible = false;
                _caretOnlyRedraw = true;
                InvalidateVisual();
            }
        }

        private async Task PrefetchClipboardAsync()
        {
            try
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard is null) return;
#pragma warning disable CS0618
                _clipboardCache = await clipboard.TryGetTextAsync();
#pragma warning restore CS0618
            }
            catch { }
        }

        private void SubscribeToScrollViewer()
        {
            StyledElement? parent = Parent;
            while (parent is not null)
            {
                if (parent is ScrollViewer sv)
                {
                    _parentScrollViewer = sv;
                    sv.ScrollChanged += OnScrollChanged;
                    sv.PropertyChanged += OnScrollViewerPropertyChanged;
                    _scrollOffsetY = sv.Offset.Y;
                    _viewportHeight = sv.Viewport.Height;
                    break;
                }
                parent = parent.Parent;
            }
        }

        private void OnViewportSizeChanged()
        {
            if (_parentScrollViewer is null) return;
            _viewportHeight = _parentScrollViewer.Viewport.Height;
            // Принудительно пересчитываем layout — viewport мог измениться
            // из-за закрытия/открытия панели dock, страница должна перецентроваться.
            InvalidateMeasure();
        }

        private void OnScrollViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == ScrollViewer.ViewportProperty)
                OnViewportSizeChanged();
        }

        private void UnsubscribeFromScrollViewer()
        {
            if (_parentScrollViewer is null) return;
            _parentScrollViewer.ScrollChanged -= OnScrollChanged;
            _parentScrollViewer.PropertyChanged -= OnScrollViewerPropertyChanged;
            _parentScrollViewer = null;
        }

        /// <summary>Число страниц текущей раскладки.</summary>
        public int PageCount
        {
            get { lock (_renderLock) { return Math.Max(1, _pages.Count); } }
        }

        /// <summary>
        /// Номер страницы (1-based) у верха вьюпорта при заданном вертикальном смещении прокрутки (px).
        /// Используется всплывающей подсказкой при перетаскивании ползунка.
        /// </summary>
        public int GetPageAtOffset(double offsetYPx)
        {
            List<PageRect> pages;
            lock (_renderLock) { pages = _pages; }
            if (pages.Count == 0) return 1;
            double zoom = Zoom;
            float viewTopPt = (float)(offsetYPx / zoom * PxToPt);
            int page = 1;
            for (int i = 0; i < pages.Count; i++)
            {
                if (pages[i].Ypt <= viewTopPt + 1f) page = i + 1;
                else break;
            }
            return page;
        }

        private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (sender is not ScrollViewer sv) return;
            _scrollOffsetY = sv.Offset.Y;
            _viewportHeight = sv.Viewport.Height;

            // Горизонтальная позиция листа для линейки зависит от Offset.X, но публикуется
            // в линейку только при пересборке раскладки (RebuildPageModePass). При скролле
            // раскладка не пересобирается, поэтому линейка держала старое значение и уезжала
            // относительно страницы. Переотправляем позицию при каждом изменении прокрутки —
            // расчёт совпадает с расчётом в RebuildPageModePass.
            double pageOffsetXPx = _layoutPageXPt * PtToPx * Zoom - sv.Offset.X;
            if (Math.Abs(pageOffsetXPx - _lastPageOffsetXPx) > 0.01)
            {
                _lastPageOffsetXPx = pageOffsetXPx;
                PageOffsetXChanged?.Invoke(pageOffsetXPx);
            }

            // Контент не менялся — скролл лишь сдвигает окно по уже отрисованному
            // overscan-битмапу. Ветка _caretOnlyRedraw в RenderWithSKCanvas переиспользует
            // битмап, пока вьюпорт внутри его диапазона, и уходит в полный рендер только
            // когда прокрутка выходит за край отрисованной области.
            _caretOnlyRedraw = true;
            InvalidateVisual();
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);

            if (_docVm is not null)
            {
                // Отписываемся от всех параграфов старого DocVm.
                // Без этого каждый ParagraphViewModel держит замыкание на этот канвас
                // через FocusRequested и RequestFocusAtPosition — canvas не освобождается GC.
                foreach (var pvm in _docVm.Paragraphs)
                    UnwirePvm(pvm);

                _docVm.ReadingSettingsChanged -= ApplyReadingSettings;
                _docVm.ReadingVisualChanged -= ApplyReadingVisualSettings;
                _docVm.Paragraphs.CollectionChanged -= OnParagraphsChanged;
                _docVm.PropertyChanged -= OnDocVmPropertyChanged;
                _docVm.ParagraphFormatChanged -= OnParagraphFormatChanged;
                _docVm.StructureChanged -= OnStructureChanged;
                _docVm.BeginFontPreviewDelegate = null;
                _docVm.PreviewFontFamilyDelegate = null;
                _docVm.EndFontPreviewDelegate = null;
                _docVm.FocusEditorDelegate = null;
                _docVm.OnPageBreakInserted = null;
                _docVm.UndoDelegate = null;
                _docVm.RedoDelegate = null;
                _docVm.CutDelegate = null;
                _docVm.CopyDelegate = null;
                _docVm.PasteDelegate = null;
                _docVm.BeginEditDelegate = null;
                _docVm.CommitEditDelegate = null;
                _docVm.CommitRunPropertyGranularDelegate = null;
                _docVm.CommitTextEditsDelegate = null;
                _docVm.CommitParagraphPropertyGranularDelegate = null;
                _docVm.GetCaretWordRangeDelegate = null;
                _docVm.BeginUndoStepDelegate = null;
                _docVm.CommitUndoStepDelegate = null;
                _docVm.BeginTableUndoStepDelegate = null;
                _docVm.CommitTableUndoStepDelegate = null;
            }

            _docVm = DataContext as DocumentViewModel;
            _layoutCache.Clear();
            _pvmFocusHandlers.Clear();
            _cellVmCache.Clear();
            InvalidateCellLayoutCaches();
            ResetUndoOrder();

            // Другой документ — другой архив: картинка, которой не было там,
            // здесь может лежать на месте. Список потерянных начинается заново.
            lock (_imageCacheLock) _imageMissing.Clear();

            if (DocVm is not null)
            {
                _styleResolver = CreateStyleResolver();
                _lastZoom = DocVm.Zoom;
                WireDocVmSubscriptions();
            }

            InvalidateMeasure();
        }

        /// <summary>
        /// Подписки канваса на DocumentViewModel, его параграфы и делегаты.
        /// Вызывается из OnDataContextChanged и ПОВТОРНО из OnAttachedToVisualTree:
        /// при detach все подписки снимаются, а у кэшированной вьюхи (переиспользование
        /// в доке) OnDataContextChanged не срабатывает — без повторной подписки ввод
        /// менял модель, но перерисовка не запускалась (цепочка PlainText →
        /// ScheduleRebuild → InvalidateFull была мертва). Идемпотентен: перед каждой
        /// подпиской выполняется отписка.
        /// </summary>
        private void WireDocVmSubscriptions()
        {
            if (DocVm is null) return;

            // Вход в историю отмены для операций, выполняемых во вью-модели.
            // Регистрируется здесь, а не при входе в таблицу: правки из контекстного
            // меню и переключатели вкладки доступны и без каретки внутри таблицы.
            DocVm.BeginUndoStepDelegate = BeginEdit;
            DocVm.CommitUndoStepDelegate = CommitEdit;
            DocVm.BeginTableUndoStepDelegate = BeginTableEdit;
            DocVm.CommitTableUndoStepDelegate = CommitTableEdit;

            DocVm.Paragraphs.CollectionChanged -= OnParagraphsChanged;
            DocVm.PropertyChanged -= OnDocVmPropertyChanged;
            DocVm.ParagraphFormatChanged -= OnParagraphFormatChanged;
            DocVm.StructureChanged -= OnStructureChanged;

            DocVm.ReadingSettingsChanged -= ApplyReadingSettings;
            DocVm.ReadingSettingsChanged += ApplyReadingSettings;

            DocVm.ReadingVisualChanged -= ApplyReadingVisualSettings;
            DocVm.ReadingVisualChanged += ApplyReadingVisualSettings;

            DocVm.Paragraphs.CollectionChanged += OnParagraphsChanged;
            DocVm.PropertyChanged += OnDocVmPropertyChanged;
            DocVm.ParagraphFormatChanged += OnParagraphFormatChanged;
            DocVm.StructureChanged += OnStructureChanged;
            DocVm.BeginFontPreviewDelegate = BeginFontPreviewSession;
            DocVm.PreviewFontFamilyDelegate = PreviewFontFamilySession;
            DocVm.EndFontPreviewDelegate = EndFontPreviewSession;
            DocVm.FocusEditorDelegate = FocusEditorFromHost;
            DocVm.OnPageBreakInserted = block => _pendingFocusBlock = block;
            DocVm.UndoDelegate = ExecuteUndo;
            DocVm.RedoDelegate = ExecuteRedo;
            DocVm.CutDelegate = ExecuteCut;
            DocVm.CopyDelegate = ExecuteCopy;
            DocVm.PasteDelegate = ExecutePaste;
            DocVm.BeginEditDelegate = BeginEdit;
            DocVm.CommitEditDelegate = CommitEdit;
            DocVm.CommitRunPropertyGranularDelegate = CommitRunPropertyGranular;
            DocVm.GetCellSelectionRangesDelegate = GetCellSelectionRanges;
            DocVm.GetSelectedCellParagraphsDelegate = QuerySelectedCellParagraphs;
            DocVm.CommitTextEditsDelegate = CommitTextEditsGranular;
            DocVm.CommitParagraphPropertyGranularDelegate = CommitParagraphPropertyGranular;
            DocVm.GetCaretWordRangeDelegate = GetCaretWordRange;
            DocVm.GetCaretTargetDelegate = GetCaretTarget;
            DocVm.InlineImageInserted -= OnInlineImageInserted;
            DocVm.InlineImageInserted += OnInlineImageInserted;
            DocVm.InlineObjectsChanged -= RefreshParagraphAfterInlineChange;
            DocVm.InlineObjectsChanged += RefreshParagraphAfterInlineChange;
            DocVm.TrySetImageAlignmentDelegate = TrySetSelectedImageAlignment;
            DocVm.GetSelectedImageAlignmentDelegate = GetSelectedImageAlignment;
            DocVm.SetImageWrapModeDelegate = SetSelectedImageWrapMode;
            DocVm.SetImageWrapSideDelegate = SetSelectedImageWrapSide;
            DocVm.GetSelectedImageWrapSideDelegate = GetSelectedImageWrapSide;
            DocVm.SetImagePinnedPageDelegate = SetSelectedImagePinnedPage;
            DocVm.GetSelectedImagePinnedPageDelegate = GetSelectedImagePinnedPage;
            DocVm.GetSelectedImageCurrentPageDelegate = GetSelectedImageCurrentPage;
            DocVm.SetImageLockAspectDelegate = SetSelectedImageLockAspect;
            DocVm.DeleteSelectedImageDelegate = DeleteSelectedImageFromCanvas;
            DocVm.GetSelectedImageInfoDelegate = GetSelectedImageInfo;
            DocVm.SetImageRotationDelegate = SetSelectedImageRotation;
            DocVm.GetSelectedImageRotationDelegate = GetSelectedImageRotation;
            DocVm.SetImageWidthDelegate = SetSelectedImageWidth;
            DocVm.SetImageHeightDelegate = SetSelectedImageHeight;
            DocVm.SetImageOpacityDelegate = SetSelectedImageOpacity;
            DocVm.SetImageBorderDelegate = SetSelectedImageBorder;
            DocVm.GetSelectedImageStyleDelegate = GetSelectedImageStyle;
            DocVm.ToggleImageFlipHorizontalDelegate = ToggleSelectedImageFlipHorizontal;
            DocVm.ToggleImageFlipVerticalDelegate = ToggleSelectedImageFlipVertical;
            DocVm.SetImageCropModeDelegate = SetSelectedImageCropMode;
            DocVm.GetImageCropModeDelegate = GetSelectedImageCropMode;
            DocVm.SetImageWrapPaddingDelegate = SetSelectedImageWrapPadding;
            DocVm.GetSelectedImageWrapPaddingDelegate = GetSelectedImageWrapPadding;
            WireInlineImageSizeResolver();
            _pagesPerRowSetting = DocVm.PagesPerRow;
            UpdateEffectivePagesPerRow();

            foreach (var pvm in DocVm.Paragraphs)
            {
                UnwirePvm(pvm);
                WirePvm(pvm);
            }
        }

        /// <summary>
        /// Применяет горизонтальное выравнивание к выделенной блок-картинке (Inline).
        /// Возвращает true, если картинка выделена и выравнивание изменено или уже совпадает —
        /// в этом случае команда выравнивания не должна трогать абзац.
        /// </summary>
        private bool TrySetSelectedImageAlignment(
            Writersword.Modules.TextEditor.Models.Styles.TextAlignment alignment)
        {
            if (_selectedImage is null)
                return false;

            // Картинка в строке — обычный символ абзаца: выравнивание относится к абзацу,
            // как в Word. Команду не поглощаем, пусть идёт по обычному пути.
            if (IsInlineObjectImage(_selectedImage))
                return false;

            // Плавающая картинка позиционируется смещением якоря, выравнивание к ней
            // неприменимо. Команду всё равно поглощаем: иначе она проваливалась в
            // ApplyParaProperty и меняла выравнивание абзаца при выделенной картинке.
            if (_selectedImage.WrapMode != WrapMode.Inline)
                return true;

            if (_selectedImage.Alignment != alignment)
            {
                BeginImageEdit("Выравнивание изображения");
                _selectedImage.Alignment = alignment;
                CommitImageEdit();
                RebuildLayouts();
                InvalidateFull();
            }

            // Обновляем риббон: кнопки должны отражать выравнивание картинки и не
            // «залипать» при повторных кликах (ToggleButton с OneWay-биндингом).
            DocVm?.FireCursorContextChanged();
            return true;
        }

        /// <summary>Выравнивание выделенной блок-картинки для отображения в риббоне (или null).</summary>
        private Writersword.Modules.TextEditor.Models.Styles.TextAlignment? GetSelectedImageAlignment()
            => _selectedImage is not null
               && _selectedImage.WrapMode == WrapMode.Inline
               && !IsInlineObjectImage(_selectedImage)
                ? _selectedImage.Alignment
                : (Writersword.Modules.TextEditor.Models.Styles.TextAlignment?)null;

        // Меняет режим обтекания выделенной картинки (команда контекстной вкладки).
        private void SetSelectedImageWrapMode(WrapMode mode)
        {
            if (_selectedImage is null || _selectedImage.WrapMode == mode) return;

            BeginImageEdit("Обтекание изображения");

            // Переход из строки текста в плавающий режим: фиксируем текущее положение
            // как смещение якоря, чтобы картинка не прыгнула в угол страницы.
            double offsetXPt = _selectedImage.OffsetXPt;
            double offsetYPt = _selectedImage.OffsetYPt;
            if (_selectedImage.WrapMode == WrapMode.Inline && mode != WrapMode.Inline)
            {
                for (int i = 0; i < _images.Count; i++)
                {
                    var entry = _images[i];
                    if (!ReferenceEquals(entry.Block, _selectedImage)) continue;
                    if (entry.PageIndex >= 0 && entry.PageIndex < _pages.Count)
                    {
                        var pg = _pages[entry.PageIndex];
                        offsetXPt = entry.XPt - pg.PadLeftPt - pg.MarginLeftPt;
                        offsetYPt = entry.Ypt - pg.Ypt - pg.PadTopPt;
                    }
                    break;
                }
            }

            // «В тексте» — это символ в строке, остальные режимы — отдельный блок.
            // Смена режима переносит картинку между двумя представлениями.
            bool wasInLine = IsInlineObjectImage(_selectedImage);
            bool converted = false;

            if (wasInLine && mode != WrapMode.Inline)
            {
                InvalidateInlineImageLayout(_selectedImage);
                converted = DocVm?.ConvertInlineImageToBlock(
                    _selectedImage, mode, offsetXPt, offsetYPt) ?? false;
            }
            else if (!wasInLine && mode == WrapMode.Inline)
            {
                converted = DocVm?.ConvertBlockImageToInline(_selectedImage) ?? false;
            }

            if (!converted)
            {
                _selectedImage.WrapMode = mode;
                _selectedImage.OffsetXPt = offsetXPt;
                _selectedImage.OffsetYPt = offsetYPt;
            }

            // Картинка «в тексте» — обычный символ абзаца: её место определяет текст,
            // а не номер страницы. Закрепление тут бессмысленно и опасно — текст утащит
            // символ на другую страницу, а закрепление осталось бы от старой. Снимаем.
            if (mode == WrapMode.Inline) _selectedImage.PinnedPage = 0;

            CommitImageEdit();
            InvalidateInlineImageLayout(_selectedImage);
            RebuildLayouts();
            InvalidateMeasure();
            InvalidateFull();
        }

        // Меняет сторону обтекания выделенной картинки: с обеих сторон, только слева,
        // только справа или по большей стороне.
        private void SetSelectedImageWrapSide(Models.Document.WrapSide side)
        {
            if (_selectedImage is null || _selectedImage.WrapSide == side) return;

            BeginImageEdit("Сторона обтекания");
            _selectedImage.WrapSide = side;
            CommitImageEdit();
            RebuildLayouts();
            InvalidateMeasure();
            InvalidateFull();
        }

        private Models.Document.WrapSide? GetSelectedImageWrapSide()
            => _selectedImage?.WrapSide;

        /// <summary>
        /// Включает жёсткую привязку картинки к странице (1-based) или снимает её (0).
        /// При включении смещения пересчитываются от краёв этой страницы — картинка
        /// остаётся визуально там же, где была, и дальше уже никуда не переезжает.
        /// </summary>
        private void SetSelectedImagePinnedPage(int page)
        {
            if (_selectedImage is null) return;

            int normalized = Math.Max(0, page);
            if (_selectedImage.PinnedPage == normalized) return;

            BeginImageEdit("Привязка к странице");

            // Текущее положение картинки на листе — единственное, что нельзя терять
            // ни при закреплении, ни при откреплении: смещения после переключения
            // начинают отсчитываться от ДРУГОЙ страницы, и без пересчёта картинка
            // прыгает на столько страниц, на сколько её увело закрепление.
            ImageEntry? current = null;
            foreach (var ie in _images)
            {
                if (!ReferenceEquals(ie.Block, _selectedImage)) continue;
                current = ie;
                break;
            }

            if (normalized > 0)
            {
                // Закрепление: смещения переводим на ту страницу, где картинка стоит.
                if (current is { } pinEntry
                    && pinEntry.PageIndex >= 0 && pinEntry.PageIndex < _pages.Count)
                {
                    var pg = _pages[pinEntry.PageIndex];
                    _selectedImage.OffsetXPt = pinEntry.XPt - (pg.PadLeftPt + pg.MarginLeftPt);
                    _selectedImage.OffsetYPt = pinEntry.Ypt - (pg.Ypt + pg.PadTopPt);
                }
            }
            else if (current is { } freeEntry)
            {

                // Открепление: картинка снова пойдёт за своим местом в потоке, поэтому
                // и место в потоке переносим на её страницу — за первый абзац той
                // страницы, где она сейчас лежит. Смещения пересчитываются от этой же
                // страницы: картинка остаётся ровно там, где была, и сразу начинает
                // обтекаться текстом вокруг себя, а не улетает на чужой лист.
                MoveImageBlockToPage(_selectedImage, freeEntry);
            }

            _selectedImage.PinnedPage = normalized;
            CommitImageEdit();
            RebuildLayouts();
            InvalidateMeasure();
            InvalidateFull();
        }

        /// <summary>
        /// Переносит блок картинки в поток той страницы, на которой она сейчас лежит,
        /// и пересчитывает смещения от краёв этой страницы. Визуально картинка остаётся
        /// на месте, но дальше живёт как обычная плавающая картинка этой страницы.
        /// </summary>
        private void MoveImageBlockToPage(ImageBlock image, ImageEntry entry)
        {
            if (DocVm is null) return;

            // Целевая страница — та, НАД КОТОРОЙ картинка лежит визуально, а не та, за
            // которой она числилась. Закреплённую картинку можно утащить на соседний лист:
            // в многостраничном виде он стоит рядом по горизонтали, и смещения становятся
            // большими отрицательными. Пока закрепление включено, это рисуется правильно,
            // но при откреплении отсчёт от прежней страницы уносил картинку за край листа.
            var (targetPage, targetXPt, targetYPt) = ResolveVisiblePlacement(entry);
            if (targetPage < 0 || targetPage >= _pages.Count) return;

            var section = DocVm.Document.Sections[0];

            // Первый абзац этой страницы — новое место картинки в потоке блоков.
            ParagraphBlock? host = null;
            foreach (var pl in _layouts)
            {
                if (pl.PageIndex != targetPage || pl.Cell is not null) continue;
                if (pl.Vm?.Model is not { } model) continue;
                if (section.Blocks.IndexOf(model) < 0) continue;
                host = model;
                break;
            }

            if (host is not null)
            {
                // Сначала переносим место в потоке, и только потом считаем смещения:
                // отсчитывать их нужно от страницы, на которой поток РЕАЛЬНО дойдёт до
                // нового места картинки, а это не всегда та страница, где она видна.
                // Абзац-хозяин может начинаться раньше и тянуться дальше — тогда поток
                // доходит до вставленной за ним картинки только после его окончания,
                // и смещения, посчитанные от видимой страницы, уносили картинку на
                // столько листов, сколько занимает хвост абзаца.
                section.Blocks.Remove(image);
                int hostIdx = section.Blocks.IndexOf(host);
                if (hostIdx < 0) section.Blocks.Add(image);
                else section.Blocks.Insert(hostIdx + 1, image);

                ApplyUnpinnedOffsets(image, entry, targetXPt, targetYPt,
                    _pages[targetPage], _pages[ResolveFlowPageIndex(image, section)]);
                return;
            }

            // Абзаца на этой странице нет: лист держала сама привязка. Место в потоке
            // менять не на что, оно остаётся прежним — а плавающая картинка без
            // привязки отсчитывается от страницы СВОЕГО МЕСТА В ПОТОКЕ, а не от той,
            // где она видна (см. RebuildPageModePass: fy = pageYPt + mt + OffsetYPt).
            // Считать смещения от видимой страницы здесь нельзя: раскладка прибавит
            // их к другой странице, и картинка прыгнет ровно на столько листов, на
            // сколько её увела привязка. Лист под ней при этом не исчезает — его
            // достроит проход по картинкам, которым нужна своя страница.
            ApplyUnpinnedOffsets(image, entry, targetXPt, targetYPt,
                _pages[targetPage], _pages[ResolveFlowPageIndex(image, section)]);
        }

        /// <summary>
        /// Пересчитывает смещения открепляемой картинки.
        ///
        /// Положение задаётся на ЛИСТЕ, над которым картинка находится физически
        /// (<paramref name="visiblePage"/>): если её увели в серое поле за границу
        /// страницы, она прижимается к ближайшему краю этого листа, иначе остаётся
        /// ровно на месте. И только потом результат переводится в смещения от той
        /// страницы, от которой их будет отсчитывать раскладка
        /// (<paramref name="flowPage"/>) — а это может быть совсем другой лист, если
        /// на видимом нет ни одного абзаца и место в потоке осталось прежним.
        ///
        /// Ограничивать сами смещения нельзя: у картинки, уведённой за несколько
        /// страниц от своего места в потоке, они законно велики, и обрезка по размеру
        /// одного листа отбрасывала её на страницы назад.
        /// </summary>
        private static void ApplyUnpinnedOffsets(
            ImageBlock image, ImageEntry entry,
            float targetXPt, float targetYPt, PageRect visiblePage, PageRect flowPage)
        {
            float left = visiblePage.PadLeftPt;
            float top = visiblePage.Ypt;
            float right = left + visiblePage.WidthPt;
            float bottom = top + visiblePage.HeightPt;

            // Край в край, без запаса: положение меняется только если картинка реально
            // вышла за лист. Целиком не помещается — прижимаем к левому/верхнему краю.
            float maxX = right - entry.WidthPt;
            float maxY = bottom - entry.HeightPt;
            if (maxX < left) maxX = left;
            if (maxY < top) maxY = top;

            float docX = Math.Clamp(targetXPt, left, maxX);
            float docY = Math.Clamp(targetYPt, top, maxY);

            image.OffsetXPt = docX - (flowPage.PadLeftPt + flowPage.MarginLeftPt);
            image.OffsetYPt = docY - (flowPage.Ypt + flowPage.PadTopPt);
        }

        /// <summary>
        /// Страница, над которой картинка находится визуально. Берётся лист, накрывающий
        /// её центр; если центр не попал ни на один (картинка в межстраничном поле или
        /// свисает за край), возвращается ближайший по расстоянию до центра.
        ///
        /// Нужна при откреплении: закреплённую картинку можно увести на соседний лист,
        /// и запомненный за ней номер страницы перестаёт описывать то, что видит глаз.
        /// </summary>
        private (int Page, float XPt, float YPt) ResolveVisiblePlacement(ImageEntry entry)
        {
            if (_pages.Count == 0) return (entry.PageIndex, entry.XPt, entry.Ypt);

            // Раскладка держит страницы строго друг под другом: у всех листов один и тот
            // же диапазон X. В несколько колонок их расставляет только отрисовка, поэтому
            // «увести картинку на соседний лист справа» в координатах документа выглядит
            // как «уехать за правый край своего». Определять лист по этим координатам
            // бессмысленно — сначала переводим положение картинки в экранное, там сетка
            // уже собрана, и лист под картинкой виден так же, как его видит глаз.
            int srcPage = entry.PageIndex >= 0 && entry.PageIndex < _pages.Count
                ? entry.PageIndex : 0;

            var (srcDx, srcDy) = PageVisualDelta(srcPage, _pages);
            float visX = entry.XPt + srcDx;
            float visY = entry.Ypt + srcDy;
            float visCx = visX + entry.WidthPt / 2f;
            float visCy = visY + entry.HeightPt / 2f;

            int nearest = -1;
            float nearestDist = float.MaxValue;
            float nearestDx = 0f, nearestDy = 0f;

            for (int i = 0; i < _pages.Count; i++)
            {
                var pg = _pages[i];
                var (dx, dy) = PageVisualDelta(i, _pages);

                float left = pg.PadLeftPt + dx;
                float right = left + pg.WidthPt;
                float top = pg.Ypt + dy;
                float bottom = top + pg.HeightPt;

                if (visCx >= left && visCx <= right && visCy >= top && visCy <= bottom)
                    return (i, visX - dx, visY - dy);

                // Расстояние от центра до листа: ноль по той оси, вдоль которой центр
                // уже внутри его границ. Нужно, когда картинка зависла в поле между
                // листами — тогда берётся ближайший.
                float ddx = visCx < left ? left - visCx : (visCx > right ? visCx - right : 0f);
                float ddy = visCy < top ? top - visCy : (visCy > bottom ? visCy - bottom : 0f);
                float dist = ddx * ddx + ddy * ddy;

                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = i;
                    nearestDx = dx;
                    nearestDy = dy;
                }
            }

            return nearest >= 0
                ? (nearest, visX - nearestDx, visY - nearestDy)
                : (entry.PageIndex, entry.XPt, entry.Ypt);
        }

        /// <summary>
        /// Страница, на которой поток дойдёт до места этого блока: страница последнего
        /// абзаца перед ним. Плавающая картинка без привязки отсчитывает свои смещения
        /// именно от неё. Абзац может быть разорван по страницам — берём последнюю его
        /// запись, поток продолжается там, где абзац закончился.
        /// </summary>
        private int ResolveFlowPageIndex(ImageBlock image, SectionModel section)
        {
            int blockIdx = section.Blocks.IndexOf(image);
            if (blockIdx <= 0) return 0;

            for (int i = blockIdx - 1; i >= 0; i--)
            {
                if (section.Blocks[i] is not ParagraphBlock previous) continue;

                int pageIdx = -1;
                foreach (var pl in _layouts)
                {
                    if (pl.Cell is not null) continue;
                    if (!ReferenceEquals(pl.Vm?.Model, previous)) continue;
                    pageIdx = pl.PageIndex;
                }

                if (pageIdx >= 0 && pageIdx < _pages.Count) return pageIdx;
            }

            return 0;
        }

        private int? GetSelectedImagePinnedPage() => _selectedImage?.PinnedPage;

        /// <summary>Номер страницы (1-based), на которой картинка сейчас лежит.</summary>
        private int? GetSelectedImageCurrentPage()
        {
            if (_selectedImage is null) return null;
            foreach (var ie in _images)
                if (ReferenceEquals(ie.Block, _selectedImage))
                    return ie.PageIndex + 1;
            return null;
        }

        // Включает/выключает блокировку пропорций выделенной картинки.
        private void SetSelectedImageLockAspect(bool locked)
        {
            if (_selectedImage is null || _selectedImage.LockAspectRatio == locked) return;
            BeginImageEdit("Пропорции изображения");
            _selectedImage.LockAspectRatio = locked;
            CommitImageEdit();
            InvalidateFull();
        }

        // Удаляет выделенную картинку (команда контекстной вкладки).
        private void DeleteSelectedImageFromCanvas()
        {
            if (_selectedImage is null) return;
            var img = _selectedImage;
            ExitImageCropMode(apply: false);
            _selectedImage = null;
            DocVm?.RemoveImage(img);
            ImageSelectionChanged?.Invoke(false);
            InvalidateFull();
        }

        // Текущие параметры выделенной картинки для синхронизации вкладки (или null).
        private (WrapMode Wrap, bool LockAspect, Writersword.Modules.TextEditor.Models.Styles.TextAlignment Align)? GetSelectedImageInfo()
            => _selectedImage is null
                ? null
                : (_selectedImage.WrapMode, _selectedImage.LockAspectRatio, _selectedImage.Alignment);

        // Задаёт ширину выделенной картинки в пунктах. При включённых пропорциях
        // высота масштабируется тем же коэффициентом.
        private void SetSelectedImageWidth(double widthPt)
        {
            if (_selectedImage is null) return;
            double w = Math.Max(widthPt, 4.0);
            if (Math.Abs(_selectedImage.WidthPt - w) < 0.01) return;
            BeginImageEdit("Размер изображения");
            if (_selectedImage.LockAspectRatio && _selectedImage.WidthPt > 0.0)
                _selectedImage.HeightPt = Math.Max(4.0, _selectedImage.HeightPt * (w / _selectedImage.WidthPt));
            _selectedImage.WidthPt = w;
            CommitImageEdit();
            InvalidateInlineImageLayout(_selectedImage);
            RebuildLayouts();
            InvalidateMeasure();
            InvalidateFull();
            ImageSelectionChanged?.Invoke(true);
        }

        // Задаёт высоту выделенной картинки в пунктах. При включённых пропорциях
        // ширина масштабируется тем же коэффициентом.
        private void SetSelectedImageHeight(double heightPt)
        {
            if (_selectedImage is null) return;
            double h = Math.Max(heightPt, 4.0);
            if (Math.Abs(_selectedImage.HeightPt - h) < 0.01) return;
            BeginImageEdit("Размер изображения");
            if (_selectedImage.LockAspectRatio && _selectedImage.HeightPt > 0.0)
                _selectedImage.WidthPt = Math.Max(4.0, _selectedImage.WidthPt * (h / _selectedImage.HeightPt));
            _selectedImage.HeightPt = h;
            CommitImageEdit();
            InvalidateInlineImageLayout(_selectedImage);
            RebuildLayouts();
            InvalidateMeasure();
            InvalidateFull();
            ImageSelectionChanged?.Invoke(true);
        }

        // Задаёт непрозрачность выделенной картинки (0..1).
        private void SetSelectedImageOpacity(double opacity)
        {
            if (_selectedImage is null) return;
            double o = Math.Clamp(opacity, 0.0, 1.0);
            if (Math.Abs(_selectedImage.Opacity - o) < 0.001) return;
            BeginImageEdit("Прозрачность изображения");
            _selectedImage.Opacity = o;
            CommitImageEdit();
            InvalidateFull();
        }

        // Задаёт рамку выделенной картинки: цвет в hex и толщину в пунктах.
        // null или полностью прозрачный цвет — рамка убирается.
        private void SetSelectedImageBorder(string? colorHex, double thicknessPt)
        {
            if (_selectedImage is null) return;
            string? color = NormalizeBorderColor(colorHex);
            double thick = Math.Clamp(thicknessPt, 0.0, 50.0);
            // Толщина без цвета рисовала «невидимую» рамку — пользователь менял
            // число, а на листе ничего не появлялось. Ненулевая толщина всегда даёт
            // видимую рамку: цвет по умолчанию — чёрный.
            if (thick > 0.0 && color is null) color = "#000000";
            _logger.Debug("[IMG] border request color={C} thick={T}", color ?? "none", thick);
            if (_selectedImage.BorderColor == color
                && Math.Abs(_selectedImage.BorderThicknessPt - thick) < 0.01) return;
            BeginImageEdit("Рамка изображения");
            _selectedImage.BorderColor = color;
            _selectedImage.BorderThicknessPt = thick;
            CommitImageEdit();
            InvalidateFull();
        }

        // Приводит код цвета рамки к сплошному hex, понятному SKColor.TryParse.
        // Палитра умеет отдавать код градиента ("grad|...") — из него берётся
        // первый стоп. Пустой и полностью прозрачный цвет означают «рамки нет».
        private static string? NormalizeBorderColor(string? colorHex)
        {
            if (string.IsNullOrWhiteSpace(colorHex)) return null;
            string raw = colorHex.Trim();
            if (string.Equals(raw, "#00000000", StringComparison.OrdinalIgnoreCase)) return null;

            string hex = raw;
            try
            {
                var spec = Writersword.Core.Models.Project.GradientSpec.Parse(raw);
                if (!string.IsNullOrWhiteSpace(spec?.SolidHex)) hex = spec!.SolidHex.Trim();
            }
            catch
            {
                hex = raw;
            }

            if (string.Equals(hex, "#00000000", StringComparison.OrdinalIgnoreCase)) return null;
            return SKColor.TryParse(hex, out var parsed) && parsed.Alpha > 0 ? hex : null;
        }

        // Переключает зеркальное отражение выделенной картинки по горизонтали.
        private void ToggleSelectedImageFlipHorizontal()
        {
            if (_selectedImage is null) return;
            BeginImageEdit("Отражение изображения");
            _selectedImage.FlipHorizontal = !_selectedImage.FlipHorizontal;
            CommitImageEdit();
            InvalidateFull();
        }

        // Переключает зеркальное отражение выделенной картинки по вертикали.
        private void ToggleSelectedImageFlipVertical()
        {
            if (_selectedImage is null) return;
            BeginImageEdit("Отражение изображения");
            _selectedImage.FlipVertical = !_selectedImage.FlipVertical;
            CommitImageEdit();
            InvalidateFull();
        }

        // Включает/выключает режим обрезки выделенной картинки. Выключение
        // применяет накопленную рамку кадрирования к картинке.
        private void SetSelectedImageCropMode(bool on)
        {
            bool next = on && _selectedImage is not null;
            if (_imageCropMode == next) return;
            if (next) EnterImageCropMode();
            else ExitImageCropMode(apply: true);
        }

        // Входит в режим обрезки: запоминает исходные доли среза и полный размер
        // картинки, от которого считается рамка кадрирования.
        private void EnterImageCropMode()
        {
            if (_selectedImage is null) return;
            var img = _selectedImage;

            double visWFrac = Math.Max(1.0 - img.CropLeftFrac - img.CropRightFrac, 0.01);
            double visHFrac = Math.Max(1.0 - img.CropTopFrac - img.CropBottomFrac, 0.01);

            _cropImage = img;
            _cropFullWPt = img.WidthPt / visWFrac;
            _cropFullHPt = img.HeightPt / visHFrac;
            _cropPendLeft = img.CropLeftFrac;
            _cropPendTop = img.CropTopFrac;
            _cropPendRight = img.CropRightFrac;
            _cropPendBottom = img.CropBottomFrac;
            _imageCropMode = true;
            _imageCropDragging = false;
            InvalidateFull();
        }

        /// <summary>
        /// Выходит из режима обрезки. apply = true — накопленная рамка применяется
        /// к картинке одной undo-операцией, false — изменения отбрасываются.
        /// </summary>
        private void ExitImageCropMode(bool apply)
        {
            if (!_imageCropMode)
            {
                _cropImage = null;
                return;
            }

            var img = _cropImage;
            _imageCropMode = false;
            _imageCropDragging = false;
            _cropImage = null;

            if (apply && img is not null) ApplyPendingCrop(img);
            else InvalidateFull();
        }

        // Переносит отложенную рамку кадрирования на картинку: доли среза, новый
        // видимый размер и (для плавающей картинки) сдвиг якоря, чтобы видимая
        // область осталась ровно там, где её показывал предпросмотр.
        private void ApplyPendingCrop(ImageBlock img)
        {
            double pl = Math.Clamp(_cropPendLeft, 0.0, 0.95);
            double pt = Math.Clamp(_cropPendTop, 0.0, 0.95);
            double pr = Math.Clamp(_cropPendRight, 0.0, 0.95);
            double pb = Math.Clamp(_cropPendBottom, 0.0, 0.95);

            double newW = _cropFullWPt * Math.Max(1.0 - pl - pr, 0.01);
            double newH = _cropFullHPt * Math.Max(1.0 - pt - pb, 0.01);

            bool changed = Math.Abs(img.CropLeftFrac - pl) > 0.0005
                || Math.Abs(img.CropTopFrac - pt) > 0.0005
                || Math.Abs(img.CropRightFrac - pr) > 0.0005
                || Math.Abs(img.CropBottomFrac - pb) > 0.0005;
            if (!changed)
            {
                InvalidateFull();
                return;
            }

            BeginImageEdit(img, "Обрезка изображения");
            if (img.WrapMode != WrapMode.Inline)
            {
                img.OffsetXPt += _cropFullWPt * (pl - img.CropLeftFrac);
                img.OffsetYPt += _cropFullHPt * (pt - img.CropTopFrac);
            }
            img.CropLeftFrac = pl;
            img.CropTopFrac = pt;
            img.CropRightFrac = pr;
            img.CropBottomFrac = pb;
            img.WidthPt = newW;
            img.HeightPt = newH;
            CommitImageEdit();

            RebuildLayouts();
            InvalidateMeasure();
            InvalidateFull();
            ImageSelectionChanged?.Invoke(_selectedImage is not null);
        }

        /// <summary>
        /// Прямоугольники предпросмотра обрезки для записи раскладки: full — исходная
        /// картинка целиком, pending — текущая рамка кадрирования. false — запись
        /// принадлежит не обрезаемой картинке или режим обрезки выключен.
        /// </summary>
        private bool TryGetCropRects(ImageEntry ie, out SKRect full, out SKRect pending)
        {
            full = default;
            pending = default;
            if (!_imageCropMode || _cropImage is null) return false;
            if (!ReferenceEquals(ie.Block, _cropImage)) return false;
            if (_cropFullWPt <= 0.0 || _cropFullHPt <= 0.0) return false;

            float fullW = (float)_cropFullWPt;
            float fullH = (float)_cropFullHPt;
            float fullX = ie.XPt - fullW * (float)ie.Block.CropLeftFrac;
            float fullY = ie.Ypt - fullH * (float)ie.Block.CropTopFrac;

            full = new SKRect(fullX, fullY, fullX + fullW, fullY + fullH);
            pending = new SKRect(
                fullX + fullW * (float)_cropPendLeft,
                fullY + fullH * (float)_cropPendTop,
                fullX + fullW * (float)(1.0 - _cropPendRight),
                fullY + fullH * (float)(1.0 - _cropPendBottom));
            return true;
        }

        // Текущее состояние режима обрезки для синхронизации вкладки.
        private bool GetSelectedImageCropMode() => _imageCropMode;

        // Геометрия и оформление выделенной картинки для полей вкладки (или null).
        private (double WidthPt, double HeightPt, double Opacity, string? BorderColor, double BorderThicknessPt)? GetSelectedImageStyle()
            => _selectedImage is null
                ? null
                : (_selectedImage.WidthPt, _selectedImage.HeightPt, _selectedImage.Opacity,
                   _selectedImage.BorderColor, _selectedImage.BorderThicknessPt);

        // Задаёт угол поворота выделенной картинки (команда контекстной вкладки).
        private void SetSelectedImageRotation(double degrees)
        {
            if (_selectedImage is null) return;
            double normalized = ((degrees % 360.0) + 360.0) % 360.0;
            if (Math.Abs(_selectedImage.RotationDeg - normalized) < 0.01) return;
            BeginImageEdit("Поворот изображения");
            _selectedImage.RotationDeg = normalized;
            CommitImageEdit();
            InvalidateInlineImageLayout(_selectedImage);
            RebuildLayouts();
            InvalidateMeasure();
            InvalidateFull();
        }

        // Текущий угол поворота выделенной картинки для вкладки (или null).
        private double? GetSelectedImageRotation()
            => _selectedImage?.RotationDeg;

        // Задаёт отступы обтекания выделенной картинки (по 4 сторонам, в пунктах).
        private void SetSelectedImageWrapPadding(double topPt, double bottomPt, double leftPt, double rightPt)
        {
            if (_selectedImage is null) return;
            topPt = Math.Max(0.0, topPt);
            bottomPt = Math.Max(0.0, bottomPt);
            leftPt = Math.Max(0.0, leftPt);
            rightPt = Math.Max(0.0, rightPt);
            if (Math.Abs(_selectedImage.WrapPadTopPt - topPt) < 0.01
                && Math.Abs(_selectedImage.WrapPadBottomPt - bottomPt) < 0.01
                && Math.Abs(_selectedImage.WrapPadLeftPt - leftPt) < 0.01
                && Math.Abs(_selectedImage.WrapPadRightPt - rightPt) < 0.01)
                return;
            BeginImageEdit("Отступы обтекания");
            _selectedImage.WrapPadTopPt = topPt;
            _selectedImage.WrapPadBottomPt = bottomPt;
            _selectedImage.WrapPadLeftPt = leftPt;
            _selectedImage.WrapPadRightPt = rightPt;
            CommitImageEdit();
            RebuildLayouts();
            InvalidateFull();
        }

        // Текущие отступы обтекания выделенной картинки (top, bottom, left, right) в пунктах, или null.
        private (double TopPt, double BottomPt, double LeftPt, double RightPt)? GetSelectedImageWrapPadding()
            => _selectedImage is null
                ? null
                : (_selectedImage.WrapPadTopPt, _selectedImage.WrapPadBottomPt,
                   _selectedImage.WrapPadLeftPt, _selectedImage.WrapPadRightPt);

        // Структурное изменение (вставка/удаление картинки и т.п.): пересобираем раскладку
        // БЕЗ очистки кэша абзацев — текст абзацев не менялся, переформировывать их не нужно,
        // поэтому операция быстрая даже на большом документе.
        private void OnStructureChanged()
        {
            RebuildLayouts();
            _caretLineHint = -1;
            SnapCaretToCorrectSlice();
            InvalidateFull();
        }

        private void OnParagraphFormatChanged()
        {
            // При изменении форматирования (шрифт, размер, цвет и т.п.) текст параграфа
            // не меняется, поэтому _layoutCache не инвалидируется автоматически — чистим явно.
            // Берём список затронутых абзацев: если он есть — инвалидируем кэш ТОЛЬКО у них,
            // а раскладки остальных (на больших документах — тысячи) остаются валидными и
            // переиспользуются при RebuildLayouts. Это убирает полный пересбор всего документа
            // через Skia на каждый коммит форматирования.
            var affected = DocVm?.TakeLastFormatAffected();
            if (affected is { Count: > 0 })
            {
                foreach (var pvm in affected)
                    _layoutCache.Remove(pvm);
            }
            else
            {
                // Затронутые неизвестны (например, форматирование ячейки) — полный сброс.
                _layoutCache.Clear();
                InvalidateCellLayoutCaches();
            }

            RebuildLayouts();
            // Подсказка строки каретки могла устареть: при смене форматирования (шрифт,
            // размер) абзац перетекает по строкам иначе. Сбрасываем, иначе DrawCaret
            // нарисует каретку на старой строке.
            _caretLineHint = -1;
            SnapCaretToCorrectSlice();
            UpdatePreferredX();

            // Если каретка в таблице — обновляем маркеры линейки.
            // Без этого после смены LeftIndentPt или ширины колонки линейка
            // показывает старые позиции и следующий drag считается от них.
            if (_activeTableBlock is not null)
                NotifyCaretEnteredTableCallback();

            // Раскладка пересобрана — отступы могли поменяться и без движения каретки.
            PublishRulerGeometry();

            InvalidateFull();
        }

        private double _lastZoom = 1.0;

        // На больших документах RebuildLayouts (пагинация всех абзацев) слишком тяжёл, чтобы
        // гонять его на каждый шаг зума. Во время жеста масштабирования рендерим уже посчитанную
        // раскладку, лишь масштабируя её, а полный пересчёт делаем один раз после остановки
        // (debounce). _zooming на это время заставляет Measure/Arrange пропускать RebuildLayouts.
        // Флаг гарантированно сбрасывается таймером и принудительно на любом вводе (см.
        // FinishZoomImmediately), поэтому залипнуть и заблокировать отрисовку/undo не может.
        private bool _zooming;
        private DispatcherTimer? _zoomSettleTimer;

        private void OnDocVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Изменение масштаба. Ключевое: НЕ чистим кэш раскладки. В режиме страниц ширина
            // текста от зума не зависит (она равна ширине страницы), поэтому кэш абзацев валиден,
            // и RebuildLayouts только пере-позиционирует страницы (центрирование pageXPt) и берёт
            // абзацы из кэша — это дёшево. Чистка кэша заставляла бы перелейаутить все абзацы на
            // каждый шаг зума (фриз на больших документах). Текст рисуется векторно с масштабом,
            // поэтому пере-растеризация не нужна.
            if (e.PropertyName == nameof(DocumentViewModel.Zoom))
            {
                // Скролл при зуме НЕ трогаем. Горизонтально лист центрирует рендер (сдвиг по
                // живому _canvasWidth). Вертикально — контент просто масштабируется от текущей
                // прокрутки. Раньше тут синхронно ставилось вертикальное смещение, но ScrollViewer
                // ещё не знал новую высоту контента (она обновляется в Measure следующим кадром),
                // поэтому на увеличении смещение обрезалось по старой высоте: на один кадр текст
                // прыгал вниз и сверху мелькала пустота. Без этой привязки мерцания нет.
                _lastZoom = Zoom;

                // Тяжёлый RebuildLayouts (пагинация всех абзацев) откладываем: во время жеста
                // Measure/Arrange его пропускают (флаг _zooming), рендерится посчитанное ранее,
                // масштабированное под новый зум. Полный пересчёт — после остановки.
                _zooming = true;
                InvalidateMeasure();
                InvalidateVisual();
                ScheduleZoomSettle();
                return;
            }

            if (e.PropertyName is nameof(DocumentViewModel.ViewMode)
                               or nameof(DocumentViewModel.ReadingFlow)
                               or nameof(DocumentViewModel.IsSpreadReading)
                               or nameof(DocumentViewModel.PageSettings))
            {
                if (DocVm is not null)
                    _styleResolver = CreateStyleResolver();

                // Смена подачи чтения меняет размер листа, а значит и всю раскладку:
                // кэш абзацев считан под прежнюю ширину и целиком недействителен.
                ResetSpreadState();
                _layoutCache.Clear();
                InvalidateCellLayoutCaches();
                RebuildLayouts();

                _lastZoom = Zoom;
                InvalidateMeasure();
                InvalidateFull();
                return;
            }

            // Смена числа страниц в ряду: раскладка не меняется, но высота канваса
            // и визуальные позиции страниц другие — пересбор дёшев (кеш абзацев тёплый).
            if (e.PropertyName == nameof(DocumentViewModel.PagesPerRow) && DocVm is not null)
            {
                _pagesPerRowSetting = DocVm.PagesPerRow;
                UpdateEffectivePagesPerRow();
                RebuildLayouts();
                InvalidateMeasure();
                InvalidateFull();
            }
        }

        private void ScheduleZoomSettle()
        {
            if (_zoomSettleTimer is null)
            {
                _zoomSettleTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(140)
                };
                _zoomSettleTimer.Tick += (_, _) => FinishZoomImmediately();
            }
            _zoomSettleTimer.Stop();
            _zoomSettleTimer.Start();
        }

        private void FinishZoomImmediately()
        {
            _zoomSettleTimer?.Stop();
            if (!_zooming) return;
            _zooming = false;
            RebuildLayouts();
            InvalidateMeasure();
            InvalidateFull();
        }

        private void OnParagraphsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems is not null)
                foreach (ParagraphViewModel pvm in e.NewItems) WirePvm(pvm);

            if (e.OldItems is not null)
                foreach (ParagraphViewModel pvm in e.OldItems)
                {
                    UnwirePvm(pvm);
                    _layoutCache.Remove(pvm);
                }

            int dirtyIdx = 0;
            if (e.NewItems is not null && e.NewStartingIndex >= 0)
                dirtyIdx = e.NewStartingIndex;
            else if (e.OldItems is not null && e.OldStartingIndex >= 0)
                dirtyIdx = Math.Max(0, e.OldStartingIndex - 1);

            ScheduleRebuild(dirtyIdx);
        }

        private void WirePvm(ParagraphViewModel pvm)
        {
            pvm.PropertyChanged += OnPvmPropertyChanged;

            // Сохраняем лямбду чтобы точно отписать в UnwirePvm.
            // Анонимную лямбду нельзя отписать через -= без сохранённой ссылки.
            Action handler = () => OnPvmFocusRequested(pvm);
            _pvmFocusHandlers[pvm] = handler;
            pvm.FocusRequested += handler;

            pvm.RequestFocusAtPosition = pos => OnPvmRequestFocusAtPosition(pvm, pos);
        }

        private void OnPvmFocusRequested(ParagraphViewModel pvm)
        {
            if (DocVm is null) return;
            int idx = DocVm.Paragraphs.IndexOf(pvm);
            if (idx < 0) return;
            _caretPara = FindFirstSliceForDocVmParagraph(idx);
            _caretChar = pvm.PlainText?.Length ?? 0;
            NotifyLeftCell();
            SnapCaretToCorrectSlice();
            UpdatePreferredX();
            SyncSel(); ResetCaret(); InvalidateVisual();
        }

        private void OnPvmRequestFocusAtPosition(ParagraphViewModel pvm, int pos)
        {
            if (DocVm is null) return;
            int idx = DocVm.Paragraphs.IndexOf(pvm);
            if (idx < 0) return;
            _caretPara = FindFirstSliceForDocVmParagraph(idx);
            _caretChar = Clamp(pos, 0, pvm.PlainText?.Length ?? 0);
            NotifyLeftCell();
            SnapCaretToCorrectSlice();
            UpdatePreferredX();
            SyncSel(); ResetCaret(); InvalidateVisual();
        }

        private void UnwirePvm(ParagraphViewModel pvm)
        {
            pvm.PropertyChanged -= OnPvmPropertyChanged;

            if (_pvmFocusHandlers.TryGetValue(pvm, out var focusHandler))
            {
                pvm.FocusRequested -= focusHandler;
                _pvmFocusHandlers.Remove(pvm);
            }

            pvm.RequestFocusAtPosition = null;
        }

        private void OnPvmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ParagraphViewModel.PlainText)) return;
            if (sender is ParagraphViewModel pvm && DocVm is not null)
            {
                int idx = DocVm.Paragraphs.IndexOf(pvm);
                if (idx >= 0) { ScheduleRebuild(idx); return; }
            }
            ScheduleRebuild(0);
        }

        // ── Дебаунс пересчёта ─────────────────────────────────────────────
        private void ScheduleRebuild(int dirtyParaIdx)
        {
            if (DocVm?.IsBulkRebuilding != true)
            {
                ParagraphViewModel? dirtyPvm = null;
                if (DocVm is not null && dirtyParaIdx >= 0 && dirtyParaIdx < DocVm.Paragraphs.Count)
                {
                    dirtyPvm = DocVm.Paragraphs[dirtyParaIdx];
                    _layoutCache.Remove(dirtyPvm);

                    int sliceCount = _layouts.Count(l => l.Vm == dirtyPvm && l.Cell is null);
                    if (sliceCount == 1)
                    {
                        // Быстрый путь для редактирования: обновляем только один параграф.
                        QuickUpdateParagraphLayout(dirtyPvm);
                    }
                    else if (sliceCount == 0)
                    {
                        // Новый параграф (Enter): вставляем в _layouts с оценочной высотой
                        // чтобы ScrollToCaret мог найти его позицию немедленно.
                        QuickInsertParagraphLayout(dirtyParaIdx, dirtyPvm);
                    }
                    // sliceCount > 1: пропускаем быстрый путь, полный пересчёт ниже.
                }
            }

            var oldCts = _rebuildCts;
            _rebuildCts = new System.Threading.CancellationTokenSource();
            oldCts.Cancel();
            oldCts.Dispose();
            var cts = _rebuildCts;

            InvalidateFull();

            Dispatcher.UIThread.Post(() =>
            {
                if (cts.IsCancellationRequested) return;

                double oldCanvasH = _canvasHeight;
                RebuildLayouts();
                SnapCaretToCorrectSlice(); 

                if (_pendingFocusBlock is not null && DocVm is not null)
                {
                    var anchorVm = DocVm.Paragraphs.FirstOrDefault(p => p.Model == _pendingFocusBlock);
                    _pendingFocusBlock = null;
                    if (anchorVm is not null)
                    {
                        int pvmIdx = DocVm.Paragraphs.IndexOf(anchorVm);
                        _caretPara = FindFirstSliceForDocVmParagraph(pvmIdx);
                        _caretChar = 0;
                        NotifyLeftCell();
                        SnapCaretToCorrectSlice();
                        UpdatePreferredX();
                        SyncSel();
                        _caretVisible = true;
                        _caretTimer.Stop();
                        _caretTimer.Start();
                        if (_caretPara >= 0 && _caretPara < _layouts.Count)
                            CaretPageChanged?.Invoke(_layouts[_caretPara].PageIndex);
                        ScrollToCenterCaret();
                    }
                }

                if (Math.Abs(_canvasHeight - oldCanvasH) > 0.5)
                    InvalidateMeasure();
                else
                    InvalidateFull();

                // После полного rebuild _layouts актуален — прокручиваем к каретке
                // Нужно при Enter: ResetCaret вызывается до rebuild, каретка вне _layouts
                ScrollToCaret();

            }, DispatcherPriority.Background);
        }

        // ── Measure / Layout ──────────────────────────────────────────────
        protected override Size MeasureOverride(Size available)
        {
            double availW = double.IsInfinity(available.Width) ? 800 : Math.Max(available.Width, 1);
            double viewportW = _parentScrollViewer?.Viewport.Width > 0
                ? _parentScrollViewer.Viewport.Width : availW;

            // Ширина вьюпорта запоминается ДО чтения масштаба: в чтении масштаб
            // вписывает книгу в окно и считается по этой самой ширине.
            _readingViewportWidthPx = Math.Max(viewportW, 1);

            double zoom = Zoom;
            _canvasWidth = Math.Max(viewportW / zoom, 1);

            // Авто-режим страниц в ряду зависит от ширины канваса и масштаба — обе
            // величины только что посчитаны, здесь и пересчитываем.
            UpdateEffectivePagesPerRow();

            if (_styleResolver is null && DocVm is not null)
                _styleResolver = CreateStyleResolver();

            // Пересчёт раскладки выполняется только если отпечаток не совпал:
            // measure вызывается при каждом переприкреплении вьюхи (переключение
            // вкладок и воркмодов) и после каждого InvalidateMeasure, а полный проход
            // пагинации большого документа занимает секунды даже с тёплым кешем
            // лейаутов. Все содержательные изменения (ввод, форматирование, таблицы,
            // смена документа, ширины, режима, стилей) идут через прямые вызовы
            // RebuildLayouts либо меняют поля отпечатка — пропуск безопасен.
            if (!_zooming && !LayoutsMatchCurrentState())
            {
                // Холодный кеш большого документа: синхронный пересчёт зашейпил бы
                // тысячи абзацев и заблокировал UI-поток на секунды. Вместо этого
                // запускается порционный прогрев кеша (PumpLayoutWarmup): абзацы
                // шейпятся с бюджетом времени на проход диспетчера, UI остаётся
                // отзывчивым, а полный пересчёт выполняется после прогрева по
                // InvalidateMeasure — уже с тёплым кешем, за десятки миллисекунд.
                if (ShouldWarmupBeforeRebuild())
                    StartLayoutWarmup();
                else
                    RebuildLayouts();
            }

            double visualH = Math.Max(_canvasHeight * zoom, 100);
            double visualW = availW;

            if (DocVm?.ViewMode == EditorViewMode.Page)
            {
                // В режиме страниц рядом ширина канваса вмещает все колонки с зазорами.
                double pagesWPt = GetPageWidthPt() * _pagesPerRow
                    + PageGapPt * 2.0 * (_pagesPerRow - 1);
                visualW = Math.Max(availW,
                    pagesWPt * PtToPx * zoom + PageGapPt * PtToPx * 4);
            }

            return new Size(visualW, visualH);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double viewportW = _parentScrollViewer?.Viewport.Width > 0
                ? _parentScrollViewer.Viewport.Width : finalSize.Width;

            // Как и в measure: ширина вьюпорта нужна масштабу вписывания, поэтому
            // запоминается до его чтения.
            _readingViewportWidthPx = Math.Max(viewportW, 1);

            double zoom = Zoom;
            double logicalW = Math.Max(viewportW / zoom, 1);

            // При изменении ширины канваса обновляем _canvasWidth. В режиме страниц это влияет
            // только на центрирование страниц, в режиме потока — на ширину текста (см. ниже).
            // Во время зум-жеста пересчёт пропускаем — его сделает FinishZoomImmediately.
            if (!_zooming && Math.Abs(logicalW - _canvasWidth) > 0.5)
            {
                _canvasWidth = logicalW;

                // В книге ширина холста — это только место, в котором она стоит:
                // ширину текста задаёт виртуальный лист, а его размер считается по
                // вьюпорту. Пересобирать раскладку и тем более чистить кеш абзацев
                // здесь нельзя — приближение книги меняет ширину холста на каждом
                // шаге, и каждый шаг стоил бы полной пагинации документа.
                if (DocVm?.IsSpreadReading == true)
                {
                    InvalidateFull();
                    return new Size(finalSize.Width, Math.Max(_canvasHeight * zoom, 100));
                }

                // В режиме страниц ширина текста равна ширине страницы и от logicalW (а значит и
                // от зума) не зависит — кэш абзацев валиден, чистить его не нужно, иначе на зуме
                // перелейаутился бы весь документ. RebuildLayouts только пере-центрирует страницы.
                // В режиме потока ширина текста = logicalW, поэтому при её изменении нужен рефлоу.
                if (DocVm?.ViewMode != EditorViewMode.Page)
                {
                    _layoutCache.Clear();
                    InvalidateCellLayoutCaches();
                }
                RebuildLayouts();
            }

            return new Size(finalSize.Width, Math.Max(_canvasHeight * zoom, 100));
        }

        // ── Пересчёт лейаута ──────────────────────────────────────────────

        // Отпечаток состояния, для которого построены _layouts/_pages/_tables.
        // Обновляется в конце RebuildLayouts; MeasureOverride сравнивает его с текущим
        // состоянием и пропускает полный пересчёт при совпадении. Смена документа
        // создаёт новый DocumentViewModel (LoadDocument), смена карты шрифтов — новый
        // StyleResolver (сеттер ScriptFontMap), поэтому оба случая ловятся сравнением
        // ссылок. Ширина и режим отображения сравниваются по значению.
        private object? _layoutsFingerprintDocVm;
        private object? _layoutsFingerprintParagraphs;
        private object? _layoutsFingerprintStyleResolver;
        private int _layoutsFingerprintParagraphCount = -1;
        private double _layoutsFingerprintWidth = double.NaN;
        private EditorViewMode _layoutsFingerprintViewMode = (EditorViewMode)(-1);

        // Подача чтения входит в отпечаток: разворот и лента верстаются по-разному.
        private bool _layoutsFingerprintSpread;

        /// <summary>
        /// Возвращает true если текущая раскладка (_layouts/_pages/_tables) построена
        /// ровно для текущего состояния канваса и полный пересчёт в measure не нужен.
        /// </summary>
        private bool LayoutsMatchCurrentState()
        {
            if (DocVm is null) return false;

            return _layouts.Count > 0
                && ReferenceEquals(_layoutsFingerprintDocVm, DocVm)
                && ReferenceEquals(_layoutsFingerprintParagraphs, DocVm.Paragraphs)
                && _layoutsFingerprintParagraphCount == DocVm.Paragraphs.Count
                && ReferenceEquals(_layoutsFingerprintStyleResolver, _styleResolver)
                && !double.IsNaN(_layoutsFingerprintWidth)
                // В книге ширина текста берётся с листа чтения, а его размер постоянен
                // и от окна не зависит вовсе. Поэтому ни ширина холста, ни размер
                // вьюпорта в отпечаток не входят: иначе сворачивание ленты и любое
                // изменение окна гнало бы полную пересборку книги.
                && (DocVm.IsSpreadReading
                    || Math.Abs(_layoutsFingerprintWidth - _canvasWidth) < 0.5)
                && _layoutsFingerprintViewMode == DocVm.ViewMode
                && _layoutsFingerprintSpread == DocVm.IsSpreadReading;
        }

        // ── Порционный прогрев кеша раскладки ─────────────────────────────
        // Холодное построение раскладки большого документа (шейпинг тысяч абзацев
        // через Skia) блокировало UI-поток на секунды при первом открытии модуля
        // в воркмоде. Прогрев шейпит абзацы порциями с бюджетом времени на проход
        // диспетчера: между проходами UI обрабатывает ввод и рендер, а полный
        // пересчёт раскладки выполняется один раз после прогрева с тёплым кешем.
        // Работа целиком на UI-потоке — гонок с вводом и моделью нет по построению.
        private bool _layoutWarmupActive;
        private const int WarmupColdThreshold = 200;
        private const int WarmupPassBudgetMs = 30;

        // Глобальный счётчик активных прогревов. Читается главным окном:
        // снапшот-оверлей вкладки (мгновенное переключение как в Chrome)
        // держится на экране, пока хоть один канвас прогревает раскладку —
        // иначе оверлей скрылся бы поверх ещё пустого канваса.
        private static int _activeWarmupCount;
        public static int ActiveWarmupCount => System.Threading.Volatile.Read(ref _activeWarmupCount);

        /// <summary>
        /// Единственная точка изменения флага прогрева — поддерживает глобальный
        /// счётчик сбалансированным при любых путях завершения (финиш, detach,
        /// потеря документа).
        /// </summary>
        private void SetWarmupActive(bool active)
        {
            if (_layoutWarmupActive == active) return;
            _layoutWarmupActive = active;
            if (active)
                System.Threading.Interlocked.Increment(ref _activeWarmupCount);
            else
                System.Threading.Interlocked.Decrement(ref _activeWarmupCount);
        }

        /// <summary>
        /// Актуальна ли кеш-запись раскладки абзаца для текущей ширины текста.
        /// Условие идентично проверке в GetOrBuildLayout: несовпадение текста или
        /// ширины означает, что абзац будет перешейплен заново.
        /// </summary>
        private bool IsLayoutCacheEntryValid(ParagraphViewModel pvm, float widthPt)
        {
            return _layoutCache.TryGetValue(pvm, out var cached)
                && cached.Text == (pvm.PlainText ?? string.Empty)
                && Math.Abs(cached.Width - widthPt) < 0.1f;
        }

        /// <summary>
        /// Возвращает true если раскладку нужно строить через прогрев: документ большой
        /// и значительная часть абзацев ещё не зашейплена (холодный кеш) либо их кеш
        /// устарел (другая ширина текста). Для тёплого кеша и маленьких документов
        /// синхронный пересчёт занимает миллисекунды и прогрев не нужен.
        /// </summary>
        private bool ShouldWarmupBeforeRebuild()
        {
            if (_layoutWarmupActive) return true;
            if (DocVm is null) return false;

            var paragraphs = DocVm.Paragraphs;
            if (paragraphs.Count < WarmupColdThreshold) return false;

            float widthPt = GetCurrentTextWidthPt();
            int uncached = 0;
            foreach (var pvm in paragraphs)
            {
                if (!IsLayoutCacheEntryValid(pvm, widthPt))
                {
                    uncached++;
                    if (uncached >= WarmupColdThreshold)
                        return true;
                }
            }
            return false;
        }

        private void StartLayoutWarmup()
        {
            if (_layoutWarmupActive) return;
            SetWarmupActive(true);
            // Один раз перед прогревом выставляем тексты маркеров списков (и чиним битые позиции),
            // чтобы раскладка учла ширину цифры уже в кэше. Раньше это делалось каждый проход.
            ApplyListMarkerTexts();
            _logger.Debug("Layout warmup started: {Count} paragraphs, cache={CacheCount}",
                DocVm?.Paragraphs.Count ?? 0, _layoutCache.Count);
            Dispatcher.UIThread.Post(PumpLayoutWarmup, DispatcherPriority.Loaded);
        }

        /// <summary>
        /// Один проход прогрева: шейпит незакешированные абзацы пока не исчерпан
        /// бюджет времени, затем перепланирует себя. Когда все абзацы зашейплены —
        /// вызывает InvalidateMeasure: пересчёт раскладки в measure пройдёт быстро,
        /// целиком из кеша. Приоритет Loaded — выше Background, проход не голодает
        /// при непрерывных layout-инвалидациях.
        /// </summary>
        // Вычисляет тексты маркеров списков (нумерацию) и кладёт их в модель, чтобы раскладка
        // могла измерить ширину цифры. Вызывается перед прогревом кэша, а также при полном
        // пересборе тексты выставляются в RebuildPageMode/FlowMode.
        private void ApplyListMarkerTexts()
        {
            if (DocVm is null) return;
            double textWidthPt = GetCurrentTextWidthPt();
            _cellListMarkers.Clear();
            foreach (var section in DocVm.Document.Sections)
            {
                var map = Rendering.ListNumberingEngine.Compute(section.Blocks);
                foreach (var block in section.Blocks)
                    if (block is ParagraphBlock p && p.ListProperties is not null)
                    {
                        p.ListProperties.ComputedMarkerText =
                            map.TryGetValue(p, out var mi) ? mi.Text : null;
                        MigrateCorruptListMarker(p, textWidthPt);
                    }

                ApplyListMarkerTextsInTables(section.Blocks, textWidthPt);
            }
        }

        /// <summary>
        /// Считает маркеры списков для абзацев внутри ячеек таблиц. Абзацы ячеек не
        /// лежат в Blocks раздела, поэтому общий проход их не видел: свойства списка
        /// у абзаца были, а текст маркера — нет, и элемент рисовался как голый
        /// отступ без номера и без буллета.
        /// Каждая ячейка считается отдельным потоком: нумерация идёт внутри ячейки
        /// и начинается заново в следующей — сквозной счёт по таблице требовал бы
        /// порядка обхода, которого у ячеек нет.
        /// </summary>
        private void ApplyListMarkerTextsInTables(
            IReadOnlyList<Models.Document.BlockModel> blocks, double textWidthPt)
        {
            foreach (var block in blocks)
            {
                if (block is not Models.Document.TableBlock table) continue;

                foreach (var cell in table.Cells)
                {
                    var cellMap = Rendering.ListNumberingEngine.Compute(cell.Paragraphs);

                    // Диагностика пропадающих маркеров: видно, у скольких абзацев
                    // ячейки есть свойства списка и скольким движок выдал маркер.
                    // Расхождение означает, что список поставлен не всем абзацам.
                    int withProps = 0;
                    foreach (var p2 in cell.Paragraphs)
                        if (p2.ListProperties is not null
                            && p2.ListProperties.MarkerType != Models.Document.ListMarkerType.None)
                            withProps++;
                    if (withProps != cellMap.Count)
                    {
                        _logger.Debug(
                            "[LIST] cell r{Row}c{Col}: list properties on {Props} paragraphs, {Markers} markers emitted",
                            cell.Row, cell.Column, withProps, cellMap.Count);
                    }
                    foreach (var para in cell.Paragraphs)
                    {
                        if (para.ListProperties is null) continue;

                        if (cellMap.TryGetValue(para, out var info))
                        {
                            para.ListProperties.ComputedMarkerText = info.Text;
                            // Сам значок рисуется не по тексту в модели, а по Marker
                            // в записи раскладки: текст в модели нужен только чтобы
                            // померить его ширину и отодвинуть первую строку.
                            // Поэтому маркер запоминается и подставляется в ParaLayout
                            // ячейки — без этого получался отступ без номера.
                            _cellListMarkers[para] = info;
                        }
                        else
                        {
                            para.ListProperties.ComputedMarkerText = null;
                        }

                        MigrateCorruptListMarker(para, textWidthPt);
                    }
                }
            }
        }

        // Маркеры списков для абзацев внутри ячеек. Заполняется
        // ApplyListMarkerTextsInTables, читается при сборке раскладки ячеек.
        private readonly Dictionary<ParagraphBlock, Rendering.ListMarkerInfo> _cellListMarkers = new();

        // Сбрасывает явно повреждённую позицию номера (левый край цифры у/за правым краем
        // текстовой зоны — след старых багов), чтобы номер вернулся к нормальному выступу слева.
        private static void MigrateCorruptListMarker(ParagraphBlock p, double textWidthPt)
        {
            // Порог был «textWidth − 20 pt» и захватывал теперь уже допустимые позиции: метку
            // разрешено доводить до правого края зоны, там текст уходит на вторую строку.
            // Сбрасываем только заведомо битое значение — номер целиком за пределами зоны.
            if (p.ListProperties?.MarkerIndentPt is double mi && mi > textWidthPt)
                p.ListProperties.MarkerIndentPt = null;
        }

        private void PumpLayoutWarmup()
        {
            if (!_layoutWarmupActive) return;

            if (DocVm is null)
            {
                SetWarmupActive(false);
                return;
            }

            if (_styleResolver is null)
                _styleResolver = CreateStyleResolver();

            float widthPt = GetCurrentTextWidthPt();
            var passStopwatch = System.Diagnostics.Stopwatch.StartNew();
            bool allShaped = true;

            var paragraphs = DocVm.Paragraphs;
            for (int i = 0; i < paragraphs.Count; i++)
            {
                var pvm = paragraphs[i];

                // Проверка идентична GetOrBuildLayout: запись с устаревшим текстом
                // или другой шириной будет перешейплена — такой абзац не пропускаем,
                // иначе вся перевёрстка свалилась бы в финальный синхронный проход.
                if (IsLayoutCacheEntryValid(pvm, widthPt)) continue;

                GetOrBuildLayout(pvm, widthPt);

                if (passStopwatch.ElapsedMilliseconds >= WarmupPassBudgetMs)
                {
                    allShaped = false;
                    break;
                }
            }

            if (!allShaped)
            {
                Dispatcher.UIThread.Post(PumpLayoutWarmup, DispatcherPriority.Loaded);
                return;
            }

            SetWarmupActive(false);
            _logger.Debug("Layout warmup finished: cache={CacheCount} — scheduling rebuild", _layoutCache.Count);

            // Пересчёт через measure: раскладка соберётся из тёплого кеша.
            InvalidateMeasure();
            InvalidateFull();
        }

        private void RebuildLayouts()
        {
            if (DocVm is null)
            {
                float emptyH = FallbackLinePt * 5f;
                lock (_renderLock)
                {
                    _layouts = new List<ParaLayout>();
                    _pages = new List<PageRect>();
                    _tables = new List<TableEntry>();
                    _canvasHeightPt = emptyH;
                    _canvasHeight = emptyH * PtToPx;
                }

                // Раскладка пуста — отпечаток недействителен.
                _layoutsFingerprintDocVm = null;
                _layoutsFingerprintParagraphs = null;
                _layoutsFingerprintStyleResolver = null;
                _layoutsFingerprintParagraphCount = -1;
                _layoutsFingerprintWidth = double.NaN;
                _layoutsFingerprintViewMode = (EditorViewMode)(-1);
                return;
            }

            if (_styleResolver is null)
                _styleResolver = CreateStyleResolver();

            // Ворота холодного пересчёта для ПРЯМЫХ вызовов (смена зума, структуры,
            // подписок во время загрузки документа): при холодном кеше полный проход
            // зашейпил бы тысячи абзацев синхронно на UI-потоке (~секунда), в обход
            // прогрева. Вместо этого запускается/продолжается порционный прогрев —
            // по завершении он сам запланирует пересчёт через InvalidateMeasure.
            if (ShouldWarmupBeforeRebuild())
            {
                StartLayoutWarmup();
                return;
            }

            // Диагностика провисаний UI-потока: полный пересчёт раскладки выполняется
            // синхронно (MeasureOverride/ScheduleRebuild), и на больших документах при
            // холодном кеше лейаутов это главный кандидат на заморозку интерфейса.
            // Замер пишется в лог только когда пересчёт превысил порог.
            var rebuildStopwatch = System.Diagnostics.Stopwatch.StartNew();

            // Размер виртуального листа освежается до пересчёта: от него зависят и
            // ширина текста, и поля, которые читает вся раскладка.
            if (SpreadMode) ComputeSpreadPageSize();

            // Подмены чтения ставятся ДО пересчёта, а не только перед отрисовкой:
            // шрифт, ступень размера и ужатие таблиц участвуют в самой вёрстке, и
            // раскладка, построенная без них, разошлась бы с тем, что видно на листе.
            PushReadingTextOverrides();
            InvalidateOwnNumbering();

            switch (DocVm.ViewMode)
            {
                case EditorViewMode.Page:
                    RebuildPageMode();
                    break;
                case EditorViewMode.Draft:
                case EditorViewMode.Web:
                    RebuildFlowMode((float)(_canvasWidth * PxToPt), DraftPadHPt, DraftPadWPt);
                    break;
                case EditorViewMode.Reading:
                    {
                        if (SpreadMode)
                        {
                            // Разворот верстается той же пагинацией, что и режим страниц:
                            // разрывы, таблицы и обтекание считаются одинаково, отличается
                            // только размер листа. Вся книжность живёт в отображении.
                            RebuildPageMode();
                            ClampSpreadPage();
                            FitCanvasToViewport();
                            break;
                        }

                        // Первым аргументом идёт ПОЛНАЯ ширина канваса, а не ширина
                        // колонки: внутри RebuildFlowMode отступ вычитается из неё
                        // дважды. С урезанной шириной на широком окне вычитание
                        // уводило колонку в минус, и текст вставал по букве в строку.
                        float cw = (float)(_canvasWidth * PxToPt);
                        float columnPt = Math.Min(cw, ReadingMaxPt);
                        RebuildFlowMode(cw, 18f, (cw - columnPt) / 2f);
                        break;
                    }
            }

            rebuildStopwatch.Stop();
            if (rebuildStopwatch.ElapsedMilliseconds > 50)
            {
                _logger.Warning(
                    "RebuildLayouts took {ElapsedMs}ms on UI thread: mode={Mode}, paragraphs={ParaCount}, layoutCache={CacheCount}",
                    rebuildStopwatch.ElapsedMilliseconds,
                    DocVm.ViewMode,
                    DocVm.Paragraphs.Count,
                    _layoutCache.Count);
            }

            // Фиксируем отпечаток состояния, для которого построена раскладка —
            // последующие measure-проходы с тем же состоянием пропустят пересчёт.
            _layoutsFingerprintDocVm = DocVm;
            _layoutsFingerprintParagraphs = DocVm.Paragraphs;
            _layoutsFingerprintStyleResolver = _styleResolver;
            _layoutsFingerprintParagraphCount = DocVm.Paragraphs.Count;
            _layoutsFingerprintWidth = _canvasWidth;
            _layoutsFingerprintViewMode = DocVm.ViewMode;
            _layoutsFingerprintSpread = DocVm.IsSpreadReading;
        }


        /// <summary>
        /// Возвращает layout для рендера параграфа.
        /// Во время live-preview оверлейный _layouts уже содержит preview-layout в pl.Layout.
        /// </summary>
        private SKTextLayout GetRenderLayout(ParaLayout pl, float widthPt)
        {
            // Во время live-preview оверлейный _layouts уже содержит preview-layout
            // прямо в pl.Layout (см. DocumentCanvas.FontPreview.cs), отдельная ветка не нужна.
            return pl.Layout ?? GetOrBuildLayout(pl.Vm, widthPt);
        }

        /// <summary>
        /// Быстрая оценка высоты параграфа без построения SKTextLayout.
        /// Используется для параграфов вне viewport-буфера — точность ~±30%,
        /// достаточная для позиционирования скроллбара и прокрутки.
        /// </summary>
        private float EstimateHeight(ParagraphViewModel pvm, float widthPt)
        {
            int charCount = pvm.PlainText?.Length ?? 0;
            if (charCount == 0) return FallbackLinePt;
            const float AvgCharWidthPt = 5.5f;
            float charsPerLine = Math.Max(widthPt / AvgCharWidthPt, 1f);
            float lines = MathF.Ceiling(charCount / charsPerLine) + 0.5f;
            return MathF.Max(lines * FallbackLinePt, FallbackLinePt);
        }

        private SKTextLayout GetOrBuildLayout(ParagraphViewModel pvm, float widthPt)
        {
            string text = pvm.PlainText ?? string.Empty;
            if (_layoutCache.TryGetValue(pvm, out var cached)
                && cached.Text == text
                && Math.Abs(cached.Width - widthPt) < 0.1f)
                return cached.Layout;
            var layout = _renderer.BuildLayout(pvm.Model, widthPt, _styleResolver!);
            _layoutCache[pvm] = (text, widthPt, layout);
            return layout;
        }

        // Отступ текста от габарита обтекаемого объекта, в пунктах.
        private const float WrapZoneMarginPt = 6f;

        /// <summary>
        /// Зоны обтекания для параграфа с верхом paraTopPt: габариты плавающих
        /// картинок в режимах Square/Tight (AABB с учётом поворота, с полями),
        /// переведённые в координаты текстовой области параграфа.
        /// null — обтекаемых объектов рядом нет.
        /// </summary>
        private List<SKWrapZone>? ComputeWrapZones(
            List<ImageEntry> images, float paraTopPt, float textXPt, float textWidthPt,
            List<PageRect>? pages = null, int? pageIndex = null)
        {
            List<SKWrapZone>? zones = null;

            foreach (var ie in images)
            {
                var wm = ie.Block.WrapMode;
                if (wm != WrapMode.Square && wm != WrapMode.Tight) continue;

                // Картинка обтекается только СВОЕЙ страницей. Чужая в расчёт не идёт
                // вообще — ни габаритом, ни отступами: свесившаяся за нижний край
                // картинка не должна двигать текст там, где её не видно.
                if (pageIndex is int paraPage && ie.PageIndex != paraPage) continue;

                double rad = ie.Block.RotationDeg * Math.PI / 180.0;
                float absCos = (float)Math.Abs(Math.Cos(rad));
                float absSin = (float)Math.Abs(Math.Sin(rad));
                float boxW = ie.WidthPt * absCos + ie.HeightPt * absSin;
                float boxH = ie.WidthPt * absSin + ie.HeightPt * absCos;
                float cx = ie.XPt + ie.WidthPt / 2f;
                float cy = ie.Ypt + ie.HeightPt / 2f;

                // Отступы обтекания задаются на самой картинке (по сторонам). Дефолт
                // равен прежнему WrapZoneMarginPt, поэтому старые документы не меняются.
                float top = cy - boxH / 2f - (float)ie.Block.WrapPadTopPt;
                float bottom = cy + boxH / 2f + (float)ie.Block.WrapPadBottomPt;
                float left = cx - boxW / 2f - (float)ie.Block.WrapPadLeftPt - textXPt;
                float right = cx + boxW / 2f + (float)ie.Block.WrapPadRightPt - textXPt;

                // Зона живёт только на своей странице. Картинка у нижнего края страницы
                // свисает за текстовую зону и рисуется обрезанной по листу — но её габарит
                // в координатах документа дотягивался до текста СЛЕДУЮЩЕЙ страницы и двигал
                // его там, где самой картинки не видно.
                //
                // Страница картинки может ещё не существовать в списке: страницы
                // достраиваются по мере вёрстки, а закреплённая картинка живёт на
                // странице, до которой текст пока не дошёл. Отсутствие страницы в
                // списке не отменяет обрезку — иначе необрезанная зона со страницы,
                // которой «ещё нет», ложится на текст текущей. Геометрия недостающей
                // страницы считается от последней имеющейся: все страницы раздела
                // одного размера и идут с постоянным шагом.
                if (pages is not null && ie.PageIndex >= 0 && pages.Count > 0)
                {
                    float pageTopPt, pageHeightPt, padTopPt, padBottomPt;

                    if (ie.PageIndex < pages.Count)
                    {
                        var pg = pages[ie.PageIndex];
                        pageTopPt = pg.Ypt;
                        pageHeightPt = pg.HeightPt;
                        padTopPt = pg.PadTopPt;
                        padBottomPt = pg.PadBottomPt;
                    }
                    else
                    {
                        var lastPage = pages[pages.Count - 1];
                        float stepPt = lastPage.HeightPt + PageGapPt;
                        pageTopPt = lastPage.Ypt + stepPt * (ie.PageIndex - (pages.Count - 1));
                        pageHeightPt = lastPage.HeightPt;
                        padTopPt = lastPage.PadTopPt;
                        padBottomPt = lastPage.PadBottomPt;
                    }

                    float pageTextTopPt = pageTopPt + padTopPt;
                    float pageTextBottomPt = pageTopPt + pageHeightPt - padBottomPt;

                    if (top < pageTextTopPt) top = pageTextTopPt;
                    if (bottom > pageTextBottomPt) bottom = pageTextBottomPt;
                    if (bottom <= top) continue;
                }

                // Зона целиком выше параграфа или слишком далеко ниже — не влияет.
                if (bottom <= paraTopPt) continue;
                if (top >= paraTopPt + 3000f) continue;
                // Зона вне текстовой колонки по горизонтали — не влияет.
                if (right <= 0f || left >= textWidthPt) continue;

                left = Math.Max(left, 0f);
                right = Math.Min(right, textWidthPt);

                zones ??= new List<SKWrapZone>();
                zones.Add(new SKWrapZone(
                    top - paraTopPt, bottom - paraTopPt, left, right,
                    ie.Block.WrapSide switch
                    {
                        Models.Document.WrapSide.BothSides => SKWrapSide.BothSides,
                        Models.Document.WrapSide.LeftOnly => SKWrapSide.LeftOnly,
                        Models.Document.WrapSide.RightOnly => SKWrapSide.RightOnly,
                        _ => SKWrapSide.LargestOnly
                    }));
            }

            return zones;
        }

        /// <summary>
        /// Встроенная в строку картинка по её Id. Живёт в InlineObjects раздела,
        /// а run абзаца хранит только ссылку.
        /// </summary>
        private ImageBlock? FindInlineImage(Guid id)
        {
            var document = DocVm?.Document;
            if (document is null) return null;

            foreach (var section in document.Sections)
            {
                foreach (var block in section.InlineObjects)
                {
                    if (block is ImageBlock image && image.Id == id)
                        return image;
                }
            }

            return null;
        }

        /// <summary>
        /// Переносит картинку «в тексте» в позицию каретки — туда, где её отпустили.
        /// Картинка в тексте это символ абзаца, поэтому «перетаскивание» для неё —
        /// вырезать символ из старого места и вставить в новое, ровно как в Word.
        /// </summary>
        private void MoveInlineImageToCaret(ImageBlock image)
        {
            if (DocVm is null || IsEditingBlocked) return;

            var owner = DocVm.FindInlineImageOwner(image);
            if (owner is not { } source) return;

            if (_caretPara < 0 || _caretPara >= _layouts.Count) return;
            var target = _layouts[_caretPara].Cell?.ParaBlock ?? _layouts[_caretPara].Vm?.Model;
            if (target is null) return;

            int at = Math.Max(0, Math.Min(_caretChar, target.TotalLength));

            // Бросили ровно туда же, откуда взяли — ничего не делаем, иначе в историю
            // отмены попадёт пустая операция.
            bool sameParagraph = ReferenceEquals(target, source.Para);
            if (sameParagraph && (at == source.CharIndex || at == source.CharIndex + 1)) return;

            BeginEdit("Перемещение картинки в тексте");

            source.Para.SpliceText(source.CharIndex, source.CharIndex + 1, string.Empty);

            // Изъятие символа сдвинуло позиции правее него в ТОМ ЖЕ абзаце.
            if (sameParagraph && at > source.CharIndex) at--;

            target.InsertInlineObject(at, image.Id);

            CommitEdit();

            RefreshParagraphAfterInlineChange(source.Para);
            if (!sameParagraph) RefreshParagraphAfterInlineChange(target);

            // Каретка встаёт сразу за картинкой, сама картинка остаётся выделенной.
            for (int i = 0; i < _layouts.Count; i++)
            {
                var pl = _layouts[i];
                var plPara = pl.Cell?.ParaBlock ?? pl.Vm?.Model;
                if (!ReferenceEquals(plPara, target)) continue;
                _caretPara = i;
                _caretChar = at + 1;
                _caretLineHint = -1;
                break;
            }

            SyncSel();
            InvalidateMeasure();
            InvalidateFull();
        }

        /// <summary>Живёт ли картинка в строке текста (а не блоком в потоке).</summary>
        private bool IsInlineObjectImage(ImageBlock? image)
        {
            if (image is null) return false;
            var document = DocVm?.Document;
            if (document is null) return false;

            foreach (var section in document.Sections)
                if (section.InlineObjects.Contains(image))
                    return true;

            return false;
        }

        /// <summary>
        /// Сбрасывает раскладку абзацев, в которых стоит эта встроенная картинка.
        /// Кеш раскладки валиден по тексту и ширине колонки, а размер и поворот картинки
        /// не меняют ни того, ни другого — без явного сброса новая геометрия картинки
        /// осталась бы невидимой до следующей правки текста.
        /// </summary>
        private void InvalidateInlineImageLayout(ImageBlock? image)
        {
            if (DocVm is null || !IsInlineObjectImage(image)) return;

            Guid imageId = image!.Id;
            bool cellTouched = false;

            foreach (var pvm in DocVm.Paragraphs)
            {
                if (!ParagraphHasInlineImage(pvm.Model, imageId)) continue;
                _layoutCache.Remove(pvm);
            }

            foreach (var kv in _cellVmCache)
            {
                if (!ParagraphHasInlineImage(kv.Key, imageId)) continue;
                _layoutCache.Remove(kv.Value);
                cellTouched = true;
            }

            if (cellTouched) InvalidateCellLayoutCaches();
        }

        private static bool ParagraphHasInlineImage(ParagraphBlock para, Guid imageId)
        {
            foreach (var id in para.EnumerateInlineImageIds())
                if (id == imageId) return true;
            return false;
        }

        /// <summary>
        /// Абзац и позиция каретки — точка вставки картинки в строку. Для каретки внутри
        /// ячейки таблицы отдаёт абзац ячейки: его нет в Blocks, и без этого картинка
        /// ушла бы в основной поток вместо ячейки.
        /// </summary>
        private (ParagraphBlock Para, int CharIndex)? GetCaretTarget()
        {
            if (_caretPara < 0 || _caretPara >= _layouts.Count) return null;

            var pl = _layouts[_caretPara];
            var para = pl.Cell?.ParaBlock ?? pl.Vm?.Model;
            if (para is null) return null;

            return (para, Math.Max(0, Math.Min(_caretChar, para.TotalLength)));
        }

        /// <summary>
        /// Картинка встала в строку: ставим каретку сразу за ней и пересобираем раскладку
        /// абзаца — его текст изменился на один символ, кеш раскладки больше не годится.
        /// </summary>
        private void OnInlineImageInserted(ParagraphBlock para, int charIndex)
        {
            RefreshParagraphAfterInlineChange(para);

            for (int i = 0; i < _layouts.Count; i++)
            {
                var pl = _layouts[i];
                var plPara = pl.Cell?.ParaBlock ?? pl.Vm?.Model;
                if (!ReferenceEquals(plPara, para)) continue;

                _caretPara = i;
                _caretChar = charIndex + 1;
                _caretLineHint = -1;
                break;
            }

            InvalidateMeasure();
            InvalidateFull();
        }

        /// <summary>
        /// Приводит абзац в согласованное состояние после изменения его объектов в строке:
        /// текст вью-модели и кеш раскладки должны увидеть новый символ-заполнитель.
        /// </summary>
        private void RefreshParagraphAfterInlineChange(ParagraphBlock para)
        {
            if (DocVm is null) return;

            foreach (var pvm in DocVm.Paragraphs)
            {
                if (!ReferenceEquals(pvm.Model, para)) continue;
                pvm.RefreshPlainTextFromModel();
                _layoutCache.Remove(pvm);
                RebuildLayouts();
                return;
            }

            if (_cellVmCache.TryGetValue(para, out var cellVm))
            {
                cellVm.RefreshPlainTextFromModel();
                _layoutCache.Remove(cellVm);
                InvalidateCellLayoutCaches();
            }

            RebuildLayouts();
        }

        /// <summary>
        /// Габарит встроенной картинки для вёрстки строки, в пунктах.
        /// Повёрнутая картинка занимает в строке свой AABB — как и в потоке блоков.
        /// </summary>
        private (float WidthPt, float HeightPt)? GetInlineImageSize(Guid id)
        {
            var image = FindInlineImage(id);
            if (image is null) return null;

            double rad = image.RotationDeg * Math.PI / 180.0;
            float absCos = (float)Math.Abs(Math.Cos(rad));
            float absSin = (float)Math.Abs(Math.Sin(rad));

            var (w, h) = ReadingImageSize(image);

            return (w * absCos + h * absSin, w * absSin + h * absCos);
        }

        /// <summary>
        /// Свободная горизонтальная полоса для картинки-блока на её вертикали.
        /// Картинка в потоке обходит соседнюю обтекаемую картинку так же, как это
        /// делает текст: раньше блок занимал всю колонку и просто наезжал на неё.
        /// Если бокс не помещается ни в один свободный промежуток, картинка уезжает
        /// под зону — как строка, которую не удалось поставить сбоку.
        /// contentYPt при этом сдвигается вниз.
        /// </summary>
        private void ResolveInlineImageBand(
            List<ImageEntry> zoneSource,
            ref float contentYPt,
            float boxWpt,
            float boxHpt,
            float textXPt,
            float textWidthPt,
            float pageBottomPt,
            out float bandLeftPt,
            out float bandRightPt,
            List<PageRect>? pages = null,
            int? pageIndex = null)
        {
            bandLeftPt = textXPt;
            bandRightPt = textXPt + textWidthPt;

            if (DocVm?.ViewMode != EditorViewMode.Page) return;

            var blocking = new List<SKWrapZone>();

            for (int guard = 0; guard < 8; guard++)
            {
                var zones = ComputeWrapZones(
                    zoneSource, contentYPt, textXPt, textWidthPt, pages, pageIndex);
                if (zones is null || zones.Count == 0) return;

                // Зоны, пересекающие вертикальную полосу самой картинки.
                blocking.Clear();
                float lowestBottomPt = float.MinValue;
                foreach (var zone in zones)
                {
                    if (zone.BottomPt <= 0f || zone.TopPt >= boxHpt) continue;
                    blocking.Add(zone);
                    if (zone.BottomPt > lowestBottomPt) lowestBottomPt = zone.BottomPt;
                }

                if (blocking.Count == 0) return;

                // Самый широкий свободный промежуток колонки между занятыми участками.
                blocking.Sort((a, b) => a.LeftPt.CompareTo(b.LeftPt));
                float cursorPt = 0f;
                float bestLeftPt = 0f;
                float bestWidthPt = 0f;

                foreach (var zone in blocking)
                {
                    float gapPt = zone.LeftPt - cursorPt;
                    if (gapPt > bestWidthPt) { bestWidthPt = gapPt; bestLeftPt = cursorPt; }
                    if (zone.RightPt > cursorPt) cursorPt = zone.RightPt;
                }

                float tailPt = textWidthPt - cursorPt;
                if (tailPt > bestWidthPt) { bestWidthPt = tailPt; bestLeftPt = cursorPt; }

                if (bestWidthPt >= boxWpt)
                {
                    bandLeftPt = textXPt + bestLeftPt;
                    bandRightPt = bandLeftPt + bestWidthPt;
                    return;
                }

                // Сбоку не помещается — опускаем картинку под зону и проверяем заново:
                // ниже может лежать следующая обтекаемая картинка.
                float nextYPt = contentYPt + lowestBottomPt + WrapThrowGapPt;
                if (nextYPt <= contentYPt) return;

                contentYPt = nextYPt;

                // Ушли за нижнюю границу листа — дальше решает перенос на страницу.
                if (nextYPt >= pageBottomPt) return;
            }
        }

        /// <summary>
        /// Опускает верх таблицы под обтекаемые картинки, чьи зоны перекрывают её по
        /// горизонтали.
        ///
        /// Таблица, в отличие от текста и картинки в потоке, встать сбоку от объекта не
        /// может: её ширина и левый край заданы самой таблицей, сузить колонки под остаток
        /// свободной полосы нельзя. Поэтому единственный способ не пересечься — сдвинуть
        /// таблицу целиком под нижнюю границу мешающих зон.
        ///
        /// Проверка идёт по полной высоте таблицы, а не по первой строке: картинка,
        /// накрывающая её середину, обязана отодвинуть таблицу так же, как накрывающая верх.
        /// </summary>
        private void ResolveTableTop(
            List<ImageEntry> zoneSource,
            ref float contentYPt,
            float tableXPt,
            float tableWidthPt,
            float tableHeightPt,
            float textXPt,
            float textWidthPt,
            float pageBottomPt,
            List<PageRect>? pages = null,
            int? pageIndex = null)
        {
            if (DocVm?.ViewMode != EditorViewMode.Page) return;
            if (tableWidthPt <= 0f || tableHeightPt <= 0f) return;

            // Зоны приходят в координатах: X отсчитывается от textXPt, Y — от того верха,
            // который передан в ComputeWrapZones.
            float tableLeftPt = tableXPt - textXPt;
            float tableRightPt = tableLeftPt + tableWidthPt;

            // Опустившись под одну картинку, таблица может упереться в следующую, поэтому
            // проверка повторяется. Потолок итераций защищает от зацикливания.
            for (int guard = 0; guard < 8; guard++)
            {
                var zones = ComputeWrapZones(
                    zoneSource, contentYPt, textXPt, textWidthPt, pages, pageIndex);
                if (zones is null || zones.Count == 0) return;

                bool blocked = false;
                float lowestBottomPt = 0f;

                foreach (var zone in zones)
                {
                    // Зона кончается выше таблицы или начинается ниже её низа — не мешает.
                    if (zone.BottomPt <= 0f || zone.TopPt >= tableHeightPt) continue;

                    // Зона целиком левее или правее таблицы — не мешает.
                    if (zone.RightPt <= tableLeftPt || zone.LeftPt >= tableRightPt) continue;

                    if (!blocked || zone.BottomPt > lowestBottomPt)
                        lowestBottomPt = zone.BottomPt;
                    blocked = true;
                }

                if (!blocked) return;

                float nextYPt = contentYPt + lowestBottomPt + WrapThrowGapPt;
                if (nextYPt <= contentYPt + 0.5f) return;

                contentYPt = nextYPt;

                // Ушли за нижний край листа — дальше решает перенос строк на страницу.
                if (nextYPt >= pageBottomPt) return;
            }
        }

        /// <summary>
        /// Высота ещё не размещённой части таблицы: строки начиная с rowFrom и до конца,
        /// за вычетом уже показанной сверху части первой из них.
        ///
        /// Нужна при переносе таблицы на новую страницу: проверять обтекание там следует
        /// по габариту остатка, а не по полной высоте — размещённые слайсы остались выше
        /// и к новой позиции отношения не имеют.
        /// </summary>
        private static float RemainingTableHeightPt(
            SKTableLayout tableLayout, int rowFrom, float firstRowOffsetPt)
        {
            if (tableLayout is null) return 0f;

            float heightPt = 0f;
            for (int i = rowFrom; i < tableLayout.Rows.Count; i++)
            {
                if (i < 0) continue;
                heightPt += tableLayout.Rows[i].HeightPt;
            }

            heightPt -= firstRowOffsetPt;
            return heightPt > 0f ? heightPt : 0f;
        }

        /// <summary>
        /// Раскладка параграфа с зонами обтекания. Кеш не используется: зоны зависят
        /// от позиций плавающих объектов, а ключ кеша (текст, ширина) их не учитывает.
        /// </summary>
        // Состояние гистерезиса обтекания: был ли абзац вытеснен под объект в прошлой
        // пересборке. Ключ — блок абзаца, живёт ровно столько же, сколько документ.
        private readonly Dictionary<ParagraphBlock, bool> _wrapPushState = new();

        private SKTextLayout BuildWrappedLayout(
            ParagraphViewModel pvm, float widthPt, IReadOnlyList<SKWrapZone> zones,
            Rendering.SKTextRenderer.WrapPageContext? pages = null)
        {
            // Абзац без модели верстать нечем — отдаём пустую раскладку той же ширины,
            // чтобы вызывающий не разбирал null отдельной веткой.
            var block = pvm.Model;
            if (block is null)
                return _renderer.BuildLayout(new ParagraphBlock(), widthPt, _styleResolver!);

            bool preferPushDown =
                _wrapPushState.TryGetValue(block, out var prev) && prev;

            var layout = _renderer.BuildLayout(
                block, widthPt, _styleResolver!, isCell: false,
                wrapZones: zones, wrapPreferPushDown: preferPushDown,
                wrapPages: pages);

            _wrapPushState[block] = layout.WrapPushedDown;

            return layout;
        }

        // Зазор между низом картинки и строкой, переброшенной под неё по фактической
        // позиции. Небольшой, чтобы текст не прилипал к краю объекта.
        private const float WrapThrowGapPt = 2f;

        // ── ICustomDrawOperation ──────────────────────────────────────────
        private sealed class CanvasSKDrawOperation : ICustomDrawOperation
        {
            private readonly DocumentCanvas _canvas;
            public Rect Bounds { get; }

            public CanvasSKDrawOperation(DocumentCanvas canvas, Rect bounds)
            {
                _canvas = canvas;
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
                _canvas.RenderWithSKCanvas(lease.SkCanvas);
            }
        }
    }
}