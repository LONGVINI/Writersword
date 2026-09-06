using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Writersword.Modules.TextEditor.ViewModels.StatusBar;

namespace Writersword.Modules.TextEditor.Views.Dialogs
{
    /// <summary>
    /// Окно «Статистика»: страницы, слова, знаки с пробелами и без, абзацы и строки —
    /// тот же набор величин, что показывает Word. Окно ничего не считает само:
    /// значения приходят снимком из строки состояния, где они собираются по тексту
    /// документа и по построенной раскладке.
    /// </summary>
    public partial class DocumentStatisticsOverlay : UserControl
    {
        private Border _scrim = null!;
        private TextBlock _pagesValue = null!;
        private TextBlock _wordsValue = null!;
        private TextBlock _charsNoSpacesValue = null!;
        private TextBlock _charsWithSpacesValue = null!;
        private TextBlock _paragraphsValue = null!;
        private TextBlock _linesValue = null!;
        private TextBlock _draftHint = null!;

        public DocumentStatisticsOverlay()
        {
            InitializeComponent();
            IsVisible = false;

            _scrim = this.FindControl<Border>("Scrim")!;
            _pagesValue = this.FindControl<TextBlock>("PagesValue")!;
            _wordsValue = this.FindControl<TextBlock>("WordsValue")!;
            _charsNoSpacesValue = this.FindControl<TextBlock>("CharsNoSpacesValue")!;
            _charsWithSpacesValue = this.FindControl<TextBlock>("CharsWithSpacesValue")!;
            _paragraphsValue = this.FindControl<TextBlock>("ParagraphsValue")!;
            _linesValue = this.FindControl<TextBlock>("LinesValue")!;
            _draftHint = this.FindControl<TextBlock>("DraftHint")!;

            var okBtn = this.FindControl<Button>("OkBtn")!;
            var closeBtn = this.FindControl<Button>("CloseBtn")!;
            okBtn.Click += OnClose;
            closeBtn.Click += OnClose;
            _scrim.PointerPressed += OnScrimPressed;
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            TopLevel.GetTopLevel(this)?.AddHandler(KeyDownEvent, OnOverlayKeyDown, RoutingStrategies.Tunnel);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            TopLevel.GetTopLevel(this)?.RemoveHandler(KeyDownEvent, OnOverlayKeyDown);
            base.OnDetachedFromVisualTree(e);
        }

        private void OnOverlayKeyDown(object? sender, KeyEventArgs e)
        {
            if (!IsVisible) return;
            if (e.Key is Key.Escape or Key.Enter) { HideOverlay(); e.Handled = true; }
        }

        /// <summary>
        /// Показывает окно со снимком статистики. draftLayout — документ показан
        /// черновиком или веб-разметкой: страниц и строк там нет, и вместо чисел,
        /// оставшихся от прошлой постраничной раскладки, окно поясняет это.
        /// </summary>
        public void Show(DocumentStatistics stats, bool draftLayout)
        {
            _pagesValue.Text = draftLayout ? "—" : StatusBarViewModel.FormatNumber(stats.Pages);
            _linesValue.Text = draftLayout ? "—" : StatusBarViewModel.FormatNumber(stats.Lines);
            _wordsValue.Text = StatusBarViewModel.FormatNumber(stats.Words);
            _charsNoSpacesValue.Text = StatusBarViewModel.FormatNumber(stats.CharsNoSpaces);
            _charsWithSpacesValue.Text = StatusBarViewModel.FormatNumber(stats.CharsWithSpaces);
            _paragraphsValue.Text = StatusBarViewModel.FormatNumber(stats.Paragraphs);
            _draftHint.IsVisible = draftLayout;

            IsVisible = true;
            Focus();
        }

        private void HideOverlay() => IsVisible = false;

        private void OnClose(object? sender, RoutedEventArgs e) => HideOverlay();
        private void OnScrimPressed(object? sender, PointerPressedEventArgs e) => HideOverlay();
    }
}
