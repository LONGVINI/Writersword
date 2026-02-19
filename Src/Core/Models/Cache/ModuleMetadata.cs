using System;

namespace Writersword.Core.Models.Cache
{
    /// <summary>
    /// Метаданные одного модуля в кеше
    /// Используются для проверки изменений и диагностики
    /// </summary>
    public class ModuleHashMetadata
    {
        /// <summary>
        /// SHA256 хеш CustomData модуля
        /// Вычисляется ТОЛЬКО для CustomData (SessionData не влияет на хеш)
        /// </summary>
        public string Hash { get; set; } = "";

        /// <summary>
        /// Дата последнего изменения CustomData модуля
        /// </summary>
        public DateTime LastModified { get; set; }

        /// <summary>
        /// Размер CustomData модуля в байтах (после сериализации в JSON)
        /// </summary>
        public long Size { get; set; }
    }
}