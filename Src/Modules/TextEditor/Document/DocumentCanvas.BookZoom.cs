using Avalonia.Controls;
using System;
using Writersword.Modules.TextEditor.ViewModels;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Плавное приближение книги в чтении: Ctrl + колесо и Ctrl + плюс / минус.
    ///
    /// Раньше каждый щелчок сразу переставлял приближение на восьмую долю, и книга
    /// прыгала ступенькой. К тому же каждый шаг шёл полным применением вида чтения:
    /// картинки бумаги выгружались и читались с диска заново, снимки страниц для
    /// переворота выбрасывались. Для одного щелчка это незаметно, для анимации в
    /// шестьдесят кадров — рывки на каждом.
    ///
    /// Теперь приближение доезжает до цели покадрово, как масштаб в правке. На кадре
    /// меняется только геометрия: подгонка холста, место ленты, перемер. Полное
    /// применение вида идёт один раз, когда приближение встало.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        // Шаг приближения книги за один полный щелчок — тот же, что был у ChangeBookZoom.
        private const double BookZoomStepFactor = 1.12;

        // Постоянная времени догоняния цели, мс.
        private const double BookZoomSmoothTauMs = 60.0;

        private bool _bookZoomAnimActive;
        private double _bookZoomTarget;
        private double _bookZoomCurrent;
        private TimeSpan _bookZoomLastFrame;
        private bool _bookZoomHasLastFrame;
        private DocumentViewModel? _bookZoomDocVm;

        /// <summary>
        /// Приблизить или отдалить книгу на заданное число щелчков. Дробные щелчки
        /// тачпада дают дробный шаг.
        /// </summary>
        public void AnimateBookZoomBy(double notches)
        {
            if (DocVm is not { } docVm || Math.Abs(notches) < 1e-6) return;

            // Приближение сменил кто-то другой (ползунок ленты, Ctrl + 0) или полотно
            // перешло на другой документ — серия начинается заново.
            if (_bookZoomAnimActive
                && (!ReferenceEquals(_bookZoomDocVm, docVm)
                    || Math.Abs(docVm.Reading.Zoom - _bookZoomCurrent) > 1e-6))
                StopBookZoomAnimation(raiseVisual: true);

            if (!_bookZoomAnimActive)
            {
                _bookZoomCurrent = docVm.Reading.Zoom;
                _bookZoomTarget = _bookZoomCurrent;
            }

            _bookZoomTarget = Math.Clamp(
                _bookZoomTarget * Math.Pow(BookZoomStepFactor, notches),
                Models.Settings.ReadingSettings.MinZoom,
                Models.Settings.ReadingSettings.MaxZoom);

            if (!_bookZoomAnimActive && Math.Abs(_bookZoomTarget - _bookZoomCurrent) < 0.0005)
                return;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null)
            {
                SetBookZoom(_bookZoomTarget);
                return;
            }

            if (!_bookZoomAnimActive)
            {
                _bookZoomAnimActive = true;
                _bookZoomHasLastFrame = false;
                _bookZoomDocVm = docVm;
                topLevel.RequestAnimationFrame(OnBookZoomAnimationFrame);
            }
        }

        private void OnBookZoomAnimationFrame(TimeSpan timestamp)
        {
            if (!_bookZoomAnimActive) return;

            var docVm = DocVm;
            if (docVm is null || !ReferenceEquals(docVm, _bookZoomDocVm) || !ReadingActive)
            {
                StopBookZoomAnimation(raiseVisual: true);
                return;
            }

            // Приближение сменил кто-то другой — отдаём управление ему.
            if (Math.Abs(docVm.Reading.Zoom - _bookZoomCurrent) > 1e-6)
            {
                StopBookZoomAnimation(raiseVisual: false);
                return;
            }

            double dtMs = _bookZoomHasLastFrame
                ? Math.Clamp((timestamp - _bookZoomLastFrame).TotalMilliseconds, 1.0, 50.0)
                : 16.7;
            _bookZoomLastFrame = timestamp;
            _bookZoomHasLastFrame = true;

            double ratio = _bookZoomTarget / Math.Max(_bookZoomCurrent, 0.01);
            double k = 1.0 - Math.Exp(-dtMs / BookZoomSmoothTauMs);
            bool arrive = Math.Abs(Math.Log(ratio)) < 0.002;
            double next = arrive
                ? _bookZoomTarget
                : _bookZoomCurrent * Math.Pow(ratio, k);

            docVm.Reading.Zoom = next;
            _bookZoomCurrent = docVm.Reading.Zoom;

            ApplyBookZoomGeometry();

            if (arrive || Math.Abs(_bookZoomTarget - _bookZoomCurrent) < 1e-6)
            {
                StopBookZoomAnimation(raiseVisual: true);
                return;
            }

            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnBookZoomAnimationFrame);
        }

        /// <summary>
        /// Геометрическая часть применения вида чтения (см. ApplyReadingVisualSettings):
        /// то, что зависит от приближения. Бумага и снимки страниц не трогаются.
        /// </summary>
        private void ApplyBookZoomGeometry()
        {
            if (SpreadMode)
            {
                ResetReadingPan();
                FitCanvasToViewport();
            }
            else if (ReadingRibbon)
            {
                KeepReadingRibbonPlace();
            }

            InvalidateMeasure();
            InvalidateFull();
        }

        /// <summary>
        /// Гасит анимацию приближения книги. raiseVisual — применить вид чтения
        /// полностью, с итоговым приближением.
        /// </summary>
        private void StopBookZoomAnimation(bool raiseVisual)
        {
            _bookZoomAnimActive = false;

            var docVm = _bookZoomDocVm;
            _bookZoomDocVm = null;
            if (docVm is null) return;

            if (raiseVisual && ReferenceEquals(docVm, DocVm))
                docVm.RaiseReadingVisualChanged();
        }
    }
}
