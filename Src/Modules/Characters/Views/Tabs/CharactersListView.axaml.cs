using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Writersword.Modules.Characters.Interfaces;
using Writersword.Modules.Characters.ViewModels;
using Writersword.Modules.Characters.Views;
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

        // Троттлинг пересчёта позиции вставки при перетаскивании. Призрак летит за
        // мышью каждый кадр (это дёшево — двигается только его позиция), а тяжёлый
        // пересчёт целевого индекса и раскладка (UpdatePreview → возможный
        // BeginFlipAnimation с UpdateLayout) запускаются не чаще ~10 раз в секунду.
        // Раньше стояло 16 мс (60/сек), и каждый переход между ячейками форсил полный
        // проход раскладки всего дерева карточек — отсюда дёрганье при перетаскивании.
        private const long PreviewRecalcThrottleMs = 100;


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
        private ICharacterAvatarService? _avatarService;


        private IDisposable? _containerBoundsSubscription;

        private Canvas? _ghostCanvas;
        private Border? _ghostBorder;
        private TextBlock? _ghostText;
        private double _ghostWidth = 148;

        // Автопрокрутка списка во время перетаскивания.
        private ScrollViewer? _dragScroll;
        private DispatcherTimer? _autoScrollTimer;
        private double _autoScrollVel;
        private Point _lastDragPos;

        public CharactersListView()
        {
            InitializeComponent();

            AddHandler(PointerPressedEvent, OnGlobalPointerPressed, RoutingStrategies.Tunnel);
            AddHandler(PointerMovedEvent, OnGlobalPointerMoved, RoutingStrategies.Tunnel);
            AddHandler(PointerReleasedEvent, OnGlobalPointerReleased, RoutingStrategies.Tunnel);
            AddHandler(PointerWheelChangedEvent, OnGlobalPointerWheel, RoutingStrategies.Tunnel);
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

        // Открывает окно настроек карточки по центру модуля (CardSettingsOverlay
        // хостится в CharactersModuleView поверх содержимого, со скримом).
        private void OnCardSettingsClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not Control c || c.DataContext is not CharacterListItemViewModel item) return;
            var host = this.FindAncestorOfType<CharactersModuleView>();
            var overlay = host?.FindControl<CardSettingsOverlay>("CardSettingsOverlayControl");
            overlay?.ShowFor(item, DataContext as CharactersViewModel);
            e.Handled = true;
        }

        // Прокрутка к только что созданному персонажу или группе. Вызывается
        // вьюмоделью сразу после добавления, когда раскладка ещё не построила
        // новую карточку, поэтому сама работа откладывается до конца прохода
        // раскладки.
        //
        // _scrollRequestSeq гасит гонку при быстром массовом добавлении: если
        // несколько запросов на прокрутку встают в очередь UI-потока подряд
        // (персонажи добавляются быстрее, чем раскладка успевает осесть),
        // GetOrCreateElement/BringIntoView для устаревших индексов сталкивались
        // друг с другом и оставляли в репитере нереализованный (пустой)
        // контейнер, пока список не перестраивался вручную. Выполняется только
        // самый последний запрос на момент, когда очередь до него дошла.
        private string? _pendingScrollFolderId;
        private string? _pendingScrollCharacterId;
        private long _scrollRequestSeq;

        private void ScrollToCharacter(string? folderId, string characterId)
        {
            var seq = System.Threading.Interlocked.Increment(ref _scrollRequestSeq);
            _pendingScrollFolderId = folderId;
            _pendingScrollCharacterId = characterId;

            Dispatcher.UIThread.Post(
                () =>
                {
                    if (seq != _scrollRequestSeq) return;
                    ScrollToCharacterCore(_pendingScrollFolderId, _pendingScrollCharacterId!);
                },
                DispatcherPriority.Background);
        }

        private void ScrollToCharacterCore(string? folderId, string characterId)
        {
            if (DataContext is not CharactersViewModel vm) return;

            var folder = (folderId is not null
                    ? vm.Folders.FirstOrDefault(f => f.FolderId == folderId)
                    : null)
                ?? vm.Folders.FirstOrDefault(f => f.Characters.Any(c => c.Id == characterId));
            if (folder is null) return;

            int index = -1;
            var visible = folder.VisibleCharacters;
            for (int i = 0; i < visible.Count; i++)
                if (visible[i].Id == characterId) { index = i; break; }
            if (index < 0) return;

            // Репитер нужной папки ищем по живому дереву: кэш репитеров
            // (_folderItemsCtrlCache) заполняется только на время перетаскивания.
            var repeater = this.GetVisualDescendants()
                .OfType<ItemsRepeater>()
                .FirstOrDefault(r => r.DataContext is CharacterFolderViewModel fv
                                     && fv.FolderId == folder.FolderId);
            if (repeater is null) return;

            try
            {
                // GetOrCreateElement реализует элемент, даже если его контейнер ещё
                // не создан (позиция далеко за пределами видимой области), после
                // чего карточку можно подвести в видимую область.
                var element = repeater.GetOrCreateElement(index);
                if (element is null) return;
                repeater.UpdateLayout();
                element.BringIntoView();
            }
            catch (ArgumentException)
            {
                // Индекс успел устареть (список перестроился между постом и
                // выполнением) — прокрутку просто пропускаем.
            }
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
                vmAvatar.ScrollToCharacterCallback = ScrollToCharacter;

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

                // Мгновенный отклик: приподнимаем карточку тенью. Заодно запоминаем
                // реальную ширину карточки (в списке — во всю строку) для призрака —
                // в момент нажатия она ещё на месте и корректно разложена.
                var pressedCard = FindCardBorder(source);
                SetPicked(pressedCard);
                if (pressedCard is not null && pressedCard.Bounds.Width > 0)
                    _ghostWidth = pressedCard.Bounds.Width;
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

                    _dragScroll = _folderItemsCtrlCache.TryGetValue(_dragTargetFolderId!, out var repForScroll)
                        ? repForScroll.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault()
                        : null;
                    StartAutoScroll();
                }

                ShowGhost(_dragCandidate, pos);
            }
            else
            {
                _lastDragPos = pos;
                UpdateAutoScrollVelocity(pos);
                MoveGhost(pos);

                var now = Environment.TickCount64;
                if (now - _lastPreviewTick >= PreviewRecalcThrottleMs)
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

            // Текущее место плейсхолдера в целевой папке. Если он здесь — перестановка
            // внутри папки: двигаются только карточки между ним и целью, снимаем лишь их.
            int oldPh = -1;
            for (int i = 0; i < targetFolderVm.Characters.Count; i++)
                if (targetFolderVm.Characters[i].IsPlaceholder) { oldPh = i; break; }

            Dictionary<string, Point> snap;
            if (oldPh >= 0)
            {
                int newPh = Math.Min(targetIndex, targetFolderVm.Characters.Count - 1);
                snap = SnapshotPositions(targetFolderVm, Math.Min(oldPh, newPh), Math.Max(oldPh, newPh));
            }
            else
            {
                // Кросс-папка: двигаются обе папки целиком — снимок всего окна.
                snap = SnapshotPositions();
            }

            vm.UpdateDragPreview(_dragCandidate.Id, _dragTargetFolderId, _dragTargetIndex);
            BeginFlipAnimation(snap);
        }

        // Снимаем визуальные позиции (с текущим TranslateTransform) в координатах
        // репитера, а не вьюпорта: прокрутка не должна порождать ложные дельты FLIP.
        // Не сбрасываем — карточки в середине анимации не прерываются.
        private Dictionary<string, Point> SnapshotPositions() => SnapshotPositions(null, 0, 0);

        // Если задана папка с диапазоном [lo..hi] — снимаем только её карточки в этом
        // диапазоне (только они и сдвигаются при перестановке), остальные статичны и
        // мерить их незачем. Без папки — снимок всего реализованного окна.
        private Dictionary<string, Point> SnapshotPositions(CharacterFolderViewModel? folder, int lo, int hi)
        {
            HashSet<string>? ids = null;
            if (folder is not null)
            {
                ids = new HashSet<string>();
                int a = Math.Max(0, lo);
                int b = Math.Min(folder.Characters.Count - 1, hi);
                for (int i = a; i <= b; i++) ids.Add(folder.Characters[i].Id);
            }

            var result = new Dictionary<string, Point>();
            foreach (var (id, border, repeater) in EnumerateLiveCards())
            {
                if (ids is not null && !ids.Contains(id)) continue;
                var pt = border.TranslatePoint(new Point(0, 0), repeater);
                if (pt.HasValue) result[id] = pt.Value;
            }
            return result;
        }

        // Живые реализованные карточки: id -> корневой Border + его репитер. Стартовый
        // кэш на момент начала drag ещё пуст (дети репитера не реализованы), поэтому
        // снапшоты и FLIP берут карточки из живого дерева на каждом шаге. Репитер нужен
        // как система координат для FLIP: он скроллится вместе с карточкой, поэтому
        // позиция относительно него не зависит от прокрутки.
        private IEnumerable<(string id, Border border, ItemsRepeater repeater)> EnumerateLiveCards()
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
                        yield return (cvm.Id, border, repeater);
                }
            }
        }

        private void ResetTransformsInstant()
        {
            foreach (var (_, border, _) in EnumerateLiveCards())
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

            // Прогоняем layout СИНХРОННО после изменения коллекции и измеряем новые
            // позиции сразу. Раньше замер откладывался постом с приоритетом Loaded,
            // который НИЖЕ Input: при непрерывном перетаскивании входные события его
            // вытесняют, колбэк не успевает отработать — отсюда «нет анимаций».
            UpdateLayout();

            var toAnimate = new List<(TranslateTransform tt, double dx, double dy)>(beforePositions.Count);

            foreach (var (id, border, repeater) in EnumerateLiveCards())
            {
                if (!beforePositions.TryGetValue(id, out var before))
                {
                    // Карточка вне затронутого диапазона — двигаться не должна. Если на ней
                    // остался трансформ, направляем его к нулю С ПЕРЕХОДОМ (не мгновенно):
                    // уже едущая к месту анимация продолжится плавно, а «застрявшая» (чей
                    // FLIP-доезд не сыграл) доедет красиво — и не уедет под соседнюю дыркой.
                    // Цель та же (0), поэтому едущую анимацию это не перезапускает.
                    if (border.RenderTransform is TranslateTransform stale &&
                        (stale.X != 0.0 || stale.Y != 0.0))
                    {
                        stale.X = 0.0;
                        stale.Y = 0.0;
                    }
                    continue;
                }
                if (border.RenderTransform is not TranslateTransform tt) continue;

                // Точечный сброс: временно обнуляем трансформ для замера layout-позиции
                double oldX = tt.X, oldY = tt.Y;
                var saved = tt.Transitions;
                tt.Transitions = null;
                tt.X = 0.0;
                tt.Y = 0.0;

                var layoutPt = border.TranslatePoint(new Point(0, 0), repeater);

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

                // Отбрасываем только большой ВЕРТИКАЛЬНЫЙ прыжок — это артефакт
                // переиспользования (recycle) при виртуализации (карточка «прыгнула» через
                // полэкрана по вертикали). Большой ГОРИЗОНТАЛЬНЫЙ сдвиг при маленьком
                // вертикальном — это нормальный перенос карточки с конца строки в начало
                // следующей (и наоборот), его надо анимировать, а не глотать.
                if (Math.Abs(dy) > 600)
                {
                    tt.Transitions = saved;
                    continue;
                }

                tt.X = dx;
                tt.Y = dy;
                tt.Transitions = saved;
                toAnimate.Add((tt, dx, dy));
            }

            if (toAnimate.Count == 0) return;

            // Финальный «доезд» к нулю — на следующий тик с приоритетом Render (он выше
            // Input, перетаскиванием не вытесняется), чтобы сыграл переход. Сбрасываем
            // только если трансформ всё ещё хранит ИМЕННО нашу дельту: если более новый
            // FLIP уже задал этой карточке другую дельту — пропускаем, её доведёт его
            // собственный пост. Без проверки поколения: иначе при гонке шагов карточка
            // навсегда застывала со смещением (дырки в сетке + «призраки» при recycle).
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var (tt, dx, dy) in toAnimate)
                    if (tt.X == dx && tt.Y == dy) { tt.X = 0.0; tt.Y = 0.0; }
            }, DispatcherPriority.Render);
        }

        private int ComputeTargetIndex(Point pos, CharacterFolderViewModel folderVm)
        {
            if (!_folderItemsCtrlCache.TryGetValue(folderVm.FolderId, out var ctrl))
                return _dragTargetIndex;

            // Место вставки меряем по ФАКТИЧЕСКОЙ геометрии реализованных ячеек, а не по
            // расчётным размерам слота. Плейсхолдер держится СТРОКИ КУРСОРА: по горизонтали
            // — левее/правее центров ячеек; правее всех в строке = правый край строки (без
            // переноса в начало следующей), левее всех = левый край. Переход на другую
            // строку происходит ТОЛЬКО вертикальным движением курсора — никаких перескоков
            // туда, куда мышь не смотрит.
            if (ctrl is ItemsRepeater repeater)
            {
                // Все реализованные ячейки, включая плейсхолдер: он — реальная ячейка строки,
                // и его учёт делает выбор устойчивым (наведение на его клетку = без сдвига).
                var cells = new List<(int idx, double cx, double cy)>();
                var rowYs = new List<double>();
                double cardH = 0;

                foreach (var child in repeater.GetVisualChildren().OfType<Control>())
                {
                    if (child.DataContext is not CharacterListItemViewModel cvm) continue;
                    if (child.Bounds.Width <= 1 || child.Bounds.Height <= 1) continue;
                    int aidx = folderVm.Characters.IndexOf(cvm);
                    if (aidx < 0) continue;
                    var ctr = child.TranslatePoint(
                        new Point(child.Bounds.Width / 2.0, child.Bounds.Height / 2.0), this);
                    if (!ctr.HasValue) continue;

                    // Стабильный центр без учёта текущего FLIP-трансформа.
                    double offX = 0, offY = 0;
                    if (child.RenderTransform is TranslateTransform tt) { offX = tt.X; offY = tt.Y; }
                    double cx = ctr.Value.X - offX;
                    double cy = ctr.Value.Y - offY;

                    cells.Add((aidx, cx, cy));
                    if (child.Bounds.Height > cardH) cardH = child.Bounds.Height;
                    if (!rowYs.Any(y => Math.Abs(y - cy) <= 4)) rowYs.Add(cy);
                }

                if (cells.Count > 0)
                {
                    // Фактический шаг строк = минимальная разница между центрами соседних
                    // строк. Полоса в полшага точно покрывает строку без зазоров и нахлёста.
                    rowYs.Sort();
                    double rowPitch = double.MaxValue;
                    for (int i = 1; i < rowYs.Count; i++)
                    {
                        double d = rowYs[i] - rowYs[i - 1];
                        if (d > 1 && d < rowPitch) rowPitch = d;
                    }
                    if (rowPitch == double.MaxValue) rowPitch = cardH > 1 ? cardH : 100;
                    double halfRow = rowPitch / 2.0;

                    // Ячейки строки курсора (по измеренной полосе вокруг pos.Y).
                    var row = cells.Where(c => Math.Abs(c.cy - pos.Y) <= halfRow).ToList();
                    if (row.Count == 0)
                    {
                        double ny = cells.OrderBy(c => Math.Abs(c.cy - pos.Y)).First().cy;
                        row = cells.Where(c => Math.Abs(c.cy - ny) <= halfRow).ToList();
                    }

                    var nearest = row.OrderBy(c => Math.Abs(c.cx - pos.X)).First();
                    int target = nearest.idx;

                    bool hasRowBelow = cells.Any(c => c.cy > pos.Y + halfRow);
                    if (!hasRowBelow)
                    {
                        var right = row.OrderByDescending(c => c.cx).First();
                        double colPitch = double.MaxValue;
                        var xs = row.Select(c => c.cx).OrderBy(x => x).ToList();
                        for (int i = 1; i < xs.Count; i++)
                        {
                            double d = xs[i] - xs[i - 1];
                            if (d > 1 && d < colPitch) colPitch = d;
                        }
                        if (colPitch == double.MaxValue) colPitch = _slotWidth > 1 ? _slotWidth : 100;
                        if (pos.X > right.cx + colPitch / 2.0)
                            target = right.idx + 1;
                    }

                    return Math.Clamp(target, 0, folderVm.Characters.Count);
                }
            }

            // Фолбэк: оценка по слот-геометрии (карточки ещё не реализованы).
            var topLeft = ctrl.TranslatePoint(new Point(0, 0), this);
            if (topLeft is null) return _dragTargetIndex;

            double relX = pos.X - topLeft.Value.X;
            double relY = pos.Y - topLeft.Value.Y;

            int r = Math.Max(0, (int)(relY / _slotHeight));
            int c = Math.Max(0, (int)(relX / _slotWidth));
            c = Math.Min(c, _cardsPerRow - 1);

            return Math.Min(r * _cardsPerRow + c, folderVm.Characters.Count);
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

                // Сброс зависшего TranslateTransform при переиспользовании элемента
                // виртуализацией (recycle) и при реализации. Идемпотентно через -=/+=.
                repeater.ElementPrepared -= OnRepeaterElementPrepared;
                repeater.ElementPrepared += OnRepeaterElementPrepared;
                repeater.ElementClearing -= OnRepeaterElementClearing;
                repeater.ElementClearing += OnRepeaterElementClearing;
            }
        }

        private void OnRepeaterElementPrepared(object? sender, ItemsRepeaterElementPreparedEventArgs e)
            => ResetElementTransform(e.Element);

        private void OnRepeaterElementClearing(object? sender, ItemsRepeaterElementClearingEventArgs e)
            => ResetElementTransform(e.Element);

        // Мгновенно (без перехода) обнуляет TranslateTransform карточки.
        private static void ResetElementTransform(Control? element)
        {
            if (element?.RenderTransform is not TranslateTransform tt) return;
            if (tt.X == 0.0 && tt.Y == 0.0) return;
            var saved = tt.Transitions;
            tt.Transitions = null;
            tt.X = 0.0;
            tt.Y = 0.0;
            tt.Transitions = saved;
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
                // Применяем результат. FLIP на дропе НЕ нужен: плейсхолдер уже стоит ровно
                // на финальном месте, реальная карточка встаёт туда же — двигать нечего.
                // Раскладку НЕ пересобираем: мягкая чистка трансформов на каждом ходе уже
                // не даёт «дыркам» оставаться, а пересборка Layout целевой папки ломала
                // замер ячейки единственной карточки и резала ей аватар.
                if (_dragTargetFolderId is not null)
                    vm.CommitDragPreview(_dragCandidate.Id, _dragTargetFolderId, _dragTargetIndex);
                else
                    vm.CancelDragPreview(_dragCandidate.Id);
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
            StopAutoScroll();
        }

        private void ShowGhost(CharacterListItemViewModel item, Point pos)
        {
            if (_ghostCanvas is null || _ghostBorder is null || _ghostText is null) return;

            _ghostText.Text = item.Name;

            // Кисть строится тем же конвертером, что и на карточке: код цвета
            // может быть градиентом ("grad|..."), и простой Color.TryParse ронял
            // призрак в дефолтный серо-синий.
            var brush = Writersword.Infrastructure.Converters.ColorCodeToBrushConverter.Instance
                    .Convert(item.Color, typeof(IBrush), null,
                        System.Globalization.CultureInfo.InvariantCulture) as IBrush
                ?? new SolidColorBrush(Color.FromRgb(96, 125, 139));
            _ghostBorder.BorderBrush = brush;
            _ghostBorder.BorderThickness = new Thickness(item.FrameThickness);

            // Закладка группы — как на реальной карточке.
            var bookmark = this.FindControl<Avalonia.Controls.Shapes.Path>("DragGhostBookmark");
            if (bookmark is not null)
            {
                bookmark.IsVisible = item.ShowGroupBookmark;
                bookmark.Fill = brush;
            }

            // Кружок-аватар: заливка цветом персонажа, поверх — картинка (если есть),
            // иначе запасной значок. Делает призрак похожим на реальную карточку.
            var avatarBg = this.FindControl<Border>("DragGhostAvatarBg");
            if (avatarBg is not null) avatarBg.Background = brush;

            var bmp = item.AvatarBitmap;
            var avatar = this.FindControl<Image>("DragGhostAvatar");
            if (avatar is not null) avatar.Source = bmp;

            var fallback = this.FindControl<TextBlock>("DragGhostFallback");
            if (fallback is not null)
            {
                fallback.Text = string.IsNullOrEmpty(item.FallbackIcon) ? "?" : item.FallbackIcon;
                fallback.IsVisible = bmp is null;
            }

            // Ширина призрака = ширина перетаскиваемой карточки (замерена при нажатии;
            // в списке — во всю строку).
            _ghostBorder.Width = _ghostWidth;

            MoveGhost(pos);
            _ghostCanvas.IsVisible = true;
        }

        private void MoveGhost(Point pos)
        {
            if (_ghostBorder is null || DataContext is not CharactersViewModel vm) return;
            Canvas.SetLeft(_ghostBorder, pos.X - _ghostWidth / 2.0);
            Canvas.SetTop(_ghostBorder, pos.Y - vm.CardTotalHeight / 2.0);
        }

        private void HideGhost()
        {
            if (_ghostCanvas is not null) _ghostCanvas.IsVisible = false;
        }

        private void StartAutoScroll()
        {
            if (_autoScrollTimer is null)
            {
                _autoScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                _autoScrollTimer.Tick += OnAutoScrollTick;
            }
            _autoScrollVel = 0;
            _autoScrollTimer.Start();
        }

        private void StopAutoScroll()
        {
            _autoScrollTimer?.Stop();
            _autoScrollVel = 0;
            _dragScroll = null;
        }

        // Скорость автопрокрутки тем больше, чем ближе курсор к верхнему/нижнему краю
        // прокручиваемой области (как страница в браузере).
        private void UpdateAutoScrollVelocity(Point pos)
        {
            _autoScrollVel = 0;
            if (_dragScroll is null) return;
            var tl = _dragScroll.TranslatePoint(new Point(0, 0), this);
            if (tl is null) return;
            double top = tl.Value.Y;
            double bottom = top + _dragScroll.Bounds.Height;
            const double zone = 90.0;
            const double maxSpeed = 24.0;
            if (pos.Y < top + zone)
                _autoScrollVel = -maxSpeed * Math.Clamp((top + zone - pos.Y) / zone, 0, 1);
            else if (pos.Y > bottom - zone)
                _autoScrollVel = maxSpeed * Math.Clamp((pos.Y - (bottom - zone)) / zone, 0, 1);
        }

        private void OnAutoScrollTick(object? sender, EventArgs e)
        {
            if (!_isDragging || _dragScroll is null || Math.Abs(_autoScrollVel) < 0.5) return;
            var off = _dragScroll.Offset;
            double maxY = Math.Max(0, _dragScroll.Extent.Height - _dragScroll.Viewport.Height);
            double newY = Math.Clamp(off.Y + _autoScrollVel, 0, maxY);
            if (Math.Abs(newY - off.Y) < 0.1) return;
            _dragScroll.Offset = new Vector(off.X, newY);
            MoveGhost(_lastDragPos);
            UpdatePreview(_lastDragPos);
        }

        private void OnGlobalPointerWheel(object? sender, PointerWheelEventArgs e)
        {
            if (!_isDragging || _dragScroll is null) return;
            var off = _dragScroll.Offset;
            double maxY = Math.Max(0, _dragScroll.Extent.Height - _dragScroll.Viewport.Height);
            double newY = Math.Clamp(off.Y - e.Delta.Y * 60.0, 0, maxY);
            _dragScroll.Offset = new Vector(off.X, newY);
            var p = e.GetPosition(this);
            _lastDragPos = p;
            MoveGhost(p);
            UpdatePreview(p);
            e.Handled = true;
        }
    }
}