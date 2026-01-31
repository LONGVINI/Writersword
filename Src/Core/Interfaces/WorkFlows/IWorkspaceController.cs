using Dock.Model.Controls;
using Dock.Model.Core;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Models.WorkModes;

namespace Writersword.Src.Core.Interfaces.Workspace
{
    /// <summary>
    /// Интерфейс контроллера рабочего пространства одной вкладки (проекта)
    /// Управляет всем UI состоянием проекта:
    /// - WorkModes и переключение между ними
    /// - DockLayout и расположение модулей
    /// - Float окна (создание, закрытие, восстановление)
    /// - Автосохранение workspace.json
    /// Полностью изолирован - каждая вкладка имеет свой экземпляр
    /// </summary>
    public interface IWorkspaceController : IDisposable
    {
        /// <summary>
        /// Событие изменения workspace
        /// Используется для обновления UI компонентов в MainWindow
        /// </summary>
        event EventHandler? WorkspaceChanged;

        /// <summary>
        /// Получить текущий DockLayout для отображения в MainWindow
        /// </summary>
        IRootDock GetCurrentLayout();

        /// <summary>
        /// Получить все доступные WorkModes проекта
        /// </summary>
        List<WorkMode> GetAvailableWorkModes();

        /// <summary>
        /// Получить активный WorkMode
        /// </summary>
        WorkMode GetActiveWorkMode();

        /// <summary>
        /// Получить активные модули текущего WorkMode
        /// Используется для кеширования и сохранения
        /// Фильтрует модули по InstanceId из активного WorkMode
        /// </summary>
        List<IModule> GetActiveModules();

        /// <summary>
        /// Переключить WorkMode
        /// Сохраняет текущий WorkMode, уничтожает модули, создаёт новый Layout
        /// </summary>
        void SwitchWorkMode(WorkMode newMode);

        /// <summary>
        /// Добавить модуль динамически в активный WorkMode
        /// </summary>
        void AddModule(string moduleId);

        /// <summary>
        /// Удалить модуль из активного WorkMode
        /// </summary>
        void RemoveModule(string moduleId);

        /// <summary>
        /// Вернуть обязательный модуль из Float окна обратно в Dock
        /// Восстанавливает позицию из workspace.json
        /// </summary>
        void ReturnRequiredModuleToDock(string moduleId);

        /// <summary>
        /// Сохранить workspace.json
        /// Вызывается перед закрытием вкладки
        /// </summary>
        Task SaveWorkspaceAsync();

        /// <summary>
        /// Обработчик закрытия модуля в Dock
        /// Вызывается из DockFactory когда модуль закрыт пользователем
        /// </summary>
        void HandleModuleClosedInDock(string moduleId);
    }
}