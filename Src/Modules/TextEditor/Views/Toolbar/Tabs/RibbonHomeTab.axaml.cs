using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System;
using System.Linq;
using Writersword.Modules.TextEditor.Document;
using Writersword.Modules.TextEditor.Models.Styles;
using Writersword.Modules.TextEditor.Services;
using Writersword.Modules.TextEditor.ViewModels;
using Writersword.Modules.TextEditor.ViewModels.Toolbar;
using Writersword.Modules.TextEditor.Views;
using Writersword.Modules.TextEditor.Views.Dialogs;
using Writersword.Styles.UserControls;

namespace Writersword.Modules.TextEditor.Views.Toolbar.Tabs
{
    public partial class RibbonHomeTab : UserControl
    {
        private RibbonScrollContainer? _scrollContainer;
        private ListBox? _fontSizeList;

        public RibbonHomeTab()
        {
            InitializeComponent();
            SizeChanged += OnSizeChanged;
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            AttachControls();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            DetachControls();
        }

        private void AttachControls()
        {
            _scrollContainer = this.FindControl<RibbonScrollContainer>("ScrollContainer");

            _fontSizeList = this.FindControl<ListBox>("FontSizeListBox");
            if (_fontSizeList is not null)
            {
                _fontSizeList.SelectionChanged -= OnFontSizeListSelectionChanged;
                _fontSizeList.SelectionChanged += OnFontSizeListSelectionChanged;
            }
        }

        private void DetachControls()
        {
            if (_fontSizeList is not null)
                _fontSizeList.SelectionChanged -= OnFontSizeListSelectionChanged;
        }

        // ── Ribbon resize ─────────────────────────────────────────────────

        private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (DataContext is RibbonHomeTabViewModel vm)
            {
                vm.UpdateLayout(e.NewSize.Width);
                if (_scrollContainer is not null)
                    _scrollContainer.ArrowsVisible = !vm.IsClipboardGroupExpanded;
            }
            _scrollContainer?.NotifySizeChanged();
        }

        // ── FontSize list ─────────────────────────────────────────────────

        private void OnFontSizeListSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (sender is not ListBox lb) return;
            if (lb.SelectedItem is not string sizeStr) return;
            if (DataContext is RibbonHomeTabViewModel vm)
                vm.SelectFontSizeCommand.Execute(sizeStr);
            lb.SelectedItem = null;
        }

        // ── Гарнитура ─────────────────────────────────────────────────────

        // Открывает список гарнитур оверлеем того же модуля, которому принадлежит
        // эта лента. Сеанс предпросмотра открывается до показа списка и закрывается
        // ровно один раз, чем бы список ни кончился: выбором, Esc или щелчком мимо.
        private async void OnFontBoxClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not RibbonHomeTabViewModel vm) return;
            if (sender is not Control anchor) return;

            var host = this.FindAncestorOfType<TextEditorView>();
            if (host is null) return;

            var overlay = host.FindControl<FontPickerOverlay>("FontOverlay")
                          ?? host.GetVisualDescendants().OfType<FontPickerOverlay>().FirstOrDefault();
            if (overlay is null) return;

            vm.BeginFontPreview();

            string? chosen = await overlay.ShowAsync(
                vm.AvailableFonts,
                vm.CurrentFontFamily,
                anchor,
                vm.PreviewFontFamily);

            if (chosen is not null)
            {
                // Гарнитура кладётся во вьюмодель до завершения сеанса: пока он открыт,
                // сеттер только обновляет поле ленты, а рукопись меняет EndFontPreview
                // одной командой отмены.
                vm.CurrentFontFamily = chosen;
                vm.PreviewFontFamily(chosen);
                FontUsage.NoteUsed(chosen);
            }

            vm.EndFontPreview(chosen is not null);
        }

        // ── Настройки абзаца ──────────────────────────────────────────────

        // Открывает оверлей настроек абзаца внутри того же модуля (TextEditorView), которому
        // принадлежит этот риббон. Результат применяется одной командой отмены.
        private async void OnParagraphSettingsClick(object? sender, RoutedEventArgs e)
        {
            var host = this.FindAncestorOfType<TextEditorView>();
            if (host is null) return;

            var canvas = host.FindControl<DocumentCanvas>("PageCanvas")
                         ?? host.GetVisualDescendants().OfType<DocumentCanvas>().FirstOrDefault();
            if (canvas?.DataContext is not DocumentViewModel doc) return;

            var overlay = host.FindControl<ParagraphSettingsOverlay>("ParagraphOverlay")
                          ?? host.GetVisualDescendants().OfType<ParagraphSettingsOverlay>().FirstOrDefault();
            if (overlay is null) return;

            var current = doc.GetActiveParagraphProperties();
            if (current is null) return;

            var result = await overlay.ShowAsync(current);
            if (result is not null)
                doc.ApplyParagraphSettings(result);
        }

        // Открывает оверлей «Определить новый список» и применяет результат к выделению.
        private async void OnDefineListClick(object? sender, RoutedEventArgs e)
        {
            var host = this.FindAncestorOfType<TextEditorView>();
            if (host is null) return;

            var canvas = host.FindControl<DocumentCanvas>("PageCanvas")
                         ?? host.GetVisualDescendants().OfType<DocumentCanvas>().FirstOrDefault();
            if (canvas?.DataContext is not DocumentViewModel doc) return;

            var overlay = host.FindControl<ListSettingsOverlay>("ListOverlay")
                          ?? host.GetVisualDescendants().OfType<ListSettingsOverlay>().FirstOrDefault();
            if (overlay is null) return;

            var current = doc.GetActiveListProperties();
            var result = await overlay.ShowAsync(current);
            if (result is not null)
                doc.ApplyListSettings(result);
        }

        // Открывает оверлей «Уровни списка» и применяет выбранную схему многоуровневого списка.
        private async void OnMultilevelSettingsClick(object? sender, RoutedEventArgs e)
        {
            var host = this.FindAncestorOfType<TextEditorView>();
            if (host is null) return;

            var canvas = host.FindControl<DocumentCanvas>("PageCanvas")
                         ?? host.GetVisualDescendants().OfType<DocumentCanvas>().FirstOrDefault();
            if (canvas?.DataContext is not DocumentViewModel doc) return;

            var overlay = host.FindControl<ListLevelsOverlay>("ListLevelsOverlay")
                          ?? host.GetVisualDescendants().OfType<ListLevelsOverlay>().FirstOrDefault();
            if (overlay is null) return;

            var current = doc.GetActiveListLevelMarkers();
            var scheme = await overlay.ShowAsync(current);
            if (scheme is not null)
                doc.ApplyMultilevelScheme(scheme);
        }
    }
}