using System;
using System.Threading.Tasks;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Interfaces.Services;

namespace Writersword.Infrastructure.Services.Modules
{
    /// <summary>
    /// Сервис управления жизненным циклом модулей
    /// Отвечает за открытие, закрытие и восстановление состояния модулей
    /// </summary>
    public class ModuleLifecycleService : IModuleLifecycleService
    {
        private readonly IZipCacheService _cacheService;
        private readonly IModuleStateCollectorService _stateCollector;

        public ModuleLifecycleService(
            IZipCacheService cacheService,
            IModuleStateCollectorService stateCollector)
        {
            _cacheService = cacheService;
            _stateCollector = stateCollector;
        }

        /// <summary>
        /// Закрыть модуль с сохранением его состояния в кеш
        /// ЕДИНАЯ ТОЧКА закрытия модулей во всём приложении
        /// </summary>
        /// <param name="module">Модуль для закрытия</param>
        /// <param name="projectPath">Путь к проекту</param>
        /// <param name="projectId">GUID проекта</param>
        public async Task CloseModuleAsync(IModule module, string projectPath, string projectId)
        {
            Console.WriteLine($"[ModuleLifecycle] Closing module: {module.ModuleId}");

            try
            {
                // 1. Собираем состояние модуля
                var state = _stateCollector.CollectModuleState(module);

                // 2. Сохраняем в кеш (если есть что сохранять)
                if (state != null)
                {
                    await _cacheService.SaveModuleStateAsync(projectPath, projectId, module.ModuleId.ToString(), state);
                    Console.WriteLine($"[ModuleLifecycle] State saved for: {module.ModuleId}");
                }

                // 3. Модуль сам решает: остановиться или работать в фоне
                module.Dispose();

                Console.WriteLine($"[ModuleLifecycle] Module closed: {module.ModuleId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ModuleLifecycle] Error closing module {module.ModuleId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Восстановить состояние модуля из кеша
        /// </summary>
        public void RestoreModule(IModule module, string projectPath)
        {
            Console.WriteLine($"[ModuleLifecycle] Restoring module: {module.ModuleId}");

            try
            {
                // Загружаем состояние из кеша
                var state = _cacheService.GetModuleState(projectPath, module.ModuleId.ToString());

                if (state != null)
                {
                    // Восстанавливаем состояние
                    module.RestoreState(state);
                    Console.WriteLine($"[ModuleLifecycle] State restored for: {module.ModuleId}");
                }
                else
                {
                    Console.WriteLine($"[ModuleLifecycle] No cached state for: {module.ModuleId}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ModuleLifecycle] Error restoring module {module.ModuleId}: {ex.Message}");
            }
        }
    }
}