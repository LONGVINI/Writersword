using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Writersword.Modules.Characters.Controls
{
    /// <summary>
    /// ItemsRepeater с защитой от зацикленного перемера.
    ///
    /// Защита: когда виртуализованный UniformGridLayout получает пустую
    /// область реализации при непустой коллекции (папка вне видимой области),
    /// он отвечает нулевой высотой и сам инвалидирует себя внутри измерения —
    /// LayoutManager перемеряет его до капа очереди каждый проход, бесконечно
    /// (замер показывал 400-1400 перемеров в секунду на папках с одной
    /// карточкой, по 2 мс каждый). Здесь такой репитер переводится в режим
    /// удержания: базовое измерение не вызывается вовсе, возвращается
    /// последняя настоящая высота. Право на настоящий перемер возвращают
    /// изменение числа элементов, изменение ширины контейнера, реальный сдвиг
    /// EffectiveViewport и страховочная попытка раз в секунду.
    ///
    /// Счётчики Measure/Arrange с временем остаются: их читает проба отсоединения
    /// вкладки в DockFactory.
    /// </summary>
    public class PerfItemsRepeater : ItemsRepeater
    {
        public int MeasureCount;
        public int ArrangeCount;
        public double MeasureMs;
        public double ArrangeMs;

        // Состояние защиты от зацикленного перемера.
        //
        // Второй предохранитель — по числу перемеров внутри одного прохода.
        // Первый (по нулевой высоте) ловит только случай «папка вне видимой
        // области». Есть и другой цикл, с нормальной высотой: базовое
        // измерение меняет размеры, от этого меняется видимая область,
        // EffectiveViewportChanged ставит _viewportKicked, тот снимает
        // удержание — и база зовётся снова. Зонд ловил такой круг на 1962
        // перемера за один проход по миллисекунде каждый.
        //
        // Счётчик обнуляется расстановкой: внутри прохода измерения её нет,
        // поэтому счёт растёт ровно пока проход не сходится. И он намеренно
        // НЕ обнуляется по _viewportKicked — иначе цикл сам себя и разрешал бы.
        private int _passMeasures;
        private const int MaxMeasuresPerPass = 8;

        private bool _holdActive;
        private long _lastBaseRunTick;
        private bool _viewportKicked;
        private Size _lastStableDesired;
        private double _lastStableWidth = double.NaN;
        private int _lastStableCount = -1;

        public PerfItemsRepeater()
        {
            EffectiveViewportChanged += OnSelfEffectiveViewportChanged;
        }

        private void OnSelfEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
            => _viewportKicked = true;

        protected override Size MeasureOverride(Size availableSize)
        {
            var now = Environment.TickCount64;

            int itemCount = (ItemsSource as System.Collections.ICollection)?.Count ?? 0;
            bool contextChanged = itemCount != _lastStableCount
                || double.IsNaN(_lastStableWidth)
                || Math.Abs(availableSize.Width - _lastStableWidth) >= 0.5;

            // Смена контекста — законный повод мерить заново, счёт начинается
            // сначала.
            if (contextChanged) _passMeasures = 0;
            _passMeasures++;

            // Проход не сходится: отдаём последний настоящий размер и не зовём
            // базу. Обычная работа укладывается в один-два перемера, так что
            // порог задевает только цикл.
            if (_passMeasures > MaxMeasuresPerPass
                && itemCount > 0
                && !contextChanged
                && _lastStableDesired.Height >= 1.0)
            {
                MeasureCount++;
                return _lastStableDesired;
            }

            // Режим удержания: базовая раскладка в зацикленном «скрытом»
            // состоянии не вызывается, размер отдаётся мгновенно. Иначе она
            // снова инвалидирует себя и цикл продолжается.
            if (_holdActive && itemCount > 0 && !contextChanged && !_viewportKicked
                && now - _lastBaseRunTick < 1000)
            {
                MeasureCount++;
                return _lastStableDesired;
            }

            _viewportKicked = false;
            _lastBaseRunTick = now;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = base.MeasureOverride(availableSize);
            sw.Stop();
            MeasureCount++;
            MeasureMs += sw.Elapsed.TotalMilliseconds;

            if (itemCount > 0)
            {
                if (result.Height < 1.0 && !contextChanged && _lastStableDesired.Height >= 1.0)
                {
                    _holdActive = true;
                    result = _lastStableDesired;
                }
                else if (result.Height >= 1.0)
                {
                    _holdActive = false;
                    _lastStableDesired = result;
                    _lastStableWidth = availableSize.Width;
                    _lastStableCount = itemCount;
                }
                else
                {
                    // Нулевая высота при сменившемся контексте либо без
                    // удержанного значения: запоминаем контекст, чтобы
                    // следующий нулевой ответ уже мог включить удержание
                    // (высота остаётся от последнего настоящего измерения).
                    _holdActive = false;
                    _lastStableWidth = availableSize.Width;
                    _lastStableCount = itemCount;
                }
            }
            else
            {
                _holdActive = false;
                _lastStableDesired = default;
                _lastStableWidth = double.NaN;
                _lastStableCount = -1;
            }

            return result;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            // Расстановка означает, что проход измерения сошёлся и закончился —
            // счёт перемеров начинается заново.
            _passMeasures = 0;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = base.ArrangeOverride(finalSize);
            sw.Stop();
            ArrangeCount++;
            ArrangeMs += sw.Elapsed.TotalMilliseconds;
            return result;
        }

        public void ResetPerfCounters()
        {
            MeasureCount = 0;
            ArrangeCount = 0;
            MeasureMs = 0;
            ArrangeMs = 0;
        }
    }
}
