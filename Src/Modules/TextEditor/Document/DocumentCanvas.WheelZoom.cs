using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.ViewModels;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Плавное масштабирование с привязкой к месту в документе.
    ///
    /// Раньше масштаб менялся мгновенно, а прокрутка при этом не трогалась. Документ
    /// растягивался от верхнего края холста: при том же смещении под окном оказывался
    /// совсем другой участок, и чем глубже в книге стоял человек, тем дальше улетал
    /// лист — на несколько страниц за один щелчок. Тачпад и колёса с мелким шагом
    /// присылают много дробных щелчков подряд, и каждый считался полным.
    ///
    /// Теперь любой способ сменить масштаб идёт одной дорогой:
    /// — Ctrl + колесо: шаг пропорционален величине щелчка, точка под курсором
    ///   остаётся под курсором;
    /// — клавиши масштаба, кнопки и ползунок строки состояния: масштаб доезжает до
    ///   цели, на месте остаётся середина окна;
    /// — масштаб, заданный извне мгновенно, тоже держит середину окна. Исключение —
    ///   начало документа: там верх листа остаётся наверху.
    ///
    /// Масштаб доезжает до цели покадрово, как прокрутка колесом. Серия щелчков
    /// складывается в одну цель. Пока идёт жест, лист не перерисовывается заново, а
    /// прежний снимок растягивается под новый масштаб (DocumentCanvas.ZoomPreview).
    ///
    /// Движение к цели — критически задемпфированная пружина: у масштаба есть
    /// скорость, и она меняется плавно. Прежде масштаб догонял цель по экспоненте:
    /// в первый кадр после щелчка скорость сразу была наибольшей и дальше гасла, и
    /// каждый щелчок давал толчок с места, а серия щелчков шла ступеньками.
    ///
    /// На месте остаётся каретка, если она видна в окне, — как у Word: человек
    /// смотрит туда, где пишет. Каретки в окне нет — точка под указателем мыши.
    ///
    /// Масштаб и прокрутка обязаны смениться в одном кадре. Если прокрутка отстаёт
    /// хотя бы на кадр, этот кадр показывает документ, растянутый от верха холста, —
    /// на глубине в сотню страниц это скачок на несколько листов, и следующий кадр
    /// возвращает его обратно: текст бьётся. ScrollViewer узнаёт новую высоту холста
    /// только после прохода раскладки, поэтому проход выполняется сразу, на месте,
    /// и прокрутка ставится уже по новой высоте — до отрисовки кадра.
    ///
    /// Число листов в ряду на время жеста замораживается: авто-режим при отдалении
    /// перекладывал листы в сетку прямо посреди анимации, и отдалённый документ
    /// рассыпался на пустые листы. Раскладка в ряд меняется один раз, когда масштаб
    /// встал, и место в документе при этом сохраняется по листу, а не по пикселям.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        // Шаг масштаба за один полный щелчок колеса.
        private const double WheelZoomStepFactor = 1.1;

        // Границы масштаба — те же, что держит DocumentViewModel.Zoom.
        private const double WheelZoomMin = 0.25;
        private const double WheelZoomMax = 5.0;

        // Собственная частота пружины масштаба, 1/с. Один щелчок проходит 95% пути
        // примерно за 215 мс, без перелёта.
        private const double WheelZoomSpringOmega = 22.0;

        // Скорость масштаба в логарифмической шкале, 1/с.
        private double _wheelZoomVelocity;

        private bool _wheelZoomAnimActive;

        // Кадр анимации уже заказан у окна. Жест, остановленный не из кадра (клик,
        // клавиша, смена документа), оставлял заказанный кадр в очереди; следующий
        // щелчок колеса заказывал ещё один, и анимацию вели две цепочки сразу: масштаб
        // шагал вдвое чаще и дёргался. Заказ теперь один на кадр.
        private bool _wheelZoomFrameRequested;
        private double _wheelZoomTarget;
        private double _wheelZoomCurrent;
        private TimeSpan _wheelZoomLastFrame;
        private bool _wheelZoomHasLastFrame;

        // Документ, у которого открыт жест масштабирования: ему и закрывать жест,
        // даже если полотно успело переключиться на другой документ.
        private DocumentViewModel? _wheelZoomDocVm;

        // Точка привязки: положение в единицах холста при масштабе 1 и её же
        // положение в окне прокрутки.
        private bool _wheelZoomAnchorValid;
        private double _wheelZoomAnchorDocX;
        private double _wheelZoomAnchorDocY;
        private double _wheelZoomAnchorViewX;
        private double _wheelZoomAnchorViewY;

        // Лист, на котором стоит точка привязки, и её место на нём по горизонтали, пт.
        // По горизонтали холст не растягивается от левого края: одиночный лист стоит
        // по центру окна, листы в ряд — по центру ряда. Привязка по пикселям холста
        // уводила вид к левому краю листа, как только лист становился шире окна.
        // По листу точка остаётся на месте при любом центрировании. -1 — листа нет,
        // привязка по пикселям.
        private int _wheelZoomAnchorPage = -1;
        private float _wheelZoomAnchorPageXPt;

        // Где была мышь (точка окна), когда снималась привязка жеста. Пока мышь не
        // ушла от неё дальше допуска, привязка жеста не меняется.
        private double _wheelZoomSeriesPointerViewX;
        private double _wheelZoomSeriesPointerViewY;
        private const double WheelZoomPointerSlopPx = 24.0;

        // Точка окна, которую держал последний жест масштаба. По ней же держится
        // место, когда после жеста меняется число листов в ряду.
        private bool _lastZoomAnchorViewValid;
        private double _lastZoomAnchorViewX;
        private double _lastZoomAnchorViewY;

        // Масштаб ставит сама анимация: смену Zoom не нужно принимать за чужую.
        private bool _applyingZoomStep;

        // Диагностика жеста: кадры анимации, самый долгий промежуток между ними и
        // самая долгая работа кадра в UI-потоке.
        private int _zoomDiagFrames;
        private double _zoomDiagMaxDtMs;
        private double _zoomDiagMaxUiMs;
        private double _zoomDiagFrom;
        private int _zoomDiagClamped;

        /// <summary>
        /// Ctrl + колесо в редакторе: плавный масштаб с привязкой к курсору.
        /// </summary>
        private void HandleWheelZoom(PointerWheelEventArgs e)
        {
            if (DocVm is not { } docVm) return;

            double delta = e.Delta.Y;

            // Наклон колеса вбок и горизонтальные жесты тачпада при зажатом Ctrl
            // масштаба не меняют.
            if (Math.Abs(delta) < 1e-6) return;

            bool seriesRunning = _wheelZoomAnimActive && ReferenceEquals(_wheelZoomDocVm, docVm);

            double baseZoom = BeginZoomSeries(docVm);
            double target = Math.Clamp(
                baseZoom * Math.Pow(WheelZoomStepFactor, delta),
                WheelZoomMin, WheelZoomMax);

            var p = e.GetPosition(this);
            double pointerViewX = p.X - (_parentScrollViewer?.Offset.X ?? 0);
            double pointerViewY = p.Y - (_parentScrollViewer?.Offset.Y ?? 0);

            // Точка привязки выбирается один раз на жест. Колесо с мелким шагом
            // присылает событие каждые 10–15 мс, рука на колесе чуть сдвигает мышь, и
            // привязка, снятая заново на каждом событии, каждый раз оказывалась чуть в
            // другом месте: весь документ дёргался вверх-вниз на эту разницу. Заново
            // точка берётся, только если мышь заметно сдвинули.
            bool keepAnchor = seriesRunning
                && _wheelZoomAnchorValid
                && Math.Abs(pointerViewX - _wheelZoomSeriesPointerViewX) <= WheelZoomPointerSlopPx
                && Math.Abs(pointerViewY - _wheelZoomSeriesPointerViewY) <= WheelZoomPointerSlopPx;

            if (!keepAnchor)
            {
                _wheelZoomSeriesPointerViewX = pointerViewX;
                _wheelZoomSeriesPointerViewY = pointerViewY;

                // На месте остаётся каретка, если её строка видна в окне: по вертикали —
                // её строка, по горизонтали — она сама, если и по ширине она в окне.
                // Иначе — точка под указателем мыши.
                double anchorX = p.X;
                double anchorY = p.Y;
                bool onCaret = false;
                if (TryGetCaretPointPx(out double caretX, out double caretY, out bool caretVisibleX, out bool caretVisibleY)
                    && caretVisibleY)
                {
                    anchorY = caretY;
                    if (caretVisibleX) anchorX = caretX;
                    onCaret = true;
                }
                CaptureZoomAnchorAtCanvasPoint(anchorX, anchorY, docVm.Zoom);

                _logger.Debug(
                    "[ZOOM] Привязка: {Source}, точка окна {X:F0}×{Y:F0}, лист {Page}",
                    onCaret ? "каретка" : "мышь",
                    _wheelZoomAnchorViewX, _wheelZoomAnchorViewY, _wheelZoomAnchorPage + 1);
            }

            RunZoomAnimation(docVm, target);
        }

        /// <summary>
        /// Запрос плавного масштаба от клавиш, кнопок и строки состояния. Привязка —
        /// середина окна.
        /// </summary>
        private void OnZoomAnimationRequested(double target)
        {
            if (DocVm is not { } docVm) return;

            double clamped = Math.Clamp(target, WheelZoomMin, WheelZoomMax);

            // Тот же запрос приходит повторно: строка состояния, показав цель, сообщает
            // её обратно. Привязку при этом не трогаем — иначе колесо теряло бы курсор.
            if (_wheelZoomAnimActive && ReferenceEquals(_wheelZoomDocVm, docVm)
                && Math.Abs(clamped - _wheelZoomTarget) < 1e-6)
                return;

            if (!_wheelZoomAnimActive && Math.Abs(clamped - docVm.Zoom) < 1e-6)
                return;

            BeginZoomSeries(docVm);
            CaptureZoomAnchorAtViewCenter(docVm.Zoom);

            RunZoomAnimation(docVm, clamped);
        }

        /// <summary>
        /// Готовит серию шагов масштаба и возвращает масштаб, от которого считать шаг:
        /// ещё не достигнутую цель, если серия уже идёт, иначе фактический масштаб.
        /// </summary>
        private double BeginZoomSeries(DocumentViewModel docVm)
        {
            // Прокрутка колесом и прокрутка к каретке уступают масштабу: человек
            // масштабирует прямо сейчас.
            _wheelAnimActive = false;
            _scrollAnimTimer?.Stop();

            // Полотно переключилось на другой документ посреди жеста — прежний жест
            // закрывается, новый открывается уже у нового документа.
            if (_wheelZoomAnimActive && !ReferenceEquals(_wheelZoomDocVm, docVm))
                StopWheelZoomAnimation();

            double displayed = docVm.Zoom;

            // Новая серия начинается от фактического масштаба. Внутри серии цель
            // копится. Если масштаб сменил кто-то другой, серия начинается заново от
            // его значения.
            if (!_wheelZoomAnimActive || Math.Abs(displayed - _wheelZoomCurrent) > 1e-6)
            {
                _wheelZoomCurrent = displayed;
                _wheelZoomTarget = displayed;
                _wheelZoomVelocity = 0;
            }

            return _wheelZoomTarget;
        }

        /// <summary>Запускает анимацию к цели или переводит идущую на новую цель.</summary>
        private void RunZoomAnimation(DocumentViewModel docVm, double target)
        {
            _wheelZoomTarget = target;

            if (Math.Abs(_wheelZoomTarget - _wheelZoomCurrent) < 1e-6)
                return;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null)
            {
                // Кадров анимации нет — ставим цель сразу, с той же привязкой.
                ApplyWheelZoomStep(docVm, _wheelZoomTarget);
                return;
            }

            if (!_wheelZoomAnimActive)
            {
                _wheelZoomAnimActive = true;
                _wheelZoomHasLastFrame = false;

                // Пока идёт жест, масштаб меняется на каждом кадре. Настройки вида
                // записываются один раз, когда масштаб доехал до цели.
                _wheelZoomDocVm = docVm;
                docVm.BeginZoomGesture();

                ResetZoomDiagnostics(docVm.Zoom);

                RequestWheelZoomFrame(topLevel);
            }

            // Строка состояния показывает цель, а не промежуточные кадры.
            docVm.SetZoomGestureTarget(_wheelZoomTarget);

            PerfCount("ui.wheelzoom");
        }

        /// <summary>
        /// Привязка к точке холста (координаты полотна). Масштаб — тот, при котором
        /// холст сейчас разложен.
        /// </summary>
        private void CaptureZoomAnchorAtCanvasPoint(double canvasX, double canvasY, double displayedZoom)
        {
            if (_parentScrollViewer is not { } sv)
            {
                _wheelZoomAnchorValid = false;
                return;
            }

            SetZoomAnchor(canvasX - sv.Offset.X, canvasY - sv.Offset.Y, displayedZoom);
        }

        /// <summary>
        /// Привязка к каретке, если она видна в окне, иначе к середине окна. В самом
        /// начале документа привязка — верх окна: лист, который стоял у верхнего края,
        /// там и остаётся.
        /// </summary>
        private void CaptureZoomAnchorAtViewCenter(double displayedZoom)
        {
            if (_parentScrollViewer is not { } sv)
            {
                _wheelZoomAnchorValid = false;
                return;
            }

            double viewX = sv.Viewport.Width / 2.0;
            double viewY = sv.Offset.Y < 0.5 ? 0.0 : sv.Viewport.Height / 2.0;

            if (TryGetCaretPointPx(out double caretX, out double caretY, out bool caretVisibleX, out bool caretVisibleY)
                && caretVisibleY)
            {
                viewY = caretY - sv.Offset.Y;
                if (caretVisibleX) viewX = caretX - sv.Offset.X;
            }

            SetZoomAnchor(viewX, viewY, displayedZoom);
        }

        /// <summary>
        /// Где каретка на холсте, px: её левый край по горизонтали и середина по
        /// вертикали, и видна ли она в окне по каждой из осей. Расчёт тот же, что у
        /// отрисовки каретки (DrawCaret): сдвиг строки по выравниванию и растяжка
        /// пробелов по ширине учтены, лист стоит там, где его ставит отрисовка.
        /// false — каретки нет или режим не страничный.
        /// </summary>
        private bool TryGetCaretPointPx(
            out double xPx, out double centerYPx, out bool visibleX, out bool visibleY)
        {
            xPx = 0;
            centerYPx = 0;
            visibleX = false;
            visibleY = false;

            if (_parentScrollViewer is not { } sv) return false;
            if (DocVm?.ViewMode != EditorViewMode.Page || ReadingActive || SpreadMode) return false;
            if (_caretPara < 0 || _caretPara >= _layouts.Count) return false;

            double zoom = Zoom;
            double s = PtToPx * zoom;
            var pl = _layouts[_caretPara];

            List<PageRect> pages;
            lock (_renderLock) { pages = _pages; }

            // Сдвиг листа каретки: в ряду — место листа в сетке, один в ряду — его
            // до-центрирование по живой ширине холста.
            float shiftXPt;
            float shiftYPt;
            if (_pagesPerRow > 1)
            {
                (shiftXPt, shiftYPt) = PageVisualDelta(pl.PageIndex, pages);
            }
            else
            {
                shiftXPt = GetPageShiftXPt();
                shiftYPt = 0f;
            }

            float caretXPt;
            float caretYPt;
            float caretHPt;

            var layout = pl.Layout ?? GetOrBuildLayout(pl.Vm, (float)(_canvasWidth * PxToPt));

            if (string.IsNullOrEmpty(pl.Vm.PlainText))
            {
                var emptyCaret = layout.HitTestPosition(0);
                float emptyYBase = layout.Lines.Count > 0 ? layout.Lines[0].Y : 0f;
                float alignOffset = layout.Alignment switch
                {
                    Writersword.Core.Models.Rendering.TextAlignment.Center => layout.TextAreaWidthPt / 2f,
                    Writersword.Core.Models.Rendering.TextAlignment.Right => layout.TextAreaWidthPt,
                    _ => 0f
                };

                caretXPt = pl.AbsXPt + emptyCaret.X + alignOffset;
                caretYPt = pl.Ypt + (emptyCaret.Y - emptyYBase);
                caretHPt = emptyCaret.Height > 0.01f ? emptyCaret.Height : FallbackLinePt;
            }
            else
            {
                int pos = Clamp(_caretChar, 0, pl.Vm.PlainText?.Length ?? 0);
                var caret = layout.HitTestPosition(pos);

                float yBase = pl.LineFrom < layout.Lines.Count
                    ? layout.Lines[pl.LineFrom].Y : 0f;

                // Строка каретки — для сдвига по выравниванию.
                int lineIdx = Math.Max(0, Math.Min(pl.LineFrom, layout.Lines.Count - 1));
                for (int li = 0; li < layout.Lines.Count; li++)
                {
                    var line = layout.Lines[li];
                    if (pos >= line.FirstCharIndex && pos <= line.LastCharIndex + 1)
                    {
                        lineIdx = li;
                        break;
                    }
                }

                float alignOffset = 0f;
                if (layout.Lines.Count > 0)
                {
                    float firstLineBaked = lineIdx == 0 ? layout.FirstLineIndentPt : 0f;
                    alignOffset = LineAlignShift(layout, lineIdx) - firstLineBaked
                        + JustifyShiftBeforeChar(layout, lineIdx, pos);
                }

                caretXPt = pl.AbsXPt + caret.X + alignOffset;
                caretYPt = pl.Ypt + (caret.Y - yBase);
                caretHPt = caret.Height;
            }

            xPx = (caretXPt + shiftXPt) * s;
            double topPx = (caretYPt + shiftYPt) * s;
            double heightPx = caretHPt * s;
            centerYPx = topPx + heightPx / 2.0;

            visibleY = topPx >= sv.Offset.Y && topPx + heightPx <= sv.Offset.Y + sv.Viewport.Height;
            visibleX = xPx >= sv.Offset.X && xPx <= sv.Offset.X + sv.Viewport.Width;
            return true;
        }

        private void SetZoomAnchor(double viewX, double viewY, double displayedZoom)
        {
            if (_parentScrollViewer is not { } sv)
            {
                _wheelZoomAnchorValid = false;
                return;
            }

            double z = Math.Max(displayedZoom, 0.01);

            _wheelZoomAnchorViewX = viewX;
            _wheelZoomAnchorViewY = viewY;
            _wheelZoomAnchorDocX = (viewX + sv.Offset.X) / z;
            _wheelZoomAnchorDocY = (viewY + sv.Offset.Y) / z;
            _wheelZoomAnchorValid = true;

            CaptureZoomAnchorPage(viewX + sv.Offset.X, viewY + sv.Offset.Y, z);

            _lastZoomAnchorViewX = viewX;
            _lastZoomAnchorViewY = viewY;
            _lastZoomAnchorViewValid = true;
        }

        /// <summary>
        /// Лист под точкой привязки (px холста) и место точки на нём по горизонтали.
        /// Берётся лист того же ряда, ближайший по горизонтали.
        /// </summary>
        private void CaptureZoomAnchorPage(double canvasXPx, double canvasYPx, double zoom)
        {
            _wheelZoomAnchorPage = -1;

            if (DocVm?.ViewMode != EditorViewMode.Page || ReadingActive || SpreadMode) return;

            List<PageRect> pages;
            lock (_renderLock) { pages = _pages; }
            if (pages.Count == 0) return;

            double s = PtToPx * Math.Max(zoom, 0.01);
            float xPt = (float)(canvasXPx / s);
            float yPt = (float)(canvasYPx / s);

            int best = -1;
            float bestDy = float.MaxValue;
            float bestDx = float.MaxValue;
            for (int i = 0; i < pages.Count; i++)
            {
                var r = PageVisualRectPt(i, pages);
                float dy = yPt < r.Top ? r.Top - yPt : yPt > r.Bottom ? yPt - r.Bottom : 0f;
                float dx = xPt < r.Left ? r.Left - xPt : xPt > r.Right ? xPt - r.Right : 0f;

                if (dy < bestDy - 0.5f || (Math.Abs(dy - bestDy) <= 0.5f && dx < bestDx))
                {
                    best = i;
                    bestDy = dy;
                    bestDx = dx;
                }
            }

            if (best < 0) return;

            _wheelZoomAnchorPage = best;
            _wheelZoomAnchorPageXPt = xPt - PageVisualRectPt(best, pages).Left;
        }

        /// <summary>
        /// Ставит прокрутку по точке привязки при текущем масштабе. Проход раскладки —
        /// до расчёта: по нему холст узнаёт новую ширину, а от неё зависит, где стоит лист.
        /// </summary>
        private void ApplyZoomAnchorOffset()
        {
            UpdateLayout();

            double z = Math.Max(_wheelZoomCurrent, 0.01);
            double x = _wheelZoomAnchorDocX * z - _wheelZoomAnchorViewX;

            if (_wheelZoomAnchorPage >= 0)
            {
                List<PageRect> pages;
                lock (_renderLock) { pages = _pages; }
                if (_wheelZoomAnchorPage < pages.Count)
                {
                    var r = PageVisualRectPt(_wheelZoomAnchorPage, pages);
                    x = (r.Left + _wheelZoomAnchorPageXPt) * PtToPx * z - _wheelZoomAnchorViewX;
                }
            }

            SetZoomOffsetNow(x, _wheelZoomAnchorDocY * z - _wheelZoomAnchorViewY);
        }

        /// <summary>Заказывает следующий кадр анимации, если он ещё не заказан.</summary>
        private void RequestWheelZoomFrame(TopLevel? topLevel)
        {
            if (topLevel is null || _wheelZoomFrameRequested) return;
            _wheelZoomFrameRequested = true;
            topLevel.RequestAnimationFrame(OnWheelZoomAnimationFrame);
        }

        private void OnWheelZoomAnimationFrame(TimeSpan timestamp)
        {
            _wheelZoomFrameRequested = false;

            if (!_wheelZoomAnimActive) return;

            var docVm = DocVm;
            if (docVm is null || !ReferenceEquals(docVm, _wheelZoomDocVm) || ReadingActive)
            {
                StopWheelZoomAnimation();
                return;
            }

            // Масштаб сменил кто-то другой — отдаём управление ему.
            if (Math.Abs(docVm.Zoom - _wheelZoomCurrent) > 1e-6)
            {
                StopWheelZoomAnimation();
                return;
            }

            double dtMs = _wheelZoomHasLastFrame
                ? Math.Clamp((timestamp - _wheelZoomLastFrame).TotalMilliseconds, 1.0, 50.0)
                : 16.7;

            if (_wheelZoomHasLastFrame)
                _zoomDiagMaxDtMs = Math.Max(_zoomDiagMaxDtMs, (timestamp - _wheelZoomLastFrame).TotalMilliseconds);

            _wheelZoomLastFrame = timestamp;
            _wheelZoomHasLastFrame = true;

            // Масштаб идёт к цели в логарифмической шкале — одинаково на любом
            // масштабе — как критически задемпфированная пружина. Шаг считается точным
            // решением её уравнения на промежутке кадра: от длины кадра не зависит и
            // не раскачивается. Новый щелчок меняет только цель, скорость сохраняется.
            double logCurrent = Math.Log(Math.Max(_wheelZoomCurrent, 0.01));
            double logTarget = Math.Log(Math.Max(_wheelZoomTarget, 0.01));
            double offset = logCurrent - logTarget;
            double t = dtMs / 1000.0;
            double decay = Math.Exp(-WheelZoomSpringOmega * t);
            double c = _wheelZoomVelocity + WheelZoomSpringOmega * offset;
            double nextOffset = (offset + c * t) * decay;
            _wheelZoomVelocity = (_wheelZoomVelocity - WheelZoomSpringOmega * c * t) * decay;

            bool arrive = Math.Abs(nextOffset) < 0.002 && Math.Abs(_wheelZoomVelocity) < 0.05;
            double next = arrive
                ? _wheelZoomTarget
                : Math.Clamp(Math.Exp(logTarget + nextOffset), WheelZoomMin, WheelZoomMax);

            long uiStart = Stopwatch.GetTimestamp();
            ApplyWheelZoomStep(docVm, next);
            double uiMs = (Stopwatch.GetTimestamp() - uiStart) * 1000.0 / Stopwatch.Frequency;

            _zoomDiagFrames++;
            _zoomDiagMaxUiMs = Math.Max(_zoomDiagMaxUiMs, uiMs);

            if (arrive || Math.Abs(_wheelZoomTarget - _wheelZoomCurrent) < 1e-6)
            {
                StopWheelZoomAnimation();
                return;
            }

            RequestWheelZoomFrame(TopLevel.GetTopLevel(this));
        }

        /// <summary>
        /// Ставит масштаб и сразу же — прокрутку, при которой точка привязки остаётся
        /// на месте.
        /// </summary>
        private void ApplyWheelZoomStep(DocumentViewModel docVm, double zoom)
        {
            _applyingZoomStep = true;
            try
            {
                docVm.ApplyZoomGestureStep(zoom);
            }
            finally
            {
                _applyingZoomStep = false;
            }

            _wheelZoomCurrent = docVm.Zoom;

            if (!_wheelZoomAnchorValid) return;

            ApplyZoomAnchorOffset();
        }

        /// <summary>
        /// Масштаб сменили мгновенно, в обход анимации (ползунок при перетаскивании,
        /// восстановление вида). Середина окна остаётся на месте. Зовётся после того,
        /// как полотно приняло новый масштаб (флаг _zooming уже стоит).
        /// </summary>
        private void AnchorExternalZoomChange(double oldZoom, double newZoom)
        {
            if (_applyingZoomStep) return;
            if (_parentScrollViewer is not { } sv) return;
            if (_layouts.Count == 0 || _pendingViewRestore is not null) return;
            if (oldZoom <= 0 || Math.Abs(oldZoom - newZoom) < 1e-9) return;

            // Документ стоит в самом начале — открытие, первое вписывание масштаба:
            // верх листа остаётся наверху, прокрутку не трогаем.
            if (sv.Offset.X < 0.5 && sv.Offset.Y < 0.5) return;

            double viewX = sv.Viewport.Width / 2.0;
            double viewY = sv.Viewport.Height / 2.0;
            double docX = (viewX + sv.Offset.X) / oldZoom;
            double docY = (viewY + sv.Offset.Y) / oldZoom;

            _lastZoomAnchorViewX = viewX;
            _lastZoomAnchorViewY = viewY;
            _lastZoomAnchorViewValid = true;

            SetZoomOffsetNow(docX * newZoom - viewX, docY * newZoom - viewY);
        }

        /// <summary>
        /// Ставит прокрутку сейчас же, в том же кадре, что и масштаб.
        ///
        /// Проход раскладки выполняется на месте: после него ScrollViewer знает новую
        /// высоту холста, и смещение не обрежется по прежней. Второй проход раскладывает
        /// содержимое уже с новым смещением — кадр уходит на отрисовку готовым.
        /// </summary>
        private void SetZoomOffsetNow(double x, double y)
        {
            if (_parentScrollViewer is not { } sv) return;

            UpdateLayout();

            double maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
            double maxX = Math.Max(0, sv.Extent.Width - sv.Viewport.Width);

            double ty = Math.Clamp(y, 0, maxY);

            // По горизонтали лист, который уже окна, центрирует сама отрисовка: там
            // прокрутки нет, и трогать её незачем.
            double tx = maxX > 0.5 ? Math.Clamp(x, 0, maxX) : sv.Offset.X;

            if (Math.Abs(ty - y) > 0.5) _zoomDiagClamped++;

            if (Math.Abs(sv.Offset.Y - ty) > 0.01 || Math.Abs(sv.Offset.X - tx) > 0.01)
            {
                sv.Offset = new Vector(tx, ty);
                UpdateLayout();
            }

            _logger.Debug(
                "[ZOOM] Кадр: масштаб {Zoom:F3}, прокрутка цель {Target:F0} → стала {Actual:F0}, высота холста {Extent:F0}",
                Zoom, y, sv.Offset.Y, sv.Extent.Height);
        }

        // ── Смена числа листов в ряду после жеста ─────────────────────────

        /// <summary>
        /// Место в документе, привязанное к листу: номер листа и точка на нём. В отличие
        /// от пикселей холста переживает перекладку листов в ряд.
        /// </summary>
        private readonly record struct PageAnchor(
            int PageIndex, float OffsetYPt, double ViewY);

        /// <summary>
        /// Точка окна для привязки после жеста: та, что держал жест, иначе середина окна.
        /// </summary>
        private (double X, double Y) SettleAnchorViewPoint()
        {
            if (_lastZoomAnchorViewValid)
                return (_lastZoomAnchorViewX, _lastZoomAnchorViewY);

            var sv = _parentScrollViewer;
            return sv is null
                ? (0.0, 0.0)
                : (sv.Viewport.Width / 2.0, sv.Viewport.Height / 2.0);
        }

        /// <summary>
        /// Какой лист и какая точка на нём стоят в заданном месте окна. Только режим
        /// страниц: в остальных листов в ряд нет.
        /// </summary>
        private PageAnchor? CapturePageAnchor(double viewX, double viewY)
        {
            if (_parentScrollViewer is not { } sv) return null;
            if (DocVm?.ViewMode != EditorViewMode.Page || ReadingActive) return null;

            List<PageRect> pages;
            lock (_renderLock) { pages = _pages; }
            if (pages.Count == 0) return null;

            double s = PtToPx * Math.Max(Zoom, 0.01);
            float xPt = (float)((viewX + sv.Offset.X) / s);
            float yPt = (float)((viewY + sv.Offset.Y) / s);

            // Листы по одному отрисовка сдвигает до-центрированием по живой ширине
            // холста; в координатах раскладки этого сдвига нет.
            if (_pagesPerRow <= 1)
            {
                float canvasWPt = (float)(_canvasWidth * PxToPt);
                float curPageXPt = Math.Max((canvasWPt - GetPageWidthPt()) / 2f, 0f);
                xPt -= curPageXPt - _layoutPageXPt;
            }

            int pageIdx = NearestVisualPage(xPt, yPt, pages);
            var (_, dy) = PageVisualDelta(pageIdx, pages);
            float topPt = pages[pageIdx].Ypt + dy;

            return new PageAnchor(pageIdx, yPt - topPt, viewY);
        }

        /// <summary>
        /// Возвращает лист привязки в ту же точку окна после перекладки листов.
        /// </summary>
        private void RestorePageAnchor(PageAnchor anchor)
        {
            if (_parentScrollViewer is not { } sv) return;

            UpdateLayout();

            List<PageRect> pages;
            lock (_renderLock) { pages = _pages; }
            if (pages.Count == 0) return;

            int pageIdx = Math.Clamp(anchor.PageIndex, 0, pages.Count - 1);
            var (_, dy) = PageVisualDelta(pageIdx, pages);
            float topPt = pages[pageIdx].Ypt + dy;

            double s = PtToPx * Math.Max(Zoom, 0.01);
            double targetY = (topPt + anchor.OffsetYPt) * s - anchor.ViewY;

            SetZoomOffsetNow(sv.Offset.X, targetY);

            _logger.Debug(
                "[ZOOM] Листов в ряду стало {PerRow}: лист {Page} оставлен на месте, прокрутка {Offset:F0}",
                _pagesPerRow, pageIdx + 1, sv.Offset.Y);
        }

        // ── Диагностика ───────────────────────────────────────────────────

        private void ResetZoomDiagnostics(double fromZoom)
        {
            _zoomDiagFrames = 0;
            _zoomDiagMaxDtMs = 0;
            _zoomDiagMaxUiMs = 0;
            _zoomDiagFrom = fromZoom;
            _zoomDiagClamped = 0;
            System.Threading.Interlocked.Exchange(ref _zoomDiagPreviewFrames, 0);
            System.Threading.Interlocked.Exchange(ref _zoomDiagFullFrames, 0);
            _zoomDiagFullReason = string.Empty;
        }

        /// <summary>
        /// Гасит анимацию масштаба и закрывает жест: настройки вида записываются
        /// с итоговым масштабом.
        /// </summary>
        private void StopWheelZoomAnimation()
        {
            _wheelZoomAnimActive = false;
            _wheelZoomVelocity = 0;

            var gestureDocVm = _wheelZoomDocVm;
            _wheelZoomDocVm = null;
            if (gestureDocVm is null) return;

            gestureDocVm.EndZoomGesture();

            int preview = System.Threading.Interlocked.CompareExchange(ref _zoomDiagPreviewFrames, 0, 0);
            int full = System.Threading.Interlocked.CompareExchange(ref _zoomDiagFullFrames, 0, 0);

            _logger.Debug(
                "[ZOOM] Жест: масштаб {From:F3} → {To:F3}; кадров анимации {Frames}, самый долгий промежуток {MaxDt:F0} мс, " +
                "работа кадра в UI до {MaxUi:F1} мс; отрисовано растяжением {Preview}, полным рендером {Full} ({Reason}); " +
                "прокрутка упиралась в край {Clamped} раз; листов в ряду {PerRow}",
                _zoomDiagFrom, gestureDocVm.Zoom, _zoomDiagFrames, _zoomDiagMaxDtMs,
                _zoomDiagMaxUiMs, preview, full,
                string.IsNullOrEmpty(_zoomDiagFullReason) ? "—" : _zoomDiagFullReason,
                _zoomDiagClamped, _pagesPerRow);
        }
    }
}
