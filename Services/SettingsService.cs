using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Writersword.Core.Models.Project;
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
        private const int MaxRecentProjects = 10;
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

                    // Удаляем несуществующие файлы из списка недавних
                    _settings.RecentProjects = _settings.RecentProjects
                        .Where(r => File.Exists(r.Path))
                        .ToList();
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
                var json = JsonConvert.SerializeObject(_settings, Formatting.Indented);
                File.WriteAllText(_settingsPath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save settings: {ex.Message}");
            }
        }

        /// <summary>Добавить проект в список недавних</summary>
        public void AddRecentProject(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return;

            try
            {
                // Загружаем проект чтобы получить информацию
                var json = File.ReadAllText(filePath);
                var project = JsonConvert.DeserializeObject<ProjectFile>(json);

                if (project == null)
                    return;

                // Удаляем дубликат если есть
                _settings.RecentProjects.RemoveAll(r => r.Path.Equals(filePath, StringComparison.OrdinalIgnoreCase));

                // Добавляем в начало списка
                _settings.RecentProjects.Insert(0, new RecentProject
                {
                    Name = project.Title,
                    Path = filePath,
                    Type = project.Type,
                    LastOpened = DateTime.Now
                });

                // Ограничиваем количество
                if (_settings.RecentProjects.Count > MaxRecentProjects)
                {
                    _settings.RecentProjects = _settings.RecentProjects.Take(MaxRecentProjects).ToList();
                }

                Console.WriteLine($"Added recent project: {project.Title}, total: {_settings.RecentProjects.Count}");
                Save();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to add recent project: {ex.Message}");
            }
        }

        /// <summary>Список недавних проектов</summary>
        public List<RecentProject> RecentProjects => _settings.RecentProjects;

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

        /// <summary>Список открытых проектов из последней сессии</summary>
        public List<string> OpenProjectPaths
        {
            get => _settings.OpenProjectPaths;
            set
            {
                _settings.OpenProjectPaths = value;
                Save();
            }
        }

        /// <summary>Сохранить список открытых проектов</summary>
        public void SaveOpenProjects(List<string> projectPaths)
        {
            _settings.OpenProjectPaths = projectPaths.Where(p => !string.IsNullOrEmpty(p) && File.Exists(p)).ToList();
            Console.WriteLine($"[SettingsService] Saved {_settings.OpenProjectPaths.Count} open projects");
            Save();
        }

        /// <summary>Класс для JSON сериализации настроек</summary>
        private class AppSettings
        {
            public string Theme { get; set; } = "Dark";
            public string Language { get; set; } = "en";
            public string? LastOpenedProject { get; set; }
            public string DefaultProjectsFolder { get; set; } = string.Empty;
            public string? LastUsedPath { get; set; }
            public List<RecentProject> RecentProjects { get; set; } = new List<RecentProject>();

            // Список открытых вкладок из последней сессии
            public List<string> OpenProjectPaths { get; set; } = new List<string>();
        }
    }
}