using ReactiveUI;
using Writersword.Modules.TextEditor.Contracts;

namespace Writersword.Modules.TextEditor.ViewModels.Toolbar
{
    /// <summary>
    /// ViewModel Ribbon — контейнер для вкладок.
    /// Хранит выбранную вкладку и ViewModel каждой вкладки.
    /// </summary>
    public sealed class RibbonViewModel : ReactiveObject
    {
        private int _selectedTabIndex;

        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set => this.RaiseAndSetIfChanged(ref _selectedTabIndex, value);
        }

        public RibbonHomeTabViewModel       Home       { get; }
        public RibbonInsertTabViewModel     Insert     { get; }
        public RibbonLayoutTabViewModel     Layout     { get; }
        public RibbonReferencesTabViewModel References { get; }

        public RibbonViewModel(ITextEditorCommandTarget target)
        {
            Home       = new RibbonHomeTabViewModel(target);
            Insert     = new RibbonInsertTabViewModel(target);
            Layout     = new RibbonLayoutTabViewModel(target);
            References = new RibbonReferencesTabViewModel(target);
        }
    }
}
