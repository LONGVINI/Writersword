using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Writersword.Modules.Characters.ViewModels.Templates;
using Writersword.Modules.Characters.Views;

namespace Writersword.Modules.Characters.Views.Tabs
{
    /// <summary>
    /// Шаблоны блоками. Перетаскивание — своей обработкой указателя, а не
    /// DragDrop. Свои блоки двигают ровно как карточки персонажей: зажать в
    /// любом месте блока и повести — блок приподнимается, за курсором едет его
    /// призрак, соседи расступаются и доезжают до новых мест. Значки анкет
    /// двигают внутри своего блока: за курсором призрак, полоска показывает
    /// место вставки.
    /// </summary>
    public partial class CharactersTemplatesView : UserControl
    {
        private static readonly ILogger _logger = Log.ForContext<CharactersTemplatesView>();

        public CharactersTemplatesView()
        {
            InitializeComponent();

            // Часть «Шаблоны» живёт рядом с «Анкетами» и переключается
            // видимостью: при показе анкеты могли быть уже другими.
            PropertyChanged += (_, e) =>
            {
                if (e.Property != IsVisibleProperty) return;

                if (e.NewValue is true) Vm?.Refresh();
                else CancelDrag();
            };

            // Щелчок мимо блоков — по пустому месту между ними и под ними —
            // снимает выбор. Слушаются и разобранные нажатия: кнопки внутри
            // блоков гасят событие, а решать здесь всё равно по месту щелчка.
            BlocksScroll.AddHandler(PointerPressedEvent, OnBlocksAreaPressed,
                Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);

            // Перетаскивание блоков слушается на погружении, как у карточек
            // персонажей: нажатие видно раньше кнопок и полей блока, но не
            // гасится — быстрый щелчок доходит до них как обычно.
            AddHandler(PointerPressedEvent, OnBlockDragPressed, RoutingStrategies.Tunnel);
            AddHandler(PointerMovedEvent, OnBlockDragMoved, RoutingStrategies.Tunnel);
            AddHandler(PointerReleasedEvent, OnBlockDragReleased, RoutingStrategies.Tunnel);
            AddHandler(PointerWheelChangedEvent, OnBlockDragWheel, RoutingStrategies.Tunnel);
            AddHandler(PointerCaptureLostEvent, OnBlockDragCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
            AddHandler(KeyDownEvent, OnBlockDragKeyDown, RoutingStrategies.Tunnel);
        }

        private CharactersTemplatesViewModel? Vm => DataContext as CharactersTemplatesViewModel;

        private CharactersTemplatesViewModel? _subscribed;

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);

            if (_subscribed != null) _subscribed.CopyCreated -= OnCopyCreated;
            _subscribed = Vm;
            if (_subscribed != null) _subscribed.CopyCreated += OnCopyCreated;
        }

        /// <summary>Правка встроенного шаблона создала копию — показать её.</summary>
        private void OnCopyCreated(string templateId)
        {
            if (Vm?.FindBlock(templateId) is { } block) ShowBlock(block, openCustomize: false);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);

            // Анкеты могли переименовать или дополнить на их вкладке.
            Vm?.Refresh();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            CancelDrag();
            base.OnDetachedFromVisualTree(e);
        }

        private static TemplateBlockViewModel? BlockOf(object? sender) =>
            (sender as Control)?.DataContext as TemplateBlockViewModel;

        /// <summary>
        /// Показанные разделы: модель, список блоков раздела и сам раздел —
        /// шапка вместе с блоками.
        /// </summary>
        private IEnumerable<(TemplateFolderViewModel Folder, ItemsControl List, Control Section)> Sections()
        {
            if (Vm is not { } vm) yield break;

            for (int i = 0; i < vm.Folders.Count; i++)
            {
                if (FoldersList.ContainerFromIndex(i) is not Control section) continue;
                var list = section.GetVisualDescendants()
                    .OfType<ItemsControl>()
                    .FirstOrDefault(c => c.Classes.Contains("tpl-blocks"));
                if (list == null) continue;
                yield return (vm.Folders[i], list, section);
            }
        }

        private Control? ContainerOf(TemplateBlockViewModel block)
        {
            foreach (var (_, list, _) in Sections())
                if (list.ContainerFromItem(block) is Control container)
                    return container;
            return null;
        }

        private static void ClosePopupOf(object? sender) =>
            (sender as Control)?.FindLogicalAncestorOfType<Popup>()?.Close();

        // ── Панель ───────────────────────────────────────────────────────

        private void OnNewTemplateClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (Vm?.CreateTemplate() is { } block) ShowBlock(block, openCustomize: true);
        }

        /// <summary>Новая папка — в конце папок, список прокручивается к ней, поле названия открыто.</summary>
        private void OnNewFolderClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (Vm?.CreateFolder() is not { } folder) return;

            Dispatcher.UIThread.Post(() =>
            {
                foreach (var (model, _, section) in Sections())
                    if (ReferenceEquals(model, folder))
                        section.BringIntoView();
            }, DispatcherPriority.Background);
        }

        /// <summary>
        /// Прокрутить к блоку; только что созданному — сразу поставить курсор
        /// в его название: новый шаблон почти всегда сразу переименовывают.
        /// </summary>
        private void ShowBlock(TemplateBlockViewModel block, bool openCustomize)
        {
            // Свёрнутая папка раскрывается: иначе блок встал бы туда, где его
            // не видно.
            if (Vm?.FolderOf(block) is { IsExpanded: false } folder)
                folder.IsExpanded = true;

            Dispatcher.UIThread.Post(() =>
            {
                if (ContainerOf(block) is not { } container) return;

                container.BringIntoView();
                if (!openCustomize) return;

                var name = container.GetVisualDescendants()
                    .OfType<TextBox>()
                    .FirstOrDefault(b => b.Classes.Contains("tpl-name") && b.IsEffectivelyVisible);
                if (name == null) return;

                name.Focus();
                name.SelectAll();
            }, DispatcherPriority.Background);
        }

        // ── Блок ─────────────────────────────────────────────────────────

        /// <summary>
        /// Щелчок мимо блоков снимает выбор: ни один шаблон не живой. Щелчок
        /// по полосе прокрутки выбор не трогает — это не «мимо», а прокрутка.
        /// </summary>
        private void OnBlocksAreaPressed(object? sender, PointerPressedEventArgs e)
        {
            if (Vm is not { ActiveBlock: not null } vm) return;
            if (!e.GetCurrentPoint(BlocksScroll).Properties.IsLeftButtonPressed) return;

            for (var v = e.Source as Visual; v != null && !ReferenceEquals(v, BlocksScroll); v = v.GetVisualParent())
            {
                if (v is ScrollBar) return;
                if (v is Border { Classes: var classes } && classes.Contains("tpl-block")) return;
            }

            vm.Activate(null);
        }

        /// <summary>Щелчок по блоку делает его выбранным — живым.</summary>
        private void OnBlockPressed(object? sender, PointerPressedEventArgs e)
        {
            if (BlockOf(sender) is not { } block || Vm is not { } vm) return;
            if (!e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed) return;
            if (!block.IsActive) vm.Activate(block);
        }

        /// <summary>
        /// Щелчок по картинке шаблона — сразу окно значков: значок, цвет,
        /// подложка, рамка, свои картинки. У встроенного шаблона выбранное
        /// достаётся его своей копии — её заводит сама правка.
        ///
        /// Своя картинка шаблона закрывает значок. Если в окне значков что-то
        /// поменяли, картинка убирается: на плитке должно встать ровно то, что
        /// было в превью.
        /// </summary>
        private async void OnImageClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (BlockOf(sender) is not { } block || Vm is not { } vm) return;
            if (Icons.IconPickerOverlay.Find(this) is not { } picker) return;

            if (!block.IsActive) vm.Activate(block);

            var choice = await picker.PickAsync(new Models.CharacterIconRequest(
                "Значок шаблона", block.Icon, block.IconColor, AllowColor: true,
                Background: block.IconBackground,
                Border: block.IconBorder,
                Look: Models.CharacterIconLook.Tile,
                PreviewTitle: block.Name,
                PreviewDescription: block.Description,
                BlockFrame: block.FrameColor));
            if (choice == null) return;

            var hadImage = block.HasImage;
            var changedId = vm.SetIconAndColor(block.Id, choice.Icon, choice.Color, choice.Background, choice.Border,
                choice.BlockFrame);
            if (changedId != null && hadImage) vm.ClearImage(changedId);
        }

        // ── Название и описание ──────────────────────────────────────────
        //
        // Поля прямо в шапке блока. Запись — по Enter или уходу из поля,
        // одной правкой на оба: у встроенного шаблона каждая правка заводит
        // копию. Esc возвращает записанное.

        private void OnInlineGotFocus(object? sender, FocusChangedEventArgs e)
        {
            if (BlockOf(sender) is not { } block || Vm is not { } vm) return;
            if (!block.IsActive) vm.Activate(block);
            block.BeginTextEdit();
        }

        private void OnDescriptionIconClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is Button { Parent: Panel slot })
                slot.Children.OfType<TextBox>().FirstOrDefault()?.Focus();
        }

        private void OnInlineLostFocus(object? sender, FocusChangedEventArgs e)
        {
            BlockOf(sender)?.CommitTextEdit();
        }

        private void OnInlineKeyDown(object? sender, KeyEventArgs e)
        {
            if (BlockOf(sender) is not { } block) return;

            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                block.CommitTextEdit();
                LeaveInline();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                block.CancelTextEdit();
                LeaveInline();
            }
        }

        // У IFocusManager нет ClearFocus: фокус уводится на саму вкладку —
        // поле перестаёт быть в фокусе, ввод визуально завершён.
        private void LeaveInline()
        {
            Focusable = true;
            Focus();
        }

        // ── Картинка броском ─────────────────────────────────────────────
        //
        // Картинку шаблона бросают прямо на плитку картинки. У встроенного
        // шаблона она достаётся его своей копии: сам он не меняется.

        private void OnImageDragOver(object? sender, DragEventArgs e)
        {
            if (CharactersReadOnlyGate.RejectDrop(sender, e)) return;

            var accepts = BlockOf(sender) != null && Vm is { CanUseImages: true } && Icons.IconImport.HasFiles(e);
            e.DragEffects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
            (sender as Control)?.Classes.Set("drop", accepts);
            e.Handled = true;
        }

        private void OnImageDragLeave(object? sender, DragEventArgs e)
        {
            (sender as Control)?.Classes.Set("drop", false);
            e.Handled = true;
        }

        private async void OnImageDrop(object? sender, DragEventArgs e)
        {
            if (CharactersReadOnlyGate.RejectDrop(sender, e)) return;

            e.Handled = true;
            (sender as Control)?.Classes.Set("drop", false);
            if (BlockOf(sender) is not { } block || Vm is not { CanUseImages: true } vm) return;

            var file = Icons.IconImport.DroppedIconFiles(e)
                .FirstOrDefault(f => !f.Name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase));
            if (file == null) return;

            ClosePopupOf(sender);

            try
            {
                await using var stream = await file.OpenReadAsync();
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory);

                // Встроенный шаблон не меняется — картинку получает его копия.
                var targetId = block.Id;
                if (!block.IsEditable)
                {
                    if (vm.DuplicateTemplate(block.Id) is not { } copy) return;
                    targetId = copy.Id;
                    ShowBlock(copy, openCustomize: false);
                }

                await vm.SetImageAsync(targetId, memory.ToArray(), file.Name);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Template image drop failed");
            }
        }

        /// <summary>Картинка шаблона из файла — из меню правого щелчка по плитке.</summary>
        private async void OnPickImageClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (BlockOf(sender) is not { } block || Vm is not { CanUseImages: true } vm) return;

            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage == null) return;

            try
            {
                var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Картинка шаблона",
                    AllowMultiple = false,
                    FileTypeFilter = new List<FilePickerFileType> { FilePickerFileTypes.ImageAll }
                });
                if (files == null || files.Count == 0) return;

                await using var stream = await files[0].OpenReadAsync();
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory);

                // Встроенный шаблон не меняется — картинку получает его копия.
                var targetId = block.Id;
                if (!block.IsEditable)
                {
                    if (vm.DuplicateTemplate(block.Id) is not { } copy) return;
                    targetId = copy.Id;
                    ShowBlock(copy, openCustomize: false);
                }

                await vm.SetImageAsync(targetId, memory.ToArray(), files[0].Name);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Template image pick failed");
            }
        }

        private void OnClearImageClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (BlockOf(sender) is { } block) Vm?.ClearImage(block.Id);
        }

        private void OnExpandClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            BlockOf(sender)?.ToggleExpanded();
        }

        private void OnMoreClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            BlockOf(sender)?.ToggleExpanded();
        }

        /// <summary>
        /// Дублировать: копия встаёт в конец своих шаблонов, становится
        /// выбранной, список прокручивается к ней, а курсор встаёт в её
        /// название — копию почти всегда сразу переименовывают.
        /// </summary>
        private void OnDuplicateClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (BlockOf(sender) is { } block && Vm?.DuplicateTemplate(block.Id) is { } copy)
                ShowBlock(copy, openCustomize: true);
        }

        private void OnDeleteClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            ClosePopupOf(sender);
            if (BlockOf(sender) is { } block) Vm?.DeleteTemplate(block.Id);
        }

        /// <summary>Скрыть шаблон или вернуть: скрытый стоит на месте бледнее, но поиск и списки его не видят.</summary>
        private void OnHideClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (BlockOf(sender) is { } block) Vm?.ToggleHidden(block);
        }

        // ── Папки ────────────────────────────────────────────────────────

        private static TemplateFolderViewModel? FolderOfSender(object? sender) =>
            (sender as Control)?.DataContext as TemplateFolderViewModel;

        /// <summary>Щелчок по шапке делает папку текущей: новый шаблон встанет в неё.</summary>
        private void OnFolderHeadPressed(object? sender, PointerPressedEventArgs e)
        {
            if (FolderOfSender(sender) is not { } folder) return;
            if (!e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed) return;
            Vm?.SelectFolder(folder);
        }

        private void OnFolderRenameKeyDown(object? sender, KeyEventArgs e)
        {
            if (FolderOfSender(sender) is not { } folder) return;

            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                folder.ConfirmRename();
                LeaveInline();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                folder.CancelRename();
                LeaveInline();
            }
        }

        private void OnFolderRenameLostFocus(object? sender, FocusChangedEventArgs e) =>
            FolderOfSender(sender)?.ConfirmRename();

        private void OnFolderCommentKeyDown(object? sender, KeyEventArgs e)
        {
            if (FolderOfSender(sender) is not { } folder) return;

            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                folder.ConfirmComment();
                LeaveInline();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                folder.CancelComment();
                LeaveInline();
            }
        }

        private void OnFolderCommentLostFocus(object? sender, FocusChangedEventArgs e) =>
            FolderOfSender(sender)?.ConfirmComment();

        private void OnDeleteFolderClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            ClosePopupOf(sender);
            if (FolderOfSender(sender) is { IsCustom: true } folder) Vm?.DeleteFolder(folder.Id);
        }

        // ── Анкеты в блоке ───────────────────────────────────────────────

        private FlyoutBase? _pickerFlyout;
        private string? _pickerTemplateId;

        private void OnPickerOpening(object? sender, EventArgs e)
        {
            if (sender is not FlyoutBase { Target: { DataContext: TemplateBlockViewModel block } } flyout) return;

            _pickerFlyout = flyout;
            _pickerTemplateId = block.Id;
            Vm?.Activate(block);

            block.PickerQuery = string.Empty;
            block.RefreshPicker();
        }

        /// <summary>Меню открылось — курсор сразу в поиске: можно печатать, не целясь мышью.</summary>
        private void OnPickerOpened(object? sender, EventArgs e)
        {
            if (sender is not Flyout { Content: Control content }) return;

            Dispatcher.UIThread.Post(() =>
            {
                var search = content.GetLogicalDescendants()
                    .OfType<TextBox>()
                    .FirstOrDefault(t => t.Classes.Contains("picker-search"));
                if (search == null) return;

                search.Focus();
                search.SelectAll();
            }, DispatcherPriority.Loaded);
        }

        private void OnPickAnketaClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if ((sender as Control)?.DataContext is not TemplatePickItemViewModel item) return;
            if (Vm is not { } vm || _pickerTemplateId is not { } templateId) return;

            var target = vm.AddAnketa(templateId, item.Id);
            if (target == null) return;

            if (target != templateId)
            {
                // Встроенный шаблон дал копию: её меню — у её блока.
                _pickerFlyout?.Hide();
                _pickerTemplateId = null;
                return;
            }

            // Меню остаётся открытым: в шаблон обычно добавляют несколько анкет подряд.
            vm.FindBlock(templateId)?.RefreshPicker();
        }

        /// <summary>
        /// Новая анкета прямо из шаблона: открывается в редакторе анкет и
        /// после первого сохранения сама встаёт в этот шаблон.
        /// </summary>
        private void OnNewAnketaForTemplateClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;

            var templateId = _pickerTemplateId;
            _pickerFlyout?.Hide();
            _pickerTemplateId = null;

            if (templateId != null) Vm?.RequestNewAnketa(templateId);
        }

        private void OnRemoveAnketaClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if ((sender as Control)?.DataContext is TemplateAnketaItemViewModel item)
                Vm?.RemoveAnketa(item.TemplateId, item.Id);
        }

        /// <summary>Строка меню глубины у значка анкеты: уровень встаёт в шаблон, меню закрывается.</summary>
        private void OnTemplateDepthChoiceClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is not Control control || control.DataContext is not TemplateDepthChoiceViewModel choice) return;

            control.FindLogicalAncestorOfType<Popup>()?.SetCurrentValue(Popup.IsOpenProperty, false);
            Vm?.SetAnketaDepth(choice.TemplateId, choice.AnketaId, choice.Level == 0 ? null : choice.Level);
        }

        private void OnEditAnketaClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if ((sender as Control)?.DataContext is TemplateAnketaItemViewModel item)
                Vm?.OpenAnketa(item.Id);
        }

        private void OnIconDoubleTapped(object? sender, TappedEventArgs e)
        {
            e.Handled = true;
            if ((sender as Control)?.DataContext is TemplateAnketaItemViewModel item)
                Vm?.OpenAnketa(item.Id);
        }

        // ── Перетаскивание значков анкет ─────────────────────────────────

        private enum DragKind { Icon }

        private sealed class DragState
        {
            public required DragKind Kind { get; init; }
            public required Control Source { get; init; }
            public required Point Start { get; init; }
            public TemplateAnketaItemViewModel? Icon { get; init; }
            public bool Active { get; set; }
            public int Index { get; set; } = -1;
        }

        private DragState? _drag;

        private void BeginGhost(string? icon, string? color, string text,
            string? background = null, Models.CharacterIconLook look = Models.CharacterIconLook.Badge,
            string? border = null)
        {
            GhostBadge.Icon = icon;
            GhostBadge.IconColor = color;
            GhostBadge.IconBackground = background;
            GhostBadge.IconBorder = border;
            GhostBadge.Look = look;
            GhostText.Text = text;
            Ghost.IsVisible = true;
        }

        private void MoveGhost(Point p)
        {
            Canvas.SetLeft(Ghost, p.X + 10);
            Canvas.SetTop(Ghost, p.Y + 6);
        }

        private void FinishDrag(bool commit)
        {
            var drag = _drag;
            _drag = null;
            if (drag == null) return;

            Ghost.IsVisible = false;
            InsertMark.IsVisible = false;

            if (!drag.Active) return;

            if (drag.Icon != null) drag.Icon.IsDragSource = false;

            if (!commit || drag.Index < 0 || Vm is not { } vm) return;

            if (drag.Kind == DragKind.Icon && drag.Icon != null)
                vm.MoveAnketa(drag.Icon.TemplateId, drag.Icon.Id, drag.Index);
        }

        private void CancelDrag()
        {
            FinishDrag(commit: false);
            CancelBlockDrag();
        }

        // Значок анкеты — внутри выбранного блока

        private void OnIconPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Control tile || tile.DataContext is not TemplateAnketaItemViewModel item) return;
            if (!e.GetCurrentPoint(tile).Properties.IsLeftButtonPressed) return;

            if (Vm is not { } vm || vm.FindBlock(item.TemplateId) is not { } block) return;

            if (!block.IsActive) vm.Activate(block);

            // Встроенный шаблон не переставляют: порядок его анкет задан.
            if (!block.IsEditable) return;

            _drag = new DragState
            {
                Kind = DragKind.Icon,
                Source = tile,
                Start = e.GetPosition(DragLayer),
                Icon = item
            };

            e.Pointer.Capture(tile);
            e.Handled = true;
        }

        private void OnIconMoved(object? sender, PointerEventArgs e)
        {
            if (_drag is not { Kind: DragKind.Icon, Icon: { } item } drag) return;

            var p = e.GetPosition(DragLayer);
            if (!drag.Active)
            {
                if (Math.Abs(p.X - drag.Start.X) < 4 && Math.Abs(p.Y - drag.Start.Y) < 4) return;

                drag.Active = true;
                item.IsDragSource = true;
                BeginGhost(item.Icon, item.IconColor, item.Name, item.IconBackground, border: item.IconBorder);
            }

            MoveGhost(p);
            PlaceIconMark(drag, item, p);
            e.Handled = true;
        }

        private void OnIconReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_drag is not { Kind: DragKind.Icon }) return;

            FinishDrag(commit: true);
            e.Pointer.Capture(null);
            e.Handled = true;
        }

        private void OnIconCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            if (_drag is { Kind: DragKind.Icon }) CancelDrag();
        }

        /// <summary>
        /// Место значка: перед значком, левее середины которого курсор (или
        /// ряд которого ниже курсора), иначе в конец показанных.
        /// </summary>
        private void PlaceIconMark(DragState drag, TemplateAnketaItemViewModel item, Point p)
        {
            if (Vm?.FindBlock(item.TemplateId) is not { } block || ContainerOf(block) is not { } container)
            {
                drag.Index = -1;
                InsertMark.IsVisible = false;
                return;
            }

            var list = container.GetVisualDescendants()
                .OfType<ItemsControl>()
                .FirstOrDefault(i => i.Classes.Contains("tpl-icons"));
            if (list == null) return;

            var rects = new List<Rect>();
            for (int i = 0; i < block.Anketas.Count; i++)
            {
                if (list.ContainerFromIndex(i) is not Control c) continue;
                var tile = c.GetVisualDescendants().OfType<Panel>().FirstOrDefault(x => x.Classes.Contains("tpl-ic")) as Control ?? c;
                var tl = tile.TranslatePoint(new Point(0, 0), DragLayer);
                if (tl.HasValue) rects.Add(new Rect(tl.Value, tile.Bounds.Size));
            }

            if (rects.Count != block.Anketas.Count || rects.Count == 0)
            {
                drag.Index = -1;
                InsertMark.IsVisible = false;
                return;
            }

            var target = rects.Count;
            for (int i = 0; i < rects.Count; i++)
            {
                var r = rects[i];
                if (p.Y < r.Top || (p.Y <= r.Bottom && p.X < r.X + r.Width / 2))
                {
                    target = i;
                    break;
                }
            }

            var from = block.Anketas.IndexOf(item);
            if (target == from || target == from + 1)
            {
                drag.Index = -1;
                InsertMark.IsVisible = false;
                return;
            }

            drag.Index = target;
            var anchor = target < rects.Count ? rects[target] : rects[^1];
            InsertMark.Height = anchor.Height;
            Canvas.SetLeft(InsertMark, target < rects.Count ? anchor.X - 4.5 : anchor.Right + 1.5);
            Canvas.SetTop(InsertMark, anchor.Y);
            InsertMark.IsVisible = true;
        }

        // ── Перетаскивание блоков — как карточки персонажей ──────────────
        //
        // Нажатие на свой блок сразу приподнимает его тенью. Если указатель
        // увели раньше паузы удержания — это щелчок или выделение текста, а не
        // перетаскивание: блок отпускается, нажатие достаётся кнопкам и полям.
        // После паузы и сдвига за порог блок становится местом-заглушкой, за
        // курсором едет призрак, а сам блок переезжает в списке на ячейку под
        // курсором — соседи расступаются и доезжают до новых мест (FLIP). В
        // проект порядок уходит одним шагом по отпусканию; Esc и потеря захвата
        // возвращают блок на прежнее место. Встроенные шаблоны стоят на месте.

        private const double BlockDragThreshold = 8.0;
        private const long BlockDragHoldDelayMs = 90;
        private const long BlockPreviewThrottleMs = 100;

        private sealed class BlockDragState
        {
            public required TemplateBlockViewModel Block { get; init; }
            public required Border BlockBorder { get; init; }
            public required Point Start { get; init; }
            public required Point GrabOffset { get; init; }
            public required long PressTick { get; init; }
            public bool Active { get; set; }
            public TemplateFolderViewModel? OriginalFolder { get; set; }
            public int OriginalIndex { get; set; } = -1;
            public IPointer? Pointer { get; set; }
        }

        private BlockDragState? _blockDrag;
        private Border? _pickedBlock;
        private long _lastBlockPreviewTick;
        private Point _lastBlockDragPos;
        private DispatcherTimer? _blockScrollTimer;
        private double _blockScrollVelocity;

        private void OnBlockDragPressed(object? sender, PointerPressedEventArgs e)
        {
            if (_drag != null || _blockDrag != null) return;
            if (Vm is not { } vm) return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (CharactersReadOnlyGate.IsReadOnly(this)) return;

            Border? blockBorder = null;
            for (var v = e.Source as Visual; v != null && !ReferenceEquals(v, this); v = v.GetVisualParent())
            {
                // Полоса прокрутки — прокрутка, значки анкет таскаются сами,
                // а в поле, где уже пишут, ведут мышью ради выделения текста.
                if (v is ScrollBar) return;
                if (v is Panel panel && panel.Classes.Contains("tpl-ic")) return;
                if (v is TextBox { IsFocused: true }) return;

                if (v is Border border && border.Classes.Contains("tpl-block"))
                {
                    blockBorder = border;
                    break;
                }
            }

            if (blockBorder?.DataContext is not TemplateBlockViewModel block) return;
            if (!block.IsEditable || vm.FolderOf(block) is not { IsBuiltIn: false }) return;

            _blockDrag = new BlockDragState
            {
                Block = block,
                BlockBorder = blockBorder,
                Start = e.GetPosition(DragLayer),
                GrabOffset = e.GetPosition(blockBorder),
                PressTick = Environment.TickCount64
            };

            SetPickedBlock(blockBorder);
        }

        private void OnBlockDragMoved(object? sender, PointerEventArgs e)
        {
            if (_blockDrag is not { } drag) return;

            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                CancelBlockDrag();
                return;
            }

            var pos = e.GetPosition(DragLayer);

            if (!drag.Active)
            {
                var delta = pos - drag.Start;
                var beyond = Math.Abs(delta.X) >= BlockDragThreshold || Math.Abs(delta.Y) >= BlockDragThreshold;

                // Увели раньше паузы удержания — щелчок или выделение текста.
                if (beyond && Environment.TickCount64 - drag.PressTick < BlockDragHoldDelayMs)
                {
                    ClearPickedBlock();
                    _blockDrag = null;
                    return;
                }

                if (!beyond) return;

                StartBlockDrag(drag, e);
                if (_blockDrag == null) return;
            }

            _lastBlockDragPos = pos;
            MoveBlockGhost(drag, pos);
            UpdateBlockScrollVelocity(pos);

            var now = Environment.TickCount64;
            if (now - _lastBlockPreviewTick >= BlockPreviewThrottleMs)
            {
                _lastBlockPreviewTick = now;
                UpdateBlockPreview(drag, pos);
            }

            e.Handled = true;
        }

        private void StartBlockDrag(BlockDragState drag, PointerEventArgs e)
        {
            if (Vm is not { } vm)
            {
                CancelBlockDrag();
                return;
            }

            drag.OriginalFolder = vm.FolderOf(drag.Block);
            drag.OriginalIndex = drag.OriginalFolder?.Blocks.IndexOf(drag.Block) ?? -1;
            if (drag.OriginalFolder == null || drag.OriginalIndex < 0)
            {
                CancelBlockDrag();
                return;
            }

            drag.Active = true;
            drag.Pointer = e.Pointer;

            // Захват — только теперь: при простом щелчке захват на вкладке
            // гасил бы щелчки кнопок блока. Заодно он отбирает указатель у
            // поля названия, если блок взяли за него.
            e.Pointer.Capture(this);

            // Поле названия или описания, за которое взяли блок, отпускает
            // фокус: ввод в нём закончен, и Esc достаётся перетаскиванию.
            LeaveInline();

            ClearPickedBlock();
            ShowBlockGhost(drag, e.GetPosition(DragLayer));
            drag.Block.IsDragSource = true;

            _lastBlockPreviewTick = Environment.TickCount64;
            StartBlockAutoScroll();
        }

        private void OnBlockDragReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_blockDrag is not { } drag) return;
            _blockDrag = null;

            if (!drag.Active)
            {
                // Щелчок: выбор блока и кнопки отработают своим чередом.
                ClearPickedBlock();
                return;
            }

            // Место под курсором в момент отпускания — даже если троттлинг
            // пересчёта ещё не успел до него дойти.
            UpdateBlockPreview(drag, e.GetPosition(DragLayer));

            EndBlockDragVisuals(drag);
            Vm?.CommitArrangement();

            e.Handled = true;
            e.Pointer.Capture(null);
        }

        private void OnBlockDragCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            // Захват теряют и поля внутри блока, когда его забирает вкладка, —
            // это не повод отменять. Отменяет только потеря захвата самой вкладкой.
            if (!ReferenceEquals(e.Source, this)) return;
            if (_blockDrag is { Active: true }) CancelBlockDrag();
        }

        private void OnBlockDragKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || _blockDrag is not { Active: true }) return;

            CancelBlockDrag();
            e.Handled = true;
        }

        private void OnBlockDragWheel(object? sender, PointerWheelEventArgs e)
        {
            if (_blockDrag is not { Active: true } drag) return;

            var offset = BlocksScroll.Offset;
            var maxY = Math.Max(0, BlocksScroll.Extent.Height - BlocksScroll.Viewport.Height);
            BlocksScroll.Offset = offset.WithY(Math.Clamp(offset.Y - e.Delta.Y * 60.0, 0, maxY));

            var pos = e.GetPosition(DragLayer);
            _lastBlockDragPos = pos;
            MoveBlockGhost(drag, pos);

            var now = Environment.TickCount64;
            if (now - _lastBlockPreviewTick >= BlockPreviewThrottleMs)
            {
                _lastBlockPreviewTick = now;
                UpdateBlockPreview(drag, pos);
            }

            e.Handled = true;
        }

        /// <summary>
        /// Отменить перетаскивание блока: блок возвращается на прежнее место,
        /// соседи доезжают обратно. До старта — просто снять тень.
        /// </summary>
        private void CancelBlockDrag()
        {
            var drag = _blockDrag;
            _blockDrag = null;
            ClearPickedBlock();
            if (drag is not { Active: true }) return;

            EndBlockDragVisuals(drag);

            if (Vm is { } vm && drag.OriginalFolder != null && drag.OriginalIndex >= 0)
            {
                var current = vm.FolderOf(drag.Block);
                var index = current?.Blocks.IndexOf(drag.Block) ?? -1;
                if (!ReferenceEquals(current, drag.OriginalFolder) || index != drag.OriginalIndex)
                {
                    var snapshot = SnapshotBlockPositions();
                    vm.PreviewPlaceBlock(drag.Block, drag.OriginalFolder, drag.OriginalIndex);
                    BeginBlockFlip(snapshot);
                }
            }

            drag.Pointer?.Capture(null);
        }

        private void EndBlockDragVisuals(BlockDragState drag)
        {
            SetDragOverFolder(null);
            drag.Block.IsDragSource = false;
            BlockGhost.IsVisible = false;
            StopBlockAutoScroll();
        }

        private void SetPickedBlock(Border border)
        {
            ClearPickedBlock();
            _pickedBlock = border;
            if (!border.Classes.Contains("picked")) border.Classes.Add("picked");
        }

        private void ClearPickedBlock()
        {
            if (_pickedBlock == null) return;
            _pickedBlock.Classes.Remove("picked");
            _pickedBlock = null;
        }

        // Призрак

        private void ShowBlockGhost(BlockDragState drag, Point pos)
        {
            var block = drag.Block;

            BlockGhost.Width = drag.BlockBorder.Bounds.Width > 0 ? drag.BlockBorder.Bounds.Width : 404;
            BlockGhost.BorderBrush = block.ColorBrush;

            BlockGhostBadge.Icon = block.Icon;
            BlockGhostBadge.IconColor = block.IconColor;
            BlockGhostBadge.IconBackground = block.IconBackground;
            BlockGhostBadge.IconBorder = block.IconBorder;
            BlockGhostBadge.IsVisible = !block.HasImage;

            BlockGhostImage.Source = block.Image;
            BlockGhostImageHost.IsVisible = block.HasImage;

            BlockGhostName.Text = block.Name;
            BlockGhostDescription.Text = block.Description;
            BlockGhostDescription.IsVisible = !string.IsNullOrWhiteSpace(block.Description);

            MoveBlockGhost(drag, pos);
            BlockGhost.IsVisible = true;
        }

        /// <summary>
        /// Призрак держится за то же место, за которое взяли блок: по ширине
        /// он совпадает с блоком, а по высоте — короче (только шапка), поэтому
        /// курсор не уходит ниже его середины.
        /// </summary>
        private void MoveBlockGhost(BlockDragState drag, Point pos)
        {
            var ghostHeight = BlockGhost.Bounds.Height > 0 ? BlockGhost.Bounds.Height : 92;
            Canvas.SetLeft(BlockGhost, pos.X - drag.GrabOffset.X);
            Canvas.SetTop(BlockGhost, pos.Y - Math.Min(drag.GrabOffset.Y, ghostHeight / 2.0));
        }

        // Место под курсором

        /// <summary>
        /// Место под курсором — как у карточек. Сначала раздел: тот, в чьей
        /// полосе курсор (между разделами — ближний; встроенные блоков не
        /// принимают, и над ними берётся ближний свой). Свёрнутый раздел
        /// подсвечивается, и блок уходит в его конец. В развёрнутом блок
        /// держится строки курсора и встаёт в ячейку, над которой курсор:
        /// левее всех — в первую ячейку строки, правее всех — в последнюю, а
        /// в чужом разделе правее последней ячейки последней строки — в
        /// конец. Строку меняет только движение по вертикали. Заглушка самого
        /// блока — обычная ячейка: курсор над ней ничего не двигает. Пустой
        /// раздел принимает блок первым.
        /// </summary>
        private void UpdateBlockPreview(BlockDragState drag, Point pos)
        {
            if (Vm is not { } vm) return;
            if (vm.FolderOf(drag.Block) is not { } current) return;

            (TemplateFolderViewModel Folder, ItemsControl List, Rect Rect)? best = null;
            var bestDistance = double.MaxValue;
            foreach (var (folder, list, section) in Sections())
            {
                if (!folder.AcceptsBlocks || !section.IsEffectivelyVisible) continue;
                if (section.Bounds.Height <= 1) continue;

                var tl = section.TranslatePoint(new Point(0, 0), DragLayer);
                if (!tl.HasValue) continue;

                var rect = new Rect(tl.Value, section.Bounds.Size);
                var distance = pos.Y < rect.Top ? rect.Top - pos.Y : pos.Y > rect.Bottom ? pos.Y - rect.Bottom : 0;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = (folder, list, rect);
                }
            }
            if (best is not { } target) return;

            var targetFolder = target.Folder;
            var sameFolder = ReferenceEquals(targetFolder, current);

            // Свёрнутый раздел: подсветка шапки, блок — в конец раздела.
            if (!targetFolder.ShowBlocks)
            {
                SetDragOverFolder(targetFolder);
                if (sameFolder) return;

                var collapsedSnapshot = SnapshotBlockPositions();
                vm.PreviewPlaceBlock(drag.Block, targetFolder, targetFolder.Blocks.Count);
                BeginBlockFlip(collapsedSnapshot);
                return;
            }

            SetDragOverFolder(null);

            var cells = new List<(int Index, Rect Rect)>();
            for (int i = 0; i < targetFolder.Blocks.Count; i++)
            {
                if (target.List.ContainerFromIndex(i) is not Control container) continue;
                if (container.Bounds.Width <= 1 || container.Bounds.Height <= 1) continue;
                var tl = container.TranslatePoint(new Point(0, 0), DragLayer);
                if (tl.HasValue) cells.Add((i, new Rect(tl.Value, container.Bounds.Size)));
            }

            if (cells.Count == 0)
            {
                // Пустой раздел (или в нём всё спрятано поиском) — блок первым.
                if (sameFolder) return;

                var emptySnapshot = SnapshotBlockPositions();
                vm.PreviewPlaceBlock(drag.Block, targetFolder, 0);
                BeginBlockFlip(emptySnapshot);
                return;
            }

            // Строки — по верхнему краю ячеек.
            var rows = cells
                .GroupBy(c => Math.Round(c.Rect.Top))
                .OrderBy(g => g.Key)
                .Select(g => g.OrderBy(c => c.Rect.X).ToList())
                .ToList();

            List<(int Index, Rect Rect)>? row = null;
            var rowDistance = double.MaxValue;
            foreach (var candidate in rows)
            {
                var top = candidate.Min(c => c.Rect.Top);
                var bottom = candidate.Max(c => c.Rect.Bottom);
                var distance = pos.Y < top ? top - pos.Y : pos.Y > bottom ? pos.Y - bottom : 0;
                if (distance < rowDistance)
                {
                    rowDistance = distance;
                    row = candidate;
                }
            }
            if (row == null) return;

            int index;
            if (pos.X < row[0].Rect.Right)
                index = row[0].Index;
            else
            {
                index = row[^1].Index;
                var beyond = true;
                foreach (var cell in row)
                {
                    if (pos.X < cell.Rect.Right)
                    {
                        index = cell.Index;
                        beyond = false;
                        break;
                    }
                }

                // Правее последней ячейки последней строки чужого раздела —
                // в его конец, а не на место последнего блока.
                if (beyond && !sameFolder && ReferenceEquals(row, rows[^1]))
                    index = targetFolder.Blocks.Count;
            }

            if (sameFolder && targetFolder.Blocks.IndexOf(drag.Block) == index) return;

            var snapshot = SnapshotBlockPositions();
            vm.PreviewPlaceBlock(drag.Block, targetFolder, index);
            BeginBlockFlip(snapshot);
        }

        /// <summary>Подсветить шапку свёрнутого раздела, над которым тащат блок; null — снять.</summary>
        private void SetDragOverFolder(TemplateFolderViewModel? folder)
        {
            if (Vm is not { } vm) return;
            foreach (var f in vm.Folders)
                f.IsDragOver = ReferenceEquals(f, folder);
        }

        // FLIP

        private IEnumerable<(TemplateBlockViewModel Block, Border Border)> LiveCustomBlocks()
        {
            foreach (var (folder, list, _) in Sections())
            {
                if (folder.IsBuiltIn) continue;

                for (int i = 0; i < folder.Blocks.Count; i++)
                {
                    if (list.ContainerFromIndex(i) is not Control container) continue;
                    var border = container.GetVisualDescendants()
                        .OfType<Border>()
                        .FirstOrDefault(b => b.Classes.Contains("tpl-block"));
                    if (border?.DataContext is TemplateBlockViewModel block)
                        yield return (block, border);
                }
            }
        }

        // Видимые места блоков вместе с текущим сдвигом: блок, который ещё
        // доезжает, начнёт следующий доезд оттуда, где его видно. Места —
        // относительно всего списка разделов: блок, переехавший в другой
        // раздел, доезжает через границу разделов.
        private Dictionary<TemplateBlockViewModel, Point> SnapshotBlockPositions()
        {
            var result = new Dictionary<TemplateBlockViewModel, Point>();
            foreach (var (block, border) in LiveCustomBlocks())
            {
                var pt = border.TranslatePoint(new Point(0, 0), FoldersList);
                if (pt.HasValue) result[block] = pt.Value;
            }
            return result;
        }

        /// <summary>
        /// Доезд после перестановки — как у карточек: раскладка прогоняется
        /// сразу, каждый сдвинутый блок ставится сдвигом туда, где его было
        /// видно, и на следующем кадре едет к нулю. Сбрасывается только тот
        /// сдвиг, который поставлен здесь: если блок уже получил новый, его
        /// доведёт новый доезд.
        /// </summary>
        private void BeginBlockFlip(Dictionary<TemplateBlockViewModel, Point> before)
        {
            if (before.Count == 0) return;

            UpdateLayout();

            var toAnimate = new List<(TranslateTransform Transform, double Dx, double Dy)>();

            foreach (var (block, border) in LiveCustomBlocks())
            {
                if (border.RenderTransform is not TranslateTransform tt) continue;

                if (!before.TryGetValue(block, out var was))
                {
                    if (tt.X != 0.0 || tt.Y != 0.0)
                    {
                        tt.X = 0.0;
                        tt.Y = 0.0;
                    }
                    continue;
                }

                double oldX = tt.X, oldY = tt.Y;
                var saved = tt.Transitions;
                tt.Transitions = null;
                tt.X = 0.0;
                tt.Y = 0.0;

                var now = border.TranslatePoint(new Point(0, 0), FoldersList);
                if (!now.HasValue)
                {
                    tt.X = oldX;
                    tt.Y = oldY;
                    tt.Transitions = saved;
                    continue;
                }

                var dx = was.X - now.Value.X;
                var dy = was.Y - now.Value.Y;

                if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5)
                {
                    tt.X = oldX;
                    tt.Y = oldY;
                    tt.Transitions = saved;
                    continue;
                }

                tt.X = dx;
                tt.Y = dy;
                tt.Transitions = saved;
                toAnimate.Add((tt, dx, dy));
            }

            if (toAnimate.Count == 0) return;

            Dispatcher.UIThread.Post(() =>
            {
                foreach (var (tt, dx, dy) in toAnimate)
                    if (tt.X == dx && tt.Y == dy) { tt.X = 0.0; tt.Y = 0.0; }
            }, DispatcherPriority.Render);
        }

        // Автопрокрутка: чем ближе курсор к верхнему или нижнему краю списка,
        // тем быстрее он едет — и пока курсор стоит у края, тоже.

        private void StartBlockAutoScroll()
        {
            if (_blockScrollTimer == null)
            {
                _blockScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                _blockScrollTimer.Tick += OnBlockAutoScrollTick;
            }
            _blockScrollVelocity = 0;
            _blockScrollTimer.Start();
        }

        private void StopBlockAutoScroll()
        {
            _blockScrollTimer?.Stop();
            _blockScrollVelocity = 0;
        }

        private void UpdateBlockScrollVelocity(Point pos)
        {
            _blockScrollVelocity = 0;
            var tl = BlocksScroll.TranslatePoint(new Point(0, 0), DragLayer);
            if (!tl.HasValue) return;

            const double zone = 90.0;
            const double maxSpeed = 24.0;
            var top = tl.Value.Y;
            var bottom = top + BlocksScroll.Bounds.Height;

            if (pos.Y < top + zone)
                _blockScrollVelocity = -maxSpeed * Math.Clamp((top + zone - pos.Y) / zone, 0, 1);
            else if (pos.Y > bottom - zone)
                _blockScrollVelocity = maxSpeed * Math.Clamp((pos.Y - (bottom - zone)) / zone, 0, 1);
        }

        private void OnBlockAutoScrollTick(object? sender, EventArgs e)
        {
            if (_blockDrag is not { Active: true } drag || Math.Abs(_blockScrollVelocity) < 0.5) return;

            var offset = BlocksScroll.Offset;
            var maxY = Math.Max(0, BlocksScroll.Extent.Height - BlocksScroll.Viewport.Height);
            var newY = Math.Clamp(offset.Y + _blockScrollVelocity, 0, maxY);
            if (Math.Abs(newY - offset.Y) < 0.1) return;

            BlocksScroll.Offset = offset.WithY(newY);
            MoveBlockGhost(drag, _lastBlockDragPos);

            var now = Environment.TickCount64;
            if (now - _lastBlockPreviewTick >= BlockPreviewThrottleMs)
            {
                _lastBlockPreviewTick = now;
                UpdateBlockPreview(drag, _lastBlockDragPos);
            }
        }
    }
}
