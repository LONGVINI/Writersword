using System;
using System.IO;
using Newtonsoft.Json;
using Writersword.Services.Interfaces;

namespace Writersword.Services
{
    /// <summary>
    /// Сервис для работы с настройками приложения
    /// Хранит настройки рядом с .exe файлом (портативный режим)
    /// </summary>
    public class SettingsService : ISettingsService
    {
        private const string SettingsFileName = "settings.json";
        private readonly string _settingsPath;
        private readonly string _applicationDirectory;
        private AppSettings _settings;

        public SettingsService()
        {
            // Папка с .exe файлом
            _applicationDirectory = AppContext.BaseDirectory;
            _settingsPath = Path.Combine(_applicationDirectory, SettingsFileName);

            _settings = new AppSettings();
        }

        /// <summary>Загрузить настройки из файла</summary>
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
                // Настройки по умолчанию (первый запуск)
                _settings = new AppSettings
                {
                    Theme = "Dark",
                    Language = "en",
                    DefaultProjectsFolder = Path.Combine(_applicationDirectory, "Projects")
                };

                // Создаём папку для проектов
                Directory.CreateDirectory(_settings.DefaultProjectsFolder);

                // Сохраняем настройки при первом запуске
                Save();
            }
        }

        /// <summary>Сохранить настройки в файл</summary>
        public void Save()
        {
            try
            {
                var json = JsonConvert.SerializeObject(_settings, Newtonsoft.Json.Formatting.Indented);
                File.WriteAllText(_settingsPath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save settings: {ex.Message}");
            }
        }

        /// <summary>Тема приложения (Dark, Light, Sepia)</summary>
        public string Theme
        {
            get => _settings.Theme;
            set
            {
                _settings.Theme = value;
                Save();
            }
        }

        /// <summary>Язык интерфейса (ru, uk, en)</summary>
        public string Language
        {
            get => _settings.Language;
            set
            {
                _settings.Language = value;
                Save();
            }
        }

        /// <summary>Последний открытый проект (полный путь к .writersword файлу)</summary>
        public string? LastOpenedProject
        {
            get => _settings.LastOpenedProject;
            set
            {
                _settings.LastOpenedProject = value;
                Save();
            }
        }

        /// <summary>Папка для проектов по умолчанию</summary>
        public string DefaultProjectsFolder
        {
            get => _settings.DefaultProjectsFolder;
            set
            {
                _settings.DefaultProjectsFolder = value;
                Save();
            }
        }

        /// <summary>Последний использованный путь (для диалогов Open/Save)</summary>
        public string? LastUsedPath
        {
            get => _settings.LastUsedPath;
            set
            {
                _settings.LastUsedPath = value;
                Save();
            }
        }

        /// <summary>Класс для JSON сериализации настроек</summary>
        private class AppSettings
        {
            public string Theme { get; set; } = "Dark";
            public string Language { get; set; } = "en";
            public string? LastOpenedProject { get; set; }
            public string DefaultProjectsFolder { get; set; } = string.Empty;
            public string? LastUsedPath { get; set; }
        }
    }
}