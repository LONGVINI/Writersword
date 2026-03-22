using System;
using Writersword.Core.Interfaces.Services;

namespace Writersword.Core.Interfaces.Services.Storage
{
    /// <summary>
    /// Сервис для сохранения и загрузки локальных настроек модулей.
    /// Каждый модуль хранит свои настройки в {moduleType}/settings.json внутри project.zip.
    /// </summary>
    public interface ILocalSettingsStorageService
    {
        /// <summary>
        /// Сохранить локальные настройки модуля в project.zip.
        /// Путь: {moduleType}/settings.json
        /// </summary>
        void Save(IProjectFileStorage storage, string moduleType, object settings);

        /// <summary>
        /// Загрузить локальные настройки модуля из project.zip.
        /// Возвращает null если файл не найден или не удалось десериализовать.
        /// </summary>
        object? Load(IProjectFileStorage storage, string moduleType, Type settingsType);
    }
}