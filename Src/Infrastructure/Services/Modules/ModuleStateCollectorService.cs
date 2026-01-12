using System;
using System.Collections.Generic;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Models.Modules;
using Writersword.Core.Interfaces.Services;

namespace Writersword.Infrastructure.Services.Modules
{
    /// <summary>
    /// Сервис для сбора состояний модулей
    /// Используется при кешировании, переключении WorkMode и сохранении проекта
    /// 
    /// ФУНКЦИИ:
    /// - Собирает данные из активных модулей через module.SaveState()
    /// - Фильтрует данные (CustomData, SessionData, или всё)
    /// - Упаковывает в словари для передачи другим сервисам
    /// 
    /// НЕ ДЕЛАЕТ:
    /// - Не сравнивает данные (для этого IDataComparisonService)
    /// - Не сохраняет данные (для этого CacheService/ProjectService)
    /// - Не принимает решения о сохранении
    /// </summary>
    public class ModuleStateCollectorService : IModuleStateCollectorService
    {
        /// <summary>
        /// Собрать ПОЛНЫЕ состояния ВСЕХ модулей (CustomData + SessionData)
        /// БЕЗ проверки IsDirty - собирает всегда
        /// Используется при кешировании (.wsasd) и переключении вкладок
        /// </summary>
        public Dictionary<string, ModuleState> CollectAllStates(IEnumerable<IModule> modules)
        {
            var states = new Dictionary<string, ModuleState>();

            foreach (var module in modules)
            {
                var state = CollectModuleState(module);
                if (state != null)
                {
                    states[module.ModuleId] = state;
                    Console.WriteLine($"[ModuleStateCollector] Collected full state: {module.ModuleId}");
                }
            }

            Console.WriteLine($"[ModuleStateCollector] Collected {states.Count} full module states");
            return states;
        }

        /// <summary>
        /// Собрать ТОЛЬКО CustomData всех модулей (для сохранения в .writersword)
        /// Используется при Ctrl+S для сохранения основных данных проекта
        /// SessionData не включается (это временные рабочие данные)
        /// </summary>
        public Dictionary<string, object?> CollectCustomData(IEnumerable<IModule> modules)
        {
            var customData = new Dictionary<string, object?>();

            foreach (var module in modules)
            {
                var state = CollectModuleState(module);
                if (state?.CustomData != null)
                {
                    customData[module.ModuleId] = state.CustomData;
                    Console.WriteLine($"[ModuleStateCollector] Collected CustomData: {module.ModuleId}");
                }
            }

            Console.WriteLine($"[ModuleStateCollector] Collected {customData.Count} CustomData entries");
            return customData;
        }

        /// <summary>
        /// Собрать ТОЛЬКО SessionData всех модулей
        /// Используется редко, в основном для отладки или специальных сценариев
        /// SessionData = временные данные (курсор, скролл, время редактирования)
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
        /// Вызывает module.SaveState() - модуль сам решает что возвращать
        /// Возвращает null если модуль пустой (нет ни CustomData, ни SessionData)
        /// </summary>
        public ModuleState? CollectModuleState(IModule module)
        {
            try
            {
                // Модуль сам решает что сохранять
                var state = module.SaveState();

                // Проверяем есть ли хоть что-то для сохранения
                if (state.CustomData != null || state.SessionData != null)
                {
                    return state;
                }

                // Модуль пустой - не сохраняем
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