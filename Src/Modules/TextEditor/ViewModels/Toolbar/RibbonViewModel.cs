using System;
using ReactiveUI;

namespace Writersword.Modules.TextEditor.ViewModels.Toolbar
{
    /// <summary>
    /// ViewModel всей Ribbon-панели.
    /// Содержит вкладки и управляет активной вкладкой.
    /// </summary>
    public sealed class RibbonViewModel : ReactiveObject
    {
        private int _selectedTabIndex;

        /// <summary>Индекс активной вкладки (0=Главная, 1=Вставка, 2=Макет, 3=Ссылки).</summary>
        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set => this.RaiseAndSetIfChanged(ref _selectedTabIndex, value);
        }

        public RibbonHomeTabViewModel Home { get; }
        public RibbonInsertTabViewModel Insert { get; }
        public RibbonLayoutTabViewModel Layout { get; }
        public RibbonReferencesTabViewModel References { get; }

        public RibbonViewModel(ITextEditorCommandTarget target)
        {
            if (target is null) throw new ArgumentNullException(nameof(target));

            Home       = new RibbonHomeTabViewModel(target);
            Insert     = new RibbonInsertTabViewModel(target);
            Layout     = new RibbonLayoutTabViewModel(target);
            References = new RibbonReferencesTabViewModel(target);
        }
    }
}
