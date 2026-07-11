using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Writersword.Modules.Characters.ViewModels;

namespace Writersword.Modules.Characters.Controls
{
    /// <summary>
    /// ItemsRepeater с защитой от зацикленного перемера и временной
    /// диагностикой производительности.
    ///
    /// Защита: когда виртуализованный UniformGridLayout получает пустую
    /// область реализации при непустой коллекции (папка вне видимой области),
    /// он отвечает нулевой высотой и сам инвалидирует себя внутри измерения —
    /// LayoutManager перемеряет его до капа очереди каждый проход, бесконечно
    /// (зонд фиксировал 400-1400 перемеров в секунду на папках с одной
    /// карточкой, по 2 мс каждый). Здесь такой репитер переводится в режим
    /// удержания: базовое измерение не вызывается вовсе, возвращается
    /// последняя настоящая высота. Право на настоящий перемер возвращают
    /// изменение числа элементов, изменение ширины контейнера, реальный сдвиг
    /// EffectiveViewport и страховочная попытка раз в секунду.
    ///
    /// Диагностика: счётчики Measure/Arrange с временем для телеметрии
    /// CharactersListView и зонд-строки [RepeaterProbe]. После стабилизации
    /// диагностику убрать, защиту удержания оставить.
    /// </summary>
    public class PerfItemsRepeater : ItemsRepeater
    {
        private static readonly Serilog.ILogger _log =
            Serilog.Log.ForContext<PerfItemsRepeater>();

        public int MeasureCount;
        public int ArrangeCount;
        public double MeasureMs;
        public double ArrangeMs;

        private long _windowStartTick;
        private int _windowMeasureCount;
        private int _windowSamplesLogged;
        private bool _windowStackLogged;
        private bool _windowClampLogged;

        // Состояние защиты от зацикленного перемера.
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

        private string FolderName =>
            (DataContext as CharacterFolderViewModel)?.Name ?? "?";

        protected override Size MeasureOverride(Size availableSize)
        {
            var now = Environment.TickCount64;
            if (now - _windowStartTick >= 1000)
            {
                _windowStartTick = now;
                _windowMeasureCount = 0;
                _windowSamplesLogged = 0;
                _windowStackLogged = false;
                _windowClampLogged = false;
            }
            _windowMeasureCount++;

            int itemCount = (ItemsSource as System.Collections.ICollection)?.Count ?? 0;
            bool contextChanged = itemCount != _lastStableCount
                || double.IsNaN(_lastStableWidth)
                || Math.Abs(availableSize.Width - _lastStableWidth) >= 0.5;

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
                    if (!_windowClampLogged)
                    {
                        _windowClampLogged = true;
                        _log.Debug(
                            "[RepeaterProbe] '{Folder}' нулевой ответ при непустой коллекции: режим удержания, высота {H:F0}",
                            FolderName, _lastStableDesired.Height);
                    }
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

            if (_windowSamplesLogged < 3)
            {
                _windowSamplesLogged++;
                _log.Debug(
                    "[RepeaterProbe] '{Folder}' measure #{N}: avail={AvailW:F2}x{AvailH:F2}, desired={DesW:F2}x{DesH:F2}, {Ms:F2} ms",
                    FolderName, _windowMeasureCount,
                    availableSize.Width, availableSize.Height,
                    result.Width, result.Height,
                    sw.Elapsed.TotalMilliseconds);
            }

            if (!_windowStackLogged && _windowMeasureCount == 200)
            {
                _windowStackLogged = true;
                _log.Debug(
                    "[RepeaterProbe] '{Folder}' 200 перемеров за секунду, стек вызова:\n{Stack}",
                    FolderName, Environment.StackTrace);
            }

            return result;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
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
