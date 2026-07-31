using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Models;
using Writersword.Core.Interfaces.Services.Input;
using Writersword.Core.Services;
using System.IO;

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
        private Control? _cachedView;

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
                var hotKeyService = CoreServices.GetService<IHotKeyService>();
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
                var hotKeyService = CoreServices.GetService<IHotKeyService>();
                hotKeyService?.UnbindExecutor(moduleType);
            }
            _cachedView = null;
        }


        /// <summary>
        /// Возвращает директорию где лежит DLL этого конкретного модуля.
        /// Используется для нахождения ресурсов рядом с модулем (аватарки, иконки и т.д.)
        /// GetType() намеренно — чтобы получить тип наследника, а не BaseModule.
        /// </summary>
        protected string GetModuleDirectory()
        {
            var location = GetType().Assembly.Location;
            return Path.GetDirectoryName(location)
                ?? AppContext.BaseDirectory;
        }


        /// <summary>
        /// Возвращает View модуля с кешированием.
        /// При повторном вызове возвращает существующий инстанс, предварительно
        /// отсоединяя его от устаревшего VisualParent (Dock 12 не обновляет
        /// VisualParent при перемещении между ContentPresenter-ами, поэтому
        /// новый ContentPresenter не может принять контрол без явного detach).
        /// </summary>
        public Control? GetOrCreateView()
        {
            if (_cachedView == null)
            {
                _cachedView = CreateView();
                return _cachedView;
            }

            // Отсоединяем от устаревшего родителя перед передачей новому.
            // ContentPresenter — обычный случай в доке; ContentControl, Decorator
            // и Panel встречаются во флоат-окнах и обёртках. Без отцепления новый
            // хост не может принять контрол («already has visual parent»).
            DetachFrom(_cachedView.GetVisualParent(), _cachedView);

            // Логический родитель отцепляется отдельно: он не обязан совпадать
            // с визуальным. При вытаскивании модуля в плавающее окно Dock создаёт
            // новый DeferredContentControl, отдаёт ему ту же вью, и если старая
            // логическая связь осталась, назначение нового родителя падает с
            // "AttachedToLogicalTreeCore called for 'Panel' but control has no
            // logical parent" прямо в проходе разметки.
            DetachFrom(_cachedView.Parent, _cachedView);

            // Последняя мера: связь могла остаться на хосте, который сюда не
            // подходит ни одним типом. Явный сброс родителя оставляет вью
            // свободной для нового дерева.
            if (_cachedView.Parent is not null)
                ((ISetLogicalParent)_cachedView).SetParent(null);

            // Восстанавливаем DataContext: пути закрытия/пересоздания в DockFactory
            // обнуляют его у старого Content, а вью у нас кэшированная — без
            // восстановления она возвращается «пустой» (привязки мертвы).
            if (_cachedView.DataContext is null && ViewModel is not null)
                _cachedView.DataContext = ViewModel;

            return _cachedView;
        }

        /// <summary>
        /// Убрать вью из указанного хоста, если она действительно им держится.
        /// Проверка на совпадение обязательна: у логического и визуального
        /// родителя хост может быть общим, и второй вызов иначе обнулял бы
        /// содержимое, уже отданное новому владельцу.
        /// </summary>
        private static void DetachFrom(object? host, Control view)
        {
            switch (host)
            {
                case ContentPresenter cp when ReferenceEquals(cp.Content, view):
                    cp.Content = null;
                    break;
                case ContentControl cc when ReferenceEquals(cc.Content, view):
                    cc.Content = null;
                    break;
                case Decorator d when ReferenceEquals(d.Child, view):
                    d.Child = null;
                    break;
                case Panel p:
                    p.Children.Remove(view);
                    break;
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