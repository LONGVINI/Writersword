using System.Windows.Input;
using ReactiveUI;

namespace Writersword.Modules.TextEditor.ViewModels.Toolbar
{
    public sealed class RibbonReferencesTabViewModel : ReactiveObject
    {
        private readonly ITextEditorCommandTarget _target;

        private bool _isTocGroupExpanded = true;
        private bool _isFootnotesGroupExpanded = true;
        private bool _isToolsGroupExpanded = true;
        private bool _isExportGroupExpanded = true;

        public bool IsTocGroupExpanded
        {
            get => _isTocGroupExpanded;
            set => this.RaiseAndSetIfChanged(ref _isTocGroupExpanded, value);
        }

        public bool IsFootnotesGroupExpanded
        {
            get => _isFootnotesGroupExpanded;
            set => this.RaiseAndSetIfChanged(ref _isFootnotesGroupExpanded, value);
        }

        public bool IsToolsGroupExpanded
        {
            get => _isToolsGroupExpanded;
            set => this.RaiseAndSetIfChanged(ref _isToolsGroupExpanded, value);
        }

        public bool IsExportGroupExpanded
        {
            get => _isExportGroupExpanded;
            set => this.RaiseAndSetIfChanged(ref _isExportGroupExpanded, value);
        }

        public ICommand InsertTOCCommand { get; }
        public ICommand UpdateTOCCommand { get; }
        public ICommand InsertFootnoteCommand { get; }
        public ICommand InsertEndnoteCommand { get; }
        public ICommand RunSpellCheckCommand { get; }
        public ICommand ShowWordCountCommand { get; }
        public ICommand PrintCommand { get; }
        public ICommand ExportPdfCommand { get; }
        public ICommand ExportDocxCommand { get; }
        public ICommand ExportTxtCommand { get; }
        public ICommand ExportMarkdownCommand { get; }

        public RibbonReferencesTabViewModel(ITextEditorCommandTarget target)
        {
            _target = target;

            InsertTOCCommand = ReactiveCommand.Create(() => { });
            UpdateTOCCommand = ReactiveCommand.Create(() => { });
            InsertFootnoteCommand = ReactiveCommand.Create(() => { });
            InsertEndnoteCommand = ReactiveCommand.Create(() => { });
            RunSpellCheckCommand = ReactiveCommand.Create(() => { });
            ShowWordCountCommand = ReactiveCommand.Create(() => { });
            PrintCommand = ReactiveCommand.Create(() => { });
            ExportPdfCommand = ReactiveCommand.Create(() => { });
            ExportDocxCommand = ReactiveCommand.Create(() => { });
            ExportTxtCommand = ReactiveCommand.Create(() => { });
            ExportMarkdownCommand = ReactiveCommand.Create(() => { });
        }

        public void UpdateLayout(double availableWidth)
        {
            if (availableWidth >= 800)
            {
                IsTocGroupExpanded = true;
                IsFootnotesGroupExpanded = true;
                IsToolsGroupExpanded = true;
                IsExportGroupExpanded = true;
                return;
            }

            IsExportGroupExpanded = false;

            if (availableWidth >= 620)
            {
                IsTocGroupExpanded = true;
                IsFootnotesGroupExpanded = true;
                IsToolsGroupExpanded = true;
                return;
            }

            IsToolsGroupExpanded = false;

            if (availableWidth >= 430)
            {
                IsTocGroupExpanded = true;
                IsFootnotesGroupExpanded = true;
                return;
            }

            IsFootnotesGroupExpanded = false;
            IsTocGroupExpanded = availableWidth >= 220;
        }
    }
}