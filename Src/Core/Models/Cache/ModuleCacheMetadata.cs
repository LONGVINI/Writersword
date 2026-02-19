using System;
using System.Collections.Generic;
using Writersword.Core.Interfaces.Modules;

namespace Writersword.Core.Models.Cache
{
    /// <summary>
    /// Метаданные кеша проекта
    /// Хранятся в cache.json внутри .writersword.wsasd (ZIP архив)
    /// </summary>
    public class ModuleCacheMetadata
    {
        /// <summary>
        /// ID проекта (GUID из ProjectFile)
        /// Используется для проверки что кеш принадлежит правильному проекту
        /// </summary>
        public string ProjectId { get; set; } = "";

        /// <summary>
        /// Путь к файлу проекта
        /// Для отображения в логах и диагностики
        /// </summary>
        public string ProjectPath { get; set; } = "";

        /// <summary>
        /// Дата создания/обновления кеша
        /// Используется для отображения в Recovery диалоге
        /// </summary>
        public DateTime CacheDate { get; set; }

        /// <summary>
        /// Версия формата кеша
        /// Для будущей совместимости при изменении структуры
        /// </summary>
        public int Version { get; set; } = 1;

        /// <summary>
        /// Метаданные модулей (хеши, размеры, даты изменений)
        /// Ключ: moduleType (например "TextEditor", "Timer")
        /// Значение: метаданные модуля
        /// </summary>
        public Dictionary<string, ModuleHashMetadata> Modules { get; set; } = new();
    }
}