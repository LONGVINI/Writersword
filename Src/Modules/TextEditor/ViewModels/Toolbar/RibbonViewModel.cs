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
        private bool _isImageTabVisible;
        private bool _isTocTabVisible;
        private bool _isHeaderFooterTabVisible;
        private bool _isEditingEnabled = true;

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

        /// <summary>
        /// Управляет видимостью контекстной вкладки «Формат» (работа с картинкой).
        /// true — на канвасе выделено изображение.
        /// </summary>
        public bool IsImageTabVisible
        {
            get => _isImageTabVisible;
            set => this.RaiseAndSetIfChanged(ref _isImageTabVisible, value);
        }

        /// <summary>
        /// Управляет видимостью контекстной вкладки «Оглавление».
        /// true — каретка стоит внутри оглавления.
        /// </summary>
        public bool IsTocTabVisible
        {
            get => _isTocTabVisible;
            set => this.RaiseAndSetIfChanged(ref _isTocTabVisible, value);
        }

        /// <summary>
        /// Управляет видимостью контекстной вкладки «Колонтитулы».
        /// true — идёт работа с колонтитулами.
        /// </summary>
        public bool IsHeaderFooterTabVisible
        {
            get => _isHeaderFooterTabVisible;
            set => this.RaiseAndSetIfChanged(ref _isHeaderFooterTabVisible, value);
        }

        /// <summary>
        /// false — режим сравнения версий: содержимое вкладок риббона не принимает
        /// клики и ввод (IsHitTestVisible), но выглядит почти обычно и продолжает
        /// отражать состояние под кареткой. Переключение вкладок остаётся доступным.
        /// </summary>
        public bool IsEditingEnabled
        {
            get => _isEditingEnabled;
            set
            {
                this.RaiseAndSetIfChanged(ref _isEditingEnabled, value);
                this.RaisePropertyChanged(nameof(ContentOpacity));
            }
        }

        /// <summary>
        /// Прозрачность содержимого вкладок: в режиме сравнения слегка приглушено,
        /// чтобы было видно, что кнопки не активны, но всё оставалось читаемым.
        /// </summary>
        public double ContentOpacity => _isEditingEnabled ? 1.0 : 0.72;

        public RibbonHomeTabViewModel Home { get; }
        public RibbonInsertTabViewModel Insert { get; }
        public RibbonLayoutTabViewModel Layout { get; }
        public RibbonReferencesTabViewModel References { get; }

        /// <summary>
        /// Вкладка «Вид»: чем залит лист при правке, каким светом, что с картинками
        /// и что остаётся на экране в режиме фокуса.
        ///
        /// Она одна получает не исполнителя команд документа, а модуль: речь идёт о
        /// показе рукописи, а не о её правке, и модель документа про цвет листа на
        /// экране ничего не знает.
        /// </summary>
        public RibbonAppearanceTabViewModel Appearance { get; }

        /// <summary>Контекстная вкладка для работы с таблицей.</summary>
        public RibbonTableTabViewModel Table { get; }

        /// <summary>Контекстная вкладка для работы с картинкой и фигурой.</summary>
        public RibbonImageTabViewModel Image { get; }

        /// <summary>Контекстная вкладка для работы с оглавлением.</summary>
        public RibbonTocTabViewModel Toc { get; }

        /// <summary>Контекстная вкладка колонтитулов и номеров страниц.</summary>
        public RibbonHeaderFooterTabViewModel HeaderFooter { get; }

        public RibbonViewModel(
            ITextEditorCommandTarget target, IEditorViewHost viewHost, ITocHost tocHost,
            IHeaderFooterHost headerFooterHost)
        {
            Home = new RibbonHomeTabViewModel(target);
            Insert = new RibbonInsertTabViewModel(target);
            Layout = new RibbonLayoutTabViewModel(target);
            References = new RibbonReferencesTabViewModel(target);
            Appearance = new RibbonAppearanceTabViewModel(viewHost);
            Table = new RibbonTableTabViewModel(target);
            Image = new RibbonImageTabViewModel(target);
            Toc = new RibbonTocTabViewModel(tocHost);
            HeaderFooter = new RibbonHeaderFooterTabViewModel(headerFooterHost);

            // Кнопки «Колонтитулы» и «Номер страницы» на «Вставке» — те же команды, что
            // на своей вкладке: одно действие не должно жить в двух реализациях.
            Insert.HeaderFooter = HeaderFooter;
        }
    }
}