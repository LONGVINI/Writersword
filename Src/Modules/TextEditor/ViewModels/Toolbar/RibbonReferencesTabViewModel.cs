using System.Windows.Input;
using ReactiveUI;
using Writersword.Modules.TextEditor.Contracts;

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

            InsertTOCCommand = ReactiveCommand.Create(() => _target.InsertTOC());
            UpdateTOCCommand = ReactiveCommand.Create(() => { });
            InsertFootnoteCommand = ReactiveCommand.Create(() => _target.InsertFootnote());
            InsertEndnoteCommand = ReactiveCommand.Create(() => _target.InsertEndnote());
            RunSpellCheckCommand = ReactiveCommand.Create(() => _target.RunSpellCheck());
            ShowWordCountCommand = ReactiveCommand.Create(() => _target.ShowWordCount());
            PrintCommand = ReactiveCommand.Create(() => _target.Print());
            ExportPdfCommand = ReactiveCommand.Create(() => _target.ExportToPdf());
            ExportDocxCommand = ReactiveCommand.Create(() => _target.ExportToDocx());
            ExportTxtCommand = ReactiveCommand.Create(() => _target.ExportToTxt());
            ExportMarkdownCommand = ReactiveCommand.Create(() => _target.ExportToMarkdown());
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