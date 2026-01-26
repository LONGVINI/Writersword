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
using Writersword.Core.Models.Modules;
using Writersword.Core.Models.Project;
using Writersword.Src.Core.Interfaces.Services.Storage;

namespace Writersword.ViewModels
{
    /// <summary>
    /// ViewModel для одной вкладки документа
    /// Теперь работает напрямую с ProjectFile и управляет DocumentContext
    /// Каждая вкладка имеет свой RecoveryBanner (если есть кеш)
    /// КЕШИРОВАНИЕ управляется MainWindowViewModel для активной вкладки
    /// </summary>
    public class DocumentTabViewModel : ViewModelBase
    {
        private readonly ProjectFile _project;
        private readonly Func<DocumentTabViewModel, Task>? _onClose;
        private bool _isActive;
        private string _filePath = "";
        private RecoveryBannerViewModel? _recoveryBanner;

        /// <summary>ID вкладки (для UI)</summary>
        public string Id { get; }

        /// <summary>
        /// Контекст документа - передаётся модулям для управления состоянием
        /// Содержит информацию о проекте и режиме просмотра
        /// </summary>
        public DocumentContext Context { get; }

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

            // Создаём контекст документа
            Context = new DocumentContext(project, filePath);

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

            // Подписываемся на изменения RecoveryBanner для обновления HasRecoveryBanner
            this.WhenAnyValue(x => x.RecoveryBanner)
                .Subscribe(_ => this.RaisePropertyChanged(nameof(HasRecoveryBanner)));
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
                    // Собираем текущие состояния всех активных модулей
                    var currentStates = stateCollector.CollectAllStates(activeModules);

                    if (currentStates.Count > 0)
                    {
                        // Временно закрываем ZIP чтобы освободить файл для чтения
                        var savedProject = await projectService.LoadAsync(FilePath);

                        if (savedProject != null)
                        {
                            bool dataChanged = false;

                            // Быстрая проверка: разное количество модулей = изменения есть
                            if (currentStates.Count != savedProject.ModulesData.Count)
                            {
                                dataChanged = true;
                                Console.WriteLine($"[DocumentTabViewModel] Module count differs");
                            }
                            else
                            {
                                // Сравниваем данные каждого модуля
                                foreach (var kvp in currentStates)
                                {
                                    if (!savedProject.ModulesData.TryGetValue(kvp.Key, out var savedData))
                                    {
                                        dataChanged = true;
                                        Console.WriteLine($"[DocumentTabViewModel] New module: {kvp.Key}");
                                        break;
                                    }

                                    var currentCustomData = kvp.Value.CustomData;

                                    // Оптимизированное сравнение для строк (основной случай)
                                    if (currentCustomData is string currentStr && savedData is string savedStr)
                                    {
                                        if (currentStr != savedStr)
                                        {
                                            dataChanged = true;
                                            Console.WriteLine($"[DocumentTabViewModel] Data changed: {kvp.Key}");
                                            break;
                                        }
                                    }
                                    else if (!Equals(currentCustomData, savedData))
                                    {
                                        dataChanged = true;
                                        Console.WriteLine($"[DocumentTabViewModel] Data changed: {kvp.Key}");
                                        break;
                                    }
                                }
                            }

                            // Сохраняем кеш только если есть несохранённые изменения
                            if (dataChanged)
                            {
                                await cacheService.SaveCacheAsync(FilePath, _project.Id, currentStates);
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
            // Обновляем ModulesData (словарь изменяется по ссылке)
            _project.ModulesData.Clear();
            foreach (var kvp in newProject.ModulesData)
            {
                _project.ModulesData[kvp.Key] = kvp.Value;
            }

            // Обновляем дату
            _project.LastModified = newProject.LastModified;

            // Уведомляем UI об изменении Content
            this.RaisePropertyChanged(nameof(Content));

            Console.WriteLine($"[DocumentTabViewModel] Project data updated, ModulesData count: {_project.ModulesData.Count}");
        }

        /// <summary>Получить проект</summary>
        public ProjectFile GetProject() => _project;
    }
}