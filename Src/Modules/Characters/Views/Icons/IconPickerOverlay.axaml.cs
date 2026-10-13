using System;
using System.Linq;
using Avalonia;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Writersword.Modules.Characters.Interfaces;
using Writersword.Modules.Characters.Models;
using Writersword.Modules.Characters.Services;
using Writersword.Modules.Characters.ViewModels;
using Writersword.Modules.Characters.ViewModels.Icons;
using Writersword.Styles.UserControls;
using Writersword.Modules.Characters.Views;

namespace Writersword.Modules.Characters.Views.Icons
{
    /// <summary>
    /// Окно значков: выбор значка анкете, шаблону, метке и библиотека своих
    /// значков проекта. Живёт на уровне модуля, как редактор метки, и
    /// вызывается отовсюду через Find — тем, кто его зовёт, не нужно ничего
    /// знать ни о библиотеке, ни о хранилище картинок.
    /// </summary>
    public partial class IconPickerOverlay : UserControl
    {
        private IconPickerViewModel? _model;
        private TaskCompletionSource<CharacterIconChoice?>? _pending;

        public IconPickerOverlay()
        {
            InitializeComponent();
            OwnColorPicker.PropertyChanged += OnOwnColorPickerPropertyChanged;
        }

        /// <summary>
        /// Щелчок по кружку своего цвета — уже выбор своего цвета: он ставится
        /// сразу, как быстрый кружок, и кнопка «Использовать свой цвет»
        /// загорается. Редактор открывается, чтобы цвет поменять; новый цвет
        /// из него ставится по привязке.
        /// </summary>
        private void OnOwnColorPickerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == ColorPickerButton.IsMenuOpenProperty && e.NewValue is true)
                _model?.ChooseOwnColor();
        }

        /// <summary>Окно значков модуля, в котором лежит control.</summary>
        public static IconPickerOverlay? Find(Control control) =>
            control.FindAncestorOfType<CharactersModuleView>()
                ?.FindControl<IconPickerOverlay>("IconPickerOverlayControl");

        private CharactersViewModel? ModuleViewModel =>
            this.FindAncestorOfType<CharactersModuleView>()?.DataContext as CharactersViewModel;

        private CharacterIconLibrary? Library => ModuleViewModel?.IconLibrary;

        private ICharacterAvatarService? AvatarService => ModuleViewModel?.AvatarService;

        /// <summary>
        /// Выбрать значок. Возвращает выбранное по «Готово» или null, если
        /// окно закрыли без выбора.
        /// </summary>
        public Task<CharacterIconChoice?> PickAsync(CharacterIconRequest request)
        {
            if (Library is not { } library) return Task.FromResult<CharacterIconChoice?>(null);

            Close(null);
            _pending = new TaskCompletionSource<CharacterIconChoice?>();
            Open(new IconPickerViewModel(library, request));
            return _pending.Task;
        }

        /// <summary>Открыть библиотеку значков — посмотреть, пополнить, убрать лишнее.</summary>
        public void ShowLibrary()
        {
            if (Library is not { } library) return;

            Close(null);
            Open(new IconPickerViewModel(library, null));
        }

        private void Open(IconPickerViewModel model)
        {
            _model = model;
            DataContext = model;
            IsVisible = true;

            // Фокус — на само окно, а не в поиск: поле не горит рамкой с
            // порога, а Esc и Enter работают сразу.
            PickerPanel.Focusable = true;
            Dispatcher.UIThread.Post(() => PickerPanel.Focus(), DispatcherPriority.Background);
        }

        private void Close(CharacterIconChoice? result)
        {
            _model?.Detach();
            _model = null;
            DataContext = null;
            IsVisible = false;
            SetDropHint(false);

            var pending = _pending;
            _pending = null;
            pending?.TrySetResult(result);
        }

        // ── Кнопки ───────────────────────────────────────────────────────

        private void OnOkClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            Close(_model?.IsPickMode == true ? _model.Result : null);
        }

        private void OnCancelClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            Close(null);
        }

        // Скрим блокирует модуль, но окно не закрывает — как в остальных
        // оверлеях модуля.
        private void OnScrimPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

        private void OnPanelKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close(null);
            }
            else if (e.Key == Key.Enter && e.Source is not TextBox { AcceptsReturn: true })
            {
                // Enter в поле имени своего значка записывает имя, а не закрывает окно.
                if (e.Source is TextBox box && box.FindLogicalAncestorOfType<Popup>() != null) return;

                e.Handled = true;
                Close(_model?.IsPickMode == true ? _model.Result : null);
            }
        }

        // ── Значки ───────────────────────────────────────────────────────

        private void OnTileClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is Control { DataContext: IconOptionViewModel option }) _model?.Choose(option);
        }

        /// <summary>Двойной щелчок — выбрать и сразу закрыть.</summary>
        private void OnTileDoubleTapped(object? sender, TappedEventArgs e)
        {
            if (_model is not { IsPickMode: true } model) return;
            if (sender is not Control { DataContext: IconOptionViewModel option }) return;

            e.Handled = true;
            model.Choose(option);
            Close(model.Result);
        }

        private void OnRemoveClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is not Control { DataContext: IconOptionViewModel option }) return;

            (sender as Control)?.FindLogicalAncestorOfType<Popup>()?.SetCurrentValue(Popup.IsOpenProperty, false);
            _model?.Remove(option);
        }

        private void OnRenameKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            (sender as Control)?.FindLogicalAncestorOfType<Popup>()?.SetCurrentValue(Popup.IsOpenProperty, false);
        }

        /// <summary>Имя записывается, когда окошко правки закрывают: Enter, щелчок мимо.</summary>
        private void OnRenameFlyoutClosed(object? sender, EventArgs e)
        {
            if (sender is not Flyout { Target: { DataContext: IconOptionViewModel option } }) return;
            if (option.EditName == option.Label) return;
            _model?.Rename(option, option.EditName);
        }

        private void OnClearSearchClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (_model == null) return;
            _model.Query = string.Empty;
            SearchBox.Focus();
        }

        // ── Цвета ────────────────────────────────────────────────────────

        /// <summary>Что красим: значок, подложку, рамку значка, рамку блока.</summary>
        private void OnColorTargetClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (_model != null && sender is Control { DataContext: IconColorTargetViewModel target })
                _model.ActiveTarget = target.Target;
        }

        /// <summary>Быстрый цвет — тому, что красим.</summary>
        private void OnPaletteSwatchClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (_model != null && sender is Control { DataContext: IconColorSwatchViewModel swatch })
                _model.ChooseColor(swatch.Hex);
        }

        /// <summary>Кружок «Свой цвет»: поставить свой цвет снова.</summary>
        private void OnOwnColorClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            _model?.ChooseOwnColor();
        }

        /// <summary>Особый вариант: бледная, залитая, без, цвета значка.</summary>
        private void OnSpecialColorClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (_model != null && sender is Control { DataContext: IconSpecialColorViewModel special })
                _model.ChooseSpecial(special);
        }

        // ── Свои картинки ────────────────────────────────────────────────

        private async void OnAddClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (Library is not { } library || AvatarService is not { } avatars) return;

            var files = await IconImport.PickFilesAsync(this, "Свои значки", allowMultiple: true);
            if (files.Count == 0) return;

            var added = await IconImport.AddToLibraryAsync(this, avatars, library, files);
            SelectAdded(added);
        }

        /// <summary>Добавленный значок сразу выбран: его и несли, чтобы поставить.</summary>
        private void SelectAdded(System.Collections.Generic.IReadOnlyList<CharacterIconEntry> added)
        {
            if (_model is not { IsPickMode: true } model || added.Count == 0) return;
            model.Icon = added[0].Icon;
        }

        private void OnDragOver(object? sender, DragEventArgs e)
        {
            if (CharactersReadOnlyGate.RejectDrop(sender, e)) return;

            var accepts = _model != null && IconImport.HasFiles(e);
            e.DragEffects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
            SetDropHint(accepts);
            e.Handled = true;
        }

        private void OnDragLeave(object? sender, DragEventArgs e)
        {
            SetDropHint(false);
            e.Handled = true;
        }

        private async void OnDrop(object? sender, DragEventArgs e)
        {
            if (CharactersReadOnlyGate.RejectDrop(sender, e)) return;

            e.Handled = true;
            SetDropHint(false);

            if (_model == null || Library is not { } library || AvatarService is not { } avatars) return;

            var files = IconImport.DroppedIconFiles(e);
            if (files.Count == 0) return;

            var added = await IconImport.AddToLibraryAsync(this, avatars, library, files);
            SelectAdded(added);
        }

        private void SetDropHint(bool value) => DropHint.IsVisible = value;
    }
}
