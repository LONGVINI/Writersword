using Avalonia;
using Avalonia.Input;
using SkiaSharp;
using System;
using System.Collections.Generic;
using Writersword.Modules.TextEditor.Commands;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Document
{
    public sealed partial class DocumentCanvas
    {
        // ── Раскладка фигур ───────────────────────────────────────────────
        // Одна запись = одна фигура на своей странице. Координаты записи —
        // абсолютные координаты раскладки в пунктах, как у ImageEntry: модель
        // хранит смещение от начала текстовой области своей страницы, а запись —
        // уже разрешённое положение на полотне.
        private record ShapeEntry(
            ShapeBlock Block,
            float Ypt,
            float XPt,
            float WidthPt,
            float HeightPt,
            int PageIndex);

        private List<ShapeEntry> _shapes = new();
        private List<ShapeEntry> _passShapes = new();

        // Выделенная фигура: рамка, маркеры, удаление. null — ничего не выделено.
        private ShapeBlock? _selectedShape;

        /// <summary>Выделена ли сейчас фигура. Лента показывает по этому признаку
        /// контекстную вкладку «Формат фигуры».</summary>
        public Action<bool>? ShapeSelectionChanged { get; set; }

        // Перетаскивание фигуры.
        private bool _shapeDragging;
        private bool _shapeDragMoved;
        private float _shapeDragStartXPt;
        private float _shapeDragStartYPt;
        private double _shapeDragStartModelX;
        private double _shapeDragStartModelY;
        private float _shapeDragEntryX0;
        private float _shapeDragEntryY0;

        // Изменение размера за маркер. Индексы маркеров те же, что у картинки:
        // 0 — верх-лево, 1 — верх-право, 2 — низ-право, 3 — низ-лево,
        // 4 — верх-центр, 5 — право-центр, 6 — низ-центр, 7 — лево-центр, 8 — поворот.
        private bool _shapeResizing;
        private bool _shapeResizeMoved;
        private int _shapeResizeCorner = -1;
        private double _shapeResizeStartModelX;
        private double _shapeResizeStartModelY;
        private float _shapeResizeStartLeft;
        private float _shapeResizeStartTop;
        private float _shapeResizeStartW;
        private float _shapeResizeStartH;

        // Смещение точки захвата от края фигуры в её собственной системе координат.
        // Маркеры стоят СНАРУЖИ габарита (см. ShapeSelectionGapPt), и без этой
        // поправки фигура на первом же движении прыгала бы ровно на зазор.
        private float _shapeResizeGrabDX;
        private float _shapeResizeGrabDY;

        // Поворот за круглый маркер над верхней гранью.
        private bool _shapeRotating;
        private bool _shapeRotateMoved;
        private double _shapeRotStartDeg;
        private float _shapeRotPointerStartDeg;
        private float _shapeRotCenterXPt;
        private float _shapeRotCenterYPt;

        private ShapePropertiesCommand? _pendingShapeCommand;

        // Буфер фигуры для копирования и вставки. Живёт в канвасе, а не в системном
        // буфере: фигура — объект документа, и переносить её через текстовый буфер
        // значило бы терять всё оформление.
        private ShapeBlock? _shapeClipboard;

        // Наименьшая сторона фигуры: за меньшую маркеры слипаются и фигуру
        // невозможно снова растянуть.
        private const float ShapeMinSidePt = 6f;

        // Шаг привязки угла при повороте с зажатым Shift.
        private const double ShapeRotationSnapDeg = 15.0;

        // Сдвиг вставленной или продублированной копии, чтобы она не пряталась
        // ровно под оригиналом.
        private const double ShapePasteOffsetPt = 14.0;

        // Зазор между габаритом фигуры и рамкой выделения. Рамка идёт СНАРУЖИ:
        // лёжа на самой границе, она закрывала обводку фигуры, и разглядеть её
        // цвет и толщину при выделении было нельзя — а правят их именно тогда,
        // когда фигура выделена.
        private const float ShapeSelectionGapPt = 3f;

        // ── Раскладка ─────────────────────────────────────────────────────

        /// <summary>
        /// Положение фигуры на полотне. Смещения отсчитываются от начала текстовой
        /// области страницы, на которой стоит блок фигуры в потоке документа, —
        /// так же, как у плавающей картинки. Страница записи затем уточняется по
        /// центру габарита: перетащенная за край фигура принадлежит тому листу,
        /// на котором её видно, и по нему же обрезается.
        /// </summary>
        private ShapeEntry BuildShapeEntry(
            ShapeBlock shape,
            float pageXPt, float pageYPt,
            float marginLeftPt, float marginTopPt,
            List<PageRect> pages, int flowPageIdx)
        {
            float wPt = (float)Math.Max(shape.WidthPt, ShapeMinSidePt);
            float hPt = (float)Math.Max(shape.HeightPt, ShapeMinSidePt);
            float xPt = pageXPt + marginLeftPt + (float)shape.OffsetXPt;
            float yPt = pageYPt + marginTopPt + (float)shape.OffsetYPt;

            return new ShapeEntry(
                shape, yPt, xPt, wPt, hPt,
                ResolveFloatingObjectPage(xPt, yPt, wPt, hPt, pages, flowPageIdx));
        }

        /// <summary>
        /// Прямоугольник рамки выделения: габарит, раздутый на зазор и на наружную
        /// половину обводки. Считается в одном месте, потому что по нему рисуется
        /// рамка и по нему же ловятся маркеры — разойдись они, маркеры перестали бы
        /// попадать туда, где нарисованы.
        /// </summary>
        private static SKRect ShapeSelectionRect(ShapeEntry se, float shiftXPt)
        {
            float outset = ShapeSelectionGapPt + (float)se.Block.WrapOutsetPt;
            return new SKRect(
                se.XPt + shiftXPt - outset,
                se.Ypt - outset,
                se.XPt + shiftXPt + se.WidthPt + outset,
                se.Ypt + se.HeightPt + outset);
        }

        /// <summary>Запись раскладки для фигуры, либо null.</summary>
        private ShapeEntry? FindShapeEntry(ShapeBlock? shape)
        {
            if (shape is null) return null;
            List<ShapeEntry> shapes;
            lock (_renderLock) { shapes = _shapes; }
            foreach (var se in shapes)
                if (ReferenceEquals(se.Block, shape))
                    return se;
            return null;
        }

        /// <summary>
        /// Переставляет запись раскладки фигуры без пересборки документа. Пока у
        /// фигуры нет обтекания, её перемещение ни на что в потоке не влияет, и
        /// правки собственной записи достаточно. С обтеканием так нельзя: текст
        /// должен переезжать за фигурой — там идёт полная пересборка.
        /// </summary>
        private void UpdateShapeEntry(ShapeBlock shape, float xPt, float yPt, float wPt, float hPt)
        {
            lock (_renderLock)
            {
                for (int i = 0; i < _shapes.Count; i++)
                {
                    if (!ReferenceEquals(_shapes[i].Block, shape)) continue;
                    _shapes[i] = _shapes[i] with
                    {
                        XPt = xPt,
                        Ypt = yPt,
                        WidthPt = wPt,
                        HeightPt = hPt
                    };
                    return;
                }
            }
        }

        /// <summary>
        /// Двигает ли фигура текст. У такой правка положения требует полной
        /// пересборки на каждом шаге жеста: зоны обтекания считаются по раскладке.
        /// </summary>
        private static bool ShapeAffectsFlow(ShapeBlock shape)
            => shape.WrapMode is WrapMode.Square or WrapMode.Tight or WrapMode.Inline;

        // ── Отрисовка ─────────────────────────────────────────────────────

        /// <summary>
        /// Фигуры видимых страниц в порядке отрисовки: сначала Z-порядок, при
        /// равном — порядок блоков в документе. Сортируется копия: список раскладки
        /// читает поток рендера, и менять его местами нельзя.
        /// </summary>
        private List<ShapeEntry> OrderedShapesForRender(int firstPage, int lastPage)
        {
            List<ShapeEntry> shapes;
            lock (_renderLock) { shapes = _shapes; }

            var visible = new List<(ShapeEntry Entry, int Order)>();
            for (int i = 0; i < shapes.Count; i++)
            {
                var se = shapes[i];
                if (se.PageIndex < firstPage || se.PageIndex > lastPage) continue;
                visible.Add((se, i));
            }

            visible.Sort((a, b) =>
            {
                int byZ = a.Entry.Block.ZOrder.CompareTo(b.Entry.Block.ZOrder);
                return byZ != 0 ? byZ : a.Order.CompareTo(b.Order);
            });

            var result = new List<ShapeEntry>(visible.Count);
            foreach (var v in visible) result.Add(v.Entry);
            return result;
        }

        /// <summary>
        /// Рисует фигуры видимых страниц. Вызывается дважды за проход: до текста —
        /// фигуры «за текстом» и фигуры-блоки в потоке, после текста — всё
        /// остальное. Порядок тот же, что у картинок.
        /// </summary>
        private void RenderShapes(
            SKCanvas canvas, List<PageRect> pages, int firstPage, int lastPage, bool beforeText)
        {
            var shapes = OrderedShapesForRender(firstPage, lastPage);
            if (shapes.Count == 0) return;

            foreach (var se in shapes)
            {
                var wm = se.Block.WrapMode;
                bool drawsFirst = wm is WrapMode.Behind or WrapMode.Inline;
                if (drawsFirst != beforeText) continue;

                // Клип по листу: часть фигуры за краем страницы или в межстраничном
                // зазоре не рисуется — по обрезу сразу видно, какому листу она
                // принадлежит. Фигура, целиком промахнувшаяся мимо своего листа,
                // не клипуется: иначе объект в документе есть, а на экране его нет.
                bool offPage = se.PageIndex >= 0 && se.PageIndex < pages.Count
                    && IsShapeOffItsPage(se, pages[se.PageIndex]);

                bool clip = se.PageIndex >= 0 && se.PageIndex < pages.Count && !offPage;
                if (clip)
                {
                    var pg = pages[se.PageIndex];
                    canvas.Save();
                    canvas.ClipRect(new SKRect(
                        pg.PadLeftPt, pg.Ypt,
                        pg.PadLeftPt + pg.WidthPt, pg.Ypt + pg.HeightPt));
                }

                // Поворот здесь НЕ применяется: его делает сам рендерер фигуры.
                // Пока он стоял и тут, и там, повёрнутая фигура уезжала на двойной угол.
                var rect = new SKRect(se.XPt, se.Ypt, se.XPt + se.WidthPt, se.Ypt + se.HeightPt);
                DrawShape(canvas, se.Block, rect, offPage);

                // Пометка «мимо листа» идёт по неповёрнутому габариту: она про место
                // объекта на странице, а не про его форму.
                if (offPage) DrawOffPageHatch(canvas, rect);

                if (clip) canvas.Restore();
            }
        }

        /// <summary>Лежит ли фигура целиком за пределами своей страницы.</summary>
        private static bool IsShapeOffItsPage(ShapeEntry se, PageRect page)
        {
            float left = se.XPt, right = se.XPt + se.WidthPt;
            float top = se.Ypt, bottom = se.Ypt + se.HeightPt;

            float pageLeft = page.PadLeftPt, pageRight = page.PadLeftPt + page.WidthPt;
            float pageTop = page.Ypt, pageBottom = page.Ypt + page.HeightPt;

            return right <= pageLeft || left >= pageRight
                || bottom <= pageTop || top >= pageBottom;
        }

        /// <summary>
        /// Замкнутый контур фигуры. По нему идёт и заливка, и обводка, и обрезка
        /// картинки-заливки — один источник геометрии на всё, иначе они разъезжаются.
        /// Для линии и стрелки контура нет: возвращается null.
        /// </summary>
        /// <summary>
        /// Рисует одну фигуру. Геометрия, штрих, наконечники и заливка картинкой
        /// живут в FloatingObjectRenderer — там же, откуда их берёт печать. Канвас
        /// добавляет только то, чего у печати нет: гашение объекта, промахнувшегося
        /// мимо листа, и загрузку картинки-заливки из своего кеша.
        /// </summary>
        private void DrawShape(SKCanvas canvas, ShapeBlock shape, SKRect rect, bool offPage)
        {
            var fillImage = string.IsNullOrEmpty(shape.FillImageFileName)
                ? null
                : GetImageBitmap(shape.FillImageFileName!);

            // Промахнувшаяся мимо листа фигура рисуется бледной: видно, что объект
            // есть и где он лежит, но что на страницу он не попадает.
            Rendering.FloatingObjectRenderer.DrawShape(
                canvas, shape, rect, fillImage, offPage ? 0.45 : 1.0);
        }

        /// <summary>
        /// Рамка и маркеры выделенной фигуры — поверх всего содержимого страницы.
        /// </summary>
        private void RenderShapeSelection(SKCanvas canvas, int firstPage, int lastPage)
        {
            if (_selectedShape is null) return;

            var se = FindShapeEntry(_selectedShape);
            if (se is null) return;
            if (se.PageIndex < firstPage || se.PageIndex > lastPage) return;

            var selRect = ShapeSelectionRect(se, 0f);
            float l = selRect.Left, t = selRect.Top;
            float r = selRect.Right, b = selRect.Bottom;
            float cx = (l + r) / 2f, cy = (t + b) / 2f;
            float rotDeg = (float)se.Block.RotationDeg;

            canvas.Save();
            if (rotDeg != 0f) canvas.RotateDegrees(rotDeg, cx, cy);

            canvas.DrawRect(new SKRect(l, t, r, b), _paintImageSelection);

            DrawImageHandle(canvas, l, t);
            DrawImageHandle(canvas, r, t);
            DrawImageHandle(canvas, r, b);
            DrawImageHandle(canvas, l, b);

            DrawImageHandle(canvas, cx, t);
            DrawImageHandle(canvas, r, cy);
            DrawImageHandle(canvas, cx, b);
            DrawImageHandle(canvas, l, cy);

            canvas.DrawLine(cx, t, cx,
                t - ImageRotateHandleOffsetPt + ImageRotateHandleRadiusPt, _paintImageSelection);
            DrawRotateHandle(canvas, cx, t - ImageRotateHandleOffsetPt);

            canvas.Restore();
        }

        // ── Ввод ──────────────────────────────────────────────────────────

        /// <summary>
        /// Точка указателя в системе координат страницы выделенной фигуры. Нужна
        /// там, где ловятся её маркеры: фигура может лежать над соседним листом, и
        /// перевод через ближайшую страницу дал бы координаты чужой системы.
        /// </summary>
        private (float XPt, float YPt) LogicalPointForSelectedShape(
            float rawXPt, float rawYPt, float fallbackXPt, float fallbackYPt)
        {
            if (_pagesPerRow <= 1 || _selectedShape is null)
                return (fallbackXPt, fallbackYPt);

            var se = FindShapeEntry(_selectedShape);
            if (se is null || se.PageIndex < 0) return (fallbackXPt, fallbackYPt);
            return VisualToLogicalPt(rawXPt, rawYPt, se.PageIndex);
        }

        /// <summary>
        /// Индекс маркера выделенной фигуры под точкой, либо -1. Точка переводится
        /// в неповёрнутую систему фигуры, поэтому маркеры ловятся при любом угле.
        /// </summary>
        private int HitTestShapeHandle(float xPt, float yPt)
        {
            var se = FindShapeEntry(_selectedShape);
            if (se is null) return -1;

            var handleRect = ShapeSelectionRect(se, GetPageShiftXPt());
            float left = handleRect.Left;
            float right = handleRect.Right;
            float top = handleRect.Top;
            float bottom = handleRect.Bottom;
            float cx = (left + right) / 2f;
            float cy = (top + bottom) / 2f;

            var (lx, ly) = RotatePointAround(xPt, yPt, cx, cy, -(float)se.Block.RotationDeg);

            var handles = new[]
            {
                (hx: left,  hy: top),
                (hx: right, hy: top),
                (hx: right, hy: bottom),
                (hx: left,  hy: bottom),
                (hx: cx,    hy: top),
                (hx: right, hy: cy),
                (hx: cx,    hy: bottom),
                (hx: left,  hy: cy)
            };
            for (int c = 0; c < handles.Length; c++)
            {
                if (Math.Abs(lx - handles[c].hx) <= ImageHandleHitPt
                    && Math.Abs(ly - handles[c].hy) <= ImageHandleHitPt)
                    return c;
            }

            float rotY = top - ImageRotateHandleOffsetPt;
            float rdx = lx - cx;
            float rdy = ly - rotY;
            if (Math.Sqrt(rdx * rdx + rdy * rdy) <= ImageRotateHandleRadiusPt + ImageHandleHitPt)
                return 8;

            return -1;
        }

        /// <summary>
        /// Фигура под точкой указателя. Перебор идёт с конца порядка отрисовки:
        /// верхняя нарисована последней, ей и должно достаться нажатие.
        /// </summary>
        private ShapeEntry? HitTestShape(Point rawPt, float xPt, float yPt, bool ctrlDown)
        {
            var shapes = OrderedShapesForRender(0, int.MaxValue);
            if (shapes.Count == 0) return null;

            double zoom = Zoom;
            float rawXPt = (float)(rawPt.X / zoom * PxToPt);
            float rawYPt = (float)(rawPt.Y / zoom * PxToPt);
            float shift = GetPageShiftXPt();

            for (int i = shapes.Count - 1; i >= 0; i--)
            {
                var se = shapes[i];

                // Фигура «за текстом»: приоритет у текста. Клик по строке ставит
                // каретку, клик по свободному от текста участку выделяет фигуру.
                // Ctrl+клик выделяет её всегда — правило то же, что у картинки.
                if (se.Block.WrapMode == WrapMode.Behind && !ctrlDown
                    && IsPointOnTextLine(xPt, yPt))
                    continue;

                float hx = xPt, hy = yPt;
                if (_pagesPerRow > 1 && se.PageIndex >= 0 && se.PageIndex != _gesturePage)
                    (hx, hy) = VisualToLogicalPt(rawXPt, rawYPt, se.PageIndex);

                float left = se.XPt + shift;
                float cx = left + se.WidthPt / 2f;
                float cy = se.Ypt + se.HeightPt / 2f;
                var (lx, ly) = RotatePointAround(hx, hy, cx, cy, -(float)se.Block.RotationDeg);

                if (lx >= left && lx <= left + se.WidthPt
                    && ly >= se.Ypt && ly <= se.Ypt + se.HeightPt)
                    return se;
            }
            return null;
        }

        /// <summary>
        /// Нажатие мыши в части, относящейся к фигурам: маркеры выделенной фигуры,
        /// выделение фигуры под курсором, снятие выделения при промахе.
        /// Возвращает true, если нажатие обработано и дальше идти не нужно.
        /// </summary>
        private bool ShapePointerPressed(PointerPressedEventArgs e, Point rawPt, float xPt, float yPt)
        {
            if (IsEditingBlocked) return false;

            // Маркеры проверяются раньше выделения: клик по маркеру уже выделенной
            // фигуры должен начинать изменение размера или поворот, а не выделять
            // то, что лежит под маркером.
            if (_selectedShape is not null)
            {
                double zoomH = Zoom;
                var (handleXPt, handleYPt) = LogicalPointForSelectedShape(
                    (float)(rawPt.X / zoomH * PxToPt), (float)(rawPt.Y / zoomH * PxToPt), xPt, yPt);

                int corner = HitTestShapeHandle(handleXPt, handleYPt);
                var seSel = corner >= 0 ? FindShapeEntry(_selectedShape) : null;
                if (seSel is not null)
                {
                    if (_pagesPerRow > 1 && seSel.PageIndex >= 0)
                        _gesturePage = seSel.PageIndex;

                    float shift = GetPageShiftXPt();
                    if (corner == 8)
                    {
                        _shapeRotCenterXPt = seSel.XPt + shift + seSel.WidthPt / 2f;
                        _shapeRotCenterYPt = seSel.Ypt + seSel.HeightPt / 2f;
                        _shapeRotating = true;
                        _shapeRotateMoved = false;
                        _shapeRotStartDeg = _selectedShape.RotationDeg;
                        _shapeRotPointerStartDeg = (float)(Math.Atan2(
                            yPt - _shapeRotCenterYPt, xPt - _shapeRotCenterXPt) * 180.0 / Math.PI);
                        BeginShapeEdit("Поворот фигуры");
                        Cursor = new Cursor(StandardCursorType.Hand);
                    }
                    else
                    {
                        _shapeResizing = true;
                        _shapeResizeMoved = false;
                        _shapeResizeCorner = corner;
                        _shapeResizeStartModelX = _selectedShape.OffsetXPt;
                        _shapeResizeStartModelY = _selectedShape.OffsetYPt;
                        _shapeResizeStartLeft = seSel.XPt + shift;
                        _shapeResizeStartTop = seSel.Ypt;
                        _shapeResizeStartW = seSel.WidthPt;
                        _shapeResizeStartH = seSel.HeightPt;

                        // Точка захвата относительно края фигуры — чтобы жест начинался
                        // без скачка, где бы внутри маркера ни нажали.
                        float grabCx = _shapeResizeStartLeft + _shapeResizeStartW / 2f;
                        float grabCy = _shapeResizeStartTop + _shapeResizeStartH / 2f;
                        var (grabX, grabY) = RotatePointAround(
                            xPt, yPt, grabCx, grabCy, -(float)_selectedShape.RotationDeg);

                        int grabSx = corner switch { 0 or 3 or 7 => -1, 1 or 2 or 5 => 1, _ => 0 };
                        int grabSy = corner switch { 0 or 1 or 4 => -1, 2 or 3 or 6 => 1, _ => 0 };

                        _shapeResizeGrabDX = grabSx == 0
                            ? 0f
                            : (grabX - grabCx) - grabSx * _shapeResizeStartW / 2f;
                        _shapeResizeGrabDY = grabSy == 0
                            ? 0f
                            : (grabY - grabCy) - grabSy * _shapeResizeStartH / 2f;

                        BeginShapeEdit("Размер фигуры");
                        Cursor = ImageHandleCursor(corner);
                    }

                    e.Pointer.Capture(this);
                    return true;
                }
            }

            var hit = HitTestShape(rawPt, xPt, yPt, e.KeyModifiers.HasFlag(KeyModifiers.Control));
            if (hit is not null)
            {
                SelectShape(hit.Block);

                _shapeDragging = true;
                _shapeDragMoved = false;
                _shapeDragStartXPt = xPt;
                _shapeDragStartYPt = yPt;
                _shapeDragStartModelX = hit.Block.OffsetXPt;
                _shapeDragStartModelY = hit.Block.OffsetYPt;
                _shapeDragEntryX0 = hit.XPt;
                _shapeDragEntryY0 = hit.Ypt;
                BeginShapeEdit("Перемещение фигуры");

                e.Pointer.Capture(this);
                Focus();
                InvalidateFull();
                return true;
            }

            // Промах мимо всех фигур снимает выделение, но нажатие на этом не
            // заканчивается: под фигурами лежит обычный текст, и каретка должна
            // встать туда, куда кликнули.
            ClearShapeSelection();
            return false;
        }

        /// <summary>
        /// Движение мыши в части, относящейся к фигурам. Возвращает true, если
        /// движение обработано жестом фигуры и дальше идти не нужно.
        /// </summary>
        private bool ShapePointerMoved(PointerEventArgs e, float xPt, float yPt)
        {
            if (_shapeRotating && _selectedShape is not null)
            {
                if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                {
                    FinishShapeGesture();
                    return true;
                }

                float cur = (float)(Math.Atan2(
                    yPt - _shapeRotCenterYPt, xPt - _shapeRotCenterXPt) * 180.0 / Math.PI);
                double deg = _shapeRotStartDeg + (cur - _shapeRotPointerStartDeg);

                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    deg = Math.Round(deg / ShapeRotationSnapDeg) * ShapeRotationSnapDeg;

                deg %= 360.0;
                if (deg < 0) deg += 360.0;

                _selectedShape.RotationDeg = deg;
                _shapeRotateMoved = true;
                RefreshAfterShapeGestureStep();
                return true;
            }

            if (_shapeResizing && _selectedShape is not null)
            {
                if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                {
                    FinishShapeGesture();
                    return true;
                }

                ResizeSelectedShapeTo(xPt, yPt, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                _shapeResizeMoved = true;
                RefreshAfterShapeGestureStep();
                return true;
            }

            if (_shapeDragging && _selectedShape is not null)
            {
                if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                {
                    FinishShapeGesture();
                    return true;
                }

                // Фигура «в тексте» свободных координат не имеет: её место задаёт
                // поток, и правка смещений на неё не влияла вовсе — фигура стояла
                // намертво. Тащим её так же, как картинку в строке: каретка идёт за
                // курсором и показывает, куда фигура встанет, а поперёк колонки
                // жест меняет выравнивание.
                if (_selectedShape.WrapMode == WrapMode.Inline)
                {
                    DragInlineShape(e, xPt);
                    _shapeDragMoved = true;
                    Cursor = new Cursor(StandardCursorType.DragMove);
                    InvalidateFull();
                    return true;
                }

                float dx = xPt - _shapeDragStartXPt;
                float dy = yPt - _shapeDragStartYPt;

                _selectedShape.OffsetXPt = _shapeDragStartModelX + dx;
                _selectedShape.OffsetYPt = _shapeDragStartModelY + dy;
                _shapeDragMoved = true;

                var se = FindShapeEntry(_selectedShape);
                if (se is not null)
                    UpdateShapeEntry(_selectedShape,
                        _shapeDragEntryX0 + dx, _shapeDragEntryY0 + dy, se.WidthPt, se.HeightPt);

                Cursor = new Cursor(StandardCursorType.SizeAll);
                RefreshAfterShapeGestureStep();
                return true;
            }

            // Жеста нет: над маркером выделенной фигуры показываем курсор
            // соответствующего направления. Курсор текста в этот момент только
            // мешал бы — по нему не видно, что маркер под указателем.
            if (_selectedShape is not null
                && !_isSelecting
                && !_imageDragging && !_imageResizing && !_imageRotating
                && _tableDragMode == TableDragMode.None)
            {
                double zoom = Zoom;
                var rawPt = e.GetPosition(this);
                var (hx, hy) = LogicalPointForSelectedShape(
                    (float)(rawPt.X / zoom * PxToPt), (float)(rawPt.Y / zoom * PxToPt), xPt, yPt);
                int hoverCorner = HitTestShapeHandle(hx, hy);
                if (hoverCorner >= 0)
                {
                    Cursor = ImageHandleCursor(hoverCorner);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Шаг перетаскивания фигуры, стоящей в потоке. Каретка ведётся за курсором
        /// (по ней на отпускании определяется новое место в потоке), а по положению
        /// поперёк текстовой колонки выбирается выравнивание: левая треть — влево,
        /// середина — по центру, правая — вправо.
        /// </summary>
        private void DragInlineShape(PointerEventArgs e, float xPt)
        {
            var rawPt = e.GetPosition(this);
            var (dropPara, dropChar) = HitTest(rawPt);
            if (dropPara >= 0)
            {
                _caretPara = dropPara;
                _caretChar = dropChar;
                _caretLineHint = -1;
                SyncSel();
                ResetCaret();
            }

            if (_selectedShape is null) return;

            // Границы текстовой колонки берём с листа самой фигуры: у разворота и у
            // режима «страницы рядом» колонка не там, где у первого листа.
            var se = FindShapeEntry(_selectedShape);
            List<PageRect> pages;
            lock (_renderLock) { pages = _pages; }
            if (pages.Count == 0) return;

            int pageIdx = se is not null && se.PageIndex >= 0 && se.PageIndex < pages.Count
                ? se.PageIndex
                : 0;
            var pg = pages[pageIdx];

            float textLeftPt = pg.PadLeftPt + pg.MarginLeftPt;
            float textWidthPt = pg.WidthPt - pg.MarginLeftPt * 2f;
            if (textWidthPt <= 0f) return;

            float frac = (xPt - textLeftPt) / textWidthPt;
            var alignment = frac switch
            {
                < 0.33f => Models.Styles.TextAlignment.Left,
                > 0.67f => Models.Styles.TextAlignment.Right,
                _ => Models.Styles.TextAlignment.Center
            };

            if (_selectedShape.Alignment == alignment) return;

            _selectedShape.Alignment = alignment;
            RebuildLayouts();
            InvalidateMeasure();
        }

        /// <summary>
        /// Ставит фигуру, стоящую в потоке, сразу за абзацем под кареткой. Место
        /// блока в потоке — это и есть её положение, поэтому «перетаскивание» для
        /// неё означает перестановку блока, а не правку координат.
        /// </summary>
        private void DropInlineShapeAtCaret(ShapeBlock shape)
        {
            if (DocVm is null || IsEditingBlocked) return;
            if (_caretPara < 0 || _caretPara >= _layouts.Count) return;

            // Ячейка таблицы фигуре-блоку не годится: она живёт в потоке раздела,
            // а не внутри ячейки.
            if (_layouts[_caretPara].Cell is not null) return;

            var target = _layouts[_caretPara].Vm?.Model;
            if (target is null) return;

            BeginEdit("Перемещение фигуры");
            DocVm.MoveShapeAfterParagraph(shape, target);
            CommitEdit();

            RebuildLayouts();
            InvalidateMeasure();
            InvalidateFull();
        }

        /// <summary>
        /// Обновление вида на каждом шаге жеста. У фигуры с обтеканием текст обязан
        /// переезжать вместе с ней, поэтому раскладка пересобирается прямо в ходе
        /// жеста — как это делает картинка. У фигуры поверх или за текстом двигать
        /// нечего, и хватает перерисовки.
        /// </summary>
        private void RefreshAfterShapeGestureStep()
        {
            if (_selectedShape is not null && ShapeAffectsFlow(_selectedShape))
            {
                RebuildLayouts();
                InvalidateMeasure();
            }
            InvalidateFull();
        }

        /// <summary>
        /// Отпускание кнопки. Возвращает true, если завершён жест фигуры.
        /// </summary>
        private bool ShapePointerReleased(PointerReleasedEventArgs e)
        {
            if (!_shapeDragging && !_shapeResizing && !_shapeRotating) return false;

            FinishShapeGesture();
            e.Pointer.Capture(null);
            return true;
        }

        /// <summary>
        /// Завершает текущий жест фигуры: пишет правку в историю отмены и
        /// пересобирает раскладку — страница фигуры определяется заново, а
        /// обтекание досчитывается до сходимости (во время жеста проход был один).
        /// </summary>
        private void FinishShapeGesture()
        {
            bool moved = _shapeDragMoved || _shapeResizeMoved || _shapeRotateMoved;

            // Фигуру в потоке переставляем ДО фиксации правки свойств: сама
            // перестановка — структурная операция и в историю идёт своим снимком.
            bool droppedInFlow = false;
            if (_shapeDragMoved && _selectedShape is { WrapMode: WrapMode.Inline } inlineShape)
            {
                CancelShapeEdit();
                DropInlineShapeAtCaret(inlineShape);
                droppedInFlow = true;
            }

            _shapeDragging = false;
            _shapeDragMoved = false;
            _shapeResizing = false;
            _shapeResizeMoved = false;
            _shapeResizeCorner = -1;
            _shapeRotating = false;
            _shapeRotateMoved = false;

            if (droppedInFlow) { /* правка уже записана снимком документа */ }
            else if (moved) CommitShapeEdit();
            else CancelShapeEdit();

            Cursor = new Cursor(StandardCursorType.Ibeam);

            if (moved && !droppedInFlow)
            {
                RebuildLayouts();
                InvalidateMeasure();
                ShapeSelectionChanged?.Invoke(true);
            }
            InvalidateFull();
        }

        /// <summary>
        /// Новое положение и размер выделенной фигуры по точке указателя.
        /// Противоположный маркеру угол остаётся на месте — в том числе у
        /// повёрнутой фигуры, поэтому счёт идёт в её собственной системе координат,
        /// а центр пересчитывается обратно в координаты полотна.
        /// </summary>
        private void ResizeSelectedShapeTo(float xPt, float yPt, bool shiftDown)
        {
            if (_selectedShape is null) return;
            int corner = _shapeResizeCorner;
            if (corner < 0 || corner > 7) return;

            float hw = _shapeResizeStartW / 2f;
            float hh = _shapeResizeStartH / 2f;
            float cx = _shapeResizeStartLeft + hw;
            float cy = _shapeResizeStartTop + hh;

            float rotDeg = (float)_selectedShape.RotationDeg;
            var (px, py) = RotatePointAround(xPt, yPt, cx, cy, -rotDeg);

            // Поправка на точку захвата: маркер стоит снаружи габарита, и без неё
            // фигура прыгала бы на зазор в самом начале жеста.
            float localX = px - cx - _shapeResizeGrabDX;
            float localY = py - cy - _shapeResizeGrabDY;

            // Знак стороны, за которую тянут: 0 — сторона не участвует.
            int sx = corner switch
            {
                0 or 3 or 7 => -1,
                1 or 2 or 5 => 1,
                _ => 0
            };
            int sy = corner switch
            {
                0 or 1 or 4 => -1,
                2 or 3 or 6 => 1,
                _ => 0
            };

            float newW = _shapeResizeStartW;
            float newH = _shapeResizeStartH;
            float mx = 0f;
            float my = 0f;

            if (sx != 0)
            {
                float anchorX = -sx * hw;
                float edgeX = localX;
                if (sx > 0) edgeX = Math.Max(edgeX, anchorX + ShapeMinSidePt);
                else edgeX = Math.Min(edgeX, anchorX - ShapeMinSidePt);
                newW = Math.Abs(edgeX - anchorX);
                mx = (anchorX + edgeX) / 2f;
            }

            if (sy != 0)
            {
                float anchorY = -sy * hh;
                float edgeY = localY;
                if (sy > 0) edgeY = Math.Max(edgeY, anchorY + ShapeMinSidePt);
                else edgeY = Math.Min(edgeY, anchorY - ShapeMinSidePt);
                newH = Math.Abs(edgeY - anchorY);
                my = (anchorY + edgeY) / 2f;
            }

            // Пропорции: замок на фигуре либо зажатый Shift. Работает только на
            // угловых маркерах — у боковых вторая сторона не участвует в жесте,
            // и «сохранять» там нечего.
            bool keepAspect = (_selectedShape.LockAspectRatio || shiftDown)
                && sx != 0 && sy != 0
                && _shapeResizeStartW > 0f && _shapeResizeStartH > 0f;
            if (keepAspect)
            {
                float aspect = _shapeResizeStartW / _shapeResizeStartH;

                // Ведёт та сторона, которую растянули сильнее: иначе при движении
                // строго по одной оси фигура почти не меняется.
                if (newW / _shapeResizeStartW >= newH / _shapeResizeStartH)
                    newH = Math.Max(newW / aspect, ShapeMinSidePt);
                else
                    newW = Math.Max(newH * aspect, ShapeMinSidePt);

                // Якорь остаётся на месте: центр отсчитывается от него заново.
                mx = -sx * hw + sx * newW / 2f;
                my = -sy * hh + sy * newH / 2f;
            }

            // Сдвиг центра задан в системе фигуры — на полотно он переносится
            // поворотом на тот же угол.
            double rad = rotDeg * Math.PI / 180.0;
            float cos = (float)Math.Cos(rad);
            float sin = (float)Math.Sin(rad);
            float newCx = cx + mx * cos - my * sin;
            float newCy = cy + mx * sin + my * cos;

            float newLeft = newCx - newW / 2f;
            float newTop = newCy - newH / 2f;

            _selectedShape.WidthPt = newW;
            _selectedShape.HeightPt = newH;
            _selectedShape.OffsetXPt = _shapeResizeStartModelX + (newLeft - _shapeResizeStartLeft);
            _selectedShape.OffsetYPt = _shapeResizeStartModelY + (newTop - _shapeResizeStartTop);

            float shift = GetPageShiftXPt();
            UpdateShapeEntry(_selectedShape, newLeft - shift, newTop, newW, newH);
        }

        /// <summary>
        /// Обработка клавиш для выделенной фигуры: удаление, снятие выделения,
        /// копирование, вставка и дублирование.
        /// Возвращает true, если нажатие обработано.
        /// </summary>
        private bool ShapeKeyDown(KeyEventArgs e)
        {
            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

            // Вставка работает и без выделения: скопированную фигуру кладут туда,
            // где сейчас каретка.
            if (ctrl && e.Key == Key.V && _shapeClipboard is not null)
            {
                if (IsEditingBlocked) return true;
                PasteShapeFromClipboard();
                return true;
            }

            if (_selectedShape is null) return false;

            if (e.Key == Key.Escape)
            {
                ClearShapeSelection();
                return true;
            }

            if (ctrl && (e.Key == Key.C || e.Key == Key.X))
            {
                _shapeClipboard = Services.DocumentCloner.CloneBlock(_selectedShape) as ShapeBlock;
                if (e.Key == Key.X && !IsEditingBlocked) DeleteSelectedShape();
                return true;
            }

            if (ctrl && e.Key == Key.D)
            {
                if (IsEditingBlocked) return true;
                _shapeClipboard = Services.DocumentCloner.CloneBlock(_selectedShape) as ShapeBlock;
                PasteShapeFromClipboard();
                return true;
            }

            if (e.Key == Key.Delete || e.Key == Key.Back)
            {
                if (IsEditingBlocked) return true;
                DeleteSelectedShape();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Кладёт копию фигуры из буфера в документ со сдвигом и делает её выделенной.
        /// Идентификатор у копии свой: клон сохраняет исходный, а два блока с одним
        /// Id ломают дельта-кеш и отмену.
        /// </summary>
        private void PasteShapeFromClipboard()
        {
            if (_shapeClipboard is null || DocVm is null) return;

            var copy = Services.DocumentCloner.CloneBlock(_shapeClipboard) as ShapeBlock;
            if (copy is null) return;

            copy.Id = Guid.NewGuid();
            copy.OffsetXPt += ShapePasteOffsetPt;
            copy.OffsetYPt += ShapePasteOffsetPt;

            DocVm.InsertShapeBlock(copy);
        }

        /// <summary>Удаляет выделенную фигуру из документа.</summary>
        private void DeleteSelectedShape()
        {
            var shape = _selectedShape;
            if (shape is null || DocVm is null) return;

            _selectedShape = null;
            ShapeSelectionChanged?.Invoke(false);

            // Снимок документа, а не гранулярная команда свойств: удаление меняет
            // состав блоков, и возвращать значения одного блока некуда — блока в
            // документе уже нет.
            BeginEdit("Удаление фигуры");
            DocVm.RemoveShape(shape);
            CommitEdit();

            RebuildLayouts();
            InvalidateMeasure();
            InvalidateFull();
        }

        /// <summary>
        /// Делает фигуру выделенной. Выделение картинки, текста и ячеек при этом
        /// снимается: одновременно активен ровно один объект.
        /// </summary>
        private void SelectShape(ShapeBlock shape)
        {
            if (_selectedImage is not null)
            {
                ExitImageCropMode(apply: true);
                _selectedImage = null;
                DocVm?.FireCursorContextChanged();
                ImageSelectionChanged?.Invoke(false);
            }

            _isSelecting = false;
            _tableSelections.Clear();

            if (ReferenceEquals(_selectedShape, shape)) return;

            _selectedShape = shape;
            ShapeSelectionChanged?.Invoke(true);
        }

        /// <summary>Снимает выделение фигуры, если оно было.</summary>
        private void ClearShapeSelection()
        {
            if (_selectedShape is null) return;
            _selectedShape = null;
            ShapeSelectionChanged?.Invoke(false);
            InvalidateFull();
        }

        /// <summary>
        /// Вставленная из ленты фигура сразу становится выделенной: пользователь
        /// видит, что именно появилось на странице, и может её тут же двигать.
        /// </summary>
        private void OnShapeInserted(ShapeBlock shape)
        {
            SelectShape(shape);
            InvalidateFull();
        }

        // ── Свойства выделенной фигуры (лента) ────────────────────────────

        /// <summary>
        /// Сводка по выделенной фигуре для ленты. null — фигура не выделена, и
        /// лента оставляет свои поля как есть: вкладка в этот момент уже скрыта.
        /// </summary>
        private (ShapeType Type, WrapMode Wrap, WrapSide WrapSide, ShapeDashStyle Dash,
                 ShapeArrowHead StartArrow, ShapeArrowHead EndArrow,
                 string? FillColor, string? StrokeColor, double StrokeThicknessPt,
                 double CornerRadiusPt, double Opacity, double WidthPt, double HeightPt,
                 double RotationDeg, bool LockAspect, int PinnedPage,
                 bool HasFillImage, bool FillImageStretch)? GetSelectedShapeInfo()
        {
            var s = _selectedShape;
            if (s is null) return null;

            return (s.ShapeType, s.WrapMode, s.WrapSide, s.DashStyle,
                    s.StartArrow, s.EndArrow,
                    s.FillColor, s.StrokeColor, s.StrokeThicknessPt,
                    s.CornerRadiusPt, s.Opacity, s.WidthPt, s.HeightPt,
                    s.RotationDeg, s.LockAspectRatio, s.PinnedPage,
                    !string.IsNullOrEmpty(s.FillImageFileName), s.FillImageStretch);
        }

        /// <summary>
        /// Общий вход для правок с ленты: снимок до, изменение, снимок после и
        /// пересборка. Всё, что лента делает с фигурой, проходит здесь — иначе
        /// каждая кнопка заводила бы свою пару Begin/Commit и половину забывала.
        /// </summary>
        private void EditSelectedShape(string description, Action<ShapeBlock> change)
        {
            var shape = _selectedShape;
            if (shape is null || IsEditingBlocked) return;

            BeginShapeEdit(description);
            change(shape);
            CommitShapeEdit();

            RebuildLayouts();
            InvalidateMeasure();
            InvalidateFull();
            ShapeSelectionChanged?.Invoke(true);
        }

        private void SetSelectedShapeType(ShapeType type)
            => EditSelectedShape("Вид фигуры", s => s.ShapeType = type);

        private void SetSelectedShapeFill(string? hexColor)
            => EditSelectedShape("Заливка фигуры", s => s.FillColor = hexColor);

        private void SetSelectedShapeStroke(string? hexColor)
            => EditSelectedShape("Цвет обводки", s => s.StrokeColor = hexColor);

        private void SetSelectedShapeStrokeThickness(double thicknessPt)
            => EditSelectedShape("Толщина обводки",
                s => s.StrokeThicknessPt = Math.Clamp(thicknessPt, 0.0, 72.0));

        private void SetSelectedShapeDash(ShapeDashStyle dash)
            => EditSelectedShape("Штрих обводки", s => s.DashStyle = dash);

        /// <summary>
        /// Положение обводки относительно контура — то же, что положение рамки
        /// у картинки. Меняет и габарит пятна фигуры на листе, поэтому раскладка
        /// пересобирается: обводка наружу отодвигает обтекающий текст.
        /// </summary>
        private void SetSelectedShapeStrokeAlign(ImageBorderAlign align)
            => EditSelectedShape("Положение обводки", s => s.StrokeAlign = align);

        private void ToggleSelectedShapeFlipHorizontal()
            => EditSelectedShape("Отражение фигуры",
                s => s.FlipHorizontal = !s.FlipHorizontal);

        private void ToggleSelectedShapeFlipVertical()
            => EditSelectedShape("Отражение фигуры",
                s => s.FlipVertical = !s.FlipVertical);

        private void SetSelectedShapeCornerRadius(double radiusPt)
            => EditSelectedShape("Скругление углов",
                s => s.CornerRadiusPt = Math.Clamp(radiusPt, 0.0, 400.0));

        private void SetSelectedShapeArrows(ShapeArrowHead start, ShapeArrowHead end)
            => EditSelectedShape("Наконечники", s => { s.StartArrow = start; s.EndArrow = end; });

        private void SetSelectedShapeOpacity(double opacity)
            => EditSelectedShape("Прозрачность фигуры",
                s => s.Opacity = Math.Clamp(opacity, 0.0, 1.0));

        private void SetSelectedShapeWidth(double widthPt)
            => EditSelectedShape("Ширина фигуры", s =>
            {
                double w = Math.Max(widthPt, ShapeMinSidePt);
                if (s.LockAspectRatio && s.WidthPt > 0.0)
                    s.HeightPt = Math.Max(s.HeightPt * (w / s.WidthPt), ShapeMinSidePt);
                s.WidthPt = w;
            });

        private void SetSelectedShapeHeight(double heightPt)
            => EditSelectedShape("Высота фигуры", s =>
            {
                double h = Math.Max(heightPt, ShapeMinSidePt);
                if (s.LockAspectRatio && s.HeightPt > 0.0)
                    s.WidthPt = Math.Max(s.WidthPt * (h / s.HeightPt), ShapeMinSidePt);
                s.HeightPt = h;
            });

        private void SetSelectedShapeRotation(double degrees)
            => EditSelectedShape("Поворот фигуры", s =>
            {
                double d = degrees % 360.0;
                if (d < 0) d += 360.0;
                s.RotationDeg = d;
            });

        private void SetSelectedShapeLockAspect(bool locked)
            => EditSelectedShape("Пропорции фигуры", s => s.LockAspectRatio = locked);

        private void SetSelectedShapeWrapMode(WrapMode mode)
            => EditSelectedShape("Обтекание фигуры", s =>
            {
                // Уход из потока в плавающий режим: фигура должна остаться там, где
                // её видно, а не прыгнуть в левый верхний угол. Позиция берётся из
                // текущей записи раскладки и переводится в смещения от своей страницы.
                if (s.WrapMode == WrapMode.Inline && mode != WrapMode.Inline)
                {
                    var entry = FindShapeEntry(s);
                    List<PageRect> pages;
                    lock (_renderLock) { pages = _pages; }

                    if (entry is not null && entry.PageIndex >= 0 && entry.PageIndex < pages.Count)
                    {
                        var pg = pages[entry.PageIndex];
                        s.OffsetXPt = entry.XPt - pg.PadLeftPt - pg.MarginLeftPt;
                        s.OffsetYPt = entry.Ypt - pg.Ypt - pg.PadTopPt;
                    }
                }

                s.WrapMode = mode;

                // В потоке привязка к странице смысла не имеет: место фигуры
                // определяет текст, а не номер листа.
                if (mode == WrapMode.Inline) s.PinnedPage = 0;
            });

        private void SetSelectedShapeWrapSide(WrapSide side)
            => EditSelectedShape("Сторона обтекания", s => s.WrapSide = side);

        private void SetSelectedShapeWrapPadding(double topPt, double bottomPt, double leftPt, double rightPt)
            => EditSelectedShape("Отступы обтекания", s =>
            {
                s.WrapPadTopPt = Math.Max(topPt, 0.0);
                s.WrapPadBottomPt = Math.Max(bottomPt, 0.0);
                s.WrapPadLeftPt = Math.Max(leftPt, 0.0);
                s.WrapPadRightPt = Math.Max(rightPt, 0.0);
            });

        private void SetSelectedShapeAlignment(Models.Styles.TextAlignment alignment)
            => EditSelectedShape("Выравнивание фигуры", s => s.Alignment = alignment);

        /// <summary>
        /// Закрепляет фигуру за страницей, на которой она сейчас стоит, либо снимает
        /// закрепление. Смещения при этом пересчитываются от краёв закреплённой
        /// страницы: иначе фигура прыгнула бы на другой лист.
        /// </summary>
        private void SetSelectedShapePinned(bool pinned)
            => EditSelectedShape("Привязка фигуры к странице", s =>
            {
                if (!pinned) { s.PinnedPage = 0; return; }

                var entry = FindShapeEntry(s);
                if (entry is null || entry.PageIndex < 0) return;

                List<PageRect> pages;
                lock (_renderLock) { pages = _pages; }
                if (entry.PageIndex >= pages.Count) return;

                var pg = pages[entry.PageIndex];
                s.OffsetXPt = entry.XPt - pg.PadLeftPt - pg.MarginLeftPt;
                s.OffsetYPt = entry.Ypt - pg.Ypt - pg.PadTopPt;
                s.PinnedPage = entry.PageIndex + 1;
            });

        /// <summary>Номер страницы выделенной фигуры (1-based), либо null.</summary>
        private int? GetSelectedShapePage()
        {
            var entry = FindShapeEntry(_selectedShape);
            return entry is null || entry.PageIndex < 0 ? null : entry.PageIndex + 1;
        }

        /// <summary>
        /// Порядок перекрытия. Фигуры сортируются при отрисовке по ZOrder, поэтому
        /// «на передний план» — это стать больше всех, «на задний» — меньше всех.
        /// Шаг в единицу: соседние значения не сливаются, а число остаётся мелким.
        /// </summary>
        private void SetSelectedShapeZOrder(bool toFront)
            => EditSelectedShape(toFront ? "На передний план" : "На задний план", s =>
            {
                List<ShapeEntry> shapes;
                lock (_renderLock) { shapes = _shapes; }

                int best = s.ZOrder;
                bool any = false;
                foreach (var se in shapes)
                {
                    if (ReferenceEquals(se.Block, s)) continue;
                    if (!any) { best = se.Block.ZOrder; any = true; continue; }
                    best = toFront
                        ? Math.Max(best, se.Block.ZOrder)
                        : Math.Min(best, se.Block.ZOrder);
                }

                if (!any) { s.ZOrder = 0; return; }
                s.ZOrder = toFront ? best + 1 : best - 1;
            });

        /// <summary>
        /// Кладёт картинку в фигуру: файл уходит в кладовку документа рядом с
        /// обычными картинками, в фигуре остаётся его имя. Пустой путь снимает
        /// заливку картинкой и возвращает одноцветную.
        /// </summary>
        private void SetSelectedShapeFillImage(string? filePath)
        {
            if (_selectedShape is null || IsEditingBlocked) return;

            if (string.IsNullOrEmpty(filePath))
            {
                EditSelectedShape("Заливка фигуры картинкой", s => s.FillImageFileName = null);
                return;
            }

            string? stored = DocVm?.StoreImageFile(filePath);
            if (string.IsNullOrEmpty(stored)) return;

            EditSelectedShape("Заливка фигуры картинкой", s => s.FillImageFileName = stored);
        }

        private void SetSelectedShapeFillImageStretch(bool stretch)
            => EditSelectedShape("Растяжение заливки", s => s.FillImageStretch = stretch);

        // ── Undo ──────────────────────────────────────────────────────────

        private void BeginShapeEdit(string description)
        {
            if (_selectedShape is null) return;
            _pendingShapeCommand = new ShapePropertiesCommand(_selectedShape, description)
            {
                Changed = () =>
                {
                    RebuildLayouts();
                    InvalidateMeasure();
                    InvalidateFull();
                    ShapeSelectionChanged?.Invoke(_selectedShape is not null);
                }
            };
        }

        private void CommitShapeEdit()
        {
            if (_pendingShapeCommand is null) return;
            DocVm?.RaiseContentModified();
            if (UndoStack is null) { _pendingShapeCommand = null; return; }
            _pendingShapeCommand.Commit();
            UndoStack.Push(_pendingShapeCommand);
            RecordSnapshotInOrder();
            _pendingShapeCommand = null;
        }

        private void CancelShapeEdit() => _pendingShapeCommand = null;
    }
}
