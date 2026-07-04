using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables.Fluent;
using System.Threading.Tasks;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Interfaces.WorkFlows;
using Writersword.Core.Interfaces.Workspace;
using Writersword.Core.Models.Project;
using Writersword.Core.Services;
using Writersword.Infrastructure.Dock;
using Writersword.Infrastructure.Workspace;
using Writersword.Modules.Common;

namespace Writersword.ViewModels
{
    /// <summary>
    /// ViewModel для одной вкладки документа
    /// Каждая вкладка имеет свой изолированный WorkspaceController и ProjectModuleContext
    /// </summary>
    public class DocumentTabViewModel : ViewModelBase, IDocumentTab
    {
        private readonly ILogger<DocumentTabViewModel> _logger;
        private readonly ProjectFile _project;
        private readonly Func<DocumentTabViewModel, Task>? _onClose;
        private bool _isActive;
        private string? _filePath = "";
        private RecoveryBannerViewModel? _recoveryBanner;

        /// <summary>Загружен ли проект полностью (workspace инициализирован)</summary>
        public bool IsLoaded { get; private set; } = false;

        private bool _hasUnsavedChanges = false;

        /// <summary>ID вкладки (для UI)</summary>
        public string Id { get; }

        /// <summary>
        /// Контекст документа — передаётся модулям для управления состоянием
        /// </summary>
        public DocumentContext Context { get; }

        /// <summary>
        /// Изолированный контейнер модулей для этого проекта
        /// При закрытии проекта все модули автоматически уничтожаются
        /// </summary>
        public ProjectModuleContext ModuleContext { get; }

        /// <summary>
        /// Контроллер рабочего пространства (WorkModes, Layout, Float окна)
        /// </summary>
        public IWorkspaceController? Workspace { get; private set; }

        /// <summary>
        /// Баннер восстановления версий (null если нет кеша)
        /// </summary>
        public RecoveryBannerViewModel? RecoveryBanner
        {
            get => _recoveryBanner;
            set => this.RaiseAndSetIfChanged(ref _recoveryBanner, value);
        }

        /// <summary>Есть ли баннер восстановления</summary>
        public bool HasRecoveryBanner => RecoveryBanner != null;

        /// <summary>Заголовок вкладки</summary>
        public string Title
        {
            get => _project.Title;
            set
            {
                _project.Title = value;
                this.RaisePropertyChanged();
            }
        }

        /// <summary>Содержимое документа (текст из TextEditor модуля)</summary>
        public string Content
        {
            get
            {
                if (_project.ModulesData.TryGetValue("TextEditor", out var data))
                {
                    if (data is string text)
                        return text;
                }
                return "";
            }
            set
            {
                _project.ModulesData["TextEditor"] = value;
                this.RaisePropertyChanged();
            }
        }

        public string? FilePath
        {
            get => _filePath;
            set
            {
                _filePath = value;
                this.RaisePropertyChanged();
            }
        }

        /// <summary>Активна ли вкладка</summary>
        public bool IsActive
        {
            get => _isActive;
            set
            {
                _isActive = value;
                this.RaisePropertyChanged();
            }
        }

        /// <summary>Команда закрытия вкладки</summary>
        public ReactiveCommand<Unit, Unit> CloseCommand { get; }

        public DocumentTabViewModel(
            ProjectFile project,
            string filePath = "",
            Func<DocumentTabViewModel, Task>? onClose = null)
        {
            _logger = App.Services.GetService<ILogger<DocumentTabViewModel>>()!;
            _project = project;
            _filePath = filePath;
            _onClose = onClose;
            Id = Guid.NewGuid().ToString();

            Context = new DocumentContext(project, filePath);

            var moduleFactory = App.Services.GetRequiredService<ModuleFactory>();
            ModuleContext = new ProjectModuleContext(project.Id, moduleFactory);
            _logger.LogDebug("ProjectModuleContext created for: {ProjectTitle}", project.Title);

            CloseCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                _logger.LogDebug("CloseCommand executed");
                if (_onClose != null)
                    await _onClose(this);
                else
                    _logger.LogError("onClose callback is null");
            });

            this.WhenAnyValue(x => x.RecoveryBanner)
                .Subscribe(_ => this.RaisePropertyChanged(nameof(HasRecoveryBanner)))
                .DisposeWith(_disposables);
        }

        /// <summary>
        /// Инициализировать WorkspaceController без активации
        /// Вызывается из ProjectWorkflow после загрузки WorkModes
        /// </summary>
        public void InitializeWorkspace(List<Core.Models.WorkModes.WorkMode> loadedWorkModes, IProjectFileStorage? storage = null)
        {
            if (string.IsNullOrEmpty(_filePath))
            {
                _logger.LogWarning("Cannot initialize workspace - no file path");
                return;
            }

            var dockFactory = App.Services.GetRequiredService<DockFactory>();
            var autoSave = App.Services.GetRequiredService<IWorkspaceAutoSaveService>();

            Workspace = new WorkspaceController(
                this,
                _filePath,
                loadedWorkModes,
                dockFactory,
                autoSave,
                storage
            );

            IsLoaded = true;
            _logger.LogDebug("WorkspaceController initialized for: {Title}", Title);
        }

        /// <summary>
        /// Активировать Workspace при первом переходе на вкладку
        /// </summary>
        public void EnsureWorkspaceActivated()
        {
            if (Workspace == null)
            {
                _logger.LogWarning("Workspace not initialized, cannot activate");
                return;
            }

            Workspace.Activate();
            _logger.LogDebug("Workspace activated for: {Title}", Title);
        }

        /// <summary>
        /// Сохранить в кеш асинхронно
        /// Сохраняет только если данные отличаются от сохранённого ZIP файла
        /// Ключ — moduleType
        /// </summary>
        public async Task SaveToCacheAsync(Func<IEnumerable<IModule>> getActiveModules)
        {
            try
            {
                var stateCollector = App.Services.GetRequiredService<IModuleStateCollectorService>();
                var cacheService = App.Services.GetRequiredService<IZipCacheService>();
                var projectService = App.Services.GetRequiredService<IProjectService>();

                var activeModules = getActiveModules().ToList();

                if (activeModules.Count == 0)
                    return;

                var (customData, sessionData) = stateCollector.CollectAllData(activeModules);

                if (customData.Count == 0)
                    return;

                var savedProject = await projectService.LoadAsync(FilePath!);

                if (savedProject != null)
                {
                    bool dataChanged = customData.Count != savedProject.ModulesData.Count;

                    if (!dataChanged)
                    {
                        foreach (var kvp in customData)
                        {
                            if (!savedProject.ModulesData.TryGetValue(kvp.Key, out var savedData))
                            {
                                dataChanged = true;
                                break;
                            }

                            if (kvp.Value is string currentStr && savedData is string savedStr)
                            {
                                if (currentStr != savedStr) { dataChanged = true; break; }
                            }
                            else if (!Equals(kvp.Value, savedData))
                            {
                                dataChanged = true;
                                break;
                            }
                        }
                    }

                    if (dataChanged)
                    {
                        await cacheService.SaveCacheAsync(FilePath!, _project.Id, customData, sessionData);
                        _logger.LogDebug("Cache saved (differs from ZIP)");
                    }
                    else
                    {
                        _logger.LogDebug("No changes, cache not needed");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SaveToCache failed");
            }
        }

        /// <summary>
        /// Обновить данные проекта (используется при переключении версий)
        /// </summary>
        public void UpdateProject(ProjectFile newProject)
        {
            _project.ModulesData.Clear();
            foreach (var kvp in newProject.ModulesData)
                _project.ModulesData[kvp.Key] = kvp.Value;

            _project.LastModified = newProject.LastModified;
            _project.ProjectPinnedColors = newProject.ProjectPinnedColors;
            _project.ProjectRecentColors = newProject.ProjectRecentColors;
            _project.AvatarRingsAll = newProject.AvatarRingsAll;
            _project.ProjectPalettes = newProject.ProjectPalettes;
            _project.GlobalPaletteOrder = newProject.GlobalPaletteOrder;

            this.RaisePropertyChanged(nameof(Content));

            _logger.LogDebug("Project data updated: {Count} modules", _project.ModulesData.Count);
        }

        /// <summary>Получить проект</summary>
        public ProjectFile GetProject() => _project;

        /// <summary>Отметить что есть несохранённые изменения</summary>
        public void MarkAsModified()
        {
            _hasUnsavedChanges = true;
            _logger.LogDebug("Marked as modified: {Title}", Title);
        }

        /// <summary>Сбросить флаг изменений после успешного сохранения</summary>
        public void MarkAsSaved()
        {
            _hasUnsavedChanges = false;
            _logger.LogDebug("Marked as saved: {Title}", Title);
        }

        /// <summary>Проверить есть ли несохранённые изменения</summary>
        public bool HasUnsavedChanges() => _hasUnsavedChanges;

        /// <summary>
        /// Очистка ресурсов
        /// Уничтожает WorkspaceController и все модули проекта
        /// </summary>
        public override void Dispose()
        {
            _logger.LogDebug("Disposing: {Title}", Title);

            Workspace?.Dispose();
            Workspace = null;

            ModuleContext?.Dispose();

            base.Dispose();
            _logger.LogDebug("Disposed: {Title}", Title);
        }
    }
}