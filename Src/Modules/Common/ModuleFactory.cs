using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Core.Interfaces.Modules;
using Writersword.Src.Core.Interfaces.Services.Input;

namespace Writersword.Modules.Common
{
    /// <summary>
    /// Фабрика для создания экземпляров модулей и получения их метаданных.
    /// Метаданные кешируются и сбрасываются при перерегистрации.
    /// При вызове RegisterAllHotKeys читает IHotKeyDescriptor из метаданных
    /// и регистрирует определения клавиш в HotKeyService без создания живых модулей.
    /// </summary>
    public class ModuleFactory
    {
        private readonly ILogger<ModuleFactory> _logger;
        private readonly Dictionary<string, Func<IModule>> _moduleCreators = new();
        private List<IModuleMetadata>? _cachedMetadata;

        public ModuleFactory()
        {
            _logger = App.Services.GetService<ILogger<ModuleFactory>>()!;
        }

        /// <summary>Зарегистрировать создатель модуля</summary>
        public void Register(string moduleType, Func<IModule> creator)
        {
            _moduleCreators[moduleType] = creator;
            _cachedMetadata = null;
            _logger.LogDebug("Registered: {moduleType}", moduleType);
        }

        /// <summary>Создать новый экземпляр модуля. Возвращает null если тип не зарегистрирован</summary>
        public IModule? Create(string moduleType)
        {
            if (_moduleCreators.TryGetValue(moduleType, out var creator))
            {
                var module = creator();
                _logger.LogDebug("Created: {moduleType}", moduleType);
                return module;
            }

            _logger.LogError("Module not registered: {moduleType}", moduleType);
            return null;
        }

        /// <summary>Проверить зарегистрирован ли тип модуля</summary>
        public bool IsRegistered(string moduleType) =>
            _moduleCreators.ContainsKey(moduleType);

        /// <summary>Получить все зарегистрированные ключи</summary>
        public IEnumerable<string> GetRegisteredTypes() =>
            _moduleCreators.Keys;

        /// <summary>
        /// Получить метаданные всех модулей для построения UI.
        /// Создаёт временные экземпляры только для чтения Metadata, сразу их уничтожает.
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

            foreach (var moduleType in GetRegisteredTypes())
            {
                var tempModule = Create(moduleType);
                if (tempModule?.Metadata != null)
                {
                    metadataList.Add(tempModule.Metadata);
                    tempModule.Dispose();
                    _logger.LogDebug("Cached metadata for: {moduleType}", moduleType);
                }
                else
                {
                    _logger.LogWarning("Failed to get metadata for: {moduleType}", moduleType);
                }
            }

            _cachedMetadata = metadataList;
            _logger.LogDebug("Metadata cache built: {Count} module types", metadataList.Count);
            return metadataList;
        }

        /// <summary>
        /// Зарегистрировать горячие клавиши всех модулей в HotKeyService.
        /// Читает IHotKeyDescriptor из метаданных каждого модуля.
        /// Не создаёт живые экземпляры модулей — только читает метаданные.
        /// Вызывается один раз при старте приложения после регистрации глобальных клавиш.
        /// </summary>
        public void RegisterAllHotKeys()
        {
            var hotKeyService = App.Services.GetRequiredService<IHotKeyService>();
            var metadata = GetAllModuleMetadata();
            int registeredCount = 0;

            foreach (var meta in metadata)
            {
                if (meta is IHotKeyDescriptor descriptor)
                {
                    hotKeyService.RegisterFromDescriptor(descriptor);
                    var keys = descriptor.GetHotKeys();
                    registeredCount += keys.Count;
                    _logger.LogDebug("Registered {Count} hotkeys from descriptor: {ModuleType}",
                        keys.Count, meta.ModuleType);
                }
            }

            _logger.LogDebug("RegisterAllHotKeys complete: {Count} total hotkeys registered", registeredCount);
        }

        /// <summary>Сбросить кеш метаданных</summary>
        public void ClearMetadataCache()
        {
            _cachedMetadata = null;
            _logger.LogDebug("Metadata cache cleared");
        }

        /// <summary>
        /// Получить все модули у которых есть настройки (реализуют IConfigurableModule).
        /// Создаёт временные экземпляры только для проверки интерфейса.
        /// </summary>
        public List<(string moduleType, IConfigurableModule configurable)> GetConfigurableModules()
        {
            var result = new List<(string, IConfigurableModule)>();

            foreach (var moduleType in GetRegisteredTypes())
            {
                var temp = Create(moduleType);
                if (temp is IConfigurableModule configurable)
                {
                    result.Add((moduleType, configurable));
                    _logger.LogDebug("Configurable module found: {moduleType}", moduleType);
                }
                else
                {
                    temp?.Dispose();
                }
            }

            _logger.LogDebug("Found {Count} configurable modules", result.Count);
            return result;
        }
    }
}