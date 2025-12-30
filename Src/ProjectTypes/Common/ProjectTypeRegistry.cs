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
        /// <summary>Словарь зарегистрированных типов проектов (ключ = Id типа)</summary>
        private readonly Dictionary<string, BaseProjectType> _types = new();

        /// <summary>Реестр WorkModes для валидации</summary>
        private readonly WorkModeRegistry _workModeRegistry;

        public ProjectTypeRegistry(WorkModeRegistry workModeRegistry)
        {
            _workModeRegistry = workModeRegistry;
        }

        /// <summary>
        /// Автоматически загрузить все типы проектов через Reflection
        /// Находит все классы наследующие BaseProjectType и регистрирует их
        /// </summary>
        public void LoadAll()
        {
            Console.WriteLine("[ProjectTypeRegistry] Starting automatic registration...");

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
                        Console.WriteLine($"[ProjectTypeRegistry] ⚠️ ERRORS in {instance.DisplayName}:");
                        foreach (var missing in missingWorkModes)
                            Console.WriteLine($"  - WorkMode '{missing}' not found");
                        continue;
                    }

                    // Всё ОК - регистрируем
                    _types[instance.Id] = instance;
                    Console.WriteLine($"[ProjectTypeRegistry] ✓ Registered: {instance.DisplayName} ({instance.Icon})");
                }
            }

            Console.WriteLine($"[ProjectTypeRegistry] Total registered: {_types.Count}");
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