namespace Writersword.Core.Models.Settings
{
    /// <summary>
    /// Настройки производительности: автосохранение и фоновое кеширование.
    /// Хранятся через ISettingsService под ключом "performance".
    /// Загружаются при старте в GeneralSettingsViewModel,
    /// применяются немедленно при изменении в UI.
    /// </summary>
    public class PerformanceSettings
    {
        /// <summary>Включено ли фоновое кеширование в .wsasd файл.</summary>
        public bool CachingEnabled { get; set; } = true;

        /// <summary>Интервал фонового кеширования в секундах.</summary>
        public int CachingIntervalSeconds { get; set; } = 10;

        /// <summary>Включено ли автосохранение активной вкладки.</summary>
        public bool AutoSaveEnabled { get; set; } = true;

        /// <summary>Интервал автосохранения в секундах.</summary>
        public int AutoSaveIntervalSeconds { get; set; } = 120;
    }
}