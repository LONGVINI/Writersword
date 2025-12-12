using System;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Writersword.Core.Models.Project;
using Writersword.Core.Enums;

namespace Writersword.Services
{
    /// <summary>
    /// Реализация сервиса работы с проектами
    /// Каждая вкладка = отдельный проект
    /// </summary>
    public class ProjectService : IProjectService
    {
        // Список всех открытых проектов
        private readonly List<ProjectFile> _openProjects = new List<ProjectFile>();

        // Соответствие: ID проекта -> путь к файлу
        private readonly Dictionary<string, string> _projectPaths = new Dictionary<string, string>();

        /// <summary>Получить все открытые проекты</summary>
        public IReadOnlyList<ProjectFile> OpenProjects => _openProjects.AsReadOnly();

        /// <summary>Найти проект по пути к файлу</summary>
        public ProjectFile? GetProjectByPath(string filePath)
        {
            var projectId = _projectPaths.FirstOrDefault(x => x.Value == filePath).Key;  // ← Key это Title!
            if (projectId == null) return null;

            return _openProjects.FirstOrDefault(p => p.Title == projectId);  // ← Ищет по Title
        }

        /// <summary>Получить путь к файлу проекта</summary>
        public string? GetProjectPath(ProjectFile project)
        {
            return _projectPaths.TryGetValue(project.Title, out var path) ? path : null;
        }

        /// <summary>Создать новый проект</summary>
        public ProjectFile CreateNew(string title, ProjectType type)
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

        /// <summary>Загрузить проект из файла</summary>
        public async Task<ProjectFile?> LoadAsync(string filePath)
        {
            try
            {
                Console.WriteLine($"Loading project from: {filePath}");

                if (!File.Exists(filePath))
                {
                    Console.WriteLine("File does not exist!");
                    return null;
                }

                // Проверяем не открыт ли уже этот проект
                var existing = GetProjectByPath(filePath);
                if (existing != null)
                {
                    Console.WriteLine("Project already loaded, RE-LOADING from file");
                    // НЕ возвращаем старую версию, а ПЕРЕЗАГРУЖАЕМ из файла!
                    // Удаляем старую версию
                    _openProjects.Remove(existing);
                    _projectPaths.Remove(existing.Title);
                }

                var json = await File.ReadAllTextAsync(filePath);
                Console.WriteLine($"JSON loaded, length: {json.Length}");

                var project = JsonConvert.DeserializeObject<ProjectFile>(json);

                if (project != null)
                {
                    Console.WriteLine($"Project deserialized: {project.Title}, Documents: {project.Documents.Count}");

                    // Добавляем в список открытых проектов
                    _openProjects.Add(project);
                    _projectPaths[project.Title] = filePath;
                }
                else
                {
                    Console.WriteLine("Failed to deserialize project!");
                }

                return project;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load project: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                return null;
            }
        }

        /// <summary>Сохранить проект в файл</summary>
        public async Task<bool> SaveAsync(ProjectFile project, string filePath)
        {
            try
            {
                Console.WriteLine($"[SAVE] Saving project: {project.Title}");
                Console.WriteLine($"[SAVE] Documents count BEFORE serialize: {project.Documents.Count}");

                if (project.Documents.Count > 0)
                {
                    Console.WriteLine($"[SAVE] First document: ID={project.Documents[0].Id}, Title={project.Documents[0].Title}");
                }

                // Обновляем дату модификации
                project.LastModified = DateTime.Now;

                // Сериализуем в JSON
                var json = JsonConvert.SerializeObject(project, Formatting.Indented);

                Console.WriteLine($"[SAVE] Serialized JSON length: {json.Length}");

                // Создаём директорию если не существует
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Сохраняем в файл
                await File.WriteAllTextAsync(filePath, json);

                Console.WriteLine($"[SAVE] File written to: {filePath}");

                // СНАЧАЛА удаляем старый проект с таким же путём
                var existingProject = GetProjectByPath(filePath);
                if (existingProject != null && existingProject != project)
                {
                    Console.WriteLine($"[SAVE] Removing old project from cache: {existingProject.Title}");
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
                Console.WriteLine($"Failed to save project: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
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