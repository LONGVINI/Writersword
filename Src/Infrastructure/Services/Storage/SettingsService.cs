using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Writersword.Core.Models.Project;
using Writersword.Core.Models.Settings;
using Writersword.Src.Core.Interfaces.Services.Storage;
using Writersword.Src.Core.Models.Settings;

namespace Writersword.Src.Infrastructure.Services.Storage
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

        /// <summary>
        /// Сохранить настройки в файл (ЛЁГКАЯ ВЕРСИЯ)
        /// Сохраняет ТОЛЬКО: Theme, Language, RecentProjects, OpenProjectPaths
        /// НЕ сохраняет WorkspaceConfigs (они сохраняются отдельно через SaveWorkspaceConfig)
        /// </summary>
        public void Save()
        {
            try
            {
                Console.WriteLine("[SettingsService] Saving settings (lightweight)");

                // Создаём облегчённую версию настроек БЕЗ WorkspaceConfigs
                var lightSettings = new
                {
                    Theme = _settings.Theme,
                    Language = _settings.Language,
                    LastOpenedProject = _settings.LastOpenedProject,
                    DefaultProjectsFolder = _settings.DefaultProjectsFolder,
                    LastUsedPath = _settings.LastUsedPath,
                    RecentProjects = _settings.RecentProjects,
                    OpenProjectPaths = _settings.OpenProjectPaths
                    // WorkspaceConfigs НЕ ВКЛЮЧЕНЫ!
                };

                // ЯВНАЯ НАСТРОЙКА: игнорировать циклы
                var jsonSettings = new JsonSerializerSettings
                {
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                    MaxDepth = 64,
                    Formatting = Formatting.Indented
                };

                var json = JsonConvert.SerializeObject(lightSettings, jsonSettings);
                File.WriteAllText(_settingsPath, json);

                Console.WriteLine("[SettingsService] Settings saved successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SettingsService] Failed to save settings: {ex.Message}");
                Console.WriteLine($"[SettingsService] Stack trace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Сохранить ВСЕ настройки включая WorkspaceConfigs
        /// Вызывается ТОЛЬКО когда пользователь явно сохраняет глобальную конфигурацию
        /// </summary>
        private void SaveFull()
        {
            try
            {
                Console.WriteLine("[SettingsService] Saving FULL settings (including WorkspaceConfigs)");

                // Сохраняем ВСЁ
                var json = JsonConvert.SerializeObject(_settings, Formatting.Indented);
                File.WriteAllText(_settingsPath, json);

                Console.WriteLine($"[SettingsService] Full settings saved: {_settings.WorkspaceConfigs.Count} workspace configs");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SettingsService] Failed to save full settings: {ex.Message}");
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

                Console.WriteLine($"[SettingsService] Added recent project: {project.Title}, total: {_settings.RecentProjects.Count}");
                Save(); // Лёгкое сохранение
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SettingsService] Failed to add recent project: {ex.Message}");
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
                Console.WriteLine($"[SettingsService] Theme changed: {_settings.Theme} → {value}");
                _settings.Theme = value;
                Save(); // Лёгкое сохранение
            }
        }

        /// <summary>Язык интерфейса (ru, uk, en)</summary>
        public string Language
        {
            get => _settings.Language;
            set
            {
                Console.WriteLine($"[SettingsService] Language changed: {_settings.Language} → {value}");
                _settings.Language = value;
                Save(); // Лёгкое сохранение
            }
        }

        /// <summary>Последний открытый проект (полный путь к .writersword файлу)</summary>
        public string? LastOpenedProject
        {
            get => _settings.LastOpenedProject;
            set
            {
                _settings.LastOpenedProject = value;
                Save(); // Лёгкое сохранение
            }
        }

        /// <summary>Папка для проектов по умолчанию</summary>
        public string DefaultProjectsFolder
        {
            get => _settings.DefaultProjectsFolder;
            set
            {
                Console.WriteLine($"[SettingsService] DefaultProjectsFolder changed: {value}");
                _settings.DefaultProjectsFolder = value;
                Save(); // Лёгкое сохранение
            }
        }

        /// <summary>Последний использованный путь (для диалогов Open/Save)</summary>
        public string? LastUsedPath
        {
            get => _settings.LastUsedPath;
            set
            {
                _settings.LastUsedPath = value;
                Save(); // Лёгкое сохранение
            }
        }

        /// <summary>Список открытых проектов из последней сессии</summary>
        public List<string> OpenProjectPaths
        {
            get => _settings.OpenProjectPaths;
            set
            {
                _settings.OpenProjectPaths = value;
                Save(); // Лёгкое сохранение
            }
        }

        /// <summary>
        /// Сохранить список открытых проектов
        /// Вызывается при изменении вкладок (добавление/удаление/перестановка)
        /// </summary>
        public void SaveOpenProjects(List<string> paths)
        {
            Console.WriteLine($"[SettingsService] SaveOpenProjects called with {paths.Count} paths");
            foreach (var path in paths)
            {
                Console.WriteLine($"[SettingsService]   Path: '{path}'");
            }

            _settings.OpenProjectPaths = paths;
            Save(); // Лёгкое сохранение (БЕЗ WorkspaceConfigs!)

            Console.WriteLine($"[SettingsService] Saved {_settings.OpenProjectPaths.Count} open projects");
        }

        /// <summary>
        /// Получить глобальную конфигурацию для типа проекта
        /// Возвращает null если конфигурация не сохранена
        /// </summary>
        public WorkspaceConfig? GetWorkspaceConfig(string projectType)
        {
            var key = projectType;
            var found = _settings.WorkspaceConfigs.TryGetValue(key, out var config);

            Console.WriteLine($"[SettingsService] GetWorkspaceConfig({projectType}): {(found ? "FOUND" : "NOT FOUND")}");

            return found ? config : null;
        }

        /// <summary>
        /// Сохранить глобальную конфигурацию для типа проекта
        /// Вызывается ТОЛЬКО когда пользователь явно нажимает "Сохранить как глобальную конфигурацию"
        /// </summary>
        public void SaveWorkspaceConfig(string projectType, WorkspaceConfig config)
        {
            var key = projectType;
            config.LastModified = DateTime.Now;
            _settings.WorkspaceConfigs[key] = config;

            Console.WriteLine($"[SettingsService] SaveWorkspaceConfig({projectType})");
            Console.WriteLine($"[SettingsService]   WorkModes count: {config.WorkModes.Count}");

            SaveFull(); // ПОЛНОЕ сохранение (включая WorkspaceConfigs!)

            Console.WriteLine($"[SettingsService] WorkspaceConfig saved for {projectType}");
        }

        /// <summary>
        /// Удалить глобальную конфигурацию для типа проекта
        /// Вызывается когда пользователь нажимает "Удалить глобальную конфигурацию"
        /// </summary>
        public void DeleteWorkspaceConfig(string projectType)
        {
            var key = projectType;
            if (_settings.WorkspaceConfigs.Remove(key))
            {
                Console.WriteLine($"[SettingsService] DeleteWorkspaceConfig({projectType})");

                SaveFull(); // ПОЛНОЕ сохранение

                Console.WriteLine($"[SettingsService] WorkspaceConfig deleted for {projectType}");
            }
        }

        /// <summary>Получить все глобальные конфигурации</summary>
        public Dictionary<string, WorkspaceConfig> GetAllWorkspaceConfigs()
        {
            Console.WriteLine($"[SettingsService] GetAllWorkspaceConfigs: {_settings.WorkspaceConfigs.Count} configs");
            return _settings.WorkspaceConfigs;
        }
    }
}