using Microsoft.Extensions.DependencyInjection;
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
    /// Хранит настройки в settings.json рядом с .exe (портативный режим)
    /// ВСЕГДА сохраняет ВСЕ настройки включая WorkspaceConfigs
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

                    Console.WriteLine($"[SettingsService] Settings loaded, WorkspaceConfigs count: {_settings.WorkspaceConfigs.Count}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SettingsService] ERROR loading settings: {ex.Message}");
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
                    Language = "ru",
                    DefaultProjectsFolder = Path.Combine(_applicationDirectory, "Projects")
                };

                // Создаём папку для проектов
                Directory.CreateDirectory(_settings.DefaultProjectsFolder);

                // Сохраняем настройки при первом запуске
                Save();
            }
        }

        /// <summary>
        /// Сохранить ВСЕ настройки в файл
        /// ВСЕГДА сохраняет полностью включая WorkspaceConfigs
        /// </summary>
        public void Save()
        {
            try
            {
                Console.WriteLine("[SettingsService] Saving ALL settings");

                // ЯВНАЯ НАСТРОЙКА: игнорировать циклы
                var jsonSettings = new JsonSerializerSettings
                {
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                    MaxDepth = 64,
                    Formatting = Formatting.Indented
                };

                var json = JsonConvert.SerializeObject(_settings, jsonSettings);
                File.WriteAllText(_settingsPath, json);

                Console.WriteLine($"[SettingsService] Settings saved successfully");
                Console.WriteLine($"[SettingsService]   WorkspaceConfigs: {_settings.WorkspaceConfigs.Count}");
                Console.WriteLine($"[SettingsService]   RecentProjects: {_settings.RecentProjects.Count}");
                Console.WriteLine($"[SettingsService]   OpenProjects: {_settings.OpenProjectPaths.Count}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SettingsService] ERROR saving settings: {ex.Message}");
                Console.WriteLine($"[SettingsService] Stack trace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Добавить проект в список недавних
        /// Получает данные из уже загруженного проекта в IProjectService
        /// </summary>
        public void AddRecentProject(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return;

            try
            {
                // Получаем IProjectService из DI контейнера
                var projectService = App.Services.GetRequiredService<IProjectService>();
                if (projectService == null)
                {
                    Console.WriteLine($"[SettingsService] IProjectService not found in DI");
                    return;
                }

                // Получаем проект из IProjectService (уже загружен в память)
                var project = projectService.GetProjectByPath(filePath);

                if (project == null)
                {
                    Console.WriteLine($"[SettingsService] Project not found in service: {filePath}");
                    return;
                }

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
                Save();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SettingsService] ERROR adding recent project: {ex.Message}");
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
                Save();
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
                Console.WriteLine($"[SettingsService] DefaultProjectsFolder changed: {value}");
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
            Save();

            Console.WriteLine($"[SettingsService] Saved {_settings.OpenProjectPaths.Count} open projects");
        }

        /// <summary>
        /// Получить глобальную конфигурацию для типа проекта
        /// Возвращает null если конфигурация не сохранена
        /// </summary>
        public WorkspaceConfig? GetWorkspaceConfig(string projectType)
        {
            var found = _settings.WorkspaceConfigs.TryGetValue(projectType, out var config);

            Console.WriteLine($"[SettingsService] GetWorkspaceConfig({projectType}): {(found ? "FOUND" : "NOT FOUND")}");

            return found ? config : null;
        }

        /// <summary>
        /// Сохранить глобальную конфигурацию для типа проекта
        /// Вызывается когда пользователь нажимает "Сохранить как глобальные"
        /// </summary>
        public void SaveWorkspaceConfig(string projectType, WorkspaceConfig config)
        {
            config.LastModified = DateTime.Now;
            _settings.WorkspaceConfigs[projectType] = config;

            Console.WriteLine($"[SettingsService] SaveWorkspaceConfig({projectType})");
            Console.WriteLine($"[SettingsService]   WorkModes count: {config.WorkModes.Count}");

            Save(); // Сохраняет ВСЁ включая WorkspaceConfigs

            Console.WriteLine($"[SettingsService] WorkspaceConfig saved for {projectType}");
        }

        /// <summary>
        /// Удалить глобальную конфигурацию для типа проекта
        /// Вызывается когда пользователь нажимает "Удалить глобальную конфигурацию"
        /// </summary>
        public void DeleteWorkspaceConfig(string projectType)
        {
            if (_settings.WorkspaceConfigs.Remove(projectType))
            {
                Console.WriteLine($"[SettingsService] DeleteWorkspaceConfig({projectType})");

                Save(); // Сохраняет ВСЁ

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