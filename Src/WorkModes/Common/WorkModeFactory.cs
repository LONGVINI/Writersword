using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using Writersword.Core.Interfaces.WorkModes;

namespace Writersword.WorkModes.Common
{
    /// <summary>
    /// Фабрика для создания экземпляров WorkMode
    /// </summary>
    public class WorkModeFactory
    {
        private readonly ILogger<WorkModeFactory> _logger;
        private readonly Dictionary<string, Func<IWorkMode>> _creators = new();

        public WorkModeFactory()
        {
            _logger = App.Services.GetService<ILogger<WorkModeFactory>>()!;
        }

        /// <summary>Зарегистрировать создатель WorkMode</summary>
        public void Register(string id, Func<IWorkMode> creator)
        {
            _creators[id] = creator;
            _logger.LogDebug("Registered: {Id}", id);
        }

        /// <summary>Создать экземпляр WorkMode</summary>
        public IWorkMode? Create(string id)
        {
            if (_creators.TryGetValue(id, out var creator))
            {
                var workMode = creator();
                _logger.LogDebug("Created: {Id}", id);
                return workMode;
            }

            _logger.LogError("Not registered: {Id}", id);
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