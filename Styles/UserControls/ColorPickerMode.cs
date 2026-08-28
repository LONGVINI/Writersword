using Serilog;
using System;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Services;

namespace Writersword.Styles.UserControls
{
    /// <summary>
    /// Каким подборщиком открывается цвет по ПРАВОЙ кнопке.
    ///
    /// Левая всегда показывает заготовки — основные, мои, недавние, палитры;
    /// это главный способ брать цвет, и подменять его чем-то другим не за чем.
    /// Правая показывает подборщик: квадрат, соты, колесо, значения или шум.
    ///
    /// Режим один на всё приложение: человек выбирает привычный ему способ
    /// подбирать цвет, и переучиваться на каждой кнопке отдельно ему незачем.
    /// Выбирается галочкой в окне настройки цвета — там видно, что именно
    /// запоминают.
    /// </summary>
    public enum ColorPickerMode
    {
        /// <summary>Квадрат насыщенности с полосой тона. По умолчанию.</summary>
        Square = 1,

        /// <summary>Соты.</summary>
        Honeycomb = 2,

        /// <summary>Колесо.</summary>
        Wheel = 3,

        /// <summary>Числовые значения: RGB, HSL, HSV.</summary>
        Values = 4,

        /// <summary>Поле шума.</summary>
        Noise = 5
    }

    /// <summary>
    /// Хранилище режима. Живёт статически, потому что режим общий: сменили его
    /// на одной кнопке — он сменился у всех, включая уже построенные.
    ///
    /// Выбор режима действует сразу, но запоминается между запусками только по
    /// «Сделать стандартным»: попробовать другой подборщик и вернуться обратно
    /// человек должен уметь, не оставляя следов в настройках.
    /// </summary>
    public static class ColorPickerModeStore
    {
        private static readonly ILogger _logger = Log.ForContext(typeof(ColorPickerModeStore));

        private const string SettingsKey = "ColorPickerMode";

        // Квадрат — то, чем цвет подбирают чаще всего, и он же стоит первой
        // вкладкой в окне настройки.
        private const ColorPickerMode Fallback = ColorPickerMode.Square;

        private static ColorPickerMode? _current;

        /// <summary>Режим сменился — кнопкам пора перечитать его.</summary>
        public static event Action? Changed;

        public static ColorPickerMode Current
        {
            get
            {
                _current ??= Load();
                return _current.Value;
            }
            set
            {
                if (_current == value) return;
                _current = value;
                try { Changed?.Invoke(); }
                catch (Exception ex) { _logger.Error(ex, "ColorPickerMode change handler failed"); }
            }
        }

        /// <summary>Запомнить текущий режим между запусками.</summary>
        public static void SaveAsDefault()
        {
            try
            {
                var settings = CoreServices.GetService<ISettingsService>();
                settings?.SaveModuleSettings(SettingsKey, new ColorPickerModeSettings
                {
                    Mode = (int)Current
                });
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Saving default color picker mode failed");
            }
        }

        private static ColorPickerMode Load()
        {
            try
            {
                var settings = CoreServices.GetService<ISettingsService>();
                var data = settings?.GetModuleSettings<ColorPickerModeSettings>(SettingsKey);
                if (data is null) return Fallback;

                if (!Enum.IsDefined(typeof(ColorPickerMode), data.Mode))
                    return Fallback;

                return (ColorPickerMode)data.Mode;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Loading default color picker mode failed");
                return Fallback;
            }
        }

        /// <summary>Вкладка редактора цвета, отвечающая режиму.</summary>
        public static int TabOf(ColorPickerMode mode) => mode switch
        {
            ColorPickerMode.Square => 0,
            ColorPickerMode.Honeycomb => 1,
            ColorPickerMode.Wheel => 2,
            ColorPickerMode.Values => 3,
            ColorPickerMode.Noise => 5,
            _ => 0
        };

        public static string TitleOf(ColorPickerMode mode) => mode switch
        {
            ColorPickerMode.Honeycomb => "Соты",
            ColorPickerMode.Wheel => "Колесо",
            ColorPickerMode.Values => "Значения",
            ColorPickerMode.Noise => "Шум",
            _ => "Квадрат"
        };

        /// <summary>
        /// Режим по вкладке редактора. Неизвестная вкладка (например, палитры)
        /// откатывается к квадрату: подборщик на правой кнопке обязан быть
        /// всегда, «никакого» варианта тут нет.
        /// </summary>
        public static ColorPickerMode ModeOfTab(int tab) => tab switch
        {
            1 => ColorPickerMode.Honeycomb,
            2 => ColorPickerMode.Wheel,
            3 => ColorPickerMode.Values,
            5 => ColorPickerMode.Noise,
            _ => ColorPickerMode.Square
        };
    }

    /// <summary>Запись режима в настройках. Число, а не имя: имена меняются.</summary>
    public class ColorPickerModeSettings
    {
        public int Mode { get; set; }
    }
}
