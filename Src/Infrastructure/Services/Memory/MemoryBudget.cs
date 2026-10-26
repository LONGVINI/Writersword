using System;
using Writersword.Core.Models.Settings;

namespace Writersword.Infrastructure.Services.Memory
{
    /// <summary>Уровень расхода оперативной памяти (PerformanceSettings.MemoryLevel).</summary>
    public enum MemoryLevel
    {
        /// <summary>Памяти меньше, возврат на давно не открытую вкладку дольше.</summary>
        Economy = 0,

        /// <summary>Середина: прежнее поведение программы.</summary>
        Normal = 1,

        /// <summary>Вкладки дольше держатся в памяти, переключение мгновенное.</summary>
        Fast = 2
    }

    /// <summary>
    /// Бюджет оперативной памяти программы: сколько держать в памяти неактивные
    /// вкладки и какой расход памяти считать предельным.
    /// <para>
    /// Человеку достаточно выбрать уровень — лимит считается сам от объёма памяти
    /// компьютера. Свой лимит в мегабайтах задаётся только явно (дополнительная
    /// настройка) и тогда заменяет посчитанный.
    /// </para>
    /// <para>
    /// При превышении лимита программа сначала сжимает кучу и возвращает
    /// свободную память системе, и только если этого мало — выгружает самые
    /// давние неактивные вкладки (MainWindowViewModel). Открытая вкладка
    /// не выгружается никогда.
    /// </para>
    /// Значения читаются с любого потока; меняются через Apply.
    /// </summary>
    public static class MemoryBudget
    {
        /// <summary>Меньше этого лимит не опускается: программа с одним проектом занимает около 400 МБ.</summary>
        public const int MinLimitMb = 512;

        /// <summary>Шаг ползунка своего лимита.</summary>
        public const int LimitStepMb = 128;

        // Объём памяти, с которым считается лимит, если сборщик мусора ещё не
        // сообщил настоящий (до первой сборки).
        private const long FallbackTotalRamMb = 8192;

        private static volatile int _level = (int)MemoryLevel.Normal;
        private static volatile bool _customLimitEnabled;
        private static volatile int _customLimitMb;
        private static long _totalRamMb;

        /// <summary>Выбранный уровень.</summary>
        public static MemoryLevel Level => (MemoryLevel)_level;

        /// <summary>Задан ли свой лимит.</summary>
        public static bool CustomLimitEnabled => _customLimitEnabled;

        /// <summary>
        /// Объём оперативной памяти, доступной процессу, в мегабайтах. Берётся у
        /// сборщика мусора: он учитывает и ограничения контейнера или задания.
        /// </summary>
        public static long TotalRamMb
        {
            get
            {
                long known = System.Threading.Interlocked.Read(ref _totalRamMb);
                if (known > 0) return known;

                long bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                if (bytes <= 0) return FallbackTotalRamMb;

                long mb = bytes / (1024 * 1024);
                System.Threading.Interlocked.Exchange(ref _totalRamMb, mb);
                return mb;
            }
        }

        /// <summary>
        /// Сколько неактивная вкладка остаётся в памяти до выгрузки.
        /// Обычный уровень — прежние две минуты: тридцати секунд оказалось мало,
        /// вкладка выгружалась между заходами и каждый возврат шёл холодной загрузкой.
        /// </summary>
        public static TimeSpan InactiveTabUnloadTimeout => Level switch
        {
            MemoryLevel.Economy => TimeSpan.FromMinutes(1),
            MemoryLevel.Fast => TimeSpan.FromMinutes(10),
            _ => TimeSpan.FromMinutes(2)
        };

        /// <summary>
        /// Лимит, посчитанный от объёма памяти компьютера: доля памяти, но не
        /// больше потолка уровня и не меньше MinLimitMb. Потолок нужен, чтобы на
        /// машине с 32 ГБ «обычный» уровень не означал десять гигабайт.
        /// </summary>
        public static int AutoLimitMb(MemoryLevel level)
        {
            double share;
            int ceiling;

            switch (level)
            {
                case MemoryLevel.Economy:
                    share = 0.20;
                    ceiling = 1024;
                    break;
                case MemoryLevel.Fast:
                    share = 0.50;
                    ceiling = 4096;
                    break;
                default:
                    share = 0.30;
                    ceiling = 2048;
                    break;
            }

            long byShare = (long)(TotalRamMb * share);
            long limit = Math.Min(byShare, ceiling);
            return RoundToStep((int)Math.Max(limit, MinLimitMb));
        }

        /// <summary>Действующий лимит в мегабайтах: свой, если задан, иначе посчитанный.</summary>
        public static int LimitMb
        {
            get
            {
                int custom = _customLimitMb;
                return _customLimitEnabled && custom >= MinLimitMb
                    ? custom
                    : AutoLimitMb(Level);
            }
        }

        /// <summary>Верхняя граница ползунка своего лимита: вся память компьютера.</summary>
        public static int SliderMaximumMb => RoundToStep((int)Math.Max(TotalRamMb, MinLimitMb * 2L));

        /// <summary>Применить настройки. Можно вызывать с любого потока.</summary>
        public static void Apply(PerformanceSettings settings)
        {
            int level = settings.MemoryLevel;
            if (level < (int)MemoryLevel.Economy || level > (int)MemoryLevel.Fast)
                level = (int)MemoryLevel.Normal;

            _level = level;
            _customLimitMb = settings.CustomMemoryLimitMb;
            _customLimitEnabled = settings.CustomMemoryLimitEnabled;
        }

        /// <summary>Округление до шага ползунка.</summary>
        public static int RoundToStep(int mb)
        {
            int steps = (int)Math.Round(mb / (double)LimitStepMb, MidpointRounding.AwayFromZero);
            return Math.Max(steps, 1) * LimitStepMb;
        }
    }
}
