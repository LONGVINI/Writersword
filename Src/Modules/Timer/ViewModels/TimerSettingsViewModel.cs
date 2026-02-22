using ReactiveUI;

namespace Writersword.Modules.Timer.ViewModels
{
    /// <summary>
    /// ViewModel для настроек таймера
    /// </summary>
    public class TimerSettingsViewModel : ReactiveObject
    {
        private int _defaultMinutes;
        private int _defaultSeconds;
        private bool _isCountdown;

        public int DefaultMinutes
        {
            get => _defaultMinutes;
            set => this.RaiseAndSetIfChanged(ref _defaultMinutes, value);
        }

        public int DefaultSeconds
        {
            get => _defaultSeconds;
            set => this.RaiseAndSetIfChanged(ref _defaultSeconds, value);
        }

        public bool IsCountdown
        {
            get => _isCountdown;
            set => this.RaiseAndSetIfChanged(ref _isCountdown, value);
        }
    }
}