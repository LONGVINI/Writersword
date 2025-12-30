using System;
using System.Threading.Tasks;
using Writersword.ViewModels;

namespace Writersword.Services.Interfaces
{
    /// <summary>
    /// Сервис управления жизненным циклом проектов
    /// Координирует открытие, сохранение, закрытие документов
    /// </summary>
    public interface IProjectWorkflow
    {
        /// <summary>Открыть документ (новый или существующий)</summary>
        /// <param name="filePath">Путь к файлу или null для нового проекта</param>
        Task<DocumentTabViewModel?> OpenDocumentAsync(string? filePath = null);

        /// <summary>Сохранить документ</summary>
        Task<bool> SaveDocumentAsync(DocumentTabViewModel tab);

        /// <summary>Сохранить как (выбрать новый путь)</summary>
        Task<bool> SaveAsDocumentAsync(DocumentTabViewModel tab);

        /// <summary>Закрыть документ с проверкой изменений</summary>
        /// <param name="force">Закрыть без сохранения</param>
        Task<bool> CloseDocumentAsync(DocumentTabViewModel tab, bool force = false);

        /// <summary>Проверить есть ли несохранённые изменения</summary>
        bool HasUnsavedChanges(DocumentTabViewModel tab);

        /// <summary>Событие открытия проекта</summary>
        event Action<DocumentTabViewModel>? ProjectOpened;

        /// <summary>Событие сохранения проекта</summary>
        event Action<DocumentTabViewModel>? ProjectSaved;

        /// <summary>Событие закрытия проекта</summary>
        event Action<DocumentTabViewModel>? ProjectClosed;
    }
}