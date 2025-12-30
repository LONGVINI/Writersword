using System;
using System.Collections.ObjectModel;
using Writersword.ViewModels;

namespace Writersword.Services.Interfaces
{
    /// <summary>
    /// Сервис управления коллекцией вкладок документов
    /// Отвечает только за хранение и базовые операции со списком вкладок
    /// </summary>
    public interface ITabCollection
    {
        /// <summary>Список всех открытых вкладок</summary>
        ObservableCollection<DocumentTabViewModel> Tabs { get; }

        /// <summary>Активная вкладка (текущая)</summary>
        DocumentTabViewModel? ActiveTab { get; set; }

        /// <summary>Добавить вкладку в коллекцию</summary>
        void Add(DocumentTabViewModel tab);

        /// <summary>Удалить вкладку из коллекции</summary>
        void Remove(DocumentTabViewModel tab);

        /// <summary>Найти вкладку по пути к файлу проекта</summary>
        DocumentTabViewModel? FindByPath(string filePath);

        /// <summary>Очистить все вкладки</summary>
        void Clear();

        /// <summary>Событие изменения активной вкладки</summary>
        event Action<DocumentTabViewModel?>? ActiveTabChanged;
    }
}