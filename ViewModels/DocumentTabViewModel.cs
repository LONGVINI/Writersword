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
using Writersword.Src.Core.Interfaces.Services.Storage;
using Writersword.Src.Infrastructure.Services.Modules;

namespace Writersword.ViewModels
{
    /// <summary>
    /// ViewModel для одной вкладки документа
    /// Теперь работает напрямую с ProjectFile и управляет DocumentContext
    /// Каждая вкладка имеет свой RecoveryBanner (если есть кеш)
    /// </summary>
    public class DocumentTabViewModel : ViewModelBase
    {
        private readonly ProjectFile _project;
        private readonly Func<DocumentTabViewModel, Task>? _onClose;
        private readonly ICacheUpdateService _cacheUpdateService;
        private Func<IEnumerable<IModule>>? _getActiveModules;
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
                Func<DocumentTabViewModel, Task>? onClose = null,
                ICacheUpdateService? cacheUpdateService = null)
        {
            _project = project;
            _filePath = filePath;
            _onClose = onClose;
            Id = Guid.NewGuid().ToString();

            // Создаём НОВЫЙ экземпляр CacheUpdateService для ЭТОЙ вкладки
            var cacheService = App.Services.GetRequiredService<ICacheService>();
            var stateCollector = App.Services.GetRequiredService<IModuleStateCollectorService>();
            var comparisonService = App.Services.GetRequiredService<IDataComparisonService>();
            _cacheUpdateService = new CacheUpdateService(
                cacheService,
                stateCollector,
                comparisonService
            );

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
        /// Установить функцию получения активных модулей
        /// Вызывается из MainWindowViewModel после создания вкладки
        /// </summary>
        public void SetActiveModulesProvider(Func<IEnumerable<IModule>> getActiveModules)
        {
            _getActiveModules = getActiveModules;
        }

        /// <summary>Запустить фоновое кеширование для этой вкладки</summary>
        public void StartCaching()
        {
            if (!string.IsNullOrEmpty(FilePath) && _getActiveModules != null)
            {
                _cacheUpdateService.Start(FilePath, _getActiveModules);
                Console.WriteLine($"[DocumentTabViewModel] Caching started for: {Title}");
            }
            else
            {
                Console.WriteLine($"[DocumentTabViewModel] Cannot start caching: FilePath={FilePath}, hasProvider={_getActiveModules != null}");
            }
        }

        /// <summary>Остановить фоновое кеширование для этой вкладки</summary>
        public void StopCaching()
        {
            _cacheUpdateService.Stop();
            Console.WriteLine($"[DocumentTabViewModel] Caching stopped for: {Title}");
        }

        /// <summary>
        /// Сохранить в кеш асинхронно
        /// Используется при переключении вкладок
        /// СРАВНИВАЕТ с кешем И с файлом перед сохранением
        /// </summary>
        public async Task SaveToCacheAsync()
        {
            try
            {
                if (_getActiveModules == null)
                {
                    Console.WriteLine($"[DocumentTabViewModel] No active modules provider, skipping cache save");
                    return;
                }

                var stateCollector = App.Services.GetRequiredService<IModuleStateCollectorService>();
                var cacheService = App.Services.GetRequiredService<ICacheService>();
                var comparisonService = App.Services.GetRequiredService<IDataComparisonService>();

                // ПОЛУЧАЕМ МОДУЛИ ЧЕРЕЗ СОХРАНЁННУЮ ФУНКЦИЮ (для ЭТОЙ вкладки)
                var activeModules = _getActiveModules().ToList();

                if (activeModules.Count > 0)
                {
                    // Собираем текущие состояния модулей
                    var moduleStates = stateCollector.CollectAllStates(activeModules);

                    if (moduleStates.Count > 0)
                    {
                        // Извлекаем ТОЛЬКО CustomData для сравнения
                        var currentCustomData = new Dictionary<string, object?>();
                        foreach (var kvp in moduleStates)
                        {
                            if (kvp.Value.CustomData != null)
                            {
                                currentCustomData[kvp.Key] = kvp.Value.CustomData;
                            }
                        }

                        // СРАВНИВАЕМ с данными из ФАЙЛА .writersword
                        var project = GetProject();
                        bool dataMatchesFile = comparisonService.AreDataEqual(currentCustomData, project.ModulesData);

                        if (dataMatchesFile)
                        {
                            Console.WriteLine($"[DocumentTabViewModel] Data matches saved file, skipping cache");

                            // Если есть старый кеш - удаляем его (он больше не нужен)
                            if (cacheService.HasCache(FilePath))
                            {
                                cacheService.DeleteCache(FilePath);
                                Console.WriteLine($"[DocumentTabViewModel] Deleted outdated cache");
                            }

                            return;
                        }

                        // Данные отличаются от файла - проверяем кеш
                        var oldCache = cacheService.LoadCache(FilePath);

                        // Сравниваем с кешем (если есть)
                        if (!comparisonService.AreStatesEqual(oldCache, moduleStates))
                        {
                            await cacheService.SaveCacheAsync(FilePath, moduleStates);
                            Console.WriteLine($"[DocumentTabViewModel] Cache saved: {moduleStates.Count} modules");
                        }
                        else
                        {
                            Console.WriteLine($"[DocumentTabViewModel] No changes from cache, skipping save");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DocumentTabViewModel] Cache save error: {ex.Message}");
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