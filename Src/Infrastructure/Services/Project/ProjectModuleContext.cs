using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Core.Interfaces.Modules;
using Writersword.Infrastructure.Services.Modules;
using Writersword.Modules.Common;

namespace Writersword.Core.Services
{
    /// <summary>
    /// Изолированный контейнер модулей для одного проекта
    /// Каждый проект имеет свой собственный экземпляр ProjectModuleContext
    /// При закрытии проекта все его модули автоматически уничтожаются
    /// - Модули хранятся по InstanceId (GUID), а не по ModuleId
    /// - Один проект может иметь несколько экземпляров одного типа модуля (например 2 TextEditor)
    /// - Модули изолированы - проект А не видит модули проекта Б
    /// </summary>
    public class ProjectModuleContext : IDisposable
    {
        private readonly Dictionary<string, IModule> _modules;
        private readonly ModuleFactory _factory;
        private readonly string _projectId;

        /// <summary>
        /// Конструктор контейнера модулей проекта
        /// </summary>
        /// <param name="projectId">ID проекта (GUID)</param>
        /// <param name="factory">Фабрика для создания модулей</param>
        public ProjectModuleContext(string projectId, ModuleFactory factory)
        {
            _projectId = projectId;
            _factory = factory;
            _modules = new Dictionary<string, IModule>();

            Console.WriteLine($"[ProjectModuleContext] Created for project: {_projectId}");
        }

        /// <summary>
        /// Создать и зарегистрировать модуль
        /// Модуль создается через фабрику и добавляется в контейнер проекта
        /// </summary>
        /// <param name="moduleId">Тип модуля (TextEditor, Notes, Timer...)</param>
        /// <param name="instanceId">ID экземпляра (GUID). Если null - генерируется новый</param>
        /// <returns>Созданный модуль или null если не удалось создать</returns>
        public IModule? CreateModule(string moduleId, string? instanceId = null)
        {
            var module = _factory.Create(moduleId, instanceId);

            if (module != null)
            {
                // Регистрируем модуль по его InstanceId (GUID)
                _modules[module.InstanceId] = module;

                // Инициализируем модуль
                module.Initialize();

                // Подписываемся на события
                module.RequestClose += OnModuleRequestClose;
                module.RequestDetach += OnModuleRequestDetach;

                Console.WriteLine($"[ProjectModuleContext] Module created: {moduleId} (Instance: {module.InstanceId})");
            }
            else
            {
                Console.WriteLine($"[ProjectModuleContext] ERROR: Failed to create module: {moduleId}");
            }

            return module;
        }

        /// <summary>
        /// Получить модуль по InstanceId (GUID)
        /// </summary>
        /// <param name="instanceId">Уникальный ID экземпляра модуля</param>
        /// <returns>Модуль или null если не найден</returns>
        public IModule? GetModule(string instanceId)
        {
            return _modules.TryGetValue(instanceId, out var module) ? module : null;
        }

        /// <summary>
        /// Получить все модули этого проекта
        /// Возвращает список всех зарегистрированных модулей
        /// </summary>
        /// <returns>Список всех модулей проекта</returns>
        public List<IModule> GetAllModules()
        {
            return _modules.Values.ToList();
        }

        /// <summary>
        /// Удалить модуль из контейнера
        /// Отписывается от событий и вызывает Dispose
        /// </summary>
        /// <param name="instanceId">ID экземпляра модуля для удаления</param>
        public void RemoveModule(string instanceId)
        {
            if (_modules.TryGetValue(instanceId, out var module))
            {
                // Отписываемся от событий
                module.RequestClose -= OnModuleRequestClose;
                module.RequestDetach -= OnModuleRequestDetach;

                // Уничтожаем модуль
                module.Dispose();

                // Удаляем из контейнера
                _modules.Remove(instanceId);

                Console.WriteLine($"[ProjectModuleContext] Module removed: {instanceId}");
            }
        }

        /// <summary>
        /// Уничтожить ВСЕ модули проекта
        /// Вызывается при закрытии проекта
        /// Гарантирует полную очистку ресурсов
        /// </summary>
        public void Dispose()
        {
            Console.WriteLine($"[ProjectModuleContext] Disposing all modules for project: {_projectId}");

            // Копируем список модулей (чтобы избежать изменения коллекции во время итерации)
            var modulesToDispose = _modules.Values.ToList();

            foreach (var module in modulesToDispose)
            {
                try
                {
                    // Отписываемся от событий
                    module.RequestClose -= OnModuleRequestClose;
                    module.RequestDetach -= OnModuleRequestDetach;

                    // Уничтожаем модуль
                    module.Dispose();

                    Console.WriteLine($"[ProjectModuleContext] Disposed module: {module.ModuleId} (Instance: {module.InstanceId})");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ProjectModuleContext] ERROR disposing module {module.InstanceId}: {ex.Message}");
                }
            }

            // Очищаем контейнер
            _modules.Clear();

            Console.WriteLine($"[ProjectModuleContext] All modules disposed for project: {_projectId}");
        }

        /// <summary>
        /// Обработчик запроса на закрытие модуля
        /// Вызывается когда модуль сам хочет закрыться (например кнопка Close)
        /// </summary>
        private void OnModuleRequestClose(IModule module)
        {
            Console.WriteLine($"[ProjectModuleContext] Module requests close: {module.InstanceId}");
            RemoveModule(module.InstanceId);
        }

        /// <summary>
        /// Обработчик запроса на открепление модуля в отдельное окно
        /// </summary>
        private void OnModuleRequestDetach(IModule module)
        {
            Console.WriteLine($"[ProjectModuleContext] Module requests detach: {module.InstanceId}");
            // Обработка открепления (уже реализована в DockFactory)
        }

        /// <summary>
        /// Получить количество модулей в контейнере
        /// Используется для диагностики
        /// </summary>
        public int Count => _modules.Count;
    }
}