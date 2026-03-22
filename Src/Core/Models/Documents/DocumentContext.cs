using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Models.Project;
using Writersword.Core.Interfaces.WorkFlows;
using Writersword.Infrastructure.Services.Storage;

namespace Writersword.Core.Services
{
    /// <summary>
    /// Контекст документа - передаётся модулям для доступа к общим данным и сервисам
    /// Позволяет модулям быть автономными и самостоятельно реагировать на изменения
    /// Объединяет данные проекта и функциональность работы с файлами
    /// </summary>
    public class DocumentContext
    {
        private readonly ILogger<DocumentContext> _logger;

        /// <summary>Режим просмотра без редактирования (для сравнения версий)</summary>
        public bool IsInCompareMode { get; set; }

        /// <summary>Проект, к которому относится документ</summary>
        public ProjectFile Project { get; set; }

        /// <summary>Путь к файлу проекта</summary>
        public string FilePath { get; set; }

        /// <summary>
        /// Сервис для работы с файлами внутри проекта (ZIP архива)
        /// Используется модулями для сохранения/загрузки файлов
        /// </summary>
        public IProjectFileStorage? FileStorage { get; set; }

        /// <summary>Конструктор</summary>
        public DocumentContext(ProjectFile project, string filePath)
        {
            _logger = App.Services.GetService<ILogger<DocumentContext>>()!;
            Project = project;
            FilePath = filePath;
            IsInCompareMode = false;
            FileStorage = null;
        }

        /// <summary>
        /// Записать файл в проект (удобный метод для модулей)
        /// </summary>
        /// <param name="relativePath">Относительный путь (например "TextEditor/images/photo.png")</param>
        /// <param name="data">Данные файла</param>
        public void WriteFile(string relativePath, byte[] data)
        {
            if (FileStorage == null)
            {
                _logger.LogWarning("FileStorage is null, cannot write file");
                return;
            }

            FileStorage.WriteFile(relativePath, data);
        }

        /// <summary>
        /// Прочитать файл из проекта (удобный метод для модулей)
        /// </summary>
        /// <param name="relativePath">Относительный путь</param>
        /// <returns>Данные файла или null если не найден</returns>
        public byte[]? ReadFile(string relativePath)
        {
            if (FileStorage == null)
            {
                _logger.LogWarning("FileStorage is null, cannot read file");
                return null;
            }

            return FileStorage.ReadFile(relativePath);
        }

        /// <summary>
        /// Проверить существует ли файл
        /// </summary>
        public bool FileExists(string relativePath)
        {
            if (FileStorage == null)
                return false;

            return FileStorage.FileExists(relativePath);
        }

        /// <summary>
        /// Удалить файл из проекта
        /// </summary>
        public void DeleteFile(string relativePath)
        {
            if (FileStorage == null)
            {
                _logger.LogWarning("FileStorage is null, cannot delete file");
                return;
            }

            FileStorage.DeleteFile(relativePath);
        }

        /// <summary>
        /// Получить список файлов в папке
        /// </summary>
        public System.Collections.Generic.IEnumerable<string> GetFiles(string relativePath)
        {
            if (FileStorage == null)
                return System.Linq.Enumerable.Empty<string>();

            return FileStorage.GetFiles(relativePath);
        }

        /// <summary>
        /// Временно закрыть ZIP для освобождения файла
        /// Используется перед операциями требующими эксклюзивный доступ к файлу (загрузка, сохранение)
        /// </summary>
        public void CloseZipStorage()
        {
            FileStorage?.Dispose();
            FileStorage = null;
        }

        /// <summary>
        /// Переоткрыть ZIP после завершения операций с файлом
        /// Восстанавливает доступ модулям к файлам внутри архива
        /// </summary>
        public void ReopenZipStorage()
        {
            if (!string.IsNullOrEmpty(FilePath) && File.Exists(FilePath))
            {
                var newStorage = new ZipFileStorageService(FilePath);
                FileStorage = newStorage;

                var projectWorkflow = App.Services.GetRequiredService<IProjectWorkflow>();
                projectWorkflow.UpdateStorageForProject(FilePath, newStorage);
            }
        }
    }
}