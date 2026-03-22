using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Writersword.Core.Models.Project;
using Writersword.Modules.Common;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Infrastructure.Services.Project;

namespace Writersword.Infrastructure.Services.Storage
{
    /// <summary>
    /// Реализация сервиса работы с проектами
    /// Каждая вкладка = отдельный проект
    /// Теперь работает с ZIP архивами вместо JSON
    /// </summary>
    public class ProjectService : IProjectService
    {
        private readonly ILogger<ProjectService> _logger;

        // Список всех открытых проектов
        private readonly List<ProjectFile> _openProjects = new List<ProjectFile>();

        // Соответствие: ID проекта -> путь к файлу
        private readonly Dictionary<string, string> _projectPaths = new Dictionary<string, string>();

        private readonly ZipProjectService _zipService;

        public ProjectService()
        {
            _logger = App.Services.GetService<ILogger<ProjectService>>()!;
            _zipService = new ZipProjectService();
        }

        /// <summary>Получить все открытые проекты</summary>
        public IReadOnlyList<ProjectFile> OpenProjects => _openProjects.AsReadOnly();

        /// <summary>Найти проект по пути к файлу</summary>
        public ProjectFile? GetProjectByPath(string filePath)
        {
            var projectId = _projectPaths.FirstOrDefault(x => x.Value == filePath).Key;
            if (projectId == null) return null;

            return _openProjects.FirstOrDefault(p => p.Title == projectId);
        }

        /// <summary>Получить путь к файлу проекта</summary>
        public string? GetProjectPath(ProjectFile project)
        {
            return _projectPaths.TryGetValue(project.Title, out var path) ? path : null;
        }

        /// <summary>Создать новый проект</summary>
        public ProjectFile CreateNew(string title, string type)
        {
            var project = new ProjectFile
            {
                Title = title,
                Type = type,
                CreatedAt = DateTime.Now,
                LastModified = DateTime.Now,
                FormatVersion = "1.0"
            };

            return project;
        }

        /// <summary>Загрузить проект из файла (ZIP архив)</summary>
        public async Task<ProjectFile?> LoadAsync(string filePath)
        {
            try
            {
                _logger.LogDebug("Loading project from: {FilePath}", filePath);

                if (!File.Exists(filePath))
                {
                    _logger.LogWarning("File does not exist");
                    return null;
                }

                // Проверяем не открыт ли уже этот проект
                var existing = GetProjectByPath(filePath);
                if (existing != null)
                {
                    _logger.LogDebug("Project already loaded, RE-LOADING from file");
                    _openProjects.Remove(existing);
                    _projectPaths.Remove(existing.Title);
                }

                // Загружаем через ZipProjectService
                var project = await _zipService.LoadFromZipAsync(filePath);

                if (project != null)
                {
                    _logger.LogDebug("Project loaded: {ProjectTitle}", project.Title);

                    // Добавляем в список открытых проектов
                    _openProjects.Add(project);
                    _projectPaths[project.Title] = filePath;
                }
                else
                {
                    _logger.LogWarning("Failed to load project");
                }

                return project;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load project");
                return null;
            }
        }

        /// <summary>Сохранить проект в файл (ZIP архив)</summary>
        public async Task<bool> SaveAsync(ProjectFile project, string filePath)
        {
            try
            {
                _logger.LogDebug("Saving project: {ProjectTitle}", project.Title);

                // Обновляем дату модификации
                project.LastModified = DateTime.Now;

                // Сохраняем через ZipProjectService
                bool success = await _zipService.SaveToZipAsync(project, filePath);

                if (!success)
                {
                    _logger.LogWarning("Failed to save to ZIP");
                    return false;
                }

                _logger.LogDebug("Project saved to: {FilePath}", filePath);

                // Удаляем старый проект с таким же путём
                var existingProject = GetProjectByPath(filePath);
                if (existingProject != null && existingProject != project)
                {
                    _logger.LogDebug("Removing old project from cache: {OldProjectTitle}", existingProject.Title);
                    _openProjects.Remove(existingProject);
                    _projectPaths.Remove(existingProject.Title);
                }

                // Добавляем в список открытых проектов если его там нет
                if (!_openProjects.Contains(project))
                {
                    _openProjects.Add(project);
                }

                // Обновляем путь к файлу
                _projectPaths[project.Title] = filePath;

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save project");
                return false;
            }
        }

        /// <summary>Закрыть проект</summary>
        public void CloseProject(ProjectFile project)
        {
            _openProjects.Remove(project);
            _projectPaths.Remove(project.Title);
        }
    }
}