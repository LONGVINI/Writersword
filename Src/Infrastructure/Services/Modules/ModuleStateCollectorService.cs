using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        private readonly ILogger<ModuleStateCollectorService> _logger;

        public ModuleStateCollectorService()
        {
            _logger = App.Services.GetService<ILogger<ModuleStateCollectorService>>()!;
        }

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
                        _logger.LogDebug("Module is empty: {ModuleId}", module.ModuleId);
                        continue;
                    }

                    customData[module.InstanceId] = data;
                    _logger.LogDebug("Collected CustomData: {ModuleId}", module.ModuleId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error collecting CustomData from {ModuleId}", module.ModuleId);
                }
            }

            _logger.LogDebug("Collected {Count} CustomData entries", customData.Count);
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
                        _logger.LogDebug("Collected SessionData: {ModuleId}", module.ModuleId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error collecting SessionData from {ModuleId}", module.ModuleId);
                }
            }

            _logger.LogDebug("Collected {Count} SessionData entries", sessionData.Count);
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
                        customData[module.InstanceId] = custom;
                        _logger.LogDebug("Collected CustomData: {ModuleId}", module.ModuleId);
                    }
                    else
                    {
                        _logger.LogDebug("Module is empty: {ModuleId}", module.ModuleId);
                    }

                    if (session != null)
                    {
                        sessionData[module.InstanceId] = session;
                        _logger.LogDebug("Collected SessionData: {ModuleId}", module.ModuleId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error collecting data from {ModuleId}", module.ModuleId);
                }
            }

            _logger.LogDebug("Collected {CustomCount} CustomData and {SessionCount} SessionData entries", customData.Count, sessionData.Count);
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