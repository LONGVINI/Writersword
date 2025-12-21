using System;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Models.Modules;

namespace Writersword.Modules.Common
{
    /// <summary>
    /// Базовый класс для всех UI модулей
    /// Все модули из Src/Modules/ наследуются от него
    /// </summary>
    public abstract class BaseModule : IModule
    {
        /// <summary>Уникальный ID экземпляра модуля</summary>
        public string InstanceId { get; } = Guid.NewGuid().ToString();

        /// <summary>Тип модуля (из enum ModuleType)</summary>
        public abstract ModuleType ModuleType { get; }

        /// <summary>Заголовок модуля (отображается в UI)</summary>
        public virtual string Title { get; set; } = "Module";

        /// <summary>Можно ли перемещать модуль</summary>
        public virtual bool IsMovable { get; } = true;

        /// <summary>Можно ли закрыть модуль</summary>
        public virtual bool IsCloseable { get; } = true;

        /// <summary>Можно ли вынести модуль в отдельное окно</summary>
        public virtual bool CanDetach { get; } = true;

        /// <summary>ViewModel для привязки к View</summary>
        public abstract object? ViewModel { get; }

        /// <summary>Метаданные модуля для отображения в меню и UI</summary>
        public abstract IModuleMetadata Metadata { get; }

        /// <summary>Событие запроса закрытия модуля</summary>
        public event Action<IModule>? RequestClose;

        /// <summary>Событие запроса выноса модуля в отдельное окно</summary>
        public event Action<IModule>? RequestDetach;

        /// <summary>Инициализация модуля (вызывается при первом создании)</summary>
        public virtual void Initialize()
        {
            // Переопределите если нужна инициализация
        }

        /// <summary>Сохранить состояние модуля для записи в файл проекта</summary>
        public virtual ModuleState SaveState()
        {
            return new ModuleState
            {
                ScrollPosition = 0,
                CustomData = null
            };
        }

        /// <summary>Восстановить состояние модуля при загрузке проекта</summary>
        public virtual void RestoreState(ModuleState state)
        {
            // Переопределите для восстановления состояния
        }

        /// <summary>Освободить ресурсы модуля</summary>
        public virtual void Dispose()
        {
            // Переопределите для освобождения ресурсов
        }

        /// <summary>Инициировать закрытие модуля</summary>
        protected void OnRequestClose()
        {
            RequestClose?.Invoke(this);
        }

        /// <summary>Инициировать вынос модуля в отдельное окно</summary>
        protected void OnRequestDetach()
        {
            RequestDetach?.Invoke(this);
        }

        /// <summary>
        /// Создать View для этого модуля
        /// Каждый модуль переопределяет этот метод и возвращает свой View
        /// </summary>
        public abstract Avalonia.Controls.Control? CreateView();
    }
}