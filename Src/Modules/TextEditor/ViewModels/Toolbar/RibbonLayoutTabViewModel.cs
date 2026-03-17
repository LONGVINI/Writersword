using System.Windows.Input;
using ReactiveUI;

namespace Writersword.Modules.TextEditor.ViewModels.Toolbar
{
    public sealed class RibbonLayoutTabViewModel : ReactiveObject
    {
        private readonly ITextEditorCommandTarget _target;

        private bool _isPageGroupExpanded = true;
        private bool _isColumnsGroupExpanded = true;
        private bool _isBreaksGroupExpanded = true;

        private int _currentColumnCount = 1;

        public bool IsPageGroupExpanded
        {
            get => _isPageGroupExpanded;
            set => this.RaiseAndSetIfChanged(ref _isPageGroupExpanded, value);
        }

        public bool IsColumnsGroupExpanded
        {
            get => _isColumnsGroupExpanded;
            set => this.RaiseAndSetIfChanged(ref _isColumnsGroupExpanded, value);
        }

        public bool IsBreaksGroupExpanded
        {
            get => _isBreaksGroupExpanded;
            set => this.RaiseAndSetIfChanged(ref _isBreaksGroupExpanded, value);
        }

        public int CurrentColumnCount
        {
            get => _currentColumnCount;
            set => this.RaiseAndSetIfChanged(ref _currentColumnCount, value);
        }

        public ICommand SetSizeA4Command { get; }
        public ICommand SetSizeA3Command { get; }
        public ICommand SetSizeA5Command { get; }
        public ICommand SetSizeLetterCommand { get; }
        public ICommand SetOrientationPortraitCommand { get; }
        public ICommand SetOrientationLandscapeCommand { get; }
        public ICommand SetMarginsCommand { get; }
        public ICommand Set1ColumnCommand { get; }
        public ICommand Set2ColumnsCommand { get; }
        public ICommand Set3ColumnsCommand { get; }
        public ICommand InsertPageBreakCommand { get; }
        public ICommand InsertSectionBreakCommand { get; }

        public RibbonLayoutTabViewModel(ITextEditorCommandTarget target)
        {
            _target = target;

            SetSizeA4Command = ReactiveCommand.Create(() => { });
            SetSizeA3Command = ReactiveCommand.Create(() => { });
            SetSizeA5Command = ReactiveCommand.Create(() => { });
            SetSizeLetterCommand = ReactiveCommand.Create(() => { });
            SetOrientationPortraitCommand = ReactiveCommand.Create(() => { });
            SetOrientationLandscapeCommand = ReactiveCommand.Create(() => { });
            SetMarginsCommand = ReactiveCommand.Create(() => { });

            Set1ColumnCommand = ReactiveCommand.Create(() => CurrentColumnCount = 1);
            Set2ColumnsCommand = ReactiveCommand.Create(() => CurrentColumnCount = 2);
            Set3ColumnsCommand = ReactiveCommand.Create(() => CurrentColumnCount = 3);

            InsertPageBreakCommand = ReactiveCommand.Create(() => { });
            InsertSectionBreakCommand = ReactiveCommand.Create(() => { });
        }

        public void UpdateLayout(double availableWidth)
        {
            if (availableWidth >= 700)
            {
                IsPageGroupExpanded = true;
                IsColumnsGroupExpanded = true;
                IsBreaksGroupExpanded = true;
                return;
            }

            IsBreaksGroupExpanded = false;

            if (availableWidth >= 530)
            {
                IsPageGroupExpanded = true;
                IsColumnsGroupExpanded = true;
                return;
            }

            IsColumnsGroupExpanded = false;
            IsPageGroupExpanded = availableWidth >= 300;
        }
    }
}