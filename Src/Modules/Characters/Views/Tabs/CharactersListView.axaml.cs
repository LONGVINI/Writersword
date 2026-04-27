using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Writersword.Modules.Characters.ViewModels;

namespace Writersword.Modules.Characters.Views.Tabs
{
    public partial class CharactersListView : UserControl
    {
        private const double DragThreshold = 8.0;

        private Point _dragStartPoint;
        private CharacterListItemViewModel? _dragCandidate;
        private bool _isDragging;
        private bool _hasPointerCapture;

        private Canvas? _ghostCanvas;
        private Border? _ghostBorder;
        private TextBlock? _ghostText;

        public CharactersListView()
        {
            InitializeComponent();

            AddHandler(PointerPressedEvent, OnGlobalPointerPressed, RoutingStrategies.Tunnel);
            AddHandler(PointerMovedEvent, OnGlobalPointerMoved, RoutingStrategies.Tunnel);
            AddHandler(PointerReleasedEvent, OnGlobalPointerReleased, RoutingStrategies.Tunnel);
            AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);

            AddHandler(InputElement.LostFocusEvent, OnCardTextBoxLostFocus, RoutingStrategies.Bubble);
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);
            _ghostCanvas = this.FindControl<Canvas>("DragGhostCanvas");
            _ghostBorder = this.FindControl<Border>("DragGhostBorder");
            _ghostText = this.FindControl<TextBlock>("DragGhostText");
        }

        public void FocusSearch()
            => this.FindControl<TextBox>("SearchTextBox")?.Focus();

        // ── поиск VM в дереве ──────────────────────────────────────────────

        private static CharacterListItemViewModel? FindCharacterItemVm(Visual? visual)
        {
            var current = visual;
            while (current is not null)
            {
                if (current is Control c && c.DataContext is CharacterListItemViewModel vm)
                    return vm;
                if (current is Control c2 && c2.DataContext is CharacterFolderViewModel)
                    return null;
                current = current.GetVisualParent();
            }
            return null;
        }

        private static CharacterFolderViewModel? FindFolderVm(Visual? visual)
        {
            var current = visual;
            while (current is not null)
            {
                if (current is Control c && c.DataContext is CharacterFolderViewModel vm)
                    return vm;
                current = current.GetVisualParent();
            }
            return null;
        }

        // ── LostFocus: сохранение имени при уходе фокуса из TextBox карточки ──

        private static void OnCardTextBoxLostFocus(object? sender, RoutedEventArgs e)
        {
            if (e.Source is not TextBox) return;

            var charVm = FindCharacterItemVm(e.Source as Visual);
            if (charVm == null) return;

            if (charVm.IsBeingNamed)
                charVm.ConfirmNameCommand.Execute().Subscribe(_ => { }, _ => { });
            else if (charVm.IsRenaming)
                charVm.ConfirmRenameCommand.Execute().Subscribe(_ => { }, _ => { });
        }

        // ── Enter / Escape в TextBox карточки ─────────────────────────────

        private static void OnGlobalKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key is not (Key.Return or Key.Enter or Key.Escape)) return;

            var charVm = FindCharacterItemVm(e.Source as Visual);
            if (charVm == null) return;

            if (e.Key is Key.Return or Key.Enter)
            {
                if (charVm.IsBeingNamed)
                {
                    charVm.ConfirmNameCommand.Execute().Subscribe(_ => { }, _ => { });
                    e.Handled = true;
                }
                else if (charVm.IsRenaming)
                {
                    charVm.ConfirmRenameCommand.Execute().Subscribe(_ => { }, _ => { });
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Escape)
            {
                if (charVm.IsBeingNamed)
                {
                    charVm.CancelNameCommand.Execute().Subscribe(_ => { }, _ => { });
                    e.Handled = true;
                }
                else if (charVm.IsRenaming)
                {
                    charVm.CancelRenameCommand.Execute().Subscribe(_ => { }, _ => { });
                    e.Handled = true;
                }
            }
        }

        // ── PointerPressed ─────────────────────────────────────────────────

        private void OnGlobalPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (DataContext is not CharactersViewModel vm) return;

            var source = e.Source as Visual;

            var folderVm = FindFolderVm(source);
            if (folderVm != null)
                vm.ActiveFolderId = folderVm.FolderId;

            var charVm = FindCharacterItemVm(source);
            if (charVm != null
                && !charVm.IsBeingNamed
                && !charVm.IsRenaming
                && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _dragCandidate = charVm;
                _dragStartPoint = e.GetPosition(this);
                _isDragging = false;
                _hasPointerCapture = false;
            }
            else
            {
                _dragCandidate = null;
                _isDragging = false;
            }
        }

        // ── PointerMoved ───────────────────────────────────────────────────

        private void OnGlobalPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_dragCandidate == null) return;

            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                // Кнопка отпущена без начала drag — просто сбрасываем состояние.
                // НЕ освобождаем capture: мы его ещё не захватывали.
                _dragCandidate = null;
                _isDragging = false;
                return;
            }

            var pos = e.GetPosition(this);

            if (!_isDragging)
            {
                var delta = pos - _dragStartPoint;
                if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
                    return;

                _isDragging = true;
                _hasPointerCapture = true;
                e.Pointer.Capture(this);
                ShowGhost(_dragCandidate.Name, _dragCandidate.Color, pos);
            }
            else
            {
                MoveGhost(pos);
            }
        }

        // ── PointerReleased ────────────────────────────────────────────────

        private void OnGlobalPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!_isDragging || _dragCandidate == null)
            {
                // Это обычный клик — НЕ трогаем capture вообще.
                // Кнопка/контрол, которые сами захватили указатель,
                // должны получить событие отпускания без вмешательства с нашей стороны.
                _dragCandidate = null;
                _isDragging = false;
                return;
            }

            // Мы были в режиме drag — освобождаем ТОЛЬКО нашу собственную capture.
            if (_hasPointerCapture)
            {
                e.Pointer.Capture(null);
                _hasPointerCapture = false;
            }

            HideGhost();

            if (DataContext is CharactersViewModel vm)
            {
                var pos = e.GetPosition(this);
                var hitTarget = this.InputHitTest(pos) as Visual;

                var targetChar = FindCharacterItemVm(hitTarget);
                if (targetChar != null && targetChar.Id != _dragCandidate.Id)
                {
                    vm.MoveCharacterBeforeInFolder(_dragCandidate.Id, targetChar.Id);
                    _dragCandidate = null;
                    _isDragging = false;
                    return;
                }

                var targetFolder = FindFolderVm(hitTarget);
                if (targetFolder != null)
                {
                    vm.MoveCharacterToFolder(_dragCandidate.Id, targetFolder.FolderId);
                    _dragCandidate = null;
                    _isDragging = false;
                    return;
                }
            }

            _dragCandidate = null;
            _isDragging = false;
        }

        // ── Призрак ────────────────────────────────────────────────────────

        private void ShowGhost(string name, string color, Point pos)
        {
            if (_ghostCanvas == null || _ghostBorder == null || _ghostText == null) return;

            _ghostText.Text = name;
            _ghostBorder.Background = Color.TryParse(color, out var parsed)
                ? new SolidColorBrush(parsed)
                : new SolidColorBrush(Color.FromRgb(96, 125, 139));

            MoveGhost(pos);
            _ghostCanvas.IsVisible = true;
        }

        private void MoveGhost(Point pos)
        {
            if (_ghostBorder == null) return;
            Canvas.SetLeft(_ghostBorder, pos.X + 14);
            Canvas.SetTop(_ghostBorder, pos.Y - 18);
        }

        private void HideGhost()
        {
            if (_ghostCanvas == null) return;
            _ghostCanvas.IsVisible = false;
        }
    }
}