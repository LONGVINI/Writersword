using ReactiveUI;
using System;
using System.Reactive;
using Writersword.Core.Models.Project;

namespace Writersword.ViewModels
{
    /// <summary>
    /// ViewModel для одной вкладки документа
    /// </summary>
    public class DocumentTabViewModel : ViewModelBase
    {
        private readonly DocumentTab _model;
        private readonly Action<DocumentTabViewModel>? _onClose;

        /// <summary>ID документа</summary>
        public string Id => _model.Id;

        /// <summary>Заголовок вкладки</summary>
        public string Title
        {
            get => _model.Title;
            set
            {
                _model.Title = value;
                this.RaisePropertyChanged();
            }
        }

        /// <summary>Содержимое документа (текст)</summary>
        public string Content
        {
            get => _model.Content;
            set
            {
                _model.Content = value;
                this.RaisePropertyChanged();
            }
        }

        /// <summary>Команда закрытия вкладки</summary>
        public ReactiveCommand<Unit, Unit> CloseCommand { get; }

        public DocumentTabViewModel(DocumentTab model, Action<DocumentTabViewModel>? onClose = null)
        {
            _model = model;
            _onClose = onClose;

            // Команда закрытия
            CloseCommand = ReactiveCommand.Create(() => _onClose?.Invoke(this));
        }

        /// <summary>Получить модель для сохранения</summary>
        public DocumentTab GetModel() => _model;
    }
}