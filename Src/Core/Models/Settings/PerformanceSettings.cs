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

        /// <summary>
        /// Уровень расхода оперативной памяти: 0 — экономный, 1 — обычный, 2 — быстрый
        /// (Writersword.Infrastructure.Services.Memory.MemoryLevel). Определяет, как
        /// долго неактивные вкладки остаются в памяти, и лимит памяти по умолчанию.
        /// </summary>
        public int MemoryLevel { get; set; } = 1;

        /// <summary>
        /// Задан ли свой лимит памяти (дополнительная настройка). Пока выключено,
        /// лимит считается сам от объёма памяти компьютера и уровня.
        /// </summary>
        public bool CustomMemoryLimitEnabled { get; set; }

        /// <summary>Свой лимит памяти в мегабайтах; действует, только если CustomMemoryLimitEnabled.</summary>
        public int CustomMemoryLimitMb { get; set; }
    }
}