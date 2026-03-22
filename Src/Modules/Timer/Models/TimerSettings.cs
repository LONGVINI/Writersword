namespace Writersword.Modules.Timer.Models
{
    /// <summary>
    /// Настройки модуля таймера
    /// Используется как для глобальных так и для локальных настроек проекта
    /// </summary>
    public class TimerSettings
    {
        /// <summary>Минуты по умолчанию для обратного отсчёта</summary>
        public int DefaultMinutes { get; set; } = 1;

        /// <summary>Секунды по умолчанию для обратного отсчёта</summary>
        public int DefaultSeconds { get; set; } = 0;

        /// <summary>Режим обратного отсчёта (true) или прямого (false)</summary>
        public bool IsCountdown { get; set; } = false;
    }
}