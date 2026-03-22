using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Models;
using Writersword.Core.Interfaces.Services.Input;
using Writersword.Core.Services;
using Writersword.ViewModels;

namespace Writersword.Modules.Common
{
    /// <summary>
    /// Базовый класс для всех модулей.
    /// Реализует общую функциональность IModule.
    /// Если модуль реализует IHotKeyProvider — executor привязывается
    /// при Initialize() и отвязывается при Dispose().
    /// Определения клавиш регистрируются отдельно при старте приложения
    /// через ModuleFactory и IHotKeyDescriptor в метаданных.
    /// </summary>
    public abstract class BaseModule : IModule
    {
        private DocumentContext? _context;

        /// <summary>
        /// Идентификатор типа модуля (строка).
        /// Должен быть уникальным для каждого типа модуля.
        /// </summary>
        public abstract string moduleType { get; }

        /// <summary>Заголовок модуля</summary>
        public virtual string Title { get; set; } = "Module";

        /// <summary>ViewModel модуля</summary>
        public abstract object? ViewModel { get; }

        /// <summary>Метаданные модуля</summary>
        public abstract IModuleMetadata Metadata { get; }

        /// <summary>
        /// Контекст документа.
        /// При изменении автоматически вызывается OnContextChanged().
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

        protected BaseModule()
        {
        }

        /// <summary>
        /// Принудительно обновить состояние модуля из контекста.
        /// Вызывает OnContextChanged заново.
        /// Используется при выходе из CompareMode.
        /// </summary>
        public void RefreshFromContext()
        {
            OnContextChanged(Context);
            Console.WriteLine($"[{moduleType}] Context refreshed");
        }

        /// <summary>
        /// Событие запроса на закрытие модуля
        /// </summary>
        public event Action<IModule>? RequestClose;

        /// <summary>
        /// Событие запроса на открепление модуля в отдельное окно
        /// </summary>
        public event Action<IModule>? RequestDetach;

        protected void RaiseRequestClose()
        {
            RequestClose?.Invoke(this);
        }

        protected void RaiseRequestDetach()
        {
            RequestDetach?.Invoke(this);
        }

        /// <summary>
        /// Вызывается при изменении контекста.
        /// Переопределите в наследниках для реакции на смену контекста/проекта.
        /// </summary>
        protected virtual void OnContextChanged(DocumentContext? context)
        {
        }

        /// <summary>
        /// Инициализация модуля.
        /// Если модуль реализует IHotKeyProvider — привязывает executor в HotKeyService.
        /// Определения клавиш к этому моменту уже зарегистрированы через RegisterFromDescriptor.
        /// </summary>
        public virtual void Initialize()
        {
            if (this is IHotKeyProvider provider)
            {
                var hotKeyService = App.Services.GetService<IHotKeyService>();
                if (hotKeyService != null)
                {
                    hotKeyService.BindExecutor(moduleType, provider);
                }
            }
        }

        /// <summary>
        /// Получить основные данные модуля для сохранения
        /// </summary>
        public virtual object? GetCustomData() => null;

        /// <summary>
        /// Получить сессионные данные модуля
        /// </summary>
        public virtual object? GetSessionData() => null;

        /// <summary>
        /// Установить основные данные модуля
        /// </summary>
        public virtual void SetCustomData(object? data)
        {
        }

        /// <summary>
        /// Установить сессионные данные модуля
        /// </summary>
        public virtual void SetSessionData(object? data)
        {
        }

        /// <summary>
        /// Очистка ресурсов.
        /// Если модуль реализует IHotKeyProvider — отвязывает executor,
        /// но определения клавиш остаются в HotKeyService.
        /// </summary>
        public virtual void Dispose()
        {
            if (this is IHotKeyProvider)
            {
                var hotKeyService = App.Services.GetService<IHotKeyService>();
                hotKeyService?.UnbindExecutor(moduleType);
            }
        }

        /// <summary>Создать View для модуля</summary>
        public abstract Control? CreateView();

        /// <summary>
        /// Поддерживает ли модуль дельта-сравнение
        /// </summary>
        public virtual bool SupportsDeltaComparison => false;

        /// <summary>
        /// Получить измененные части данных
        /// </summary>
        public virtual Dictionary<string, object?>? GetChangedParts(object? current, object? saved)
        {
            throw new NotImplementedException(
                $"Module {moduleType} does not support delta comparison. " +
                $"Override SupportsDeltaComparison and GetChangedParts to enable delta mode.");
        }
    }
}