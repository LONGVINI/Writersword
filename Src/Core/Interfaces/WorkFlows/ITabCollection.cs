using System;
using System.Collections.Generic;

namespace Writersword.Core.Interfaces.WorkFlows
{
    /// <summary>
    /// Сервис управления коллекцией вкладок документов.
    /// Отвечает только за хранение и базовые операции со списком вкладок.
    /// </summary>
    public interface ITabCollection
    {
        /// <summary>Список всех открытых вкладок.</summary>
        IEnumerable<IDocumentTab> Tabs { get; }

        /// <summary>Активная вкладка (текущая).</summary>
        IDocumentTab? ActiveTab { get; set; }

        /// <summary>
        /// Откатить активную вкладку без стрельбы ActiveTabChanged.
        /// Используется при отмене загрузки (Cancel в Recovery dialog).
        /// </summary>
        void SilentRevertActiveTab(IDocumentTab tab);

        /// <summary>Добавить вкладку в коллекцию.</summary>
        void Add(IDocumentTab tab);

        /// <summary>Удалить вкладку из коллекции.</summary>
        void Remove(IDocumentTab tab);

        /// <summary>Найти вкладку по пути к файлу проекта.</summary>
        IDocumentTab? FindByPath(string filePath);

        /// <summary>Очистить все вкладки.</summary>
        void Clear();

        /// <summary>
        /// Событие изменения активной вкладки.
        /// Передаёт (newTab, previousTab).
        /// </summary>
        event Action<IDocumentTab?, IDocumentTab?>? ActiveTabChanged;
    }
}