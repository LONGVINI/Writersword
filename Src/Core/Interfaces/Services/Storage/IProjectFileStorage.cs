using System;
using System.Collections.Generic;

namespace Writersword.Core.Interfaces.Services
{
    /// <summary>
    /// Интерфейс для работы с файлами внутри проекта (ZIP архива)
    /// Модули используют этот интерфейс через DocumentContext
    /// </summary>
    public interface IProjectFileStorage : IDisposable
    {
        /// <summary>
        /// Записать файл в проект
        /// </summary>
        /// <param name="relativePath">Относительный путь (например "TextEditor/images/photo.png")</param>
        /// <param name="data">Данные файла</param>
        void WriteFile(string relativePath, byte[] data);

        /// <summary>
        /// Прочитать файл из проекта
        /// </summary>
        /// <param name="relativePath">Относительный путь</param>
        /// <returns>Данные файла или null если не найден</returns>
        byte[]? ReadFile(string relativePath);

        /// <summary>
        /// Проверить существует ли файл
        /// </summary>
        bool FileExists(string relativePath);

        /// <summary>
        /// Удалить файл из проекта
        /// </summary>
        void DeleteFile(string relativePath);

        /// <summary>
        /// Получить список всех файлов в папке
        /// </summary>
        /// <param name="relativePath">Путь к папке (например "TextEditor/images")</param>
        /// <returns>Список относительных путей к файлам</returns>
        IEnumerable<string> GetFiles(string relativePath);
    }
}