using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Writersword.Src.WorkModes.Common;

namespace Writersword.Src.ProjectTypes.Common
{
    /// <summary>
    /// Реестр всех типов проектов
    /// Автоматически находит все классы наследующие BaseProjectType через Reflection
    /// Проверяет корректность WorkModes перед регистрацией
    /// </summary>
    public class ProjectTypeRegistry
    {
        private readonly ILogger<ProjectTypeRegistry> _logger;

        /// <summary>Словарь зарегистрированных типов проектов (ключ = Id типа)</summary>
        private readonly Dictionary<string, BaseProjectType> _types = new();

        /// <summary>Реестр WorkModes для валидации</summary>
        private readonly WorkModeRegistry _workModeRegistry;

        public ProjectTypeRegistry(WorkModeRegistry workModeRegistry)
        {
            _logger = App.Services.GetService<ILogger<ProjectTypeRegistry>>()!;
            _workModeRegistry = workModeRegistry;
        }

        /// <summary>
        /// Автоматически загрузить все типы проектов через Reflection
        /// Находит все классы наследующие BaseProjectType и регистрирует их
        /// </summary>
        public void LoadAll()
        {
            _logger.LogDebug("Starting automatic registration...");

            var assembly = Assembly.GetExecutingAssembly();

            // Находим все классы которые наследуют BaseProjectType и не являются абстрактными
            var projectTypeTypes = assembly.GetTypes()
                .Where(t => typeof(BaseProjectType).IsAssignableFrom(t) && !t.IsAbstract);

            foreach (var type in projectTypeTypes)
            {
                // Создаём экземпляр типа проекта
                var instance = Activator.CreateInstance(type) as BaseProjectType;
                if (instance != null)
                {
                    // Проверяем что все WorkMode существуют
                    var missingWorkModes = new List<string>();

                    foreach (var workModeId in instance.WorkModes)
                    {
                        if (_workModeRegistry.GetWorkMode(workModeId) == null)
                        {
                            missingWorkModes.Add(workModeId);
                        }
                    }

                    // Если есть несуществующие WorkMode - НЕ регистрируем тип проекта
                    if (missingWorkModes.Count > 0)
                    {
                        _logger.LogWarning("Errors in {DisplayName}:", instance.DisplayName);
                        foreach (var missing in missingWorkModes)
                        {
                            _logger.LogWarning("WorkMode '{WorkMode}' not found", missing);
                        }
                        continue;
                    }

                    // Всё ОК - регистрируем
                    _types[instance.Id] = instance;
                    _logger.LogDebug("Registered: {DisplayName} ({Icon})", instance.DisplayName, instance.Icon);
                }
            }

            _logger.LogDebug("Total registered: {Count}", _types.Count);
        }

        /// <summary>Получить все зарегистрированные типы проектов</summary>
        public List<BaseProjectType> GetAll()
        {
            return _types.Values.ToList();
        }

        /// <summary>Получить тип проекта по ID</summary>
        /// <param name="id">ID типа проекта (например "Novel")</param>
        /// <returns>Тип проекта или null если не найден</returns>
        public BaseProjectType? GetById(string id)
        {
            return _types.TryGetValue(id, out var projectType) ? projectType : null;
        }
    }
}