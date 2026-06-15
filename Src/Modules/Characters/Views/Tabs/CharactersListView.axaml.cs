using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Writersword.Modules.Characters.Interfaces;
using Writersword.Modules.Characters.ViewModels;
using Writersword.Modules.Characters.Views.Avatars;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Layout;
using ReactiveUI;

namespace Writersword.Modules.Characters.Views.Tabs
{
    public partial class CharactersListView : UserControl
    {
        private const double DragThreshold = 8.0;
        // Пауза удержания перед стартом перетаскивания. Быстрый клик уходит кнопкам
        // карточки, перетаскивание начинается только после зажатия и последующего движения.
        private const long DragHoldDelayMs = 90;

        private Point _dragStartPoint;
        private long _pressTick;
        private CharacterListItemViewModel? _dragCandidate;
        private bool _isDragging;
        private bool _hasPointerCapture;
        private Border? _pickedCard;

        private Dictionary<string, Border> _cardBorderCache = new();
        private Dictionary<string, Control> _folderHeaderCache = new();
        private Dictionary<string, Control> _folderItemsCtrlCache = new();
        private IDisposable? _cardsPerRowSubscription;

        private int _dragTargetIndex;
        private string? _dragTargetFolderId;

        private double _slotWidth;
        private double _slotHeight;
        private int _cardsPerRow;

        private CharacterFolderViewModel? _currentDragOverFolder;
        private long _lastPreviewTick;
        private int _flipGeneration;
        private ICharacterAvatarService? _avatarService;


        private IDisposable? _containerBoundsSubscription;

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

        public void PerformUndo()
        {
            if (DataContext is CharactersViewModel vm && vm.CanUndo) vm.Undo();
        }

        public void PerformRedo()
        {
            if (DataContext is CharactersViewModel vm && vm.CanRedo) vm.Redo();
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);
            _ghostCanvas = this.FindControl<Canvas>("DragGhostCanvas");
            _ghostBorder = this.FindControl<Border>("DragGhostBorder");
            _ghostText = this.FindControl<TextBlock>("DragGhostText");

            // Подписываемся на ширину первого ItemsControl с карточками.
            // Он знает реальную ширину после вычета скроллбара и margins.
            // Порог 10px гасит осцилляцию от скроллбара.
            if (DataContext is CharactersViewModel vmAvatar)
            {
                _avatarService = vmAvatar.AvatarService;
                vmAvatar.BindAvatarPickerCallback = BindAvatarPicker;

                // Привязываем уже существующие items (если данные загружены до OnLoaded).
                foreach (var folder in vmAvatar.Folders)
                    foreach (var item in folder.Characters)
                        BindAvatarPicker(item);

                _cardsPerRowSubscription?.Dispose();
                _cardsPerRowSubscription = vmAvatar
                    .WhenAnyValue(x => x.CardsPerRow)
                    .Subscribe(UpdateGridLayouts);
            }

            var foldersContainer = this.FindControl<ItemsControl>("FoldersContainer");
            if (foldersContainer is not null)
            {
                if (DataContext is CharactersViewModel vmInit && foldersContainer.Bounds.Width > 0)
                    vmInit.UpdateContainerWidth(foldersContainer.Bounds.Width);
                _containerBoundsSubscription = foldersContainer
                    .GetObservable(BoundsProperty)
                    .Subscribe(b =>
                    {
                        if (DataContext is CharactersViewModel vmSub && b.Width > 0)
                            vmSub.UpdateContainerWidth(b.Width);
                    });
            }
            else
            {
                _containerBoundsSubscription = this
                    .GetObservable(BoundsProperty)
                    .Subscribe(b =>
                    {
                        if (DataContext is CharactersViewModel vmSub && b.Width > 0)
                            vmSub.UpdateContainerWidth(b.Width - 40);
                    });
            }
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            _containerBoundsSubscription?.Dispose();
            _containerBoundsSubscription = null;
            _cardsPerRowSubscription?.Dispose();
            _cardsPerRowSubscription = null;
        }

        protected override void OnUnloaded(RoutedEventArgs e)
        {
            base.OnUnloaded(e);
            _containerBoundsSubscription?.Dispose();
            _containerBoundsSubscription = null;
            _cardsPerRowSubscription?.Dispose();
            _cardsPerRowSubscription = null;
        }

        private void UpdateGridLayouts(int cols)
        {
            double minItemWidth = (DataContext as CharactersViewModel)?.CardMinWidth ?? 152.0;
            foreach (var repeater in this.GetVisualDescendants().OfType<ItemsRepeater>())
            {
                if (repeater.Layout is UniformGridLayout layout)
                {
                    if (layout.MaximumRowsOrColumns != cols)
                        layout.MaximumRowsOrColumns = cols;
                    if (Math.Abs(layout.MinItemWidth - minItemWidth) > 0.5)
                        layout.MinItemWidth = minItemWidth;
                }
            }
        }

        public void FocusSearch()
            => this.FindControl<TextBox>("SearchTextBox")?.Focus();

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

        // Корневой Border карточки над переданным элементом (для отклика «взято»).
        private static Border? FindCardBorder(Visual? visual)
        {
            var current = visual;
            while (current is not null)
            {
                if (current is Border b && b.Classes.Contains("card-root")) return b;
                current = current.GetVisualParent();
            }
            return null;
        }

        // Подсветка «карточка взята» (тень-подъём) — мгновенный отклик на зажатие.
        private void SetPicked(Border? border)
        {
            ClearPicked();
            if (border is null) return;
            _pickedCard = border;
            if (!border.Classes.Contains("picked")) border.Classes.Add("picked");
        }

        private void ClearPicked()
        {
            if (_pickedCard is null) return;
            _pickedCard.Classes.Remove("picked");
            _pickedCard = null;
        }

        private static void OnCardTextBoxLostFocus(object? sender, RoutedEventArgs e)
        {
            if (e.Source is not TextBox) return;
            var charVm = FindCharacterItemVm(e.Source as Visual);
            if (charVm is null) return;
            if (charVm.IsBeingNamed)
                charVm.ConfirmNameCommand.Execute().Subscribe(_ => { }, _ => { });
            else if (charVm.IsRenaming)
                charVm.ConfirmRenameCommand.Execute().Subscribe(_ => { }, _ => { });
        }

        private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
        {
            if (DataContext is CharactersViewModel vm)
            {
                if (e.Key == Key.Z && e.KeyModifiers == KeyModifiers.Control)
                { if (vm.CanUndo) { vm.Undo(); e.Handled = true; return; } }
                if (e.Key == Key.Y && e.KeyModifiers == KeyModifiers.Control)
                { if (vm.CanRedo) { vm.Redo(); e.Handled = true; return; } }
            }

            if (e.Key is not (Key.Return or Key.Enter or Key.Escape)) return;
            var charVm = FindCharacterItemVm(e.Source as Visual);
            if (charVm is null) return;
            if (e.Key is Key.Return or Key.Enter)
            {
                if (charVm.IsBeingNamed)
                { charVm.ConfirmNameCommand.Execute().Subscribe(_ => { }, _ => { }); e.Handled = true; }
                else if (charVm.IsRenaming)
                { charVm.ConfirmRenameCommand.Execute().Subscribe(_ => { }, _ => { }); e.Handled = true; }
            }
            else
            {
                if (charVm.IsBeingNamed)
                { charVm.CancelNameCommand.Execute().Subscribe(_ => { }, _ => { }); e.Handled = true; }
                else if (charVm.IsRenaming)
                { charVm.CancelRenameCommand.Execute().Subscribe(_ => { }, _ => { }); e.Handled = true; }
            }
        }

        private void OnGlobalPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (DataContext is not CharactersViewModel vm) return;

            var source = e.Source as Visual;
            var folderVm = FindFolderVm(source);
            if (folderVm is not null) vm.ActiveFolderId = folderVm.FolderId;

            var charVm = FindCharacterItemVm(source);
            if (charVm is not null
                && !charVm.IsBeingNamed
                && !charVm.IsRenaming
                && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _dragCandidate = charVm;
                _dragStartPoint = e.GetPosition(this);
                _pressTick = Environment.TickCount64;
                _isDragging = false;
                // Указатель здесь не захватываем: при простом клике захват на UserControl
                // подавляет Click внутренних кнопок карточки (в том числе Flyout аватарки).
                // Захват берём только при реальном старте перетаскивания в OnGlobalPointerMoved.
                _hasPointerCapture = false;

                // Мгновенный отклик: приподнимаем карточку тенью, чтобы было понятно,
                // что она взята и её можно перетаскивать.
                SetPicked(FindCardBorder(source));
            }
            else
            {
                ClearPicked();
                _dragCandidate = null;
                _isDragging = false;
                _hasPointerCapture = false;
            }
        }

        private void OnGlobalPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_dragCandidate is null) return;

            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                if (_isDragging && DataContext is CharactersViewModel vmC)
                {
                    vmC.CancelDragPreview(_dragCandidate.Id);
                    ResetTransformsInstant();
                }
                ClearDragVisuals();
                _dragCandidate = null;
                _isDragging = false;
                ClearDragCaches();
                return;
            }

            var pos = e.GetPosition(this);

            if (!_isDragging)
            {
                var delta = pos - _dragStartPoint;
                bool movedBeyondThreshold =
                    Math.Abs(delta.X) >= DragThreshold || Math.Abs(delta.Y) >= DragThreshold;

                // Движение раньше паузы удержания — это клик или скролл, а не перетаскивание:
                // отпускаем кандидата, чтобы нажатие дошло до кнопок карточки.
                if (movedBeyondThreshold
                    && Environment.TickCount64 - _pressTick < DragHoldDelayMs)
                {
                    ClearPicked();
                    _dragCandidate = null;
                    return;
                }

                if (!movedBeyondThreshold)
                    return;

                _isDragging = true;
                // Захватываем указатель в момент старта перетаскивания.
                // Туннельный обработчик на корне срабатывает раньше ScrollViewer,
                // поэтому захвата на пороге достаточно, чтобы перехватить скролл.
                e.Pointer.Capture(this);
                _hasPointerCapture = true;
                // Перетаскивание началось — статичную подсветку убираем, дальше летит призрак.
                ClearPicked();

                if (DataContext is CharactersViewModel startVm)
                {
                    startVm.BeginDragPreview(_dragCandidate.Id);

                    _dragTargetFolderId = null;
                    foreach (var fv in startVm.Folders)
                    {
                        var ph = fv.Characters.FirstOrDefault(c => c.IsPlaceholder);
                        if (ph is not null)
                        {
                            _dragTargetIndex = fv.Characters.IndexOf(ph);
                            _dragTargetFolderId = fv.FolderId;
                            InitSlotGeometry(startVm);
                            break;
                        }
                    }

                    if (_dragTargetFolderId is null)
                    {
                        startVm.CancelDragPreview(_dragCandidate.Id);
                        e.Pointer.Capture(null);
                        _hasPointerCapture = false;
                        _isDragging = false;
                        _dragCandidate = null;
                        return;
                    }

                    RebuildDragCaches();
                    RecalibrateSlotGeometry();
                    _lastPreviewTick = Environment.TickCount64;
                }

                ShowGhost(_dragCandidate.Name, _dragCandidate.Color, pos);
            }
            else
            {
                MoveGhost(pos);

                var now = Environment.TickCount64;
                if (now - _lastPreviewTick >= 16)
                {
                    _lastPreviewTick = now;
                    UpdatePreview(pos);
                }
            }
        }

        private void InitSlotGeometry(CharactersViewModel vm)
        {
            const double cardMargin = 6.0;
            _slotWidth = vm.CardWidth + cardMargin * 2;
            _slotHeight = vm.CardTotalHeight + cardMargin * 2;
            _cardsPerRow = vm.CardsPerRow;
        }

        // Уточняем размер слота по реально отрисованному бордеру.
        // CardTotalHeight не включает BorderThickness (4px суммарно),
        // что даёт накопительную ошибку: на строке N смещение = N*4px.
        private void RecalibrateSlotGeometry()
        {
            const double cardMargin = 6.0;
            var border = _cardBorderCache.Values.FirstOrDefault();
            if (border is null) return;
            if (border.Bounds.Height > 0)
                _slotHeight = border.Bounds.Height + cardMargin * 2;
            if (border.Bounds.Width > 0)
                _slotWidth = border.Bounds.Width + cardMargin * 2;
        }

        private void UpdatePreview(Point pos)
        {
            if (DataContext is not CharactersViewModel vm) return;
            if (_dragCandidate is null) return;

            var targetFolderVm = FindFolderAtPoint(pos, vm);

            if (_currentDragOverFolder is not null &&
                !ReferenceEquals(_currentDragOverFolder, targetFolderVm))
            {
                _currentDragOverFolder.IsDragOver = false;
                _currentDragOverFolder = null;
            }

            if (targetFolderVm is null) return;

            if (!targetFolderVm.IsExpanded)
            {
                if (!ReferenceEquals(_currentDragOverFolder, targetFolderVm))
                {
                    targetFolderVm.IsDragOver = true;
                    _currentDragOverFolder = targetFolderVm;
                }
                if (targetFolderVm.FolderId != _dragTargetFolderId)
                {
                    _dragTargetFolderId = targetFolderVm.FolderId;
                    _dragTargetIndex = targetFolderVm.Characters.Count;
                    var snapshot = SnapshotPositions();
                    vm.UpdateDragPreview(_dragCandidate.Id, _dragTargetFolderId, _dragTargetIndex);
                    BeginFlipAnimation(snapshot);
                }
                return;
            }

            if (targetFolderVm.FolderId != _dragTargetFolderId)
                InitSlotGeometry(vm);

            int targetIndex = ComputeTargetIndex(pos, targetFolderVm);
            if (targetIndex == _dragTargetIndex && targetFolderVm.FolderId == _dragTargetFolderId)
                return;

            _dragTargetIndex = targetIndex;
            _dragTargetFolderId = targetFolderVm.FolderId;

            var snap = SnapshotPositions();
            vm.UpdateDragPreview(_dragCandidate.Id, _dragTargetFolderId, _dragTargetIndex);
            BeginFlipAnimation(snap);
        }

        // Снимаем визуальные позиции (с текущим TranslateTransform).
        // Не сбрасываем — карточки в середине анимации не прерываются.
        private Dictionary<string, Point> SnapshotPositions()
        {
            var result = new Dictionary<string, Point>();
            foreach (var (id, border) in EnumerateLiveCards())
            {
                var pt = border.TranslatePoint(new Point(0, 0), this);
                if (pt.HasValue) result[id] = pt.Value;
            }
            return result;
        }

        // Живые реализованные карточки (id -> корневой Border) из репитеров. Стартовый
        // кэш на момент начала drag ещё пуст (дети репитера не реализованы), поэтому
        // снапшоты и FLIP берут карточки из живого дерева на каждом шаге.
        private IEnumerable<(string id, Border border)> EnumerateLiveCards()
        {
            foreach (var repeater in _folderItemsCtrlCache.Values.OfType<ItemsRepeater>())
            {
                foreach (var child in repeater.GetVisualChildren().OfType<Control>())
                {
                    if (child.DataContext is not CharacterListItemViewModel cvm || cvm.IsPlaceholder)
                        continue;
                    var border = child as Border
                        ?? child.GetVisualDescendants().OfType<Border>()
                                .FirstOrDefault(b => b.Classes.Contains("card-root"));
                    if (border is not null)
                        yield return (cvm.Id, border);
                }
            }
        }

        private void ResetTransformsInstant()
        {
            foreach (var (_, border) in EnumerateLiveCards())
            {
                if (border.RenderTransform is not TranslateTransform tt) continue;
                if (tt.X == 0.0 && tt.Y == 0.0) continue;
                var saved = tt.Transitions;
                tt.Transitions = null;
                tt.X = 0.0;
                tt.Y = 0.0;
                tt.Transitions = saved;
            }
        }

        // FLIP: before = визуальная позиция до изменения коллекции.
        // После layout pass: для каждой карточки точечно сбрасываем трансформ,
        // измеряем чистую layout-позицию, вычисляем дельту, ставим обратно.
        // Остальные карточки продолжают свои анимации без обрыва.
        private void BeginFlipAnimation(Dictionary<string, Point> beforePositions)
        {
            if (beforePositions.Count == 0) return;
            int gen = ++_flipGeneration;

            // Прогоняем layout СИНХРОННО после изменения коллекции и измеряем новые
            // позиции сразу. Раньше замер откладывался постом с приоритетом Loaded,
            // который НИЖЕ Input: при непрерывном перетаскивании входные события его
            // вытесняют, колбэк не успевает отработать — отсюда «нет анимаций».
            UpdateLayout();

            var toAnimate = new List<TranslateTransform>(beforePositions.Count);

            foreach (var (id, border) in EnumerateLiveCards())
            {
                if (!beforePositions.TryGetValue(id, out var before)) continue;
                if (border.RenderTransform is not TranslateTransform tt) continue;

                // Точечный сброс: временно обнуляем трансформ для замера layout-позиции
                double oldX = tt.X, oldY = tt.Y;
                var saved = tt.Transitions;
                tt.Transitions = null;
                tt.X = 0.0;
                tt.Y = 0.0;

                var layoutPt = border.TranslatePoint(new Point(0, 0), this);

                if (!layoutPt.HasValue)
                {
                    tt.X = oldX;
                    tt.Y = oldY;
                    tt.Transitions = saved;
                    continue;
                }

                double dx = before.X - layoutPt.Value.X;
                double dy = before.Y - layoutPt.Value.Y;

                if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5)
                {
                    // позиция не изменилась — восстанавливаем текущую анимацию
                    tt.X = oldX;
                    tt.Y = oldY;
                    tt.Transitions = saved;
                    continue;
                }

                tt.X = dx;
                tt.Y = dy;
                tt.Transitions = saved;
                toAnimate.Add(tt);
            }

            if (toAnimate.Count == 0) return;

            // Финальный «доезд» к нулю — на следующий тик с приоритетом Render
            // (он выше Input, перетаскиванием не вытесняется), чтобы сыграл переход.
            Dispatcher.UIThread.Post(() =>
            {
                if (gen != _flipGeneration) return;
                foreach (var tt in toAnimate) { tt.X = 0.0; tt.Y = 0.0; }
            }, DispatcherPriority.Render);
        }

        private int ComputeTargetIndex(Point pos, CharacterFolderViewModel folderVm)
        {
            if (!_folderItemsCtrlCache.TryGetValue(folderVm.FolderId, out var ctrl))
                return _dragTargetIndex;

            // Маппим курсор на СЕТКУ ЯЧЕЕК, а не на личность карточки. Ячейки сетки
            // стоят на месте (карточки лишь переезжают между ними), поэтому индекс не
            // зависит от текущего положения плейсхолдера — нет петли «двигаем -> индекс
            // сменился -> снова двигаем», отсюда уходит мерцание. Колонки/строки берём
            // из живых позиций (вычитая анимационный сдвиг — стабильная layout-позиция),
            // плейсхолдер включаем, чтобы сетка была полной.
            if (ctrl is ItemsRepeater repeater)
            {
                const double tol = 4.0;
                var xs = new List<double>();
                var ys = new List<double>();

                foreach (var child in repeater.GetVisualChildren().OfType<Control>())
                {
                    if (child.DataContext is not CharacterListItemViewModel) continue;
                    // Только что реализованные/невыложенные карточки с нулевыми
                    // границами дают ложную колонку слева и сдвиг индекса — отсеиваем.
                    if (child.Bounds.Width <= 1 || child.Bounds.Height <= 1) continue;
                    var ctr = child.TranslatePoint(
                        new Point(child.Bounds.Width / 2.0, child.Bounds.Height / 2.0), this);
                    if (!ctr.HasValue) continue;

                    double offX = 0, offY = 0;
                    if (child.RenderTransform is TranslateTransform tt) { offX = tt.X; offY = tt.Y; }
                    double cx = ctr.Value.X - offX;
                    double cy = ctr.Value.Y - offY;

                    if (!xs.Any(v => Math.Abs(v - cx) <= tol)) xs.Add(cx);
                    if (!ys.Any(v => Math.Abs(v - cy) <= tol)) ys.Add(cy);
                }

                if (xs.Count > 0 && ys.Count > 0)
                {
                    xs.Sort();
                    ys.Sort();
                    int cols = xs.Count;
                    int col = NearestIndex(xs, pos.X);
                    int row = NearestIndex(ys, pos.Y);
                    int maxReal = Math.Max(0, folderVm.Characters.Count - 1);
                    return Math.Min(row * cols + col, maxReal);
                }
            }

            // Фолбэк: прежняя оценка по слот-геометрии (карточки ещё не реализованы).
            var topLeft = ctrl.TranslatePoint(new Point(0, 0), this);
            if (topLeft is null) return _dragTargetIndex;

            double relX = pos.X - topLeft.Value.X;
            double relY = pos.Y - topLeft.Value.Y;

            int r = Math.Max(0, (int)(relY / _slotHeight));
            int c = Math.Max(0, (int)(relX / _slotWidth));
            c = Math.Min(c, _cardsPerRow - 1);

            int maxIdx = Math.Max(0, folderVm.Characters.Count - 1);
            return Math.Min(r * _cardsPerRow + c, maxIdx);
        }

        // Индекс ближайшего по значению центра (колонки/строки) к координате курсора.
        private static int NearestIndex(List<double> centers, double value)
        {
            int best = 0;
            double bestDist = double.MaxValue;
            for (int i = 0; i < centers.Count; i++)
            {
                double d = Math.Abs(centers[i] - value);
                if (d < bestDist) { bestDist = d; best = i; }
            }
            return best;
        }

        private void RebuildDragCaches()
        {
            _cardBorderCache.Clear();
            _folderHeaderCache.Clear();
            _folderItemsCtrlCache.Clear();

            // Заголовки папок и репитеры — обычным обходом визуального дерева.
            foreach (var ctrl in this.GetVisualDescendants().OfType<Control>())
            {
                switch (ctrl)
                {
                    case StackPanel sp when sp.DataContext is CharacterFolderViewModel fvSp:
                        if (!_folderHeaderCache.ContainsKey(fvSp.FolderId))
                            _folderHeaderCache[fvSp.FolderId] = sp;
                        break;

                    case ItemsRepeater ir when ir.DataContext is CharacterFolderViewModel fvIr:
                        if (!_folderItemsCtrlCache.ContainsKey(fvIr.FolderId))
                            _folderItemsCtrlCache[fvIr.FolderId] = ir;
                        break;
                }
            }

            // Карточки берём НАПРЯМУЮ из реализованных детей репитеров — так же, как в
            // ComputeTargetIndex (этот путь карточки точно находит). Глобальный обход
            // GetVisualDescendants в реализованные дети ItemsRepeater не заходит, из-за
            // чего кэш оставался пустым и FLIP-анимации не было.
            foreach (var repeater in _folderItemsCtrlCache.Values.OfType<ItemsRepeater>())
            {
                foreach (var child in repeater.GetVisualChildren().OfType<Control>())
                {
                    if (child.DataContext is not CharacterListItemViewModel cardVm || cardVm.IsPlaceholder)
                        continue;
                    var border = child as Border
                        ?? child.GetVisualDescendants().OfType<Border>()
                                .FirstOrDefault(b => b.Classes.Contains("card-root"));
                    if (border is not null)
                        _cardBorderCache[cardVm.Id] = border;
                }
            }
        }

        // Вызывается из ViewModel при создании нового CharacterListItemViewModel
        // чтобы подключить RequestPickerOpen.
        private void BindAvatarPicker(CharacterListItemViewModel item)
        {
            item.RequestPickerOpen = async () =>
            {
                if (_avatarService == null) return null;
                var window = TopLevel.GetTopLevel(this) as Window;
                if (window == null) return null;
                return await CharacterAvatarPickerWindow.ShowAsync(
                    window, _avatarService, item.Id);
            };

            item.OnAvatarChanged = (id, avatarRef) =>
            {
                if (DataContext is not CharactersViewModel vm) return;
                var character = vm.CharacterService.GetById(id);
                if (character is null) return;
                character.AvatarPath = avatarRef;
                vm.CharacterService.Update(character);
            };
        }

        private void ClearDragCaches()
        {
            _cardBorderCache.Clear();
            _folderHeaderCache.Clear();
            _folderItemsCtrlCache.Clear();
        }

        private CharacterFolderViewModel? FindFolderAtPoint(Point pos, CharactersViewModel vm)
        {
            CharacterFolderViewModel? best = null;
            double bestTop = double.MinValue;

            foreach (var folderVm in vm.Folders)
            {
                Control? headerControl;
                if (!_folderHeaderCache.TryGetValue(folderVm.FolderId, out headerControl))
                {
                    foreach (var ctrl in this.GetVisualDescendants().OfType<Control>())
                    {
                        if (ReferenceEquals(ctrl.DataContext, folderVm) && ctrl is StackPanel)
                        { headerControl = ctrl; break; }
                    }
                }

                if (headerControl is null) continue;
                var topLeft = headerControl.TranslatePoint(new Point(0, 0), this);
                if (topLeft is null) continue;

                if (pos.Y >= topLeft.Value.Y && topLeft.Value.Y > bestTop)
                {
                    bestTop = topLeft.Value.Y;
                    best = folderVm;
                }
            }
            return best;
        }

        private void OnGlobalPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!_isDragging || _dragCandidate is null)
            {
                if (_hasPointerCapture)
                {
                    e.Pointer.Capture(null);
                    _hasPointerCapture = false;
                }
                ClearPicked();
                _dragCandidate = null;
                _isDragging = false;
                return;
            }

            if (_hasPointerCapture)
            {
                e.Pointer.Capture(null);
                _hasPointerCapture = false;
            }

            ClearDragVisuals();

            if (DataContext is CharactersViewModel vm)
            {
                if (_dragTargetFolderId is not null)
                    vm.CommitDragPreview(_dragCandidate.Id, _dragTargetFolderId, _dragTargetIndex);
                else
                {
                    vm.CancelDragPreview(_dragCandidate.Id);
                    ResetTransformsInstant();
                }
            }

            _dragCandidate = null;
            _isDragging = false;
            _dragTargetFolderId = null;
            ClearDragCaches();
        }

        private void ClearDragVisuals()
        {
            if (_currentDragOverFolder is not null)
            {
                _currentDragOverFolder.IsDragOver = false;
                _currentDragOverFolder = null;
            }
            HideGhost();
            ClearPicked();
        }

        private void ShowGhost(string name, string color, Point pos)
        {
            if (_ghostCanvas is null || _ghostBorder is null || _ghostText is null) return;
            _ghostText.Text = name;
            var brush = Color.TryParse(color, out var parsed)
                ? new SolidColorBrush(parsed)
                : new SolidColorBrush(Color.FromRgb(96, 125, 139));
            var topBg = _ghostBorder.FindControl<Border>("DragGhostTopBg");
            if (topBg is not null) topBg.Background = brush;
            MoveGhost(pos);
            _ghostCanvas.IsVisible = true;
        }

        private void MoveGhost(Point pos)
        {
            if (_ghostBorder is null || DataContext is not CharactersViewModel vm) return;
            Canvas.SetLeft(_ghostBorder, pos.X - vm.CardWidth / 2.0);
            Canvas.SetTop(_ghostBorder, pos.Y - vm.CardTotalHeight / 2.0);
        }

        private void HideGhost()
        {
            if (_ghostCanvas is not null) _ghostCanvas.IsVisible = false;
        }
    }
}