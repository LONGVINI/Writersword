using System;
using System.Collections.Generic;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Modules;

namespace Writersword.Modules.Common
{
    /// <summary>
    /// Реестр всех активных экземпляров модулей
    /// Управляет жизненным циклом модулей
    /// </summary>
    public class ModuleRegistry
    {
        private readonly Dictionary<string, IModule> _activeModules = new();
        private readonly ModuleFactory _factory;

        public ModuleRegistry(ModuleFactory factory)
        {
            _factory = factory;
        }

        /// <summary>Создать и зарегистрировать модуль</summary>
        public IModule? CreateModule(ModuleType type)
        {
            var module = _factory.Create(type);
            if (module != null)
            {
                _activeModules[module.InstanceId] = module;
                module.Initialize();
                module.RequestClose += OnModuleRequestClose;
                module.RequestDetach += OnModuleRequestDetach;
                Console.WriteLine($"[ModuleRegistry] Module created: {type}");
            }
            return module;
        }

        /// <summary>Получить модуль по ID</summary>
        public IModule? GetModule(string instanceId)
        {
            return _activeModules.TryGetValue(instanceId, out var module) ? module : null;
        }

        /// <summary>Удалить модуль</summary>
        public void RemoveModule(string instanceId)
        {
            if (_activeModules.TryGetValue(instanceId, out var module))
            {
                module.RequestClose -= OnModuleRequestClose;
                module.RequestDetach -= OnModuleRequestDetach;
                module.Dispose();
                _activeModules.Remove(instanceId);
                Console.WriteLine($"[ModuleRegistry] Module removed: {instanceId}");
            }
        }

        /// <summary>Получить все активные модули</summary>
        public IEnumerable<IModule> GetAllModules()
        {
            return _activeModules.Values;
        }

        /// <summary>Очистить все модули</summary>
        public void Clear()
        {
            foreach (var module in _activeModules.Values)
            {
                module.Dispose();
            }
            _activeModules.Clear();
        }

        private void OnModuleRequestClose(IModule module)
        {
            RemoveModule(module.InstanceId);
        }

        private void OnModuleRequestDetach(IModule module)
        {
            Console.WriteLine($"[ModuleRegistry] Module detach requested: {module.InstanceId}");
            // TODO: Открепление в отдельное окно
        }


        /// <summary>
        /// Получить метаданные ВСЕХ зарегистрированных модулей
        /// Создаёт временный экземпляр каждого типа для чтения метаданных
        /// </summary>
        public List<IModuleMetadata> GetAllModuleMetadata()
        {
            var metadataList = new List<IModuleMetadata>();

            // Получаем все зарегистрированные типы из фабрики
            foreach (var moduleType in _factory.GetRegisteredTypes())
            {
                // Создаём временный экземпляр для чтения метаданных
                var tempModule = _factory.Create(moduleType);
                if (tempModule?.Metadata != null)
                {
                    metadataList.Add(tempModule.Metadata);

                    // Сразу удаляем временный экземпляр
                    tempModule.Dispose();
                }
            }

            Console.WriteLine($"[ModuleRegistry] Loaded metadata for {metadataList.Count} module types");
            return metadataList;
        }
    }
}