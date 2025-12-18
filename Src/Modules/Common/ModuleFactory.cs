using System;
using System.Collections.Generic;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Modules;

namespace Writersword.Modules.Common
{
    /// <summary>
    /// Фабрика для создания экземпляров модулей
    /// </summary>
    public class ModuleFactory
    {
        private readonly Dictionary<ModuleType, Func<IModule>> _moduleCreators = new();

        /// <summary>Зарегистрировать создатель модуля</summary>
        public void Register(ModuleType type, Func<IModule> creator)
        {
            _moduleCreators[type] = creator;
            Console.WriteLine($"[ModuleFactory] Registered: {type}");
        }

        /// <summary>Создать экземпляр модуля</summary>
        public IModule? Create(ModuleType type)
        {
            if (_moduleCreators.TryGetValue(type, out var creator))
            {
                var module = creator();
                Console.WriteLine($"[ModuleFactory] Created: {type} (ID: {module.InstanceId})");
                return module;
            }

            Console.WriteLine($"[ModuleFactory] ERROR: Module not registered: {type}");
            return null;
        }

        /// <summary>Проверить зарегистрирован ли модуль</summary>
        public bool IsRegistered(ModuleType type)
        {
            return _moduleCreators.ContainsKey(type);
        }

        /// <summary>Получить все зарегистрированные типы</summary>
        public IEnumerable<ModuleType> GetRegisteredTypes()
        {
            return _moduleCreators.Keys;
        }
    }
}