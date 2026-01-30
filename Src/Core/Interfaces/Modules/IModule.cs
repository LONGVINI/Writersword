using Avalonia.Controls;
using System;
using Writersword.Core.Models;
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
        /// Получить основные данные модуля для сохранения в .writersword файл
        /// Возвращает данные в формате, который модуль определяет сам
        /// Может быть: строка, объект, словарь, массив, null (если модуль пустой)
        /// </summary>
        /// <returns>Данные модуля или null если нечего сохранять</returns>
        object? GetCustomData();

        /// <summary>
        /// Получить рабочие данные сессии для сохранения в .wsasd кеш
        /// Примеры: позиция курсора, скролл, текущее время таймера
        /// Может быть: строка, объект, словарь, null (если нет сессионных данных)
        /// </summary>
        /// <returns>Сессионные данные или null</returns>
        object? GetSessionData();

        /// <summary>
        /// Установить основные данные модуля из .writersword файла
        /// Вызывается при открытии проекта или переключении версий
        /// </summary>
        /// <param name="data">Данные модуля (может быть null)</param>
        void SetCustomData(object? data);

        /// <summary>
        /// Установить рабочие данные сессии из .wsasd кеша
        /// Вызывается при восстановлении из кеша
        /// </summary>
        /// <param name="data">Сессионные данные (может быть null)</param>
        void SetSessionData(object? data);

        /// <summary>Создать View для отображения модуля</summary>
        Control? CreateView();

        /// <summary>
        /// Принудительно обновить состояние модуля из контекста
        /// Используется при выходе из CompareMode
        /// </summary>
        void RefreshFromContext();
    }
}