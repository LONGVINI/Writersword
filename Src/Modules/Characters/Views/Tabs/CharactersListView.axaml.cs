using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Writersword.Modules.Characters.Models;
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

        // Карточка, по которой нажали, и точка нажатия — для выбора, а не для
        // перетаскивания. Держатся отдельно от _dragCandidate потому, что тот
        // обнуляется в OnGlobalPointerMoved при любом сдвиге раньше паузы
        // удержания: обычный клик почти всегда дёргает мышь на пару точек, и
        // выбор по нему не срабатывал вовсе.
        private CharacterListItemViewModel? _clickCandidate;
        private Point _clickStartPoint;

        // Насколько далеко можно увести указатель, чтобы отпускание всё ещё
        // считалось кликом по карточке.
        private const double ClickSlack = 6.0;
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

        // ── Разделитель боковой панели ────────────────────────────────────
        //
        // Только ширина. Выключателя у панели нет вовсе: её открывает щелчок
        // по карточке и закрывает крестик в её углу или повторный щелчок по
        // той же карточке. Разделитель показывается вместе с панелью, поэтому
        // при закрытой панели у правого края списка не остаётся ничего.

        private bool _splitDragging;
        private Point _splitPressPoint;
        private double _splitStartWidth;

        private void OnInspectorSplitPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Border split) return;
            if (DataContext is not CharactersViewModel vm) return;
            if (!e.GetCurrentPoint(split).Properties.IsLeftButtonPressed) return;

            _splitDragging = true;
            _splitPressPoint = e.GetPosition(this);
            _splitStartWidth = vm.InspectorWidth;

            e.Pointer.Capture(split);
            e.Handled = true;
        }

        private void OnInspectorSplitMoved(object? sender, PointerEventArgs e)
        {
            if (!_splitDragging) return;
            if (DataContext is not CharactersViewModel vm) return;

            // Тянут влево — панель шире, вправо — уже.
            vm.InspectorWidth = _splitStartWidth + (_splitPressPoint.X - e.GetPosition(this).X);
            e.Handled = true;
        }

        private void OnInspectorSplitReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!_splitDragging) return;

            _splitDragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }

        public void PerformUndo()
        {
            if (DataContext is CharactersViewModel vm && !vm.IsReadOnly && vm.CanUndo) vm.Undo();
        }

        public void PerformRedo()
        {
            if (DataContext is CharactersViewModel vm && !vm.IsReadOnly && vm.CanRedo) vm.Redo();
        }

        // Сравнение показанных персонажей. Берём то, что сейчас в списке:
        // фильтры, поиск и папки уже сделали выбор, а второй механизм
        // выделения означал бы делать ту же работу дважды.
        private void OnCompareClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;

            if (DataContext is not CharactersViewModel vm) return;

            var host = this.FindAncestorOfType<CharactersModuleView>();
            var overlay = host?.FindControl<CharacterComparisonOverlay>("ComparisonOverlayControl");
            if (overlay == null) return;

            var characters = vm.FilteredCharacters
                .Select(item => vm.GetCharacter(item.Id))
                .Where(c => c != null)
                .Select(c => c!)
                .ToList();

            overlay.ShowFor(characters);
        }

        // Ступень важности папки по кругу: нет, I, II, III. Событие гасится —
        // иначе щелчок дойдёт до заголовка и свернёт папку.
        private void OnFolderImportanceClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is not Control c || c.DataContext is not CharacterFolderViewModel folder) return;

            folder.CycleImportance();
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

            SetupContainerBindings();
        }

        /// <summary>
        /// Подписки снимаются при отсоединении вьюхи, поэтому и ставить их
        /// нужно при каждом присоединении, а не только в Loaded: это событие
        /// поднимается при первой загрузке контрола и при повторном входе в
        /// рабочий режим не срабатывает.
        ///
        /// Без них вьюмодель переставала получать ширину контейнера, а
        /// раскладка — число колонок и минимальную ширину карточки. Ширина
        /// оставалась той, что была задана значением по умолчанию, и карточки
        /// после возврата в режим выходили сплющенными.
        /// </summary>
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _attachedToTree = true;
            SetupContainerBindings();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _attachedToTree = false;

            if (_foldersWatched is not null)
            {
                _foldersWatched.CollectionChanged -= OnFoldersChangedForLayout;
                _foldersWatched = null;
            }
        }

        // Вьюха сейчас в визуальном дереве. Смена контекста данных снимает
        // подписки, и восстанавливать их имеет смысл только когда вьюха на
        // месте: иначе подписка повиснет на контейнере, которого ещё нет.
        private bool _attachedToTree;

        private void SetupContainerBindings()
        {
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

                // Подписка отдаёт текущее значение сразу, но в этот момент
                // визуального дерева ещё нет: UpdateGridLayouts обходит
                // репитеры и не находит ни одного. Дальше число колонок не
                // меняется, повторно подписка не срабатывает, и раскладка
                // остаётся с минимальной шириной карточки из разметки — сто
                // пятьдесят две точки, то есть размером «средние».
                //
                // Отсюда и сплющенные карточки после возврата в рабочий
                // режим: число колонок считает сама раскладка по этой ширине,
                // а не вьюмодель по выбранному размеру. Повторный вызов после
                // построения дерева ставит настоящие значения.
                ScheduleGridLayoutsUpdate();

                // Репитеры живут внутри шаблона папки и пересоздаются вместе
                // со списком папок: при смене размера карточек, при повторном
                // входе в режим, при перезагрузке данных. Каждый новый берёт
                // минимальную ширину карточки из разметки — сто пятьдесят две
                // точки, то есть размер «средние», — и делит на неё ширину
                // контейнера сам. Поэтому настройку приходится ставить заново
                // после каждой пересборки списка, а не один раз при подписке.
                if (!ReferenceEquals(_foldersWatched, vmAvatar.Folders))
                {
                    if (_foldersWatched is not null)
                        _foldersWatched.CollectionChanged -= OnFoldersChangedForLayout;

                    _foldersWatched = vmAvatar.Folders;
                    _foldersWatched.CollectionChanged += OnFoldersChangedForLayout;
                }
            }

            var foldersContainer = this.FindControl<ItemsControl>("FoldersContainer");
            if (foldersContainer is not null)
            {
                if (DataContext is CharactersViewModel vmInit && foldersContainer.Bounds.Width > 0)
                    vmInit.UpdateContainerWidth(foldersContainer.Bounds.Width);
                _containerBoundsSubscription?.Dispose();
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
                _containerBoundsSubscription?.Dispose();
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

            // Подписки сняты со старого контекста — сразу ставим их на новый.
            // При возврате в рабочий режим контекст переустанавливается уже
            // после присоединения вьюхи, и без этого вызова она оставалась
            // без подписок: вьюмодель не получала ширину контейнера, а
            // раскладка — число колонок, отчего карточки сплющивались.
            if (_attachedToTree) SetupContainerBindings();
        }

        protected override void OnUnloaded(RoutedEventArgs e)
        {
            base.OnUnloaded(e);
            _containerBoundsSubscription?.Dispose();
            _containerBoundsSubscription = null;
            _cardsPerRowSubscription?.Dispose();
            _cardsPerRowSubscription = null;
        }

        // Список папок, на пересборку которого поставлена настройка раскладки.
        private System.Collections.Specialized.INotifyCollectionChanged? _foldersWatched;

        private void OnFoldersChangedForLayout(
            object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
            => ScheduleGridLayoutsUpdate();

        /// <summary>
        /// Поставить настройку раскладки в очередь на после построения дерева.
        /// Сразу её применять бесполезно: репитеров ещё нет, и обход по
        /// визуальному дереву не находит ни одного.
        /// </summary>
        private void ScheduleGridLayoutsUpdate()
        {
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (DataContext is CharactersViewModel vm)
                        UpdateGridLayouts(vm.CardsPerRow);
                },
                DispatcherPriority.Loaded);
        }

        private void UpdateGridLayouts(int cols)
        {
            var vm = DataContext as CharactersViewModel;
            double minItemWidth = vm?.CardMinWidth ?? 152.0;

            // Высота ячейки — высота карточки плюс её поля, те же 12 точек,
            // что заложены в CardMinWidth. Без неё раскладка выводит высоту
            // строки из первого измеренного элемента и при неудачном первом
            // проходе реализует всю папку разом.
            double minItemHeight = (vm?.CardTotalHeight ?? 108.0) + 12.0;

            foreach (var repeater in this.GetVisualDescendants().OfType<ItemsRepeater>())
            {
                switch (repeater.Layout)
                {
                    case Controls.UniformCardGridLayout cardLayout:
                        if (cardLayout.MaxColumns != cols)
                            cardLayout.MaxColumns = cols;
                        if (Math.Abs(cardLayout.MinItemWidth - minItemWidth) > 0.5)
                            cardLayout.MinItemWidth = minItemWidth;
                        break;

                    case UniformGridLayout layout:
                        if (layout.MaximumRowsOrColumns != cols)
                            layout.MaximumRowsOrColumns = cols;
                        if (Math.Abs(layout.MinItemWidth - minItemWidth) > 0.5)
                            layout.MinItemWidth = minItemWidth;
                        if (Math.Abs(layout.MinItemHeight - minItemHeight) > 0.5)
                            layout.MinItemHeight = minItemHeight;
                        break;
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
            if (DataContext is CharactersViewModel vm && !vm.IsReadOnly)
            {
                if (e.Key == Key.Z && e.KeyModifiers == KeyModifiers.Control)
                { if (vm.CanUndo) { vm.Undo(); e.Handled = true; return; } }
                if (e.Key == Key.Y && e.KeyModifiers == KeyModifiers.Control)
                { if (vm.CanRedo) { vm.Redo(); e.Handled = true; return; } }
            }

            if (e.Key is not (Key.Return or Key.Enter or Key.Escape)) return;
            var charVm = FindCharacterItemVm(e.Source as Visual);

            // Escape вне карточки снимает выделение и убирает панель. Внутри
            // карточки он раньше отменял ввод имени, и это важнее: тот случай
            // разбирается ниже и до сюда не доходит.
            if (charVm is null)
            {
                if (e.Key == Key.Escape && DataContext is CharactersViewModel vmEscape
                    && vmEscape.HasSelection)
                {
                    vmEscape.ClearSelection();
                    e.Handled = true;
                }
                return;
            }
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
                && !vm.IsReadOnly
                && !charVm.IsBeingNamed
                && !charVm.IsRenaming
                && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _dragCandidate = charVm;
                _clickCandidate = charVm;
                _clickStartPoint = e.GetPosition(this);
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
                _clickCandidate = null;
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
                    // Свёрнутая цель не показывает карточек — поштучно двигаются
                    // только карточки папки-источника, остальные едут блоком.
                    var snapshot = SnapshotPositions(FindPlaceholderFolder(vm), targetFolderVm);
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
                vm.UpdateDragPreview(_dragCandidate.Id, _dragTargetFolderId, _dragTargetIndex);
                BeginFlipAnimation(snap);
            }
            else
            {
                // Кросс-папка: поштучно двигаются только карточки папки-источника
                // (откуда уходит плейсхолдер) и папки-цели. Карточки остальных
                // папок смещаются единым блоком вместе со своей папкой — их
                // поштучный FLIP стоил сотни переводов координат на каждый шаг
                // и давал рывок при пересечении границы папок; застрявшие
                // трансформы таких карточек доводит мягкая чистка в
                // BeginFlipAnimation.
                snap = SnapshotPositions(FindPlaceholderFolder(vm), targetFolderVm);
                vm.UpdateDragPreview(_dragCandidate.Id, _dragTargetFolderId, _dragTargetIndex);
                BeginFlipAnimation(snap);
            }
        }

        // Снимаем визуальные позиции (с текущим TranslateTransform) в координатах
        // репитера, а не вьюпорта: прокрутка не должна порождать ложные дельты FLIP.
        // Не сбрасываем — карточки в середине анимации не прерываются.
        private Dictionary<string, Point> SnapshotPositions() => SnapshotPositions(null, 0, 0);

        // Папка, в которой сейчас находится плейсхолдер перетаскивания.
        private static CharacterFolderViewModel? FindPlaceholderFolder(CharactersViewModel vm)
        {
            foreach (var folder in vm.Folders)
                for (int i = 0; i < folder.Characters.Count; i++)
                    if (folder.Characters[i].IsPlaceholder)
                        return folder;
            return null;
        }

        // Снимок карточек двух папок — источника и цели кросс-папочного шага.
        private Dictionary<string, Point> SnapshotPositions(
            CharacterFolderViewModel? first, CharacterFolderViewModel? second)
        {
            var ids = new HashSet<string>();
            if (first is not null)
                foreach (var c in first.Characters) ids.Add(c.Id);
            if (second is not null)
                foreach (var c in second.Characters) ids.Add(c.Id);

            var result = new Dictionary<string, Point>();
            foreach (var (id, border, repeater) in EnumerateLiveCards())
            {
                if (!ids.Contains(id)) continue;
                var pt = border.TranslatePoint(new Point(0, 0), repeater);
                if (pt.HasValue) result[id] = pt.Value;
            }
            return result;
        }

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

                // Удаление аватара показывается кнопкой в нижней панели пикера,
                // а не отдельным меню на карточке: клик по аватарке сразу ведёт
                // к выбору фото. Действие передаётся только когда удалять есть
                // что — при пустом аватаре кнопка в пикере не появляется.
                Action? deleteAvatarAction = string.IsNullOrEmpty(item.AvatarPath)
                    ? null
                    : item.RemoveAvatar;

                // Выбор аватара — оверлей по центру модуля (как редактор цвета),
                // а не отдельное системное окно.
                var host = this.FindAncestorOfType<CharactersModuleView>();
                var overlay = host?.FindControl<CharacterAvatarPickerOverlay>("AvatarPickerOverlayControl");
                if (overlay != null)
                    return await overlay.ShowAsync(
                        _avatarService, item.Id, deleteAvatarAction, item.AvatarPath, item);

                // Запасной путь, если вью показана вне модуля: прежнее окно.
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

        // ── Приём картинки, брошенной на карточку ─────────────────────────
        //
        // Файл, отпущенный на карточку, становится аватаром её персонажа.
        // Порядок шагов тот же, что и в пикере: сначала поиск уже сохранённой
        // копии (одна фотография не должна лечь в проект дважды), затем
        // обрезка, и только потом сохранение — отменённая обрезка не оставляет
        // за собой файла.

        private static readonly ILogger _dropLogger = Log.ForContext<CharactersListView>();

        private static CharacterListItemViewModel? CardItemOf(object? sender) =>
            (sender as Control)?.DataContext as CharacterListItemViewModel;

        private static void SetCardDropTarget(CharacterListItemViewModel? item, bool value)
        {
            if (item != null) item.IsImageDropTarget = value;
        }

        private void OnCardImageDragOver(object? sender, DragEventArgs e)
        {
            var item = CardItemOf(sender);
            var accepts = item != null && e.DataTransfer.Contains(DataFormat.File);

            e.DragEffects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
            SetCardDropTarget(item, accepts);
            e.Handled = true;
        }

        private void OnCardImageDragLeave(object? sender, DragEventArgs e)
        {
            SetCardDropTarget(CardItemOf(sender), false);
            e.Handled = true;
        }

        private async void OnCardImageDrop(object? sender, DragEventArgs e)
        {
            e.Handled = true;

            var item = CardItemOf(sender);
            SetCardDropTarget(item, false);
            if (item == null || _avatarService == null) return;

            var files = e.DataTransfer.TryGetFiles();
            if (files == null) return;

            // Аватар у персонажа один, поэтому из брошенной пачки берётся
            // первая пригодная картинка. Остальные молча пропускаются: открыть
            // подряд пять окон обрезки ради одного аватара — не помощь.
            foreach (var file in files)
            {
                if (file is not IStorageFile storageFile) continue;
                if (!CharacterAvatarPickerOverlay.IsDroppableImage(storageFile.Name)) continue;

                try
                {
                    byte[] bytes;
                    await using (var stream = await storageFile.OpenReadAsync())
                    using (var buffer = new MemoryStream())
                    {
                        await stream.CopyToAsync(buffer);
                        bytes = buffer.ToArray();
                    }

                    await ApplyDroppedAvatarAsync(item, bytes, storageFile.Name);
                }
                catch (Exception ex)
                {
                    // Бросить могут что угодно — папку, ярлык, недоступный файл.
                    _dropLogger.Error(ex, "Card avatar drop failed: {Name}", storageFile.Name);
                }

                return;
            }
        }

        /// <summary>
        /// Поставить брошенную картинку аватаркой персонажа. Открыт наружу
        /// ради боковой панели: она принимает файл так же, как карточка, а
        /// повторять здесь весь порядок — поиск уже сохранённой копии,
        /// обрезку, сохранение — значило бы завести второй такой же порядок,
        /// который разойдётся с этим при первой же правке.
        /// </summary>
        public async Task ApplyDroppedAvatarAsync(
            CharacterListItemViewModel item, byte[] bytes, string fileName)
        {
            if (_avatarService == null || bytes.Length == 0) return;

            string? baseRef;
            try { baseRef = _avatarService.FindStoredByContent(bytes); }
            catch (Exception ex)
            {
                _dropLogger.Error(ex, "FindStoredByContent failed");
                baseRef = null;
            }

            var reused = baseRef != null;

            var crops = await ShowCropForDroppedBytesAsync(bytes, item);
            if (crops == null) return;

            if (!reused)
            {
                baseRef = await _avatarService.SaveToProjectAsync(bytes, fileName);
                if (baseRef == null) return;
            }

            var combined = CharacterAvatarRef.Combine(baseRef, crops.Circle, crops.Strip);
            if (combined == null) return;

            // ApplyAvatarRef сообщает о смене наружу — карточка и модель
            // персонажа расходиться не должны.
            item.ApplyAvatarRef(combined);
            _avatarService.AddRecentAvatar(combined);
        }

        /// <summary>
        /// Показать обрезку для ещё не сохранённой картинки. Если окна обрезки
        /// в разметке нет, картинка берётся целиком: остаться без аватарки
        /// из-за неподключённого окна хуже, чем взять её неподрезанной.
        /// </summary>
        private async Task<CharacterAvatarCropPair?> ShowCropForDroppedBytesAsync(
            byte[] bytes, CharacterListItemViewModel item)
        {
            var host = this.FindAncestorOfType<CharactersModuleView>();
            var overlay = host?.FindControl<CharacterAvatarCropOverlay>("AvatarCropOverlayControl");
            if (overlay == null) return new CharacterAvatarCropPair(CharacterAvatarCrop.Full, null);

            Bitmap? bitmap = null;
            try
            {
                using var ms = new MemoryStream(bytes);
                bitmap = new Bitmap(ms);
                // Карточка показана полоской — открываем сразу на её кадре:
                // человек бросил картинку на то, что видит, и правит он то же.
                return await overlay.ShowAsync(bitmap, null, null, item, null, item.AvatarStrip);
            }
            catch (Exception ex)
            {
                _dropLogger.Error(ex, "Crop for dropped avatar failed");
                return new CharacterAvatarCropPair(CharacterAvatarCrop.Full, null);
            }
            finally
            {
                bitmap?.Dispose();
            }
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

                    // Найденное кладётся в кэш. Без этого промах не разовый:
                    // заголовок искался полным обходом дерева вида заново на
                    // каждом шаге предпросмотра, и так по каждой папке, которой
                    // в кэше нет. RebuildDragCaches наполняет кэш только из
                    // реализованных заголовков, поэтому свёрнутая или уехавшая
                    // за край папка промахивалась всё перетаскивание.
                    if (headerControl is not null)
                        _folderHeaderCache[folderVm.FolderId] = headerControl;
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

                // Нажали на карточку и отпустили, не уведя указатель — это
                // выбор. Место выбрано именно здесь, а не в нажатии: в нажатии
                // ещё не известно, клик это или начало перетаскивания, и
                // выделение прыгало бы на каждую попытку что-нибудь потащить.
                var clicked = _isDragging ? null : _clickCandidate;
                if (clicked is not null)
                {
                    var travel = e.GetPosition(this) - _clickStartPoint;
                    if (Math.Abs(travel.X) > ClickSlack || Math.Abs(travel.Y) > ClickSlack)
                        clicked = null;
                }

                ClearPicked();
                _dragCandidate = null;
                _clickCandidate = null;
                _isDragging = false;

                if (clicked is not null && DataContext is CharactersViewModel vmSelect)
                {
                    var additive = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) != 0;
                    vmSelect.SelectCard(clicked, additive);
                }
                return;
            }

            if (_hasPointerCapture)
            {
                e.Pointer.Capture(null);
                _hasPointerCapture = false;
            }

            ClearDragVisuals();
            _clickCandidate = null;

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

            // Пересчёт позиции вставки — под тем же троттлингом, что и в
            // OnGlobalPointerMoved. Таймер тикает каждые 16 мс, и без ограничения
            // UpdatePreview (перестановка коллекции + FLIP + синхронный UpdateLayout)
            // выполнялся до 60 раз в секунду на всём протяжении автопрокрутки —
            // ввод и отрисовка на это время замирали.
            var now = Environment.TickCount64;
            if (now - _lastPreviewTick >= PreviewRecalcThrottleMs)
            {
                _lastPreviewTick = now;
                UpdatePreview(_lastDragPos);
            }
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

            // Тот же троттлинг, что и в OnGlobalPointerMoved: быстрые щелчки колеса
            // шли подряд и каждый запускал полный пересчёт вставки с UpdateLayout.
            var now = Environment.TickCount64;
            if (now - _lastPreviewTick >= PreviewRecalcThrottleMs)
            {
                _lastPreviewTick = now;
                UpdatePreview(p);
            }
            e.Handled = true;
        }
    }
}