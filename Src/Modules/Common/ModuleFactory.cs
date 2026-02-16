using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Core.Interfaces.Modules;

namespace Writersword.Modules.Common
{
    /// <summary>
    /// Фабрика для создания экземпляров модулей
    /// Также предоставляет метаданные всех зарегистрированных типов модулей
    /// Метаданные кешируются при первом обращении
    /// </summary>
    public class ModuleFactory
    {
        private readonly ILogger<ModuleFactory> _logger;
        private readonly Dictionary<string, Func<string?, IModule>> _moduleCreators = new();
        private List<IModuleMetadata>? _cachedMetadata;

        public ModuleFactory()
        {
            _logger = App.Services.GetService<ILogger<ModuleFactory>>()!;
        }

        /// <summary>Зарегистрировать создатель модуля</summary>
        public void Register(string moduleId, Func<string?, IModule> creator)
        {
            _moduleCreators[moduleId] = creator;
            _logger.LogDebug("Registered: {ModuleId}", moduleId);

            _cachedMetadata = null;
        }

        /// <summary>Создать экземпляр модуля</summary>
        /// <param name="moduleId">Тип модуля</param>
        /// <param name="instanceId">ID экземпляра (если null - генерируется новый)</param>
        public IModule? Create(string moduleId, string? instanceId = null)
        {
            if (_moduleCreators.TryGetValue(moduleId, out var creator))
            {
                var module = creator(instanceId);
                _logger.LogDebug("Created: {ModuleId} (ID: {InstanceId})", moduleId, module.InstanceId);
                return module;
            }

            _logger.LogError("Module not registered: {ModuleId}", moduleId);
            return null;
        }

        /// <summary>Проверить зарегистрирован ли модуль</summary>
        public bool IsRegistered(string moduleId)
        {
            return _moduleCreators.ContainsKey(moduleId);
        }

        /// <summary>Получить все зарегистрированные типы</summary>
        public IEnumerable<string> GetRegisteredTypes()
        {
            return _moduleCreators.Keys;
        }

        /// <summary>
        /// Получить метаданные ВСЕХ зарегистрированных модулей
        /// Метаданные кешируются при первом вызове
        /// НЕ создаёт экземпляры модулей для проектов
        /// Используется для построения меню модулей в UI
        /// </summary>
        public List<IModuleMetadata> GetAllModuleMetadata()
        {
            if (_cachedMetadata != null)
            {
                _logger.LogDebug("Returning cached metadata for {Count} module types", _cachedMetadata.Count);
                return _cachedMetadata;
            }

            _logger.LogDebug("Building metadata cache...");

            var metadataList = new List<IModuleMetadata>();

            foreach (var moduleId in GetRegisteredTypes())
            {
                var tempModule = Create(moduleId);

                if (tempModule?.Metadata != null)
                {
                    metadataList.Add(tempModule.Metadata);

                    tempModule.Dispose();
                    _logger.LogDebug("Cached metadata for: {ModuleId}", moduleId);
                }
            }

            _cachedMetadata = metadataList;

            _logger.LogDebug("Metadata cache built: {Count} module types", metadataList.Count);
            return metadataList;
        }

        /// <summary>
        /// Очистить кеш метаданных
        /// Используется при перерегистрации модулей
        /// </summary>
        public void ClearMetadataCache()
        {
            _cachedMetadata = null;
            _logger.LogDebug("Metadata cache cleared");
        }
    }
}