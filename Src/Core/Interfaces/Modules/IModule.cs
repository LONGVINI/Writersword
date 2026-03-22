using Avalonia.Controls;
using System;
using System.Collections.Generic;
using Writersword.Core.Models;
using Writersword.Core.Services;
using Writersword.ViewModels;

namespace Writersword.Core.Interfaces.Modules
{
    /// <summary>
    /// Базовый интерфейс модуля
    /// Определяет жизненный цикл и основные свойства модуля
    /// </summary>
    public interface IModule : IDisposable
    {
        /// <summary>
        /// Идентификатор типа модуля (строка)
        /// Примеры: "TextEditor", "Timer", "Synonyms"
        /// </summary>
        string moduleType { get; }

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

        /// <summary>
        /// Поддерживает ли модуль дельта-сравнение (разбивка на части с хешами)
        /// false (по умолчанию) - сравнивать хеш всего объекта целиком (Simple режим)
        /// true - данные содержат маркер "__deltaMode" и структуру с хешами частей (Delta режим)
        /// 
        /// Simple режим (Timer, Notes, Synonyms):
        ///   GetCustomData() возвращает обычные данные (строка, объект)
        ///   DataComparisonService хеширует весь объект целиком для быстрого сравнения
        /// 
        /// Delta режим (TextEditor):
        ///   GetCustomData() возвращает структуру с "__deltaMode": true и хешами частей
        ///   DataComparisonService сравнивает хеши частей, а не хеширует весь объект
        ///   Позволяет найти только измененные части и сохранить только их
        /// </summary>
        bool SupportsDeltaComparison { get; }

        /// <summary>
        /// Получить измененные части данных (только для SupportsDeltaComparison = true)
        /// Сравнивает хеши частей между current и saved
        /// Возвращает только те части, которые изменились
        /// Используется для оптимизации сохранения больших документов
        /// 
        /// Пример для TextEditor:
        ///   current = { "__deltaMode": true, "paragraph_0": {..., hash: "aaa"}, "paragraph_1": {..., hash: "bbb"} }
        ///   saved   = { "__deltaMode": true, "paragraph_0": {..., hash: "aaa"}, "paragraph_1": {..., hash: "ccc"} }
        ///   return  = { "paragraph_1": {..., hash: "bbb"} }  - только измененный абзац
        /// </summary>
        /// <param name="current">Текущие данные из GetCustomData()</param>
        /// <param name="saved">Сохраненные данные из файла</param>
        /// <returns>Словарь измененных частей или null если изменений нет</returns>
        Dictionary<string, object?>? GetChangedParts(object? current, object? saved);
    }
}