using Microsoft.Extensions.DependencyInjection;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Writersword.Core.Enums;
using Writersword.Core.Models.Project;
using Writersword.Services.Interfaces;
using Writersword.ViewModels;
using Writersword.Views;

namespace Writersword.Services
{
    /// <summary>
    /// Реализация сервиса управления жизненным циклом проектов
    /// </summary>
    public class ProjectWorkflow : IProjectWorkflow
    {
        private readonly IProjectService _projectService;
        private readonly ICacheService _cacheService;
        private readonly IAutoSaveService _autoSaveService;
        private readonly IDialogService _dialogService;
        private readonly ISettingsService _settingsService;

        public event Action<DocumentTabViewModel>? ProjectOpened;
        public event Action<DocumentTabViewModel>? ProjectSaved;
        public event Action<DocumentTabViewModel>? ProjectClosed;

        public ProjectWorkflow(
            IProjectService projectService,
            ICacheService cacheService,
            IAutoSaveService autoSaveService,
            IDialogService dialogService,
            ISettingsService settingsService)
        {
            _projectService = projectService;
            _cacheService = cacheService;
            _autoSaveService = autoSaveService;
            _dialogService = dialogService;
            _settingsService = settingsService;
        }

        /// <summary>Открыть документ</summary>
        public async Task<DocumentTabViewModel?> OpenDocumentAsync(string? filePath = null)
        {
            try
            {
                // 1. Если путь не указан - показываем диалог выбора файла
                if (string.IsNullOrEmpty(filePath))
                {
                    filePath = await _dialogService.OpenFileAsync();
                    if (string.IsNullOrEmpty(filePath))
                    {
                        Console.WriteLine("[ProjectWorkflow] Open cancelled by user");
                        return null;
                    }
                }

                Console.WriteLine($"[ProjectWorkflow] Opening project: {filePath}");

                // 2. Проверяем есть ли кеш
                ProjectFile? project = null;
                if (_cacheService.HasCache(filePath))
                {
                    var cacheDate = _cacheService.GetCacheDate(filePath);
                    var saveDate = _cacheService.GetSaveDate(filePath);

                    Console.WriteLine($"[ProjectWorkflow] Cache found - Cache: {cacheDate}, Save: {saveDate}");

                    // Показываем диалог восстановления
                    var result = await _dialogService.ShowMessageAsync(
                        "Найдено автосохранение",
                        $"Обнаружена несохранённая версия проекта.\n\nАвтосохранение: {cacheDate:HH:mm:ss}\nПоследнее сохранение: {saveDate:HH:mm:ss}\n\nВосстановить из автосохранения?",
                        MessageBoxType.Question,
                        MessageBoxButtons.YesNo
                    );

                    if (result == MessageBoxResult.Yes)
                    {
                        project = await _cacheService.LoadFromCacheAsync(filePath);
                        Console.WriteLine("[ProjectWorkflow] Loaded from cache");
                    }
                }

                // 3. Если не из кеша - загружаем из файла
                if (project == null)
                {
                    project = await _projectService.LoadAsync(filePath);
                    if (project == null)
                    {
                        await _dialogService.ShowMessageAsync(
                            "Ошибка",
                            "Не удалось загрузить проект",
                            MessageBoxType.Error,
                            MessageBoxButtons.OK
                        );
                        return null;
                    }
                }

                // 4. Создаём вкладку из первого документа проекта
                var mainViewModel = App.Services.GetRequiredService<MainWindowViewModel>();
                var tabVM = new DocumentTabViewModel(project, filePath, mainViewModel.CloseTabAsync);

                // 5. Запускаем автосохранение
                _autoSaveService.Start(filePath, () => tabVM.Content);

                // 6. Добавляем в недавние проекты
                _settingsService.AddRecentProject(filePath);

                Console.WriteLine($"[ProjectWorkflow] Project opened: {project.Title}");
                ProjectOpened?.Invoke(tabVM);

                return tabVM;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProjectWorkflow] ERROR opening project: {ex.Message}");
                await _dialogService.ShowMessageAsync(
                    "Ошибка",
                    $"Не удалось открыть проект: {ex.Message}",
                    MessageBoxType.Error,
                    MessageBoxButtons.OK
                );
                return null;
            }
        }

        /// <summary>Сохранить документ</summary>
        public async Task<bool> SaveDocumentAsync(DocumentTabViewModel tab)
        {
            try
            {
                var project = tab.GetProject();
                var filePath = tab.FilePath;

                // Если проект новый (нет пути) - вызываем SaveAs
                if (string.IsNullOrEmpty(filePath))
                {
                    return await SaveAsDocumentAsync(tab);
                }

                Console.WriteLine($"[ProjectWorkflow] Saving project: {filePath}");

                // Обновляем дату модификации
                project.LastModified = DateTime.Now;

                // Сохраняем через ProjectService
                bool success = await _projectService.SaveAsync(project, filePath);

                if (success)
                {
                    _cacheService.DeleteCache(filePath);
                    Console.WriteLine("[ProjectWorkflow] Project saved successfully");
                    ProjectSaved?.Invoke(tab);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProjectWorkflow] ERROR saving project: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> SaveAsDocumentAsync(DocumentTabViewModel tab)
        {
            try
            {
                // Показываем диалог выбора места сохранения
                var filePath = await _dialogService.SaveFileAsync();
                if (string.IsNullOrEmpty(filePath))
                {
                    Console.WriteLine("[ProjectWorkflow] SaveAs cancelled by user");
                    return false;
                }

                Console.WriteLine($"[ProjectWorkflow] SaveAs: {filePath}");

                // Обновляем путь и заголовок
                tab.FilePath = filePath;
                tab.Title = Path.GetFileNameWithoutExtension(filePath);

                // Получаем проект и обновляем дату
                var project = tab.GetProject();
                project.LastModified = DateTime.Now;

                // Сохраняем
                bool success = await _projectService.SaveAsync(project, filePath);

                if (success)
                {
                    // Запускаем автосохранение для нового пути
                    _autoSaveService.Stop();
                    _autoSaveService.Start(filePath, () => tab.Content);

                    // Добавляем в недавние
                    _settingsService.AddRecentProject(filePath);

                    Console.WriteLine("[ProjectWorkflow] SaveAs successful");
                    ProjectSaved?.Invoke(tab);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProjectWorkflow] ERROR SaveAs: {ex.Message}");
                await _dialogService.ShowMessageAsync(
                    "Ошибка",
                    $"Не удалось сохранить проект: {ex.Message}",
                    MessageBoxType.Error,
                    MessageBoxButtons.OK
                );
                return false;
            }
        }

        /// <summary>Закрыть документ</summary>
        public async Task<bool> CloseDocumentAsync(DocumentTabViewModel tab, bool force = false)
        {
            try
            {
                Console.WriteLine($"[ProjectWorkflow] Closing tab: {tab.Title}, force: {force}");

                // Проверяем несохранённые изменения
                if (!force && HasUnsavedChanges(tab))
                {
                    var result = await _dialogService.ShowMessageAsync(
                        "Несохранённые изменения",
                        $"Документ \"{tab.Title}\" содержит несохранённые изменения.\n\nСохранить перед закрытием?",
                        MessageBoxType.Question,
                        MessageBoxButtons.YesNoCancel
                    );

                    if (result == MessageBoxResult.Cancel)
                    {
                        Console.WriteLine("[ProjectWorkflow] Close cancelled by user");
                        return false;
                    }

                    if (result == MessageBoxResult.Yes)
                    {
                        bool saved = await SaveDocumentAsync(tab);
                        if (!saved)
                            return false;
                    }
                }

                // Останавливаем автосохранение
                _autoSaveService.Stop();

                var filePath = tab.FilePath;

                // Закрываем проект в ProjectService
                if (!string.IsNullOrEmpty(filePath))
                {
                    var project = _projectService.GetProjectByPath(filePath);
                    if (project != null)
                    {
                        _projectService.CloseProject(project);
                    }
                }

                Console.WriteLine("[ProjectWorkflow] Tab closed successfully");
                ProjectClosed?.Invoke(tab);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProjectWorkflow] ERROR closing tab: {ex.Message}");
                return false;
            }
        }

        /// <summary>Проверить есть ли несохранённые изменения</summary>
        public bool HasUnsavedChanges(DocumentTabViewModel tab)
        {
            var filePath = tab.FilePath;

            // Если проект новый (нет пути) и есть контент - есть изменения
            if (string.IsNullOrEmpty(filePath))
            {
                return !string.IsNullOrEmpty(tab.Content);
            }

            // Проверяем есть ли кеш
            return _cacheService.HasCache(filePath);
        }
    }
}