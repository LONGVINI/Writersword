using System;
using System.Threading.Tasks;
using Writersword.Core.Interfaces.Services;

namespace Writersword.Core.Interfaces.WorkFlows
{
    /// <summary>
    /// Сервис управления жизненным циклом проектов
    /// Координирует открытие, сохранение, закрытие документов
    /// </summary>
    public interface IProjectWorkflow
    {
        /// <summary>
        /// Открыть документ
        /// </summary>
        /// <param name="filePath">Путь к файлу (null для диалога)</param>
        /// <param name="initializeWorkspace">Инициализировать workspace сразу (false для lazy loading)</param>
        Task<IDocumentTab?> OpenDocumentAsync(string? filePath = null, bool initializeWorkspace = true);

        /// <summary>Сохранить документ</summary>
        Task<bool> SaveDocumentAsync(IDocumentTab tab);

        /// <summary>Сохранить как (выбрать новый путь)</summary>
        Task<bool> SaveAsDocumentAsync(IDocumentTab tab);

        /// <summary>Закрыть документ с проверкой изменений</summary>
        /// <param name="force">Закрыть без сохранения</param>
        Task<bool> CloseDocumentAsync(IDocumentTab tab, bool force = false);

        /// <summary>
        /// Проверить есть ли несохранённые изменения
        /// Сравнивает текущие данные с сохранённым файлом
        /// </summary>
        Task<bool> HasUnsavedChanges(IDocumentTab tab);

        /// <summary>Событие открытия проекта</summary>
        event Action<IDocumentTab>? ProjectOpened;

        /// <summary>Событие сохранения проекта</summary>
        event Action<IDocumentTab>? ProjectSaved;

        /// <summary>Событие закрытия проекта</summary>
        event Action<IDocumentTab>? ProjectClosed;

        /// <summary>Получить WorkspaceAutoSaveService для проекта</summary>
        IWorkspaceAutoSaveService? GetAutoSaveServiceForProject(string filePath);

        /// <summary>Получить FileStorage для проекта</summary>
        IProjectFileStorage? GetFileStorageForProject(string filePath);

        /// <summary>Зарегистрировать FileStorage для проекта</summary>
        void RegisterStorage(string filePath, IDocumentTab tab);

        /// <summary>
        /// Инициализировать workspace для ленивой вкладки
        /// Вызывается при первом переключении на вкладку
        /// </summary>
        Task<bool> EnsureWorkspaceInitialized(IDocumentTab tab);
        /// <summary>Обновить FileStorage для проекта</summary>
        void UpdateStorageForProject(string filePath, IProjectFileStorage newStorage);
    }
}