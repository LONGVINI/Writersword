using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ReactiveUI;
using System;
using System.Reactive.Linq;

namespace Writersword.Modules.Characters.Views
{
    public partial class CharactersView : UserControl
    {
        private Grid? _tab0;
        private Grid? _tab1;
        private Control? _tab2;
        private Control? _tab3;
        private IDisposable? _subscription;

        public CharactersView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            DataContextChanged += OnDataContextChanged;
        }

        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            _tab0 = this.FindControl<Grid>("Tab0Panel");
            _tab1 = this.FindControl<Grid>("Tab1Panel");
            _tab2 = this.FindControl<Control>("Tab2Panel");
            _tab3 = this.FindControl<Control>("Tab3Panel");

            if (DataContext is ViewModels.CharactersViewModel vm)
                SwitchTab(vm.MainTabIndex);
            else
                SwitchTab(0);

            AddHandler(TextBox.LostFocusEvent, OnTextBoxLostFocus, RoutingStrategies.Bubble);
            AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
            AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        }

        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            _subscription?.Dispose();
            if (DataContext is ViewModels.CharactersViewModel vm)
            {
                vm.SearchFocusRequested += OnSearchFocusRequested;
                _subscription = vm.WhenAnyValue(x => x.MainTabIndex).Subscribe(SwitchTab);
            }
        }

        private void SwitchTab(int index)
        {
            if (_tab0 == null) return;

            var ribbon = this.FindControl<Control>("Tab0Ribbon");
            if (ribbon != null) ribbon.IsVisible = index == 0;

            _tab0.IsVisible = index == 0;
            if (_tab1 != null) _tab1.IsVisible = index == 1;
            if (_tab2 != null) _tab2.IsVisible = index == 2;
            if (_tab3 != null) _tab3.IsVisible = index == 3;
        }

        // ── PointerPressed: клик мимо TextBox — сбросить фокус ───────────

        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            if (focused is not TextBox) return;

            // Проверяем через визуальное дерево — e.Source в Tunnel может быть
            // внутренним элементом TextBox (TextPresenter, ScrollViewer и т.д.)
            Visual? v = e.Source as Visual;
            while (v != null)
            {
                if (v is TextBox) return;
                v = v.GetVisualParent();
            }

            TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
        }

        // ── LostFocus: сохранить и закрыть поле ──────────────────────────

        private void OnTextBoxLostFocus(object? sender, RoutedEventArgs e)
        {
            if (e.Source is not TextBox src) return;

            var folderVm = FindAncestor<ViewModels.CharacterFolderViewModel>(src);
            if (folderVm != null)
            {
                if (folderVm.IsRenaming)
                {
                    folderVm.ConfirmRenameCommand.Execute().Subscribe();
                    return;
                }
                if (folderVm.IsEditingComment)
                {
                    folderVm.ConfirmCommentCommand.Execute().Subscribe();
                    return;
                }
            }

            var charVm = FindAncestor<ViewModels.CharacterListItemViewModel>(src);
            if (charVm?.IsBeingNamed == true &&
                DataContext is ViewModels.CharactersViewModel mainVm)
            {
                mainVm.ConfirmInlineNameCommand.Execute(charVm.Id).Subscribe();
            }
        }

        // ── KeyDown: ESC/Enter ────────────────────────────────────────────

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Source is not Control src) return;
            bool isEsc = e.Key == Key.Escape;
            bool isEnter = e.Key == Key.Return;
            if (!isEsc && !isEnter) return;

            var folderVm = FindAncestor<ViewModels.CharacterFolderViewModel>(src);
            if (folderVm != null)
            {
                if (folderVm.IsRenaming)
                {
                    folderVm.ConfirmRenameCommand.Execute().Subscribe();
                    e.Handled = true;
                    return;
                }
                if (folderVm.IsEditingComment)
                {
                    folderVm.ConfirmCommentCommand.Execute().Subscribe();
                    e.Handled = true;
                    return;
                }
            }

            var charVm = FindAncestor<ViewModels.CharacterListItemViewModel>(src);
            if (charVm?.IsBeingNamed == true &&
                DataContext is ViewModels.CharactersViewModel mainVm)
            {
                if (isEnter)
                    mainVm.ConfirmInlineNameCommand.Execute(charVm.Id).Subscribe();
                else
                    mainVm.CancelInlineNameCommand.Execute(charVm.Id).Subscribe();
                e.Handled = true;
            }
        }

        // ── Вспомогательный метод ─────────────────────────────────────────

        private static T? FindAncestor<T>(Control ctrl) where T : class
        {
            Visual? v = ctrl;
            while (v != null)
            {
                if (v is Control c && c.DataContext is T result)
                    return result;
                v = v.GetVisualParent();
            }
            return null;
        }

        private void OnSearchFocusRequested()
            => this.FindControl<TextBox>("SearchTextBox")?.Focus();
    }
}