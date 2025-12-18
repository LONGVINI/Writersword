using System;
using Writersword.Core.Enums;
using Writersword.Core.Models.Modules;

namespace Writersword.Core.Interfaces.Modules
{
    /// <summary>
    /// Базовый интерфейс для всех UI модулей
    /// Все модули из Src/Modules/ должны реализовывать этот интерфейс
    /// </summary>
    public interface IModule
    {
        /// <summary>Уникальный ID экземпляра модуля (каждый раз новый)</summary>
        string InstanceId { get; }

        /// <summary>Тип модуля (из enum)</summary>
        ModuleType ModuleType { get; }

        /// <summary>Заголовок модуля (отображается в UI)</summary>
        string Title { get; set; }

        /// <summary>Можно ли перемещать модуль</summary>
        bool IsMovable { get; }

        /// <summary>Можно ли закрыть модуль</summary>
        bool IsCloseable { get; }

        /// <summary>Можно ли вынести модуль на отдельное окно</summary>
        bool CanDetach { get; }

        /// <summary>ViewModel модуля (для привязки к View)</summary>
        object? ViewModel { get; }

        /// <summary>Инициализация модуля</summary>
        void Initialize();

        /// <summary>Сохранить состояние модуля</summary>
        ModuleState SaveState();

        /// <summary>Восстановить состояние модуля</summary>
        void RestoreState(ModuleState state);

        /// <summary>Уничтожение модуля (освобождение ресурсов)</summary>
        void Dispose();

        /// <summary>Событие: модуль запросил закрытие</summary>
        event Action<IModule>? RequestClose;

        /// <summary>Событие: модуль запросил открепление в отдельное окно</summary>
        event Action<IModule>? RequestDetach;
    }
}