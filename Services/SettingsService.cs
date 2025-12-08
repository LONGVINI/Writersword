using System;
using System.IO;
using Newtonsoft.Json;
using Writersword.Services.Interfaces;

namespace Writersword.Services
{
    /// <summary>
    /// Cервис настроек
    /// Портативный: если settings.json рядом с .exe
    /// Стандартный: если нет - использует %AppData%
    /// </summary>
    public class SettingsService : ISettingsService
    {
        private const string SettingsFileName = "settings.json";
        private readonly string _settingsPath;
        private readonly bool _isPortable;
        private AppSettings _settings;

        public SettingsService()
        {
            // Проверяем портативный режим
            var exeDirectory = AppContext.BaseDirectory;
            var portablePath = Path.Combine(exeDirectory, SettingsFileName);

            if (File.Exists(portablePath))
            {
                // Портативный режим - файл рядом с .exe
                _isPortable = true;
                _settingsPath = portablePath;
            }
            else
            {
                // Стандартный режим - AppData
                _isPortable = false;
                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var appFolder = Path.Combine(appDataPath, "Writersword");

                // Создаём папку если её нет
                Directory.CreateDirectory(appFolder);

                _settingsPath = Path.Combine(appFolder, SettingsFileName);
            }

            _settings = new AppSettings();
        }

        public void Load()
        {
            if (File.Exists(_settingsPath))
            {
                try
                {
                    var json = File.ReadAllText(_settingsPath);
                    _settings = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
                }
                catch
                {
                    // Если ошибка чтения - используем настройки по умолчанию
                    _settings = new AppSettings();
                }
            }
            else
            {
                // Настройки по умолчанию
                _settings = new AppSettings
                {
                    Theme = "Dark",
                    Language = "en"
                };
            }
        }

        public void Save()
        {
            try
            {
                var json = JsonConvert.SerializeObject(_settings, Formatting.Indented);

                File.WriteAllText(_settingsPath, json);
            }
            catch (Exception ex)
            {
                // TODO: Логирование ошибки
                Console.WriteLine($"Failed to save settings: {ex.Message}");
            }
        }

        public string Theme
        {
            get => _settings.Theme;
            set
            {
                _settings.Theme = value;
                Save(); // Автосохранение при изменении
            }
        }

        public string Language
        {
            get => _settings.Language;
            set
            {
                _settings.Language = value;
                Save(); // Автосохранение
            }
        }

        public string? LastOpenedProject
        {
            get => _settings.LastOpenedProject;
            set
            {
                _settings.LastOpenedProject = value;
                Save(); // Автосохранение
            }
        }

        /// <summary>Класс для JSON сериализации</summary>
        private class AppSettings
        {
            public string Theme { get; set; } = "Dark";
            public string Language { get; set; } = "en";
            public string? LastOpenedProject { get; set; }
        }
    }
}
