using System.Collections.Generic;
using Writersword.Core.Enums;
using Writersword.Core.Models.WorkModes;

namespace Writersword.Core.Services.Interfaces
{
    /// <summary>
    /// Главный сервис для работы с WorkModes
    /// Управляет режимами работы в проекте
    /// </summary>
    public interface IWorkModeService
    {
        /// <summary>Инициализировать WorkModes для проекта</summary>
        List<WorkMode> InitializeWorkModes(ProjectType projectType, List<WorkMode>? existingWorkModes);

        /// <summary>Добавить новый режим работы</summary>
        WorkMode AddWorkMode(WorkModeType type, string title, string icon);

        /// <summary>Удалить режим работы</summary>
        bool RemoveWorkMode(WorkMode workMode);

        /// <summary>Добавить модуль в режим</summary>
        ModuleSlot AddModuleToWorkMode(WorkMode workMode, ModuleType moduleType);

        /// <summary>Удалить модуль из режима</summary>
        bool RemoveModuleFromWorkMode(WorkMode workMode, ModuleSlot moduleSlot);

        /// <summary>Переместить модуль в другую позицию</summary>
        void MoveModule(ModuleSlot moduleSlot, WorkModeGridPosition newPosition);

        /// <summary>Изменить размер модуля</summary>
        void ResizeModule(ModuleSlot moduleSlot, WorkModeGridSize newSize);

        /// <summary>Показать/скрыть модуль</summary>
        void ToggleModuleVisibility(ModuleSlot moduleSlot);

        /// <summary>Получить все WorkModes проекта</summary>
        List<WorkMode> GetAllWorkModes();

        /// <summary>Получить активный WorkMode</summary>
        WorkMode? GetActiveWorkMode();

        /// <summary>Установить активный WorkMode</summary>
        void SetActiveWorkMode(WorkMode workMode);
    }
}