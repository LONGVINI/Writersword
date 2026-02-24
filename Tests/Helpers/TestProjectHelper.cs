using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Models.Project;
using Writersword.Modules.TextEditor;
using Writersword.Modules.TextEditor.ViewModels;

namespace Tests.Helpers
{
    /// <summary>
    /// Вспомогательный класс для работы с тестовыми проектами
    /// Управляет файловой системой, создаёт/удаляет тестовые файлы
    /// Использует папку bin\Debug\net10.0\TestProjects\
    /// </summary>
    public static class TestProjectHelper
    {
        // =============================================================================
        // УПРАВЛЕНИЕ ПАПКАМИ
        // =============================================================================

        /// <summary>
        /// Получить путь к папке с тестовыми проектами
        /// Создаёт папку TestProjects рядом с папкой Projects
        /// </summary>
        public static string GetTestProjectsDirectory()
        {
            // Получаем текущую рабочую директорию (bin\Debug\net10.0)
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;

            // Создаём папку TestProjects
            var testProjectsDir = Path.Combine(baseDir, "TestProjects");

            if (!Directory.Exists(testProjectsDir))
            {
                Directory.CreateDirectory(testProjectsDir);
                Console.WriteLine($"[TestProjectHelper] Created TestProjects directory: {testProjectsDir}");
            }

            return testProjectsDir;
        }

        /// <summary>
        /// Убедиться что папка TestProjects существует
        /// </summary>
        public static void EnsureTestDirectoryExists()
        {
            GetTestProjectsDirectory();
        }

        /// <summary>
        /// Удалить все файлы из папки TestProjects
        /// Вызывается в [TearDown] или [OneTimeTearDown]
        /// НЕ ТРОГАЕТ папку Projects!
        /// </summary>
        public static void CleanupTestFiles()
        {
            try
            {
                var testProjectsDir = GetTestProjectsDirectory();

                if (Directory.Exists(testProjectsDir))
                {
                    var files = Directory.GetFiles(testProjectsDir);

                    foreach (var file in files)
                    {
                        try
                        {
                            File.Delete(file);
                            Console.WriteLine($"[TestProjectHelper] Deleted: {Path.GetFileName(file)}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[TestProjectHelper] Failed to delete {file}: {ex.Message}");
                        }
                    }

                    Console.WriteLine($"[TestProjectHelper] Cleanup completed: {files.Length} files deleted");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TestProjectHelper] Cleanup ERROR: {ex.Message}");
            }
        }

        // =============================================================================
        // РАБОТА С ФАЙЛАМИ
        // =============================================================================

        /// <summary>
        /// Получить полный путь к тестовому файлу проекта
        /// Путь: bin\Debug\net10.0\TestProjects\{name}.writersword
        /// </summary>
        /// <param name="name">Имя файла (без расширения или с ним)</param>
        public static string GetTestFilePath(string name)
        {
            // Убираем расширение если оно есть
            var nameWithoutExt = Path.GetFileNameWithoutExtension(name);

            var testProjectsDir = GetTestProjectsDirectory();
            return Path.Combine(testProjectsDir, $"{nameWithoutExt}.writersword");
        }

        /// <summary>
        /// Получить путь к файлу кеша
        /// Путь: bin\Debug\net10.0\TestProjects\{name}.writersword.wsasd
        /// </summary>
        public static string GetCacheFilePath(string projectName)
        {
            var projectPath = GetTestFilePath(projectName);
            return projectPath + ".wsasd";
        }

        /// <summary>
        /// Проверить существует ли тестовый проект
        /// </summary>
        public static bool FileExists(string name)
        {
            var filePath = GetTestFilePath(name);
            var exists = File.Exists(filePath);
            Console.WriteLine($"[TestProjectHelper] FileExists({name}): {exists}");
            return exists;
        }

        /// <summary>
        /// Проверить существует ли файл кеша
        /// </summary>
        public static bool CacheFileExists(string name)
        {
            var cachePath = GetCacheFilePath(name);
            var exists = File.Exists(cachePath);
            Console.WriteLine($"[TestProjectHelper] CacheFileExists({name}): {exists}");
            return exists;
        }

        /// <summary>
        /// Удалить тестовый проект и его кеш
        /// </summary>
        public static void DeleteTestProject(string name)
        {
            var projectPath = GetTestFilePath(name);
            var cachePath = GetCacheFilePath(name);

            if (File.Exists(projectPath))
            {
                File.Delete(projectPath);
                Console.WriteLine($"[TestProjectHelper] Deleted project: {name}");
            }

            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
                Console.WriteLine($"[TestProjectHelper] Deleted cache: {name}.wsasd");
            }
        }

        // =============================================================================
        // РАБОТА С ПРОЕКТАМИ
        // =============================================================================

        /// <summary>
        /// Создать тестовый ProjectFile в памяти
        /// НЕ сохраняет на диск!
        /// </summary>
        public static ProjectFile CreateTestProject(string title = "TestProject")
        {
            return new ProjectFile
            {
                Title = title,
                Type = "Novel",
                FormatVersion = "2.0",
                CreatedAt = DateTime.Now,
                LastModified = DateTime.Now,
                ModulesData = new Dictionary<string, object?>()
            };
        }

        /// <summary>
        /// Сохранить проект на диск
        /// Сохраняет в bin\Debug\net10.0\TestProjects\{name}.writersword
        /// </summary>
        /// <param name="project">Проект для сохранения</param>
        /// <param name="name">Имя файла (используется в GetTestFilePath)</param>
        public static async Task SaveTestProject(ProjectFile project, string name)
        {
            var filePath = GetTestFilePath(name);

            var json = JsonConvert.SerializeObject(project, Formatting.Indented);
            await File.WriteAllTextAsync(filePath, json);

            Console.WriteLine($"[TestProjectHelper] Saved project: {filePath}");
        }

        /// <summary>
        /// Загрузить проект с диска
        /// </summary>
        public static async Task<ProjectFile?> LoadTestProject(string name)
        {
            var filePath = GetTestFilePath(name);

            if (!File.Exists(filePath))
            {
                Console.WriteLine($"[TestProjectHelper] Project not found: {filePath}");
                return null;
            }

            var json = await File.ReadAllTextAsync(filePath);
            var project = JsonConvert.DeserializeObject<ProjectFile>(json);

            Console.WriteLine($"[TestProjectHelper] Loaded project: {name}");
            return project;
        }

        /// <summary>
        /// Создать и сохранить проект с данными TextEditor
        /// </summary>
        public static async Task<string> CreateAndSaveProjectWithText(string name, string text)
        {
            var project = CreateTestProject(name);
            project.ModulesData["TextEditor"] = text;

            await SaveTestProject(project, name);

            return GetTestFilePath(name);
        }

        // =============================================================================
        // РАБОТА С МОДУЛЯМИ (АБСТРАКЦИИ)
        // =============================================================================

        /// <summary>
        /// Создать TextEditorModule с текстом
        /// АБСТРАКЦИЯ: не зависит от внутреннего формата данных
        /// </summary>
        public static TextEditorModule CreateTextEditor(string content = "")
        {
            var module = new TextEditorModule();
            module.Initialize();

            if (!string.IsNullOrEmpty(content))
            {
                SetTextInModule(module, content);
            }

            return module;
        }

        /// <summary>
        /// Получить текст из TextEditorModule
        /// АБСТРАКЦИЯ: всегда возвращает текст, независимо от формата
        /// </summary>
        public static string GetTextFromModule(TextEditorModule module)
        {
            if (module.ViewModel is TextEditorViewModel viewModel)
            {
                return viewModel.PlainText ?? "";
            }

            Console.WriteLine("[TestProjectHelper] WARNING: ViewModel is null or wrong type");
            return "";
        }

        /// <summary>
        /// Установить текст в TextEditorModule
        /// АБСТРАКЦИЯ: работает независимо от внутреннего формата
        /// </summary>
        public static void SetTextInModule(TextEditorModule module, string text)
        {
            if (module.ViewModel is TextEditorViewModel viewModel)
            {
                viewModel.PlainText = text;
            }
            else
            {
                Console.WriteLine("[TestProjectHelper] WARNING: Cannot set text - ViewModel is null or wrong type");
            }
        }

        /// <summary>
        /// Цикл сохранения и восстановления модуля
        /// АБСТРАКЦИЯ: тестирует полный цикл независимо от формата данных
        /// </summary>
        public static string SaveAndRestoreModule(TextEditorModule module)
        {
            // 1. Сохранить состояние через GetCustomData
            var customData = module.GetCustomData();

            // 2. Создать новый модуль
            var newModule = CreateTextEditor();

            // 3. Восстановить состояние через SetCustomData
            newModule.SetCustomData(customData);

            // 4. Получить текст
            return GetTextFromModule(newModule);
        }

        // =============================================================================
        // ПРОВЕРКИ
        // =============================================================================

        /// <summary>
        /// Проверить что проект содержит данные TextEditor
        /// </summary>
        public static bool ProjectHasTextEditorData(ProjectFile project)
        {
            return project.ModulesData.ContainsKey("TextEditor")
                   && project.ModulesData["TextEditor"] != null;
        }

        /// <summary>
        /// Получить текст из проекта (если есть)
        /// </summary>
        public static string? GetTextFromProject(ProjectFile project)
        {
            if (project.ModulesData.TryGetValue("TextEditor", out var data))
            {
                return data as string;
            }

            return null;
        }
    }
}