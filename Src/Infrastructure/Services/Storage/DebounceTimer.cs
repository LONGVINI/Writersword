using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;

namespace Writersword.Infrastructure.Services.Storage
{
    /// <summary>
    /// Таймер с debounce (отложенное выполнение)
    /// Выполняет действие только после паузы указанной длительности
    /// Используется для оптимизации кеширования (сохранять только после паузы в печати)
    /// </summary>
    public class DebounceTimer : IDisposable
    {
        private readonly ILogger<DebounceTimer> _logger;
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
            _logger = App.Services.GetService<ILogger<DebounceTimer>>()!;
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
                _logger.LogError(ex, "Error executing action");
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