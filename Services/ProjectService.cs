using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Writersword.Core.Models.Project;
using Writersword.Core.Enums;

namespace Writersword.Services
{
    /// <summary>
    /// Реализация сервиса работы с проектами
    /// </summary>
    public class ProjectService : IProjectService
    {
        private ProjectFile? _currentProject;
        private string? _currentFilePath;

        public ProjectFile? CurrentProject => _currentProject;
        public string? CurrentFilePath => _currentFilePath;

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

            // Добавляем первый документ
            project.Documents.Add(new DocumentTab
            {
                Title = "Document 1",
                IsActive = true
            });

            _currentProject = project;
            _currentFilePath = null; // Ещё не сохранён

            return project;
        }

        /// <summary>Загрузить проект из файла</summary>
        public async Task<ProjectFile?> LoadAsync(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                    return null;

                var json = await File.ReadAllTextAsync(filePath);
                var project = JsonConvert.DeserializeObject<ProjectFile>(json);

                if (project != null)
                {
                    _currentProject = project;
                    _currentFilePath = filePath;
                }

                return project;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load project: {ex.Message}");
                return null;
            }
        }

        /// <summary>Сохранить проект в файл</summary>
        public async Task<bool> SaveAsync(ProjectFile project, string filePath)
        {
            try
            {
                // Обновляем дату модификации
                project.LastModified = DateTime.Now;

                // Сериализуем в JSON
                var json = JsonConvert.SerializeObject(project, Formatting.Indented);

                // Создаём директорию если не существует
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Сохраняем в файл
                await File.WriteAllTextAsync(filePath, json);

                _currentProject = project;
                _currentFilePath = filePath;

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save project: {ex.Message}");
                return false;
            }
        }
    }
}