//using System;
//using System.Collections.Generic;
//using Writersword.Core.Models.Modules;

//namespace Writersword.Core.Models.Cache
//{
//    /// <summary>
//    /// Модель кеша проекта (.wsasd файл)
//    /// Хранит состояния модулей для быстрого восстановления сессии
//    /// </summary>
//    public class ModuleCache
//    {
//        /// <summary>
//        /// Состояния модулей
//        /// Ключ: ModuleType (например "TextEditor", "Timer")
//        /// Значение: ModuleState (CustomData + SessionData)
//        /// </summary>
//        public Dictionary<string, ModuleState> Modules { get; set; } = new();

//        /// <summary>
//        /// Дата создания/обновления кеша
//        /// Используется для отображения в Recovery диалоге
//        /// </summary>
//        public DateTime CacheDate { get; set; }
//    }
//}