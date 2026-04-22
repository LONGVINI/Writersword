using System;
using System.Collections.ObjectModel;
using Writersword.ViewModels;

namespace Writersword.Core.Interfaces.WorkFlows
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

        /// <summary>
        /// Откатить активную вкладку без стрельбы ActiveTabChanged.
        /// Используется при отмене загрузки (Cancel в Recovery dialog).
        /// </summary>
        void SilentRevertActiveTab(DocumentTabViewModel tab);

        /// <summary>Добавить вкладку в коллекцию</summary>
        void Add(DocumentTabViewModel tab);

        /// <summary>Удалить вкладку из коллекции</summary>
        void Remove(DocumentTabViewModel tab);

        /// <summary>Найти вкладку по пути к файлу проекта</summary>
        DocumentTabViewModel? FindByPath(string filePath);

        /// <summary>Очистить все вкладки</summary>
        void Clear();

        /// <summary>
        /// Событие изменения активной вкладки
        /// Передаёт (newTab, previousTab)
        /// </summary>
        event Action<DocumentTabViewModel?, DocumentTabViewModel?>? ActiveTabChanged;
    }
}