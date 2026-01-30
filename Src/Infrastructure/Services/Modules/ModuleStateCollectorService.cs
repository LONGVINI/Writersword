using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Interfaces.Services;

namespace Writersword.Infrastructure.Services.Modules
{
    /// <summary>
    /// Сервис для сбора данных из модулей
    /// Используется при кешировании, переключении вкладок и сохранении проекта
    /// НЕ ДЕЛАЕТ:
    /// - Не сравнивает данные (для этого IDataComparisonService)
    /// - Не сохраняет данные (для этого CacheService/ProjectService)
    /// - Не записывает в Project.ModulesData (это делает ProjectWorkflow)
    /// </summary>
    public class ModuleStateCollectorService : IModuleStateCollectorService
    {
        /// <summary>
        /// Собрать ТОЛЬКО CustomData из всех модулей
        /// Используется при сохранении в .writersword файл (Ctrl+S)
        /// Модули без данных НЕ включаются в результат
        /// </summary>
        public Dictionary<string, object?> CollectCustomData(IEnumerable<IModule> modules)
        {
            var customData = new Dictionary<string, object?>();

            foreach (var module in modules)
            {
                try
                {
                    var data = module.GetCustomData();

                    if (IsDataEmpty(data))
                    {
                        Console.WriteLine($"[ModuleStateCollector] Module is empty: {module.ModuleId}");
                        continue;
                    }

                    customData[module.ModuleId] = data;
                    Console.WriteLine($"[ModuleStateCollector] Collected CustomData: {module.ModuleId}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ModuleStateCollector] Error collecting CustomData from {module.ModuleId}: {ex.Message}");
                }
            }

            Console.WriteLine($"[ModuleStateCollector] Collected {customData.Count} CustomData entries");
            return customData;
        }

        /// <summary>
        /// Собрать ТОЛЬКО SessionData из всех модулей
        /// Используется редко, в основном для отладки
        /// </summary>
        public Dictionary<string, object?> CollectSessionData(IEnumerable<IModule> modules)
        {
            var sessionData = new Dictionary<string, object?>();

            foreach (var module in modules)
            {
                try
                {
                    var data = module.GetSessionData();

                    if (data != null)
                    {
                        sessionData[module.ModuleId] = data;
                        Console.WriteLine($"[ModuleStateCollector] Collected SessionData: {module.ModuleId}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ModuleStateCollector] Error collecting SessionData from {module.ModuleId}: {ex.Message}");
                }
            }

            Console.WriteLine($"[ModuleStateCollector] Collected {sessionData.Count} SessionData entries");
            return sessionData;
        }

        /// <summary>
        /// Собрать CustomData И SessionData из всех модулей
        /// Используется при кешировании (.wsasd) и переключении вкладок
        /// Возвращает ДВА словаря в виде кортежа
        /// </summary>
        public (Dictionary<string, object?> CustomData, Dictionary<string, object?> SessionData) CollectAllData(IEnumerable<IModule> modules)
        {
            var customData = new Dictionary<string, object?>();
            var sessionData = new Dictionary<string, object?>();

            foreach (var module in modules)
            {
                try
                {
                    var custom = module.GetCustomData();
                    var session = module.GetSessionData();

                    if (!IsDataEmpty(custom))
                    {
                        customData[module.ModuleId] = custom;
                        Console.WriteLine($"[ModuleStateCollector] Collected CustomData: {module.ModuleId}");
                    }
                    else
                    {
                        Console.WriteLine($"[ModuleStateCollector] Module is empty: {module.ModuleId}");
                    }

                    if (session != null)
                    {
                        sessionData[module.ModuleId] = session;
                        Console.WriteLine($"[ModuleStateCollector] Collected SessionData: {module.ModuleId}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ModuleStateCollector] Error collecting data from {module.ModuleId}: {ex.Message}");
                }
            }

            Console.WriteLine($"[ModuleStateCollector] Collected {customData.Count} CustomData and {sessionData.Count} SessionData entries");
            return (customData, sessionData);
        }

        /// <summary>
        /// Проверить пустые ли данные
        /// null или пустая строка = пустые данные
        /// </summary>
        private bool IsDataEmpty(object? data)
        {
            if (data == null)
                return true;

            if (data is string str && string.IsNullOrWhiteSpace(str))
                return true;

            return false;
        }
    }
}