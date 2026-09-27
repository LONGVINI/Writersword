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

        // Постоянная времени догоняния цели, мс. За три таких интервала масштаб
        // проходит 95% пути.
        private const double WheelZoomSmoothTauMs = 60.0;

        private bool _wheelZoomAnimActive;
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

            double baseZoom = BeginZoomSeries(docVm);
            double target = Math.Clamp(
                baseZoom * Math.Pow(WheelZoomStepFactor, delta),
                WheelZoomMin, WheelZoomMax);

            var p = e.GetPosition(this);
            CaptureZoomAnchorAtCanvasPoint(p.X, p.Y, docVm.Zoom);

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

                topLevel.RequestAnimationFrame(OnWheelZoomAnimationFrame);
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
        /// Привязка к середине окна. В самом начале документа привязка — верх окна:
        /// лист, который стоял у верхнего края, там и остаётся.
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

            SetZoomAnchor(viewX, viewY, displayedZoom);
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

            _lastZoomAnchorViewX = viewX;
            _lastZoomAnchorViewY = viewY;
            _lastZoomAnchorViewValid = true;
        }

        private void OnWheelZoomAnimationFrame(TimeSpan timestamp)
        {
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

            // Масштаб догоняет цель в логарифмической шкале: одинаково на любом
            // масштабе, без ускорения на крупном и замедления на мелком.
            double ratio = _wheelZoomTarget / Math.Max(_wheelZoomCurrent, 0.01);
            double k = 1.0 - Math.Exp(-dtMs / WheelZoomSmoothTauMs);
            bool arrive = Math.Abs(Math.Log(ratio)) < 0.002;
            double next = arrive
                ? _wheelZoomTarget
                : _wheelZoomCurrent * Math.Pow(ratio, k);

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

            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnWheelZoomAnimationFrame);
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

            SetZoomOffsetNow(
                _wheelZoomAnchorDocX * _wheelZoomCurrent - _wheelZoomAnchorViewX,
                _wheelZoomAnchorDocY * _wheelZoomCurrent - _wheelZoomAnchorViewY);
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
