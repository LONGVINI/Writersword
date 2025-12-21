using System;
using Writersword.Core.Enums;
using Writersword.Core.Models.Modules;

namespace Writersword.Core.Interfaces.Modules
{
    /// <summary>
    /// Базовый интерфейс для всех модулей
    /// Модуль = независимый UI компонент с собственной логикой
    /// </summary>
    public interface IModule : IDisposable
    {
        /// <summary>Уникальный ID экземпляра модуля</summary>
        string InstanceId { get; }

        /// <summary>Тип модуля</summary>
        ModuleType ModuleType { get; }

        /// <summary>Заголовок модуля (отображается в UI)</summary>
        string Title { get; set; }

        /// <summary>ViewModel для привязки к View</summary>
        object? ViewModel { get; }

        /// <summary>Можно ли перемещать модуль</summary>
        bool IsMovable { get; }

        /// <summary>Можно ли закрыть модуль</summary>
        bool IsCloseable { get; }

        /// <summary>Можно ли вынести модуль в отдельное окно</summary>
        bool CanDetach { get; }

        /// <summary>Метаданные модуля для отображения в меню и UI</summary>
        IModuleMetadata Metadata { get; }

        /// <summary>Событие запроса закрытия модуля</summary>
        event Action<IModule>? RequestClose;

        /// <summary>Событие запроса выноса модуля в отдельное окно</summary>
        event Action<IModule>? RequestDetach;

        /// <summary>Инициализация модуля</summary>
        void Initialize();

        /// <summary>Сохранить состояние модуля</summary>
        ModuleState SaveState();

        /// <summary>Восстановить состояние модуля</summary>
        void RestoreState(ModuleState state);

        /// <summary>
        /// Создать View для этого модуля
        /// Модуль САМ знает какой View ему нужен!
        /// </summary>
        Avalonia.Controls.Control? CreateView();
    }
}