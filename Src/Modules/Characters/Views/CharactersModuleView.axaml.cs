using System;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ReactiveUI;
using Serilog;
using Writersword.Modules.Characters.ViewModels;
using Writersword.Modules.Characters.Views.Tabs;

namespace Writersword.Modules.Characters.Views
{
    public partial class CharactersModuleView : UserControl
    {
        private static readonly ILogger _log = Log.ForContext<CharactersModuleView>();

        private IDisposable? _subscription;
        private TopLevel? _topLevel;

        private CharactersListView? _listView;
        private CharacterEditView? _editView;
        private CharactersGraphView? _graphView;
        private CharactersTemplatesView? _templatesView;

        private ContentControl? _tabContent;

        public CharactersModuleView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            DataContextChanged += OnDataContextChanged;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _topLevel = TopLevel.GetTopLevel(this);
            AddHandler(TextBox.LostFocusEvent, OnTextBoxLostFocus, RoutingStrategies.Bubble);
            AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
            AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
            _log.Debug("CharactersModuleView attached to {Root}", e.Root?.GetType().Name);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _topLevel = null;
            CommitAllPendingEdits();
            RemoveHandler(TextBox.LostFocusEvent, OnTextBoxLostFocus);
            RemoveHandler(KeyDownEvent, OnKeyDown);
            RemoveHandler(PointerPressedEvent, OnPointerPressed);
            _log.Debug("CharactersModuleView detached");
        }

        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            _tabContent = this.FindControl<ContentControl>("TabContent");
            if (DataContext is CharactersViewModel vm)
                SwitchTab(vm.MainTabIndex);
            else
                SwitchTab(0);
            _log.Debug("CharactersModuleView loaded");
        }

        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            _subscription?.Dispose();
            _listView = null;
            _editView = null;
            _graphView = null;
            _templatesView = null;

            if (DataContext is CharactersViewModel vm)
            {
                vm.SearchFocusRequested += OnSearchFocusRequested;
                _subscription = vm.WhenAnyValue(x => x.MainTabIndex).Subscribe(SwitchTab);
            }
        }

        private void SwitchTab(int index)
        {
            if (_tabContent == null) return;
            _log.Debug("SwitchTab: index={Index}", index);

            var vm = DataContext as CharactersViewModel;

            Control? content = index switch
            {
                0 => _listView      ??= new CharactersListView(),
                1 => _editView      ??= new CharacterEditView(),
                2 => _graphView     ??= new CharactersGraphView { DataContext = vm?.GraphViewModel },
                3 => _templatesView ??= new CharactersTemplatesView { DataContext = vm?.TemplatesViewModel },
                _ => null
            };

            _tabContent.Content = content;
        }

        private void CommitAllPendingEdits()
        {
            if (DataContext is not CharactersViewModel vm) return;
            foreach (var folder in vm.Folders)
            {
                if (folder.IsRenaming)
                    folder.ConfirmRenameCommand.Execute().Subscribe();
                if (folder.IsEditingComment)
                    folder.ConfirmCommentCommand.Execute().Subscribe();
                foreach (var character in folder.Characters)
                    if (character.IsBeingNamed)
                        vm.ConfirmInlineNameCommand.Execute(character.Id).Subscribe();
            }
        }

        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            var focused = _topLevel?.FocusManager?.GetFocusedElement();
            if (focused is not TextBox) return;
            Visual? v = e.Source as Visual;
            while (v != null)
            {
                if (v is TextBox) return;
                v = v.GetVisualParent();
            }
            _topLevel?.FocusManager?.ClearFocus();
        }

        private void OnTextBoxLostFocus(object? sender, RoutedEventArgs e)
        {
            if (e.Source is not TextBox src) return;
            _log.Debug("OnTextBoxLostFocus: src={Name}", src.Name);

            var folderVm = FindAncestor<CharacterFolderViewModel>(src);
            if (folderVm != null)
            {
                if (folderVm.IsRenaming) { folderVm.ConfirmRenameCommand.Execute().Subscribe(); return; }
                if (folderVm.IsEditingComment) { folderVm.ConfirmCommentCommand.Execute().Subscribe(); return; }
            }

            var charVm = FindAncestor<CharacterListItemViewModel>(src);
            if (charVm?.IsBeingNamed == true && DataContext is CharactersViewModel mainVm)
                mainVm.ConfirmInlineNameCommand.Execute(charVm.Id).Subscribe();
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Source is not Control src) return;
            bool isEsc = e.Key == Key.Escape;
            bool isEnter = e.Key == Key.Return;
            if (!isEsc && !isEnter) return;

            var folderVm = FindAncestor<CharacterFolderViewModel>(src);
            if (folderVm != null)
            {
                if (folderVm.IsRenaming)
                {
                    folderVm.ConfirmRenameCommand.Execute().Subscribe();
                    e.Handled = true; return;
                }
                if (folderVm.IsEditingComment)
                {
                    folderVm.ConfirmCommentCommand.Execute().Subscribe();
                    e.Handled = true; return;
                }
            }

            var charVm = FindAncestor<CharacterListItemViewModel>(src);
            if (charVm?.IsBeingNamed == true && DataContext is CharactersViewModel mainVm)
            {
                if (isEnter) mainVm.ConfirmInlineNameCommand.Execute(charVm.Id).Subscribe();
                else mainVm.CancelInlineNameCommand.Execute(charVm.Id).Subscribe();
                e.Handled = true;
            }
        }

        private static T? FindAncestor<T>(Control ctrl) where T : class
        {
            Visual? v = ctrl;
            while (v != null)
            {
                if (v is Control c && c.DataContext is T result) return result;
                v = v.GetVisualParent();
            }
            return null;
        }

        private void OnSearchFocusRequested()
            => _listView?.FocusSearch();
    }
}
