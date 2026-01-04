using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Models.Modules;
using Writersword.Core.Interfaces.Services;

namespace Writersword.Infrastructure.Services.Modules
{
    /// <summary>
    /// Сервис для сбора состояний модулей
    /// Используется при автосохранении, переключении WorkMode и сохранении проекта
    /// </summary>
    public class ModuleStateCollectorService : IModuleStateCollectorService
    {
        /// <summary>
        /// Собрать ПОЛНЫЕ состояния всех модулей (CustomData + SessionData)
        /// Используется при переключении WorkMode
        /// </summary>
        public Dictionary<string, ModuleState> CollectAllStates(IEnumerable<IModule> modules)
        {
            var states = new Dictionary<string, ModuleState>();

            foreach (var module in modules)
            {
                // Проверяем изменился ли модуль с последнего сохранения
                if (!module.IsDirty)
                {
                    Console.WriteLine($"[ModuleStateCollector] Skipping {module.ModuleId} (not dirty)");
                    continue;
                }

                var state = CollectModuleState(module);
                if (state != null)
                {
                    states[module.ModuleId] = state;

                    // Помечаем модуль как сохранённый
                    module.MarkAsClean();

                    Console.WriteLine($"[ModuleStateCollector] Collected full state: {module.ModuleId}");
                }
            }

            Console.WriteLine($"[ModuleStateCollector] Collected {states.Count} full module states");
            return states;
        }

        /// <summary>
        /// Собрать ТОЛЬКО CustomData всех модулей (для сохранения в .writersword)
        /// Используется при Ctrl+S
        /// </summary>
        public Dictionary<string, object?> CollectCustomData(IEnumerable<IModule> modules)
        {
            var customData = new Dictionary<string, object?>();

            foreach (var module in modules)
            {
                // Проверяем изменился ли модуль с последнего сохранения
                if (!module.IsDirty)
                {
                    Console.WriteLine($"[ModuleStateCollector] Skipping {module.ModuleId} (not dirty)");
                    continue;
                }

                var state = CollectModuleState(module);
                if (state?.CustomData != null)
                {
                    customData[module.ModuleId] = state.CustomData;

                    // Помечаем модуль как сохранённый
                    module.MarkAsClean();

                    Console.WriteLine($"[ModuleStateCollector] Collected CustomData: {module.ModuleId}");
                }
            }

            Console.WriteLine($"[ModuleStateCollector] Collected {customData.Count} CustomData entries");
            return customData;
        }

        /// <summary>
        /// Собрать ТОЛЬКО SessionData всех модулей (для автосохранения в .wsasd)
        /// Используется при автосохранении каждые 10 секунд
        /// </summary>
        public Dictionary<string, object?> CollectSessionData(IEnumerable<IModule> modules)
        {
            var sessionData = new Dictionary<string, object?>();

            foreach (var module in modules)
            {
                var state = CollectModuleState(module);
                if (state?.SessionData != null)
                {
                    sessionData[module.ModuleId] = state.SessionData;
                    Console.WriteLine($"[ModuleStateCollector] Collected SessionData: {module.ModuleId}");
                }
            }

            Console.WriteLine($"[ModuleStateCollector] Collected {sessionData.Count} SessionData entries");
            return sessionData;
        }

        /// <summary>
        /// Собрать состояние одного модуля
        /// Модуль сам решает что сохранять через SaveState()
        /// </summary>
        public ModuleState? CollectModuleState(IModule module)
        {
            try
            {
                var state = module.SaveState();

                // Проверяем есть ли хоть что-то для сохранения
                if (state.CustomData != null || state.SessionData != null || state.ScrollPosition > 0)
                {
                    return state;
                }

                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ModuleStateCollector] Error collecting state from {module.ModuleId}: {ex.Message}");
                return null;
            }
        }
    }
}