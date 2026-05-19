using Microsoft.Extensions.DependencyInjection;
using Serilog;
using System;
using System.Collections.Generic;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Services;
using Writersword.Core.Interfaces.Services.Input;

namespace Writersword.Modules.Common
{
    /// <summary>
    /// Фабрика для создания экземпляров модулей и получения их метаданных.
    /// Хранит живые экземпляры IConfigurableModule для применения глобальных настроек.
    /// </summary>
    public class ModuleFactory
    {
        private static readonly ILogger _logger = Log.ForContext<ModuleFactory>();

        private readonly Dictionary<string, Func<IModule>> _moduleCreators = new();
        private readonly Dictionary<string, IConfigurableModule> _liveConfigurables = new();
        private List<IModuleMetadata>? _cachedMetadata;

        // ── Регистрация типов ─────────────────────────────────────────────

        /// <summary>Зарегистрировать создатель модуля.</summary>
        public void Register(string moduleType, Func<IModule> creator)
        {
            _moduleCreators[moduleType] = creator;
            _cachedMetadata = null;
            _logger.Debug("Registered: {ModuleType}", moduleType);
        }

        /// <summary>Создать новый экземпляр модуля. Возвращает null если тип не зарегистрирован.</summary>
        public IModule? Create(string moduleType)
        {
            if (_moduleCreators.TryGetValue(moduleType, out var creator))
            {
                var module = creator();
                _logger.Debug("Created: {ModuleType}", moduleType);
                return module;
            }

            _logger.Error("Module not registered: {ModuleType}", moduleType);
            return null;
        }

        /// <summary>Проверить зарегистрирован ли тип модуля.</summary>
        public bool IsRegistered(string moduleType) =>
            _moduleCreators.ContainsKey(moduleType);

        /// <summary>Получить все зарегистрированные ключи.</summary>
        public IEnumerable<string> GetRegisteredTypes() =>
            _moduleCreators.Keys;

        // ── Живые экземпляры ──────────────────────────────────────────────

        /// <summary>
        /// Зарегистрировать живой экземпляр модуля.
        /// Вызывается модулем при Initialize().
        /// </summary>
        public void RegisterLive(string moduleType, IConfigurableModule configurable)
        {
            _liveConfigurables[moduleType] = configurable;
            _logger.Debug("RegisterLive: {ModuleType}", moduleType);
        }

        /// <summary>
        /// Снять живой модуль с регистрации.
        /// Вызывается модулем при Dispose().
        /// </summary>
        public void UnregisterLive(string moduleType)
        {
            _liveConfigurables.Remove(moduleType);
            _logger.Debug("UnregisterLive: {ModuleType}", moduleType);
        }

        /// <summary>
        /// Получить живой экземпляр модуля если он зарегистрирован.
        /// Возвращает null если модуль не запущен.
        /// </summary>
        public IConfigurableModule? GetLive(string moduleType) =>
            _liveConfigurables.TryGetValue(moduleType, out var m) ? m : null;

        // ── Метаданные ────────────────────────────────────────────────────

        /// <summary>
        /// Получить метаданные всех модулей для построения UI.
        /// Создаёт временные экземпляры только для чтения Metadata, сразу их уничтожает.
        /// </summary>
        public List<IModuleMetadata> GetAllModuleMetadata()
        {
            if (_cachedMetadata != null)
            {
                _logger.Debug("Returning cached metadata: {Count} types", _cachedMetadata.Count);
                return _cachedMetadata;
            }

            _logger.Debug("Building metadata cache...");
            var metadataList = new List<IModuleMetadata>();

            foreach (var moduleType in GetRegisteredTypes())
            {
                var tempModule = Create(moduleType);
                if (tempModule?.Metadata != null)
                {
                    metadataList.Add(tempModule.Metadata);
                    tempModule.Dispose();
                    _logger.Debug("Cached metadata: {ModuleType}", moduleType);
                }
                else
                {
                    _logger.Warning("Failed to get metadata: {ModuleType}", moduleType);
                }
            }

            _cachedMetadata = metadataList;
            _logger.Debug("Metadata cache built: {Count} types", metadataList.Count);
            return _cachedMetadata;
        }

        /// <summary>Сбросить кеш метаданных.</summary>
        public void ClearMetadataCache()
        {
            _cachedMetadata = null;
            _logger.Debug("Metadata cache cleared");
        }

        // ── Горячие клавиши ───────────────────────────────────────────────

        /// <summary>
        /// Зарегистрировать горячие клавиши всех модулей в HotKeyService.
        /// Не создаёт живые экземпляры — только читает метаданные.
        /// </summary>
        public void RegisterAllHotKeys()
        {
            var hotKeyService = CoreServices.GetRequiredService<IHotKeyService>();
            var metadata = GetAllModuleMetadata();
            int total = 0;

            foreach (var meta in metadata)
            {
                if (meta is IHotKeyDescriptor descriptor)
                {
                    hotKeyService.RegisterFromDescriptor(descriptor);
                    var keys = descriptor.GetHotKeys();
                    total += keys.Count;
                    _logger.Debug("Registered {Count} hotkeys: {ModuleType}", keys.Count, meta.ModuleType);
                }
            }

            _logger.Debug("RegisterAllHotKeys complete: {Total} total hotkeys", total);
        }

        // ── Настраиваемые модули ──────────────────────────────────────────

        /// <summary>
        /// Получить все модули у которых есть настройки (реализуют IConfigurableModule).
        /// Использует живой экземпляр если модуль запущен, иначе создаёт временный.
        /// Временные экземпляры используются только как носители UI настроек — применять через GetLive().
        /// </summary>
        public List<(string moduleType, IConfigurableModule configurable)> GetConfigurableModules()
        {
            var result = new List<(string, IConfigurableModule)>();

            foreach (var moduleType in GetRegisteredTypes())
            {
                var live = GetLive(moduleType);
                if (live is not null)
                {
                    result.Add((moduleType, live));
                    _logger.Debug("Configurable module (live): {ModuleType}", moduleType);
                    continue;
                }

                // Живого нет — создаём временный экземпляр только для чтения настроек
                var temp = Create(moduleType);
                if (temp is IConfigurableModule tempConfigurable)
                {
                    result.Add((moduleType, tempConfigurable));
                    _logger.Debug("Configurable module (temp): {ModuleType}", moduleType);
                }
                else
                {
                    _logger.Warning("Module is not IConfigurableModule, skipping: {ModuleType}", moduleType);
                }
            }

            _logger.Debug("Found {Count} configurable modules", result.Count);
            return result;
        }
    }
}