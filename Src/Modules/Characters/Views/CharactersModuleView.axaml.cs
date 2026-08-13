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

        private Panel? _tabContent;
        private bool _cardsProgressiveDone;

        // Вьюха сейчас в визуальном дереве. Нужен затем, что контекст данных
        // может приехать и до присоединения, и после него, а восстанавливать
        // список папок имеет смысл только когда есть и то и другое.
        private bool _attachedToTree;

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
            _attachedToTree = true;

            // Список папок очищается при отсоединении вьюхи, значит и
            // восстанавливать его нужно здесь же, при присоединении. Раньше
            // восстановление висело на Loaded, а это другое событие: оно
            // поднимается при первой загрузке контрола и при повторном
            // присоединении к дереву не срабатывает. Из-за этого возврат в
            // рабочий режим показывал «Персонажей нет» — данные были на
            // месте, но список так и оставался пустым до перезахода в проект.
            EnsureFoldersLoaded();

            _log.Debug("CharactersModuleView attached to visual tree");
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _attachedToTree = false;
            _topLevel?.RemoveHandler(KeyDownEvent, OnKeyDown);
            _topLevel = null;
            CommitAllPendingEdits();
            RemoveHandler(TextBox.LostFocusEvent, OnTextBoxLostFocus);
            UnsubscribeFromVm();

            // При переключении воркмода/доке вьюха отсоединяется, но VM и её
            // коллекция Folders остаются заполненными. При повторном attach
            // ItemsRepeater синхронно реализует и раскладывает все карточки одним
            // проходом — фриз на ~секунду ещё до того, как OnLoaded запустит
            // прогрессивный рефреш. Очищаем список здесь, чтобы повторный вход
            // начинался с пустого репитера и карточки наполнялись плавно.
            if (DataContext is CharactersViewModel vm)
                vm.PrepareForReattach();

            _log.Debug("CharactersModuleView detached");
        }

        // Снимает обработчики событий именно с той VM, на которую подписан ЭТОТ view —
        // при detach и при смене DataContext. Так на старых VM не остаётся живых
        // обработчиков, и удаление папки не плодит диалоги и не «перехватывается»
        // мёртвым обработчиком (который не выполнял бы реальное удаление).
        private void UnsubscribeFromVm()
        {
            if (_subscribedVm is null) return;
            _subscribedVm.SearchFocusRequested -= OnSearchFocusRequested;
            _subscribedVm.FolderDeleteRequested -= OnFolderDeleteRequested;
            _subscribedVm = null;
        }

        /// <summary>
        /// Вернуть список папок, если он пуст после отсоединения вьюхи.
        /// Данные при этом не перечитываются: список собирается заново из
        /// уже загруженной вьюмодели и сервиса.
        ///
        /// Проверка на пустоту обязательна: при первом открытии список уже
        /// наполняет SetCustomData, и повторный запуск отменил бы его на
        /// середине — карточки начали бы строиться дважды.
        /// </summary>
        private void EnsureFoldersLoaded()
        {
            if (DataContext is not CharactersViewModel vm) return;
            if (vm.Folders.Count > 0) return;

            _ = vm.RequestProgressiveRefreshAsync();
        }

        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            _tabContent = this.FindControl<Panel>("TabContent");

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

                // Прогрессивная прогрузка при подключении вьюхи (workmode switch,
                // dock move): без неё повторный вход строит все реализованные
                // карточки одним проходом и замораживает UI. Для вкладки «Персонажи»
                // (index 0) её уже запустил SwitchTab, поэтому здесь — только для
                // остальных вкладок, чтобы не гонять двойную загрузку.
                if (vm2.MainTabIndex != 0)
                    _ = vm2.RequestProgressiveRefreshAsync();
            }
            else
                SwitchTab(0);

            _log.Debug("CharactersModuleView loaded");
        }

        // VM, на события которой реально подписан ЭТОТ view-инстанс. Нужна, чтобы
        // отписаться именно от неё, а не от текущего DataContext: при смене DataContext
        // или пересоздании view иначе на старой VM остаётся живой обработчик, и удаление
        // папки плодит диалоги-дубли. Это и есть причина, а не симптом.
        private CharactersViewModel? _subscribedVm;

        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            UnsubscribeFromVm();

            _subscription?.Dispose();
            _toastSubscription?.Dispose();

            foreach (var d in _folderSubscriptions) d.Dispose();
            _folderSubscriptions.Clear();

            _listView = null;
            _editView = null;
            _graphView = null;
            _templatesView = null;

            // Контекст мог приехать уже после присоединения к дереву — тогда
            // список папок восстанавливается здесь.
            if (_attachedToTree) EnsureFoldersLoaded();

            if (DataContext is CharactersViewModel vm)
            {
                // UnsubscribeFromVm() выше уже снял обработчики с прошлой VM, поэтому здесь
                // просто подписываемся и запоминаем VM как текущую подписанную.
                vm.SearchFocusRequested += OnSearchFocusRequested;
                vm.FolderDeleteRequested += OnFolderDeleteRequested;
                _subscribedVm = vm;
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

            if (content is null) return;

            // Вкладки не пересоздаются при переключении, а живут одновременно и
            // переключаются видимостью. Тяжёлая «Персонажи» (тысячи карточек) при
            // уходе НЕ уничтожается — иначе разбор всех карточек = долгий фриз ровно
            // в момент клика на другую вкладку (то самое «зависает при нажатии на
            // Editor»). При возврате она уже построена — переход мгновенный. Скрытая
            // вкладка не мерится и не рисуется, процессорной цены нет.
            if (!_tabContent.Children.Contains(content))
                _tabContent.Children.Add(content);

            foreach (var child in _tabContent.Children)
                child.IsVisible = ReferenceEquals(child, content);

            // Первый показ «Персонажей»: данные уже могут быть загружены, и тогда
            // карточки построились бы все разом и подвисли. Поэтому один раз строим
            // прогрессивно (чистим Folders и наливаем по одной). Вью карточек только
            // что создана — карточек ещё нет, поэтому чистка дешёвая. Дальше вкладка
            // живёт построенной, повторные заходы мгновенные, без пересборки.
            if (index == 0 && vm != null && !_cardsProgressiveDone)
            {
                _cardsProgressiveDone = true;
                vm.PrepareForReattach();
                _ = vm.RequestProgressiveRefreshAsync();
            }
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