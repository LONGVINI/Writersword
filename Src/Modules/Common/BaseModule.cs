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
    public abstract class BaseModule : IModule, IChangeTrackingModule
    {
        private DocumentContext? _context;
        private Control? _cachedView;

        // ── Отслеживание правок (IChangeTrackingModule) ───────────────────
        //
        // _historyState — номер состояния истории отмены (его сообщает сам модуль
        // из своих стеков Undo/Redo), _extraChanges — правки мимо истории,
        // _revision — любая правка вообще. Доступ из разных потоков: сбор данных
        // для кеша читает отметки с фонового потока, поэтому только Interlocked.
        private long _historyState;
        private long _extraChanges;
        private long _revision;

        // Состояние истории на момент прошлой проверки правки «неизвестного рода»
        // (см. NotifyContentChanged). Только UI-поток.
        private long _historyAtLastContentCheck;
        private bool _contentCheckPending;

        // Поколение отложенных проверок: AcceptLoadedState его сдвигает, и проверки,
        // поставленные до этого, ничего не учитывают. Только UI-поток.
        private long _contentCheckEpoch;

        // Контекст логгера берётся от фактического типа наследника, поэтому в
        // журнале виден конкретный модуль, а не BaseModule. Тип фиксирован на
        // время жизни объекта, так что достаточно вычислить его один раз.
        private Serilog.ILogger? _baseLogger;

        private Serilog.ILogger BaseLogger =>
            _baseLogger ??= Serilog.Log.ForContext(GetType());

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
        /// Модуль сообщает о своих правках. По умолчанию false: для такого модуля
        /// вкладка работает прежним путём — полным сбором данных и сравнением с файлом.
        /// Наследник, который вызывает NotifyHistoryChanged / NotifyDataChanged /
        /// NotifyContentChanged при каждой правке своих данных, возвращает true.
        /// Модуль без данных проекта тоже возвращает true: ему нечего менять.
        /// </summary>
        public virtual bool TracksChanges => false;

        /// <inheritdoc/>
        public ModuleChangeStamp ChangeStamp => new(
            System.Threading.Interlocked.Read(ref _historyState),
            System.Threading.Interlocked.Read(ref _extraChanges));

        /// <inheritdoc/>
        public long Revision => System.Threading.Interlocked.Read(ref _revision);

        /// <inheritdoc/>
        public event Action<IModule>? DataChanged;

        /// <summary>
        /// Начальное состояние истории — без события. Вызывается модулем один раз,
        /// когда он подключается к своим стекам Undo/Redo: данные при этом не
        /// менялись, и вкладка не должна считать модуль изменённым.
        /// </summary>
        protected void SetHistoryBaseline(long historyState)
        {
            System.Threading.Interlocked.Exchange(ref _historyState, historyState);
            _historyAtLastContentCheck = historyState;
        }

        /// <summary>
        /// История отмены сдвинулась: новая команда, Undo или Redo. historyState —
        /// номер нового состояния истории. Возврат в прежнюю позицию истории
        /// возвращает прежний номер, и правки считаются откаченными.
        /// </summary>
        protected void NotifyHistoryChanged(long historyState)
        {
            System.Threading.Interlocked.Exchange(ref _historyState, historyState);
            System.Threading.Interlocked.Increment(ref _revision);
            RaiseDataChanged();
        }

        /// <summary>
        /// Правка, которую нельзя отменить (прошла мимо истории отмены). После неё
        /// модуль остаётся изменённым до сохранения, даже если откатить всё остальное.
        /// </summary>
        protected void NotifyDataChanged()
        {
            System.Threading.Interlocked.Increment(ref _extraChanges);
            System.Threading.Interlocked.Increment(ref _revision);
            RaiseDataChanged();
        }

        /// <summary>
        /// Правка неизвестного рода: модуль видит, что данные изменились, но не знает,
        /// пришла ли правка через историю отмены. Проверка откладывается до конца
        /// текущей операции: если за это время история сдвинулась — правка уже учтена
        /// ею (команда кладётся в стек после применения), если нет — это правка мимо
        /// истории, и она учитывается как неотменяемая. Несколько событий подряд
        /// схлопываются в одну проверку.
        /// </summary>
        protected void NotifyContentChanged()
        {
            System.Threading.Interlocked.Increment(ref _revision);

            if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(ScheduleContentCheck);
                return;
            }

            ScheduleContentCheck();
        }

        /// <inheritdoc/>
        public void AcceptLoadedState()
        {
            _contentCheckEpoch++;
            _contentCheckPending = false;
            _historyAtLastContentCheck = System.Threading.Interlocked.Read(ref _historyState);
        }

        private void ScheduleContentCheck()
        {
            if (_contentCheckPending)
                return;

            _contentCheckPending = true;
            long epoch = _contentCheckEpoch;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // Проверка поставлена до того, как модуль принял загруженное состояние:
                // событие пришло от самой загрузки, а не от правки.
                if (epoch != _contentCheckEpoch)
                    return;

                _contentCheckPending = false;

                long history = System.Threading.Interlocked.Read(ref _historyState);
                if (history == _historyAtLastContentCheck)
                    System.Threading.Interlocked.Increment(ref _extraChanges);

                _historyAtLastContentCheck = history;
                RaiseDataChanged();
            }, Avalonia.Threading.DispatcherPriority.Background);
        }

        private void RaiseDataChanged()
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                DataChanged?.Invoke(this);
                return;
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() => DataChanged?.Invoke(this));
        }

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
            BaseLogger.Debug("Context refreshed: {ModuleType}", moduleType);
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
            if (this is IHotKeyProvider disposedProvider)
            {
                var hotKeyService = CoreServices.GetService<IHotKeyService>();
                // Привязка снимается только если она принадлежит этому экземпляру:
                // у другой вкладки того же типа модуля executor должен уцелеть.
                hotKeyService?.UnbindExecutor(moduleType, disposedProvider);
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
        /// Уже созданная View без побочных эффектов (см. IModule.CachedView).
        /// </summary>
        public Control? CachedView => _cachedView;

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