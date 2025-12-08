using ReactiveUI;
using System.Reactive;
using Writersword.Modules.TextEditor.ViewModels;
using Writersword.Services;
using Writersword.Services.Interfaces;

namespace Writersword.ViewModels
{
    public class MainWindowViewModel : ViewModelBase
    {
        private readonly IDialogService _dialogService;
        private readonly ISettingsService _settingsService;

        private string _title = "Writersword";
        private object? _currentModule;

        public string Title
        {
            get => _title;
            set => this.RaiseAndSetIfChanged(ref _title, value);
        }

        /// <summary>Текущий активный модуль</summary>
        public object? CurrentModule
        {
            get => _currentModule;
            set => this.RaiseAndSetIfChanged(ref _currentModule, value);
        }

        // ========================================
        // Команды для меню
        // ========================================

        /// <summary>Команда: Создать новый проект</summary>
        public ReactiveCommand<Unit, Unit> NewProjectCommand { get; }

        /// <summary>Команда: Открыть проект</summary>
        public ReactiveCommand<Unit, Unit> OpenProjectCommand { get; }

        /// <summary>Команда: Сохранить проект</summary>
        public ReactiveCommand<Unit, Unit> SaveProjectCommand { get; }

        /// <summary>Команда: Выход</summary>
        public ReactiveCommand<Unit, Unit> ExitCommand { get; }

        public MainWindowViewModel(IDialogService dialogService, ISettingsService settingsService)
        {
            _dialogService = dialogService;
            _settingsService = settingsService;

            // Инициализация команд
            NewProjectCommand = ReactiveCommand.Create(NewProject);
            OpenProjectCommand = ReactiveCommand.CreateFromTask(OpenProject);
            SaveProjectCommand = ReactiveCommand.CreateFromTask(SaveProject);
            SaveAsProjectCommand = ReactiveCommand.CreateFromTask(SaveAsProject);
            ExitCommand = ReactiveCommand.Create(Exit);

            // Загружаем настройки
            _settingsService.Load();

            // По умолчанию показываем TextEditor
            ShowTextEditor();
        }

        /// <summary>Показать модуль TextEditor</summary>
        public void ShowTextEditor()
        {
            var viewModel = new TextEditorViewModel();
            var view = new Modules.TextEditor.Views.TextEditorView
            {
                DataContext = viewModel
            };
            CurrentModule = view;
        }

        // ========================================
        // Реализация команд
        // ========================================

        private void NewProject()
        {
            // TODO: Создать новый проект
            Title = "Writersword - Untitled";
            ShowTextEditor();
        }

        private async System.Threading.Tasks.Task OpenProject()
        {
            var path = await _dialogService.OpenFileAsync();
            if (path != null)
            {
                // TODO: Загрузить проект из файла
                Title = $"Writersword - {System.IO.Path.GetFileName(path)}";
                _settingsService.LastOpenedProject = path; // Сохраняем в настройки
            }
        }

        private async System.Threading.Tasks.Task SaveProject()
        {
            var path = await _dialogService.SaveFileAsync();
            if (path != null)
            {
                // TODO: Сохранить проект в файл
                Title = $"Writersword - {System.IO.Path.GetFileName(path)}";

                // ИСПРАВЛЕНИЕ: ShowMessageAsync уже async, просто await
                await _dialogService.ShowMessageAsync("Saved", $"Project saved to: {path}");
            }
        }

        /// <summary>Команда: Сохранить как...</summary>
        public ReactiveCommand<Unit, Unit> SaveAsProjectCommand { get; }


        // Новый метод:
        private async System.Threading.Tasks.Task SaveAsProject()
        {
            var path = await _dialogService.SaveFileAsync();
            if (path != null)
            {
                // TODO: Сохранить проект в файл
                Title = $"Writersword - {System.IO.Path.GetFileName(path)}";
                await _dialogService.ShowMessageAsync("Saved", $"Project saved to: {path}");
            }
        }

        private void Exit()
        {
            // TODO: Закрыть приложение
            System.Environment.Exit(0);
        }
    }
}