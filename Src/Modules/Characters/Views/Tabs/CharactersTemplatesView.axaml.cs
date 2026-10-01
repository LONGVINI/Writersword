using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Writersword.Modules.Characters.ViewModels.Templates;

namespace Writersword.Modules.Characters.Views.Tabs
{
    /// <summary>
    /// Шаблоны блоками. Перетаскивание — своей обработкой указателя, как у
    /// карточек персонажей и галереи, а не DragDrop: за курсором едет
    /// «призрак», полоска показывает место вставки. Блоки двигают за шапку,
    /// значки анкет — внутри своего блока.
    /// </summary>
    public partial class CharactersTemplatesView : UserControl
    {
        private static readonly ILogger _logger = Log.ForContext<CharactersTemplatesView>();

        private readonly DispatcherTimer _applyResultTimer;

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

            _applyResultTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _applyResultTimer.Tick += (_, _) =>
            {
                _applyResultTimer.Stop();
                ApplyResult.IsVisible = false;
            };
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

        private Control? ContainerOf(TemplateBlockViewModel block) =>
            (CustomList.ContainerFromItem(block) ?? BuiltInList.ContainerFromItem(block)) as Control;

        private static void ClosePopupOf(object? sender) =>
            (sender as Control)?.FindLogicalAncestorOfType<Popup>()?.Close();

        // ── Панель ───────────────────────────────────────────────────────

        private void OnNewTemplateClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (Vm?.CreateTemplate() is { } block) ShowBlock(block, openCustomize: true);
        }

        private void OnApplyToAllClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (Vm is not { } vm) return;

            var touched = vm.ApplyProjectToAll();
            ApplyResultText.Text = touched > 0 ? "Добавлено персонажам: " + touched : "Уже у всех";
            ApplyResult.IsVisible = true;
            _applyResultTimer.Stop();
            _applyResultTimer.Start();
        }

        /// <summary>
        /// Прокрутить к блоку; только что созданному — сразу открыть его
        /// настройку: новый шаблон почти всегда сразу переименовывают.
        /// </summary>
        private void ShowBlock(TemplateBlockViewModel block, bool openCustomize)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (ContainerOf(block) is not { } container) return;

                container.BringIntoView();
                if (!openCustomize) return;

                var image = container.GetVisualDescendants()
                    .OfType<Button>()
                    .FirstOrDefault(b => b.Classes.Contains("tpl-image"));
                if (image != null) OpenCustomize(image, block);
            }, DispatcherPriority.Background);
        }

        // ── Блок ─────────────────────────────────────────────────────────

        /// <summary>Щелчок по блоку делает его выбранным — живым.</summary>
        private void OnBlockPressed(object? sender, PointerPressedEventArgs e)
        {
            if (BlockOf(sender) is not { } block || Vm is not { } vm) return;
            if (!e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed) return;
            if (!block.IsActive) vm.Activate(block);
        }

        private void OnImageClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is not Button button || BlockOf(sender) is not { } block || Vm is not { } vm) return;

            if (!block.IsActive)
            {
                vm.Activate(block);
                return;
            }

            if (block.CanCustomize) OpenCustomize(button, block);
        }

        private static void OpenCustomize(Button button, TemplateBlockViewModel block)
        {
            block.PrepareCustomize();
            FlyoutBase.ShowAttachedFlyout(button);
        }

        private async void OnPickImageClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (BlockOf(sender) is not { } block || Vm is not { } vm) return;

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

                await vm.SetImageAsync(block.Id, memory.ToArray(), files[0].Name);
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

        private void OnStarClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (BlockOf(sender) is { } block) Vm?.SetProjectTemplate(block.Id);
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

        private void OnDuplicateClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (BlockOf(sender) is { } block && Vm?.DuplicateTemplate(block.Id) is { } copy)
                ShowBlock(copy, openCustomize: false);
        }

        private void OnDeleteClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            ClosePopupOf(sender);
            if (BlockOf(sender) is { } block) Vm?.DeleteTemplate(block.Id);
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

        private void OnRemoveAnketaClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if ((sender as Control)?.DataContext is TemplateAnketaItemViewModel item)
                Vm?.RemoveAnketa(item.TemplateId, item.Id);
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

        // ── Перетаскивание ───────────────────────────────────────────────

        private enum DragKind { Block, Icon }

        private sealed class DragState
        {
            public required DragKind Kind { get; init; }
            public required Control Source { get; init; }
            public required Point Start { get; init; }
            public TemplateBlockViewModel? Block { get; init; }
            public TemplateAnketaItemViewModel? Icon { get; init; }
            public bool Active { get; set; }
            public int Index { get; set; } = -1;
        }

        private DragState? _drag;

        private void BeginGhost(string? icon, string? color, string text)
        {
            GhostBadge.Icon = icon;
            GhostBadge.IconColor = color;
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

            if (!drag.Active)
            {
                // Нажали и отпустили на месте: по шапке — выбрать блок.
                if (commit && drag.Kind == DragKind.Block && drag.Block is { IsActive: false } block)
                    Vm?.Activate(block);
                return;
            }

            if (drag.Block != null) drag.Block.IsDragSource = false;
            if (drag.Icon != null) drag.Icon.IsDragSource = false;

            if (!commit || drag.Index < 0 || Vm is not { } vm) return;

            if (drag.Kind == DragKind.Block && drag.Block != null)
                vm.MoveTemplate(drag.Block.Id, drag.Index);
            else if (drag.Kind == DragKind.Icon && drag.Icon != null)
                vm.MoveAnketa(drag.Icon.TemplateId, drag.Icon.Id, drag.Index);
        }

        private void CancelDrag() => FinishDrag(commit: false);

        // Блок — за шапку

        private void OnHeadPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Control head || BlockOf(sender) is not { } block) return;
            if (!e.GetCurrentPoint(head).Properties.IsLeftButtonPressed) return;

            _drag = new DragState
            {
                Kind = DragKind.Block,
                Source = head,
                Start = e.GetPosition(DragLayer),
                Block = block
            };

            e.Pointer.Capture(head);
            e.Handled = true;
        }

        private void OnHeadMoved(object? sender, PointerEventArgs e)
        {
            if (_drag is not { Kind: DragKind.Block, Block: { } block } drag) return;

            var p = e.GetPosition(DragLayer);
            if (!drag.Active)
            {
                if (Math.Abs(p.X - drag.Start.X) < 5 && Math.Abs(p.Y - drag.Start.Y) < 5) return;

                // Встроенные шаблоны стоят на своих местах.
                if (!block.IsEditable) return;

                drag.Active = true;
                block.IsDragSource = true;
                BeginGhost(block.Icon, block.IconColor, block.Name);
            }

            MoveGhost(p);
            PlaceBlockMark(drag, p);
            AutoScroll(e);
            e.Handled = true;
        }

        private void OnHeadReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_drag is not { Kind: DragKind.Block }) return;

            FinishDrag(commit: true);
            e.Pointer.Capture(null);
            e.Handled = true;
        }

        private void OnHeadCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            if (_drag is { Kind: DragKind.Block }) CancelDrag();
        }

        /// <summary>
        /// Место блока среди своих: перед блоком, левее середины которого
        /// курсор в его ряду, иначе в конец. Полоска встаёт у края блока.
        /// </summary>
        private void PlaceBlockMark(DragState drag, Point p)
        {
            if (Vm is not { } vm) return;

            var blocks = vm.CustomBlocks;
            var rects = new List<(int Index, Rect Rect)>();
            for (int i = 0; i < blocks.Count; i++)
            {
                if (!blocks[i].IsMatch) continue;
                if (CustomList.ContainerFromIndex(i) is not Control c) continue;
                var border = c.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("tpl-block"));
                if (border == null) continue;
                var tl = border.TranslatePoint(new Point(0, 0), DragLayer);
                if (tl.HasValue) rects.Add((i, new Rect(tl.Value, border.Bounds.Size)));
            }

            if (rects.Count == 0)
            {
                drag.Index = -1;
                InsertMark.IsVisible = false;
                return;
            }

            var target = blocks.Count;
            Rect anchor = rects[^1].Rect;
            bool after = true;
            foreach (var (index, r) in rects)
            {
                if (p.Y < r.Top || (p.Y <= r.Bottom && p.X < r.X + r.Width / 2))
                {
                    target = index;
                    anchor = r;
                    after = false;
                    break;
                }
            }

            var from = blocks.IndexOf(drag.Block!);
            if (target == from || target == from + 1)
            {
                drag.Index = -1;
                InsertMark.IsVisible = false;
                return;
            }

            drag.Index = target;
            InsertMark.Height = anchor.Height;
            Canvas.SetLeft(InsertMark, after ? anchor.Right + 4 : anchor.X - 7);
            Canvas.SetTop(InsertMark, anchor.Y);
            InsertMark.IsVisible = true;
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
                Block = null,
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
                BeginGhost(item.Icon, item.IconColor, item.Name);
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

        /// <summary>У краёв списка он прокручивается сам — до дальних блоков можно дотащить.</summary>
        private void AutoScroll(PointerEventArgs e)
        {
            const double edge = 40;
            const double step = 16;

            var p = e.GetPosition(BlocksScroll);
            var offset = BlocksScroll.Offset;
            if (p.Y < edge && offset.Y > 0)
                BlocksScroll.Offset = offset.WithY(Math.Max(0, offset.Y - step));
            else if (p.Y > BlocksScroll.Bounds.Height - edge)
                BlocksScroll.Offset = offset.WithY(offset.Y + step);
        }
    }
}
