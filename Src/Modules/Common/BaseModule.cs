using Avalonia.Controls;
using System;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Models;
using Writersword.ViewModels;
using System.Collections.Generic;

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
        public string InstanceId { get; private set; }

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
        /// Конструктор базового модуля
        /// </summary>
        /// <param name="instanceId">ID экземпляра модуля (если null - генерируется новый)</param>
        protected BaseModule(string? instanceId = null)
        {
            InstanceId = instanceId ?? Guid.NewGuid().ToString();
        }

        /// <summary>
        /// Принудительно обновить состояние модуля из контекста
        /// Вызывает OnContextChanged заново
        /// Используется при выходе из CompareMode
        /// </summary>
        public void RefreshFromContext()
        {
            OnContextChanged(Context);
            Console.WriteLine($"[{ModuleId}] Context refreshed");
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
        }

        /// <summary>Инициализация модуля</summary>
        public virtual void Initialize() { }

        /// <summary>
        /// Получить основные данные модуля для сохранения
        /// Базовая реализация возвращает null (модуль пустой)
        /// Переопределите в наследниках для сохранения данных
        /// </summary>
        public virtual object? GetCustomData()
        {
            return null;
        }

        /// <summary>
        /// Получить сессионные данные модуля
        /// Базовая реализация возвращает null (нет сессионных данных)
        /// Переопределите в наследниках для сохранения позиции курсора, скролла и т.д.
        /// </summary>
        public virtual object? GetSessionData()
        {
            return null;
        }

        /// <summary>
        /// Установить основные данные модуля
        /// Базовая реализация ничего не делает
        /// Переопределите в наследниках для загрузки данных
        /// </summary>
        public virtual void SetCustomData(object? data)
        {
        }

        /// <summary>
        /// Установить сессионные данные модуля
        /// Базовая реализация ничего не делает
        /// Переопределите в наследниках для восстановления курсора, скролла и т.д.
        /// </summary>
        public virtual void SetSessionData(object? data)
        {
        }

        /// <summary>Очистка ресурсов</summary>
        public virtual void Dispose() { }

        /// <summary>Создать View для модуля</summary>
        public abstract Control? CreateView();

        /// <summary>
        /// Поддерживает ли модуль дельта-сравнение
        /// Базовая реализация: false (Simple режим - хешируем весь объект)
        /// Переопределите в наследниках для включения Delta режима
        /// </summary>
        public virtual bool SupportsDeltaComparison => false;

        /// <summary>
        /// Получить измененные части данных
        /// Базовая реализация: выбрасывает исключение (не поддерживается)
        /// Переопределите в наследниках если SupportsDeltaComparison = true
        /// </summary>
        public virtual Dictionary<string, object?>? GetChangedParts(object? current, object? saved)
        {
            throw new NotImplementedException(
                $"Module {ModuleId} does not support delta comparison. " +
                $"Override SupportsDeltaComparison and GetChangedParts to enable delta mode.");
        }
    }
}