using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Reactive;
using System.Threading.Tasks;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Models;
using Writersword.Core.Models.Project;
using Writersword.Core.Interfaces.Services;

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
        private readonly IAutoSaveService _autoSaveService;
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
                // Читаем из ModulesData
                if (_project.ModulesData.TryGetValue("TextEditor", out var data))
                {
                    if (data is string text)
                        return text;
                }
                return "";
            }
            set
            {
                // Сохраняем в ModulesData
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
          IAutoSaveService? autoSaveService = null)
        {
            _project = project;
            _filePath = filePath;
            _onClose = onClose;
            Id = Guid.NewGuid().ToString();

            // Создаём собственный AutoSaveService для этой вкладки
            _autoSaveService = autoSaveService ?? Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IAutoSaveService>(App.Services);

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

        /// <summary>Запустить автосохранение для этой вкладки</summary>
        public void StartAutoSave()
        {
            if (!string.IsNullOrEmpty(FilePath) && _getActiveModules != null)
            {
                _autoSaveService.Start(FilePath, _getActiveModules);
                Console.WriteLine($"[DocumentTabViewModel] AutoSave started for: {Title}");
            }
            else
            {
                Console.WriteLine($"[DocumentTabViewModel] Cannot start AutoSave: FilePath={FilePath}, hasProvider={_getActiveModules != null}");
            }
        }

        /// <summary>Остановить автосохранение для этой вкладки</summary>
        public void StopAutoSave()
        {
            _autoSaveService.Stop();
            Console.WriteLine($"[DocumentTabViewModel] AutoSave stopped for: {Title}");
        }

        /// <summary>Получить проект</summary>
        public ProjectFile GetProject() => _project;
    }
}