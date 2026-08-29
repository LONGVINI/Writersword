using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Core.Interfaces.Modules;
using Writersword.Modules.Common;

namespace Writersword.Core.Services
{
    /// <summary>
    /// Изолированный контейнер модулей для одного проекта
    /// Каждый проект имеет свой собственный экземпляр ProjectModuleContext
    /// При закрытии проекта все его модули автоматически уничтожаются
    /// - Модули хранятся по moduleType, так как один тип модуля может быть
    ///   только один раз в рамках одного проекта
    /// - Модули изолированы — проект А не видит модули проекта Б
    /// </summary>
    public class ProjectModuleContext : IDisposable
    {
        private readonly ILogger<ProjectModuleContext> _logger;
        private readonly Dictionary<string, IModule> _modules;
        private readonly ModuleFactory _factory;
        private readonly string _projectId;

        /// <summary>
        /// Конструктор контейнера модулей проекта
        /// </summary>
        /// <param name="projectId">ID проекта (GUID) — используется для верификации данных при загрузке</param>
        /// <param name="factory">Фабрика для создания модулей</param>
        public ProjectModuleContext(string projectId, ModuleFactory factory)
        {
            _logger = App.Services.GetService<ILogger<ProjectModuleContext>>()!;
            _projectId = projectId;
            _factory = factory;
            _modules = new Dictionary<string, IModule>();

            _logger.LogDebug("Created for project: {ProjectId}", _projectId);
        }

        /// <summary>
        /// Создать и зарегистрировать модуль
        /// Если модуль такого типа уже существует — возвращает существующий
        /// Два модуля одного типа в одном проекте недопустимы
        /// </summary>
        /// <param name="moduleType">Тип модуля (TextEditor, Notes, Timer...)</param>
        /// <returns>Созданный или существующий модуль, null если не удалось создать</returns>
        public IModule? CreateModule(string moduleType)
        {
            if (_modules.TryGetValue(moduleType, out var existing))
            {
                _logger.LogDebug("Module already exists, returning existing: {moduleType}", moduleType);
                return existing;
            }

            var module = _factory.Create(moduleType);

            if (module != null)
            {
                _modules[moduleType] = module;

                module.Initialize();

                module.RequestClose += OnModuleRequestClose;
                module.RequestDetach += OnModuleRequestDetach;

                _logger.LogDebug("Module created: {moduleType}", moduleType);
            }
            else
            {
                _logger.LogError("Failed to create module: {moduleType}", moduleType);
            }

            return module;
        }

        /// <summary>
        /// Получить модуль по moduleType
        /// </summary>
        /// <param name="moduleType">Тип модуля</param>
        /// <returns>Модуль или null если не найден</returns>
        public IModule? GetModule(string moduleType)
        {
            return _modules.TryGetValue(moduleType, out var module) ? module : null;
        }

        /// <summary>
        /// Получить все модули этого проекта
        /// </summary>
        public List<IModule> GetAllModules()
        {
            return _modules.Values.ToList();
        }

        /// <summary>
        /// Удалить модуль из контейнера
        /// Отписывается от событий и вызывает Dispose
        /// </summary>
        /// <param name="moduleType">Тип модуля для удаления</param>
        public void RemoveModule(string moduleType)
        {
            if (_modules.TryGetValue(moduleType, out var module))
            {
                module.RequestClose -= OnModuleRequestClose;
                module.RequestDetach -= OnModuleRequestDetach;

                module.Dispose();

                _modules.Remove(moduleType);

                // Со стеком вызова: снос модуля означает потерю его данных до
                // следующей загрузки, и когда модуль потом поднимается пустым,
                // единственный нужный ответ — кто его снёс. Путей сюда несколько:
                // крестик на вкладке, смена рабочего режима, уборка модулей не из
                // нового режима. По имени метода они не различаются.
                _logger.LogDebug("Module removed: {moduleType}\nCall stack:\n{Stack}",
                    moduleType, ShortStack());
            }
        }

        /// <summary>
        /// Несколько верхних кадров стека, только свои. Полный стек здесь не нужен:
        /// интересны ближайшие вызывающие, а сотня строк в журнале лишь прячет
        /// соседние записи.
        /// </summary>
        private static string ShortStack()
        {
            try
            {
                var kept = new List<string>();

                foreach (var line in Environment.StackTrace.Split('\n'))
                {
                    if (line.Contains("ShortStack", StringComparison.Ordinal)) continue;
                    if (!line.Contains("Writersword", StringComparison.Ordinal)) continue;

                    kept.Add(line.TrimEnd());
                    if (kept.Count >= 8) break;
                }

                return kept.Count > 0 ? string.Join("\n", kept) : "(своих кадров не найдено)";
            }
            catch
            {
                return "(стек прочитать не удалось)";
            }
        }

        /// <summary>
        /// Уничтожить ВСЕ модули проекта
        /// Вызывается при закрытии проекта
        /// </summary>
        public void Dispose()
        {
            _logger.LogDebug("Disposing all modules for project: {ProjectId}", _projectId);

            var modulesToDispose = _modules.Values.ToList();

            foreach (var module in modulesToDispose)
            {
                try
                {
                    module.RequestClose -= OnModuleRequestClose;
                    module.RequestDetach -= OnModuleRequestDetach;

                    module.Dispose();

                    _logger.LogDebug("Disposed module: {moduleType}", module.moduleType);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error disposing module {moduleType}", module.moduleType);
                }
            }

            _modules.Clear();

            _logger.LogDebug("All modules disposed for project: {ProjectId}", _projectId);
        }

        /// <summary>
        /// Количество модулей в контейнере (для диагностики)
        /// </summary>
        public int Count => _modules.Count;

        /// <summary>
        /// Обработчик запроса на закрытие модуля
        /// </summary>
        private void OnModuleRequestClose(IModule module)
        {
            _logger.LogDebug("Module requests close: {moduleType}", module.moduleType);
            RemoveModule(module.moduleType);
        }

        /// <summary>
        /// Обработчик запроса на открепление модуля в отдельное окно
        /// </summary>
        private void OnModuleRequestDetach(IModule module)
        {
            _logger.LogDebug("Module requests detach: {moduleType}", module.moduleType);
        }
    }
}