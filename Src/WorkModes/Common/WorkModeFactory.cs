using System;
using System.Collections.Generic;
using Writersword.Src.Core.Interfaces.WorkModes;

namespace Writersword.Src.WorkModes.Common
{
    /// <summary>
    /// Фабрика для создания экземпляров WorkMode
    /// </summary>
    public class WorkModeFactory
    {
        private readonly Dictionary<string, Func<IWorkMode>> _creators = new();

        /// <summary>Зарегистрировать создатель WorkMode</summary>
        public void Register(string id, Func<IWorkMode> creator)
        {
            _creators[id] = creator;
            Console.WriteLine($"[WorkModeFactory] Registered: {id}");
        }

        /// <summary>Создать экземпляр WorkMode</summary>
        public IWorkMode? Create(string id)
        {
            if (_creators.TryGetValue(id, out var creator))
            {
                var workMode = creator();
                Console.WriteLine($"[WorkModeFactory] Created: {id}");
                return workMode;
            }

            Console.WriteLine($"[WorkModeFactory] ERROR: Not registered: {id}");
            return null;
        }

        /// <summary>Проверить зарегистрирован ли WorkMode</summary>
        public bool IsRegistered(string id)
        {
            return _creators.ContainsKey(id);
        }

        /// <summary>Получить все зарегистрированные ID</summary>
        public IEnumerable<string> GetRegisteredIds()
        {
            return _creators.Keys;
        }
    }
}