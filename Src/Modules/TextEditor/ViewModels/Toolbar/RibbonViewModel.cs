using ReactiveUI;
using Writersword.Modules.TextEditor.Contracts;

namespace Writersword.Modules.TextEditor.ViewModels.Toolbar
{
    /// <summary>
    /// ViewModel Ribbon — контейнер для вкладок.
    /// Хранит выбранную вкладку и ViewModel каждой вкладки.
    ///
    /// ДОБАВЛЕНО:
    ///   • Вкладка Table (контекстная) — видна только когда каретка в таблице.
    ///   • IsTableTabVisible — управляет видимостью вкладки.
    ///   • При входе каретки в таблицу TextEditorViewModel устанавливает IsTableTabVisible = true.
    /// </summary>
    public sealed class RibbonViewModel : ReactiveObject
    {
        private int _selectedTabIndex;
        private bool _isTableTabVisible;

        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set => this.RaiseAndSetIfChanged(ref _selectedTabIndex, value);
        }

        /// <summary>
        /// Управляет видимостью контекстной вкладки «Таблица».
        /// true — каретка находится внутри таблицы.
        /// </summary>
        public bool IsTableTabVisible
        {
            get => _isTableTabVisible;
            set => this.RaiseAndSetIfChanged(ref _isTableTabVisible, value);
        }

        public RibbonHomeTabViewModel Home { get; }
        public RibbonInsertTabViewModel Insert { get; }
        public RibbonLayoutTabViewModel Layout { get; }
        public RibbonReferencesTabViewModel References { get; }

        /// <summary>Контекстная вкладка для работы с таблицей.</summary>
        public RibbonTableTabViewModel Table { get; }

        public RibbonViewModel(ITextEditorCommandTarget target)
        {
            Home = new RibbonHomeTabViewModel(target);
            Insert = new RibbonInsertTabViewModel(target);
            Layout = new RibbonLayoutTabViewModel(target);
            References = new RibbonReferencesTabViewModel(target);
            Table = new RibbonTableTabViewModel(target);
        }
    }
}