using Avalonia.Controls;
using System;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Models;
using Writersword.Core.Models.Modules;
using Writersword.ViewModels;

namespace Writersword.Modules.Common
{
    /// <summary>
    /// Базовый класс для всех модулей
    /// Реализует общую функциональность IModule
    /// </summary>
    public abstract class BaseModule : IModule
    {
        private DocumentContext? _context;

        /// <summary>Уникальный ID экземпляра модуля</summary>
        public string InstanceId { get; } = Guid.NewGuid().ToString();

        /// <summary>
        /// Идентификатор типа модуля (строка)
        /// Должен быть уникальным для каждого типа модуля
        /// </summary>
        public abstract string ModuleId { get; }

        /// <summary>Заголовок модуля</summary>
        public virtual string Title { get; set; } = "Module";

        /// <summary>ViewModel модуля</summary>
        public abstract object? ViewModel { get; }

        /// <summary>Метаданные модуля</summary>
        public abstract IModuleMetadata Metadata { get; }

        /// <summary>
        /// Контекст документа
        /// При изменении автоматически вызывается OnContextChanged()
        /// </summary>
        public DocumentContext? Context
        {
            get => _context;
            set
            {
                if (_context != value)
                {
                    _context = value;
                    OnContextChanged(value);
                }
            }
        }

        /// <summary>
        /// Событие запроса на закрытие модуля
        /// Вызывается когда модуль хочет закрыться
        /// </summary>
        public event Action<IModule>? RequestClose;

        /// <summary>
        /// Событие запроса на открепление модуля в отдельное окно
        /// Вызывается когда модуль хочет открепиться
        /// </summary>
        public event Action<IModule>? RequestDetach;

        /// <summary>
        /// Вызвать событие RequestClose
        /// Используйте в наследниках для запроса закрытия модуля
        /// </summary>
        protected void RaiseRequestClose()
        {
            RequestClose?.Invoke(this);
        }

        /// <summary>
        /// Вызвать событие RequestDetach
        /// Используйте в наследниках для запроса открепления модуля
        /// </summary>
        protected void RaiseRequestDetach()
        {
            RequestDetach?.Invoke(this);
        }

        /// <summary>
        /// Вызывается при изменении контекста
        /// Переопределите в наследниках для реакции на смену контекста/проекта
        /// </summary>
        /// <param name="context">Новый контекст или null</param>
        protected virtual void OnContextChanged(DocumentContext? context)
        {
            // Базовая реализация - ничего не делает
            // Наследники могут переопределить для своей логики
        }

        /// <summary>Инициализация модуля</summary>
        public virtual void Initialize() { }

        /// <summary>Сохранить состояние модуля</summary>
        public virtual ModuleState SaveState()
        {
            return new ModuleState();
        }

        /// <summary>Восстановить состояние модуля</summary>
        public virtual void RestoreState(ModuleState state)
        {
            // Базовая реализация - ничего не делает
        }

        /// <summary>Очистка ресурсов</summary>
        public virtual void Dispose() { }

        /// <summary>Создать View для модуля</summary>
        public abstract Control? CreateView();
    }
}