using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Reactive;

namespace Writersword.Modules.Timer.ViewModels
{
    /// <summary>
    /// ViewModel для модуля таймера
    /// Управляет отсчётом времени, запуском/остановкой таймера
    /// </summary>
    public class TimerViewModel : ReactiveObject, IDisposable
    {
        private readonly ILogger<TimerViewModel> _logger;

        /// <summary>Сколько секунд прошло</summary>
        private int _elapsedSeconds = 0;

        /// <summary>Работает ли таймер сейчас</summary>
        private bool _isRunning = false;

        /// <summary>Системный таймер для подсчёта секунд</summary>
        private System.Timers.Timer? _timer;

        /// <summary>
        /// Количество прошедших секунд
        /// При изменении обновляется DisplayTime
        /// </summary>
        public int ElapsedSeconds
        {
            get => _elapsedSeconds;
            set
            {
                this.RaiseAndSetIfChanged(ref _elapsedSeconds, value);
                this.RaisePropertyChanged(nameof(DisplayTime));
            }
        }

        /// <summary>
        /// Работает ли таймер
        /// </summary>
        public bool IsRunning
        {
            get => _isRunning;
            set => this.RaiseAndSetIfChanged(ref _isRunning, value);
        }

        /// <summary>
        /// Отформатированное время для отображения (ЧЧ:ММ:СС)
        /// Автоматически пересчитывается при изменении ElapsedSeconds
        /// </summary>
        public string DisplayTime
        {
            get
            {
                var hours = _elapsedSeconds / 3600;
                var minutes = (_elapsedSeconds % 3600) / 60;
                var seconds = _elapsedSeconds % 60;
                return $"{hours:D2}:{minutes:D2}:{seconds:D2}";
            }
        }

        /// <summary>Команда запуска таймера</summary>
        public ReactiveCommand<Unit, Unit> StartCommand { get; }

        /// <summary>Команда остановки таймера</summary>
        public ReactiveCommand<Unit, Unit> StopCommand { get; }

        /// <summary>Команда сброса таймера</summary>
        public ReactiveCommand<Unit, Unit> ResetCommand { get; }

        /// <summary>
        /// Конструктор - создаём команды и запускаем системный таймер
        /// </summary>
        public TimerViewModel()
        {
            _logger = App.Services.GetService<ILogger<TimerViewModel>>()!;

            StartCommand = ReactiveCommand.Create(Start);
            StopCommand = ReactiveCommand.Create(Stop);
            ResetCommand = ReactiveCommand.Create(Reset);

            _timer = new System.Timers.Timer(1000);
            _timer.Elapsed += (s, e) =>
            {
                if (_isRunning)
                {
                    ElapsedSeconds++;
                }
            };
            _timer.Start();
        }

        /// <summary>
        /// Запустить таймер
        /// </summary>
        private void Start()
        {
            IsRunning = true;
            _logger.LogDebug("Timer started");
        }

        /// <summary>
        /// Остановить таймер (пауза)
        /// </summary>
        private void Stop()
        {
            IsRunning = false;
            _logger.LogDebug("Timer stopped");
        }

        /// <summary>
        /// Сбросить таймер на 00:00:00
        /// </summary>
        private void Reset()
        {
            IsRunning = false;
            ElapsedSeconds = 0;
            _logger.LogDebug("Timer reset");
        }

        /// <summary>
        /// Освободить ресурсы - остановить и удалить системный таймер
        /// Без этого будет утечка памяти
        /// </summary>
        public void Dispose()
        {
            _timer?.Stop();
            _timer?.Dispose();
            _logger.LogDebug("Disposed");
        }
    }
}