using Dock.Model.Controls;
using Dock.Model.Core;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Models.WorkModes;
using Writersword.Src.Core.Interfaces.WorkModes;

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
        /// Обновить все активные модули из Context
        /// Используется когда Context.IsInCompareMode меняется
        /// </summary>
        void RefreshModulesFromContext();

        /// <summary>
        /// Обработчик закрытия модуля в Dock
        /// Вызывается из DockFactory когда модуль закрыт пользователем
        /// </summary>
        void HandleModuleClosedInDock(string moduleId);

        /// <summary>
        /// Получить WorkModeService этого проекта
        /// Используется для получения списка WorkModes при сохранении workspace.json
        /// </summary>
        IWorkModeService GetWorkModeService();

        /// <summary>
        /// Получить список ID всех открытых модулей
        /// Сканирует основной dock и все float окна
        /// Используется для синхронизации меню модулей с реальным UI
        /// </summary>
        HashSet<string> GetOpenModuleIds();

        /// <summary>
        /// Активировать workspace (вызывается при возврате на вкладку)
        /// Полностью пересоздаёт Layout из актуальных данных WorkMode, восстанавливая все Float окна
        /// Подписывается на события Dock заново
        /// </summary>
        void Activate();

        /// <summary>
        /// Деактивировать workspace (вызывается при смене вкладки)
        /// Полностью очищает Layout от всех Document и закрывает Float окна
        /// Отписывается от событий Dock
        /// Модули остаются IsCurrentlyOpen = true для восстановления при Activate()
        /// </summary>
        void Deactivate();
    }
}