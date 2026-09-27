using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using System;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Плавная прокрутка колесом мыши.
    ///
    /// Раньше колесо обрабатывал сам ScrollViewer: каждый щелчок колеса мгновенно
    /// переставлял смещение на фиксированный шаг. Глаз видит это как рывки — лист
    /// прыгает ступеньками, и прокрутка ощущается тяжёлой, особенно на плотном тексте.
    ///
    /// Теперь щелчок колеса сдвигает не смещение, а ЦЕЛЬ прокрутки, и смещение
    /// догоняет её покадрово по экспоненте: быстро в начале, мягко в конце. Кадры идут
    /// от RequestAnimationFrame верхнего окна — в такт обновлению экрана, а не по
    /// таймеру диспетчера, у которого шаг плавает и даёт дрожь. Серия щелчков
    /// складывается в одну цель, поэтому быстрая прокрутка не тормозит на каждом
    /// щелчке, а разгоняется.
    ///
    /// Точная прокрутка тачпада (дробные дельты) идёт тем же путём: шаг у неё маленький,
    /// и анимация догоняет его почти сразу.
    ///
    /// Если смещение изменил кто-то другой — ползунок, прокрутка к каретке,
    /// автопрокрутка выделения, — анимация колеса отдаёт управление ему и гаснет.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        // Сдвиг за один щелчок колеса, px экрана. Тот же, что у ScrollViewer Avalonia
        // для немасштабируемой прокрутки, — расстояние на щелчок не меняется, меняется
        // только то, как лист до него доезжает.
        private const double WheelStepPx = 50.0;

        // Постоянная времени догоняния цели, мс. Меньше — резче, больше — мягче и
        // «тяжелее». За три таких интервала смещение проходит 95% пути.
        private const double WheelSmoothTauMs = 55.0;

        private bool _wheelAnimActive;
        private double _wheelTargetY;
        private double _wheelCurrentY;
        private TimeSpan _wheelLastFrame;
        private bool _wheelHasLastFrame;

        /// <summary>
        /// Обрабатывает вертикальное колесо плавной прокруткой. false — случай не наш
        /// (нет ScrollViewer, горизонтальная прокрутка, Shift), и событие уходит обычным
        /// путём.
        /// </summary>
        private bool TryHandleSmoothWheel(PointerWheelEventArgs e)
        {
            if (_parentScrollViewer is not { } sv) return false;

            // Горизонталь (Shift + колесо, наклон колеса, тачпад вбок) оставляем
            // ScrollViewer: плавность нужна прежде всего по вертикали.
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return false;
            if (Math.Abs(e.Delta.Y) < 1e-6) return false;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null) return false;

            double maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
            if (maxY <= 0) return false;

            // Прокрутка к каретке (SmoothScrollTo) идёт своим таймером. Колесо важнее:
            // человек крутит его прямо сейчас.
            _scrollAnimTimer?.Stop();

            double currentY = sv.Offset.Y;

            // Новая серия щелчков начинается от фактического смещения. Внутри серии
            // цель копится: следующий щелчок добавляется к ещё не достигнутой цели.
            if (!_wheelAnimActive || Math.Abs(currentY - _wheelCurrentY) > 1.0)
            {
                _wheelTargetY = currentY;
                _wheelCurrentY = currentY;
            }

            _wheelTargetY = Math.Clamp(_wheelTargetY - e.Delta.Y * WheelStepPx, 0, maxY);

            if (!_wheelAnimActive)
            {
                _wheelAnimActive = true;
                _wheelHasLastFrame = false;
                topLevel.RequestAnimationFrame(OnWheelAnimationFrame);
            }

            PerfCount("ui.wheel");
            return true;
        }

        private void OnWheelAnimationFrame(TimeSpan timestamp)
        {
            if (!_wheelAnimActive) return;

            if (_parentScrollViewer is not { } sv)
            {
                _wheelAnimActive = false;
                return;
            }

            // Смещение сдвинул кто-то другой — отдаём управление ему.
            if (Math.Abs(sv.Offset.Y - _wheelCurrentY) > 1.0)
            {
                _wheelAnimActive = false;
                return;
            }

            double dtMs = _wheelHasLastFrame
                ? Math.Clamp((timestamp - _wheelLastFrame).TotalMilliseconds, 1.0, 50.0)
                : 16.7;
            _wheelLastFrame = timestamp;
            _wheelHasLastFrame = true;

            double maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
            _wheelTargetY = Math.Clamp(_wheelTargetY, 0, maxY);

            double remaining = _wheelTargetY - _wheelCurrentY;
            double k = 1.0 - Math.Exp(-dtMs / WheelSmoothTauMs);
            double nextY = Math.Abs(remaining) < 0.5
                ? _wheelTargetY
                : _wheelCurrentY + remaining * k;

            _wheelCurrentY = nextY;
            sv.Offset = new Vector(sv.Offset.X, nextY);

            if (Math.Abs(_wheelTargetY - nextY) < 0.5)
            {
                _wheelAnimActive = false;
                return;
            }

            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnWheelAnimationFrame);
        }
    }
}
