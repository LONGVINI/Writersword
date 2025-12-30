using ReactiveUI;
using System;
using System.Reactive;
using System.Threading.Tasks;
using Writersword.Core.Models.Project;

namespace Writersword.ViewModels
{
    /// <summary>
    /// ViewModel для одной вкладки документа
    /// Теперь работает напрямую с ProjectFile
    /// </summary>
    public class DocumentTabViewModel : ViewModelBase
    {
        private readonly ProjectFile _project;
        private readonly Func<DocumentTabViewModel, Task>? _onClose;
        private bool _isActive;
        private string _filePath = "";

        /// <summary>ID вкладки (для UI)</summary>
        public string Id { get; }

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

        public DocumentTabViewModel(ProjectFile project, string filePath = "", Func<DocumentTabViewModel, Task>? onClose = null)
        {
            _project = project;
            _filePath = filePath;
            _onClose = onClose;
            Id = Guid.NewGuid().ToString();

            CloseCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                Console.WriteLine("[DocumentTabViewModel] CloseCommand EXECUTED!");
                if (_onClose != null)
                {
                    Console.WriteLine("[DocumentTabViewModel] Calling _onClose!");
                    await _onClose(this);  // ← БЕЗ КАСТА!
                    Console.WriteLine("[DocumentTabViewModel] _onClose completed!");
                }
                else
                {
                    Console.WriteLine("[DocumentTabViewModel] ERROR: _onClose is NULL!");
                }
            });
        }

        /// <summary>Получить проект</summary>
        public ProjectFile GetProject() => _project;
    }
}