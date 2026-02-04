using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Models;
using Writersword.Core.Models.Project;
using Writersword.Core.Services;
using Writersword.Modules.Common;
using Writersword.Src.Core.Interfaces.Services.Storage;
using Writersword.Src.Core.Interfaces.Workspace;
using Writersword.Src.Infrastructure.Dock;
using Writersword.Src.Infrastructure.Workspace;

namespace Writersword.ViewModels
{
    /// <summary>
    /// ViewModel для одной вкладки документа
    /// Теперь работает напрямую с ProjectFile и управляет DocumentContext
    /// Каждая вкладка имеет свой RecoveryBanner (если есть кеш)
    /// ИЗОЛЯЦИЯ: каждая вкладка имеет свой WorkspaceController
    /// </summary>
    public class DocumentTabViewModel : ViewModelBase
    {
        private readonly ProjectFile _project;
        private readonly Func<DocumentTabViewModel, Task>? _onClose;
        private bool _isActive;
        private string _filePath = "";
        private RecoveryBannerViewModel? _recoveryBanner;

        private bool _hasUnsavedChanges = false;

        /// <summary>ID вкладки (для UI)</summary>
        public string Id { get; }

        /// <summary>
        /// Контекст документа - передаётся модулям для управления состоянием
        /// Содержит информацию о проекте и режиме просмотра
        /// </summary>
        public DocumentContext Context { get; }

        /// <summary>
        /// Изолированный контейнер модулей для этого проекта
        /// Каждый проект имеет свой собственный набор модулей
        /// При закрытии проекта все модули автоматически уничтожаются
        /// </summary>
        public ProjectModuleContext ModuleContext { get; }

        /// <summary>
        /// Контроллер рабочего пространства (WorkModes, Layout, Float окна)
        /// Полностью изолирован для этой вкладки
        /// </summary>
        public IWorkspaceController? Workspace { get; private set; }

        /// <summary>
        /// Баннер восстановления версий (null если нет кеша)
        /// Каждая вкладка имеет свой баннер
        /// </summary>
        public RecoveryBannerViewModel? RecoveryBanner
        {
            get => _recoveryBanner;
            set => this.RaiseAndSetIfChanged(ref _recoveryBanner, value);
        }

        /// <summary>Есть ли баннер восстановления (для привязки в UI)</summary>
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

        /// <summary>Путь к файлу проекта</summary>
        public string FilePath
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
            _project = project;
            _filePath = filePath;
            _onClose = onClose;
            Id = Guid.NewGuid().ToString();

            Context = new DocumentContext(project, filePath);

            var moduleFactory = App.Services.GetRequiredService<ModuleFactory>();
            ModuleContext = new ProjectModuleContext(project.Id, moduleFactory);
            Console.WriteLine($"[DocumentTabViewModel] ProjectModuleContext created for: {project.Title}");

            CloseCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                Console.WriteLine("[DocumentTabViewModel] CloseCommand EXECUTED!");
                if (_onClose != null)
                {
                    Console.WriteLine("[DocumentTabViewModel] Calling _onClose!");
                    await _onClose(this);
                    Console.WriteLine("[DocumentTabViewModel] _onClose completed!");
                }
                else
                {
                    Console.WriteLine("[DocumentTabViewModel] ERROR: _onClose is NULL!");
                }
            });

            this.WhenAnyValue(x => x.RecoveryBanner)
                .Subscribe(_ => this.RaisePropertyChanged(nameof(HasRecoveryBanner)));
        }

        /// <summary>
        /// Инициализировать WorkspaceController
        /// Вызывается из ProjectWorkflow после загрузки WorkModes
        /// </summary>
        public void InitializeWorkspace(List<Core.Models.WorkModes.WorkMode> loadedWorkModes)
        {
            if (string.IsNullOrEmpty(_filePath))
            {
                Console.WriteLine("[DocumentTabViewModel] Cannot initialize workspace - no file path");
                return;
            }

            var dockFactory = App.Services.GetRequiredService<DockFactory>();
            var autoSave = App.Services.GetRequiredService<Src.Core.Interfaces.Services.IWorkspaceAutoSaveService>();

            Workspace = new WorkspaceController(
                this,
                _filePath,
                loadedWorkModes,
                dockFactory,
                autoSave
            );

            Console.WriteLine($"[DocumentTabViewModel] WorkspaceController initialized for: {Title}");
        }

        /// <summary>
        /// Сохранить в кеш асинхронно
        /// Используется при переключении вкладок
        /// ВСЕГДА создаёт .wsasd для быстрого восстановления из ZIP
        /// </summary>
        public async Task SaveToCacheAsync(Func<IEnumerable<IModule>> getActiveModules)
        {
            try
            {
                var stateCollector = App.Services.GetRequiredService<IModuleStateCollectorService>();
                var cacheService = App.Services.GetRequiredService<IZipCacheService>();
                var projectService = App.Services.GetRequiredService<IProjectService>();

                var activeModules = getActiveModules().ToList();

                if (activeModules.Count > 0)
                {
                    var (customData, sessionData) = stateCollector.CollectAllData(activeModules);

                    if (customData.Count > 0)
                    {
                        var savedProject = await projectService.LoadAsync(FilePath);

                        if (savedProject != null)
                        {
                            bool dataChanged = false;

                            if (customData.Count != savedProject.ModulesData.Count)
                            {
                                dataChanged = true;
                                Console.WriteLine($"[DocumentTabViewModel] Module count differs");
                            }
                            else
                            {
                                foreach (var kvp in customData)
                                {
                                    if (!savedProject.ModulesData.TryGetValue(kvp.Key, out var savedData))
                                    {
                                        dataChanged = true;
                                        Console.WriteLine($"[DocumentTabViewModel] New module: {kvp.Key}");
                                        break;
                                    }

                                    if (kvp.Value is string currentStr && savedData is string savedStr)
                                    {
                                        if (currentStr != savedStr)
                                        {
                                            dataChanged = true;
                                            Console.WriteLine($"[DocumentTabViewModel] Data changed: {kvp.Key}");
                                            break;
                                        }
                                    }
                                    else if (!Equals(kvp.Value, savedData))
                                    {
                                        dataChanged = true;
                                        Console.WriteLine($"[DocumentTabViewModel] Data changed: {kvp.Key}");
                                        break;
                                    }
                                }
                            }

                            if (dataChanged)
                            {
                                await cacheService.SaveCacheAsync(FilePath, _project.Id, customData, sessionData);
                                Console.WriteLine($"[DocumentTabViewModel] Cache saved (differs from ZIP)");
                            }
                            else
                            {
                                Console.WriteLine($"[DocumentTabViewModel] No changes, cache not needed");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DocumentTabViewModel] SaveToCache error: {ex.Message}");
            }
        }

        /// <summary>Обновить данные проекта (используется при переключении версий)</summary>
        public void UpdateProject(ProjectFile newProject)
        {
            _project.ModulesData.Clear();
            foreach (var kvp in newProject.ModulesData)
            {
                _project.ModulesData[kvp.Key] = kvp.Value;
            }

            _project.LastModified = newProject.LastModified;

            this.RaisePropertyChanged(nameof(Content));

            Console.WriteLine($"[DocumentTabViewModel] Project data updated, ModulesData count: {_project.ModulesData.Count}");
        }

        /// <summary>Получить проект</summary>
        public ProjectFile GetProject() => _project;

        /// <summary>
        /// Отметить что есть несохранённые изменения
        /// Вызывается при реальном изменении данных в модулях
        /// </summary>
        public void MarkAsModified()
        {
            _hasUnsavedChanges = true;
            Console.WriteLine($"[DocumentTabViewModel] Marked as modified: {Title}");
        }

        /// <summary>
        /// Сбросить флаг изменений (после успешного сохранения)
        /// </summary>
        public void MarkAsSaved()
        {
            _hasUnsavedChanges = false;
            Console.WriteLine($"[DocumentTabViewModel] Marked as saved: {Title}");
        }

        /// <summary>
        /// Проверить есть ли несохранённые изменения
        /// </summary>
        public bool HasUnsavedChanges()
        {
            return _hasUnsavedChanges;
        }

        /// <summary>
        /// Очистка ресурсов
        /// Уничтожает все модули проекта и WorkspaceController
        /// </summary>
        public void Dispose()
        {
            Console.WriteLine($"[DocumentTabViewModel] Disposing: {Title}");

            Workspace?.Dispose();
            Workspace = null;
            Console.WriteLine($"[DocumentTabViewModel] WorkspaceController disposed");

            ModuleContext?.Dispose();
            Console.WriteLine($"[DocumentTabViewModel] All modules disposed for: {Title}");
        }
    }
}