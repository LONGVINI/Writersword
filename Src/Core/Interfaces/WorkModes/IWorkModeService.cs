using System.Collections.Generic;
using Writersword.Core.Models.WorkModes;

namespace Writersword.Src.Core.Interfaces.WorkModes
{
    /// <summary>
    /// Главный сервис для работы с WorkModes
    /// Управляет режимами работы в проекте
    /// </summary>
    public interface IWorkModeService
    {
        /// <summary>Инициализировать WorkModes для проекта</summary>
        List<WorkMode> InitializeWorkModes(string projectType, List<WorkMode>? savedWorkModes = null);

        /// <summary>Добавить новый режим работы</summary>
        WorkMode AddWorkMode(string workModeId, string title, string icon);

        /// <summary>Удалить режим работы</summary>
        bool RemoveWorkMode(WorkMode workMode);

        /// <summary>Добавить модуль в режим</summary>
        ModuleSlot AddModuleToWorkMode(WorkMode workMode, string moduleType);

        /// <summary>Удалить модуль из режима</summary>
        bool RemoveModuleFromWorkMode(WorkMode workMode, ModuleSlot moduleSlot);

        /// <summary>Получить все WorkModes проекта</summary>
        List<WorkMode> GetAllWorkModes();

        /// <summary>Получить активный WorkMode</summary>
        WorkMode? GetActiveWorkMode();

        /// <summary>Установить активный WorkMode</summary>
        void SetActiveWorkMode(WorkMode workMode);
    }
}