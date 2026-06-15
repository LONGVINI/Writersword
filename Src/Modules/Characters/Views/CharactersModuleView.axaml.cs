using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Services.UI;
using Writersword.Core.Services;
using Writersword.Modules.Characters.ViewModels;
using Writersword.Modules.Characters.Views.Tabs;
using Writersword.Src.Modules.Characters.Resources;

namespace Writersword.Modules.Characters.Views
{
    public partial class CharactersModuleView : UserControl
    {
        private static readonly ILogger _log = Log.ForContext<CharactersModuleView>();

        private IDisposable? _subscription;
        private IDisposable? _toastSubscription;
        private readonly List<IDisposable> _folderSubscriptions = new();

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
            // Регистрируем на TopLevel с Tunnel чтобы Ctrl+Z всегда перехватывался
            // независимо от того, где сейчас фокус (после RefreshAll фокус теряется).
            _topLevel?.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            AddHandler(TextBox.LostFocusEvent, OnTextBoxLostFocus, RoutingStrategies.Bubble);
            _log.Debug("CharactersModuleView attached to visual tree");
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _topLevel?.RemoveHandler(KeyDownEvent, OnKeyDown);
            _topLevel = null;
            CommitAllPendingEdits();
            RemoveHandler(TextBox.LostFocusEvent, OnTextBoxLostFocus);
            _log.Debug("CharactersModuleView detached");
        }

        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            _tabContent = this.FindControl<ContentControl>("TabContent");

            // кнопка закрытия тоста
            var dismissBtn = this.FindControl<Button>("ToastDismissButton");
            if (dismissBtn is not null)
                dismissBtn.Click += (_, _) =>
                {
                    if (DataContext is CharactersViewModel vm)
                        vm.HideUndoToast();
                };

            if (DataContext is CharactersViewModel vm2)
            {
                SwitchTab(vm2.MainTabIndex);
            }
            else
                SwitchTab(0);

            _log.Debug("CharactersModuleView loaded");
        }

        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            _subscription?.Dispose();
            _toastSubscription?.Dispose();

            foreach (var d in _folderSubscriptions) d.Dispose();
            _folderSubscriptions.Clear();

            _listView = null;
            _editView = null;
            _graphView = null;
            _templatesView = null;

            if (DataContext is CharactersViewModel vm)
            {
                vm.SearchFocusRequested += OnSearchFocusRequested;
                vm.FolderDeleteRequested += OnFolderDeleteRequested;
                _subscription = vm.WhenAnyValue(x => x.MainTabIndex).Subscribe(SwitchTab);

                // автоскрытие тоста через 4 секунды после появления
                _toastSubscription = vm.WhenAnyValue(x => x.UndoToastMessage)
                    .Where(msg => !string.IsNullOrEmpty(msg))
                    .Throttle(TimeSpan.FromSeconds(4))
                    .Subscribe(_ => Dispatcher.UIThread.Post(() => vm.HideUndoToast()));

                foreach (var folder in vm.Folders)
                    SubscribeToFolderCommentEditing(folder);

                vm.Folders.CollectionChanged += (_, args) =>
                {
                    if (args.NewItems is not null)
                        foreach (CharacterFolderViewModel f in args.NewItems)
                            SubscribeToFolderCommentEditing(f);
                };
            }
        }

        // ── Диалог подтверждения удаления папки ──────────────────────────
        // Вызывается из CharactersViewModel при DeleteFolderCommand.
        // Показывает системный диалог и при подтверждении выполняет фактическое удаление.

        private async void OnFolderDeleteRequested(string folderId, string folderName)
        {
            var dialogService = CoreServices.GetRequiredService<IDialogService>();
            var result = await dialogService.ShowMessageAsync(
                CharactersStrings.Dialog_DeleteFolder_Title,
                CharactersStrings.Dialog_DeleteFolder_Before + folderName + CharactersStrings.Dialog_DeleteFolder_After,
                MessageBoxType.Warning,
                MessageBoxButtons.YesNo
            );
            if (result == MessageBoxResult.Yes && DataContext is CharactersViewModel vm)
                vm.ConfirmDeleteFolderCommand.Execute(folderId).Subscribe();
        }

        private void SubscribeToFolderCommentEditing(CharacterFolderViewModel folder)
        {
            var sub = folder.WhenAnyValue(f => f.IsEditingComment)
                .Where(editing => editing)
                .Subscribe(_ =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        var box = FindDescendantWithDataContext<TextBox>(this, folder, "FolderCommentBox");
                        if (box is null || !box.IsVisible)
                        {
                            _log.Debug("FolderCommentEditing: box not visible for {Id}", folder.FolderId);
                            return;
                        }
                        _log.Debug("FolderCommentEditing: focusing FolderCommentBox for {Id}", folder.FolderId);
                        box.Focus();
                    }, DispatcherPriority.Render);
                });
            _folderSubscriptions.Add(sub);
        }

        private static T? FindDescendantWithDataContext<T>(Visual root, object dataContext, string name)
            where T : Control
        {
            foreach (var child in root.GetVisualChildren())
            {
                if (child is T typed && typed.Name == name)
                {
                    Visual? v = typed;
                    while (v is not null)
                    {
                        if (v is Control c && c.DataContext == dataContext)
                            return typed;
                        v = v.GetVisualParent();
                        if (ReferenceEquals(v, root)) break;
                    }
                }
                var found = FindDescendantWithDataContext<T>(child, dataContext, name);
                if (found is not null) return found;
            }
            return null;
        }

        private void SwitchTab(int index)
        {
            if (_tabContent is null) return;
            _log.Debug("SwitchTab: index={Index}", index);

            var vm = DataContext as CharactersViewModel;

            Control? content = index switch
            {
                0 => _listView ??= new CharactersListView(),
                1 => _editView ??= new CharacterEditView(),
                2 => _graphView ??= new CharactersGraphView { DataContext = vm?.GraphViewModel },
                3 => _templatesView ??= new CharactersTemplatesView { DataContext = vm?.TemplatesViewModel },
                _ => null
            };

            _tabContent.Content = content;
        }

        private void CommitAllPendingEdits()
        {
            if (DataContext is not CharactersViewModel vm) return;
            // Итерируем по копиям — команды могут модифицировать коллекции
            // прямо во время enumeration, что вызывает InvalidOperationException.
            foreach (var folder in vm.Folders.ToList())
            {
                if (folder.IsRenaming)
                    folder.ConfirmRenameCommand.Execute().Subscribe();
                if (folder.IsEditingComment)
                    folder.ConfirmCommentCommand.Execute().Subscribe();
                foreach (var character in folder.Characters.ToList())
                    if (character.IsBeingNamed)
                        vm.ConfirmInlineNameCommand.Execute(character.Id).Subscribe();
            }
        }

        // ── LostFocus ─────────────────────────────────────────────────────
        // Срабатывает когда FocusManager в MainWindowView снимает фокус с TextBox,
        // а также при Alt+Tab, сворачивании и т.д.
        // ReflectionBinding обновляет источник по LostFocus — принудительно
        // синхронизируем из src.Text до вызова команды чтобы избежать race condition.

        private void OnTextBoxLostFocus(object? sender, RoutedEventArgs e)
        {
            if (e.Source is not TextBox src) return;
            _log.Debug("OnTextBoxLostFocus: src={Name}", src.Name);

            var folderVm = FindAncestor<CharacterFolderViewModel>(src);
            if (folderVm is not null)
            {
                if (folderVm.IsRenaming)
                {
                    folderVm.Name = src.Text ?? folderVm.Name;
                    folderVm.ConfirmRenameCommand.Execute().Subscribe();
                }
                else if (folderVm.IsEditingComment)
                {
                    folderVm.Comment = src.Text ?? folderVm.Comment;
                    folderVm.ConfirmCommentCommand.Execute().Subscribe();
                }
                return;
            }

            var charVm = FindAncestor<CharacterListItemViewModel>(src);
            if (charVm?.IsBeingNamed == true && DataContext is CharactersViewModel mainVm)
            {
                charVm.InlineName = src.Text ?? string.Empty;
                mainVm.ConfirmInlineNameCommand.Execute(charVm.Id).Subscribe();
            }
        }

        // ── KeyDown ───────────────────────────────────────────────────────

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            // Проверяем что этот модуль сейчас в визуальном дереве и видим
            if (!IsEffectivelyVisible) return;

            // Ctrl+Z / Ctrl+Y перехватываем первыми — до TextBox-ов,
            // чтобы не было двойной обработки (TextBox тоже умеет Ctrl+Z).
            if (DataContext is CharactersViewModel vm)
            {
                if (e.Key == Key.Z && e.KeyModifiers == KeyModifiers.Control)
                {
                    if (vm.CanUndo) { vm.Undo(); vm.HideUndoToast(); e.Handled = true; return; }
                }
                if (e.Key == Key.Y && e.KeyModifiers == KeyModifiers.Control)
                {
                    if (vm.CanRedo) { vm.Redo(); e.Handled = true; return; }
                }
            }

            if (e.Source is not Control src) return;
            bool isEsc = e.Key == Key.Escape;
            bool isEnter = e.Key == Key.Return;
            if (!isEsc && !isEnter) return;

            var folderVm = FindAncestor<CharacterFolderViewModel>(src);
            if (folderVm is not null)
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

        // ── Helpers ───────────────────────────────────────────────────────

        private static T? FindAncestor<T>(Control ctrl) where T : class
        {
            Visual? v = ctrl;
            while (v is not null)
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