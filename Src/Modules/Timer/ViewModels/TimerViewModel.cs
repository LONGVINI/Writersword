using Writersword.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Reactive;
using Writersword.Modules.Timer.Models;

namespace Writersword.Modules.Timer.ViewModels
{
    /// <summary>
    /// ViewModel ��� ������ �������
    /// ������������ ������ ���� � �������� ������
    /// </summary>
    public class TimerViewModel : ReactiveObject, IDisposable
    {
        private readonly ILogger<TimerViewModel> _logger;

        private int _elapsedSeconds = 0;
        private int _targetSeconds = 60;
        private bool _isRunning = false;
        private bool _isCountdown = false;
        private System.Timers.Timer? _timer;

        public int ElapsedSeconds
        {
            get => _elapsedSeconds;
            set
            {
                this.RaiseAndSetIfChanged(ref _elapsedSeconds, value);
                this.RaisePropertyChanged(nameof(DisplayTime));
                this.RaisePropertyChanged(nameof(IsFinished));
            }
        }

        public int TargetSeconds
        {
            get => _targetSeconds;
            set
            {
                this.RaiseAndSetIfChanged(ref _targetSeconds, value);
                this.RaisePropertyChanged(nameof(DisplayTime));
            }
        }

        public bool IsRunning
        {
            get => _isRunning;
            set => this.RaiseAndSetIfChanged(ref _isRunning, value);
        }

        public bool IsCountdown
        {
            get => _isCountdown;
            set
            {
                this.RaiseAndSetIfChanged(ref _isCountdown, value);
                this.RaisePropertyChanged(nameof(DisplayTime));
            }
        }

        /// <summary>�������� ������ ��������</summary>
        public bool IsFinished => IsCountdown && ElapsedSeconds >= TargetSeconds;

        public string DisplayTime
        {
            get
            {
                int seconds = IsCountdown
                    ? Math.Max(0, TargetSeconds - ElapsedSeconds)
                    : ElapsedSeconds;

                var hours = seconds / 3600;
                var minutes = (seconds % 3600) / 60;
                var secs = seconds % 60;
                return $"{hours:D2}:{minutes:D2}:{secs:D2}";
            }
        }

        public ReactiveCommand<Unit, Unit> StartCommand { get; }
        public ReactiveCommand<Unit, Unit> StopCommand { get; }
        public ReactiveCommand<Unit, Unit> ResetCommand { get; }

        public TimerViewModel()
        {
            _logger = CoreServices.GetService<ILogger<TimerViewModel>>()!;

            StartCommand = ReactiveCommand.Create(Start);
            StopCommand = ReactiveCommand.Create(Stop);
            ResetCommand = ReactiveCommand.Create(Reset);

            _timer = new System.Timers.Timer(1000);
            _timer.Elapsed += (s, e) =>
            {
                if (!_isRunning) return;

                if (IsCountdown && ElapsedSeconds >= TargetSeconds)
                {
                    IsRunning = false;
                    _logger.LogDebug("Countdown finished");
                    return;
                }

                ElapsedSeconds++;
            };
            _timer.Start();
        }

        public void ApplySettings(TimerSettings settings)
        {
            IsCountdown = settings.IsCountdown;
            TargetSeconds = settings.DefaultMinutes * 60 + settings.DefaultSeconds;
            ElapsedSeconds = 0;
            IsRunning = false;
            _logger.LogDebug("Settings applied: Countdown={IsCountdown}, Target={Target}s",
                settings.IsCountdown, TargetSeconds);
        }

        private void Start()
        {
            if (IsFinished) return;
            IsRunning = true;
            _logger.LogDebug("Timer started");
        }

        private void Stop()
        {
            IsRunning = false;
            _logger.LogDebug("Timer stopped");
        }

        private void Reset()
        {
            IsRunning = false;
            ElapsedSeconds = 0;
            _logger.LogDebug("Timer reset");
        }

        public void Dispose()
        {
            _timer?.Stop();
            _timer?.Dispose();
            _logger.LogDebug("Disposed");
        }
    }
}