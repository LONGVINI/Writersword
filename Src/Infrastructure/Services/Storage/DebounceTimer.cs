using System;
using System.Threading;

namespace Writersword.Src.Infrastructure.Services.Storage
{
    /// <summary>
    /// Таймер с debounce (отложенное выполнение)
    /// Выполняет действие только после паузы указанной длительности
    /// Используется для оптимизации кеширования (сохранять только после паузы в печати)
    /// </summary>
    public class DebounceTimer : IDisposable
    {
        private Timer? _timer;
        private readonly TimeSpan _delay;
        private readonly Action _action;
        private bool _isDisposed;

        /// <summary>
        /// Создать debounce таймер
        /// </summary>
        /// <param name="delay">Задержка перед выполнением действия</param>
        /// <param name="action">Действие которое нужно выполнить</param>
        public DebounceTimer(TimeSpan delay, Action action)
        {
            _delay = delay;
            _action = action;
        }

        /// <summary>
        /// Сбросить таймер (отложить выполнение)
        /// Если таймер уже запущен - он сбрасывается и отсчёт начинается заново
        /// </summary>
        public void Reset()
        {
            if (_isDisposed)
                return;

            // Останавливаем старый таймер
            _timer?.Dispose();

            // Создаём новый таймер который сработает через указанную задержку
            _timer = new Timer(
                callback: _ => OnTimerElapsed(),
                state: null,
                dueTime: _delay,
                period: Timeout.InfiniteTimeSpan // Одноразовый таймер
            );
        }

        /// <summary>
        /// Остановить таймер
        /// Отменяет ожидающее выполнение действия
        /// </summary>
        public void Stop()
        {
            _timer?.Dispose();
            _timer = null;
        }

        /// <summary>
        /// Обработчик срабатывания таймера
        /// Вызывает действие
        /// </summary>
        private void OnTimerElapsed()
        {
            if (_isDisposed)
                return;

            try
            {
                _action();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DebounceTimer] Error executing action: {ex.Message}");
            }
        }

        /// <summary>Освободить ресурсы</summary>
        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}