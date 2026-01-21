using Avalonia.Controls;
using System;
using Writersword.Core.Models;
using Writersword.Core.Models.Modules;
using Writersword.ViewModels;

namespace Writersword.Core.Interfaces.Modules
{
    /// <summary>
    /// Базовый интерфейс модуля
    /// Определяет жизненный цикл и основные свойства модуля
    /// </summary>
    public interface IModule : IDisposable
    {
        /// <summary>Уникальный ID экземпляра модуля (GUID)</summary>
        string InstanceId { get; }

        /// <summary>
        /// Идентификатор типа модуля (строка)
        /// Примеры: "TextEditor", "Timer", "Synonyms"
        /// </summary>
        string ModuleId { get; }

        /// <summary>Заголовок модуля (отображается в UI)</summary>
        string Title { get; set; }

        /// <summary>ViewModel модуля для привязки к View</summary>
        object? ViewModel { get; }

        /// <summary>Метаданные модуля (название, иконка, описание)</summary>
        IModuleMetadata Metadata { get; }

        /// <summary>Контекст документа (проект, настройки)</summary>
        DocumentContext? Context { get; set; }

        /// <summary>
        /// Событие запроса на закрытие модуля
        /// Вызывается когда модуль хочет закрыться (например, по кнопке Close)
        /// </summary>
        event Action<IModule>? RequestClose;

        /// <summary>
        /// Событие запроса на открепление модуля в отдельное окно
        /// Вызывается когда модуль хочет открепиться от главного окна
        /// </summary>
        event Action<IModule>? RequestDetach;

        /// <summary>Инициализация модуля</summary>
        void Initialize();

        /// <summary>
        /// Сохранить состояние модуля
        /// Возвращает CustomData и SessionData для записи
        /// </summary>
        ModuleState SaveState();

        /// <summary>
        /// Восстановить состояние модуля
        /// Вызывается при открытии проекта или переключении WorkMode
        /// </summary>
        void RestoreState(ModuleState state);

        /// <summary>Создать View для отображения модуля</summary>
        Control? CreateView();

        /// <summary>
        /// Принудительно обновить состояние модуля из контекста
        /// Используется при выходе из CompareMode
        /// </summary>
        void RefreshFromContext();
    }
}