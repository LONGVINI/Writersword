using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        private readonly ILogger<SettingsService> _logger;
        private const string SettingsFileName = "settings.json";
        private const int MaxRecentProjects = 10;
        private readonly string _settingsPath;
        private readonly string _applicationDirectory;
        private AppSettings _settings;

        public SettingsService()
        {
            _logger = App.Services.GetService<ILogger<SettingsService>>()!;

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

                    _logger.LogDebug("Settings loaded, WorkspaceConfigs count: {Count}", _settings.WorkspaceConfigs.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error loading settings");
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
                _logger.LogDebug("Saving ALL settings");

                // ЯВНАЯ НАСТРОЙКА: игнорировать циклы
                var jsonSettings = new JsonSerializerSettings
                {
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                    MaxDepth = 64,
                    Formatting = Formatting.Indented
                };

                var json = JsonConvert.SerializeObject(_settings, jsonSettings);
                File.WriteAllText(_settingsPath, json);

                _logger.LogDebug("Settings saved successfully");
                _logger.LogDebug("WorkspaceConfigs: {WorkspaceConfigsCount}", _settings.WorkspaceConfigs.Count);
                _logger.LogDebug("RecentProjects: {RecentProjectsCount}", _settings.RecentProjects.Count);
                _logger.LogDebug("OpenProjects: {OpenProjectsCount}", _settings.OpenProjectPaths.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving settings");
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
                    _logger.LogWarning("IProjectService not found in DI");
                    return;
                }

                // Получаем проект из IProjectService (уже загружен в память)
                var project = projectService.GetProjectByPath(filePath);

                if (project == null)
                {
                    _logger.LogWarning("Project not found in service: {FilePath}", filePath);
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

                _logger.LogDebug("Added recent project: {ProjectTitle}, total: {TotalCount}", project.Title, _settings.RecentProjects.Count);
                Save();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding recent project");
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
                _logger.LogDebug("Theme changed: {OldTheme} → {NewTheme}", _settings.Theme, value);
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
                _logger.LogDebug("Language changed: {OldLanguage} → {NewLanguage}", _settings.Language, value);
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
                _logger.LogDebug("DefaultProjectsFolder changed: {Folder}", value);
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
            _logger.LogDebug("SaveOpenProjects called with {Count} paths", paths.Count);
            foreach (var path in paths)
            {
                _logger.LogDebug("Path: '{Path}'", path);
            }

            _settings.OpenProjectPaths = paths;
            Save();

            _logger.LogDebug("Saved {Count} open projects", _settings.OpenProjectPaths.Count);
        }

        /// <summary>
        /// Получить глобальную конфигурацию для типа проекта
        /// Возвращает null если конфигурация не сохранена
        /// </summary>
        public WorkspaceConfig? GetWorkspaceConfig(string projectType)
        {
            var found = _settings.WorkspaceConfigs.TryGetValue(projectType, out var config);

            _logger.LogDebug("GetWorkspaceConfig({ProjectType}): {Result}", projectType, found ? "FOUND" : "NOT FOUND");

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

            _logger.LogDebug("SaveWorkspaceConfig({ProjectType})", projectType);
            _logger.LogDebug("WorkModes count: {Count}", config.WorkModes.Count);

            Save(); // Сохраняет ВСЁ включая WorkspaceConfigs

            _logger.LogDebug("WorkspaceConfig saved for {ProjectType}", projectType);
        }

        /// <summary>
        /// Удалить глобальную конфигурацию для типа проекта
        /// Вызывается когда пользователь нажимает "Удалить глобальную конфигурацию"
        /// </summary>
        public void DeleteWorkspaceConfig(string projectType)
        {
            if (_settings.WorkspaceConfigs.Remove(projectType))
            {
                _logger.LogDebug("DeleteWorkspaceConfig({ProjectType})", projectType);

                Save(); // Сохраняет ВСЁ

                _logger.LogDebug("WorkspaceConfig deleted for {ProjectType}", projectType);
            }
        }

        /// <summary>Получить все глобальные конфигурации</summary>
        public Dictionary<string, WorkspaceConfig> GetAllWorkspaceConfigs()
        {
            _logger.LogDebug("GetAllWorkspaceConfigs: {Count} configs", _settings.WorkspaceConfigs.Count);
            return _settings.WorkspaceConfigs;
        }

        /// <summary>
        /// Получить настройки модуля с десериализацией в нужный тип
        /// </summary>
        public T? GetModuleSettings<T>(string moduleType) where T : class
        {
            if (!_settings.ModuleSettings.TryGetValue(moduleType, out var raw) || raw == null)
            {
                _logger.LogDebug("GetModuleSettings({ModuleType}): not found", moduleType);
                return null;
            }

            try
            {
                var json = JsonConvert.SerializeObject(raw);
                var result = JsonConvert.DeserializeObject<T>(json);
                _logger.LogDebug("GetModuleSettings({ModuleType}): found and deserialized", moduleType);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deserializing module settings for {ModuleType}", moduleType);
                return null;
            }
        }

        /// <summary>
        /// Сохранить настройки модуля
        /// </summary>
        public void SaveModuleSettings(string moduleType, object settings)
        {
            _settings.ModuleSettings[moduleType] = settings;
            _logger.LogDebug("SaveModuleSettings({ModuleType})", moduleType);
            Save();
        }

        /// <summary>
        /// Удалить настройки модуля
        /// </summary>
        public void DeleteModuleSettings(string moduleType)
        {
            if (_settings.ModuleSettings.Remove(moduleType))
            {
                _logger.LogDebug("DeleteModuleSettings({ModuleType})", moduleType);
                Save();
            }
        }
    }
}