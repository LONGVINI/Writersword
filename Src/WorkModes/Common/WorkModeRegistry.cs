using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Src.Core.Interfaces.WorkModes;

namespace Writersword.Src.WorkModes.Common
{
    /// <summary>
    /// Реестр всех зарегистрированных WorkMode
    /// Хранит метаданные и управляет экземплярами
    /// </summary>
    public class WorkModeRegistry
    {
        private readonly Dictionary<string, IWorkMode> _workModes = new();

        /// <summary>Зарегистрировать WorkMode</summary>
        public void Register(IWorkMode workMode)
        {
            _workModes[workMode.Id] = workMode;
            Console.WriteLine($"[WorkModeRegistry] Registered: {workMode.Id} - {workMode.DisplayName}");
        }

        /// <summary>Получить все зарегистрированные WorkMode</summary>
        public List<IWorkMode> GetAll()
        {
            return _workModes.Values.OrderBy(m => m.Order).ToList();
        }

        /// <summary>
        /// Получить WorkMode для конкретного типа проекта
        /// Возвращает все WorkMode, которые подходят для данного типа
        /// </summary>
        /// <param name="projectType">Тип проекта (Novel, Screenplay, Poetry, etc.)</param>
        /// <returns>Список WorkMode для этого типа проекта</returns>
        public List<IWorkMode> GetWorkModesForProjectType(string projectType)
        {
            // TODO: В будущем можно добавить фильтрацию по типу проекта
            // Например, WorkMode может иметь свойство SupportedProjectTypes

            // Пока возвращаем все WorkMode (универсальные для любого проекта)
            return GetAll();
        }

        /// <summary>Получить WorkMode по ID</summary>
        public IWorkMode? GetWorkMode(string id)
        {
            return _workModes.TryGetValue(id, out var workMode) ? workMode : null;
        }

        /// <summary>Проверить зарегистрирован ли WorkMode</summary>
        public bool IsRegistered(string id)
        {
            return _workModes.ContainsKey(id);
        }
    }
}