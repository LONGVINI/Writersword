using System;
using System.Threading.Tasks;
using Writersword.Core.Interfaces.Services;
using Writersword.Src.Core.Interfaces.Services;
using Writersword.ViewModels;

namespace Writersword.Src.Core.Interfaces.WorkFlows
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

        /// <summary>
        /// Проверить есть ли несохранённые изменения
        /// Сравнивает текущие данные с сохранённым файлом
        /// </summary>
        Task<bool> HasUnsavedChanges(DocumentTabViewModel tab);

        /// <summary>Событие открытия проекта</summary>
        event Action<DocumentTabViewModel>? ProjectOpened;

        /// <summary>Событие сохранения проекта</summary>
        event Action<DocumentTabViewModel>? ProjectSaved;

        /// <summary>Событие закрытия проекта</summary>
        event Action<DocumentTabViewModel>? ProjectClosed;

        /// <summary>Получить WorkspaceAutoSaveService для проекта</summary>
        IWorkspaceAutoSaveService? GetAutoSaveServiceForProject(string filePath);

        /// <summary>Получить FileStorage для проекта</summary>
        IProjectFileStorage? GetFileStorageForProject(string filePath);

        /// <summary>Зарегистрировать FileStorage для проекта</summary>
        void RegisterStorage(string filePath, DocumentTabViewModel tab);
        /// <summary>Обновить FileStorage для проекта</summary>
        void UpdateStorageForProject(string filePath, IProjectFileStorage newStorage);
    }
}