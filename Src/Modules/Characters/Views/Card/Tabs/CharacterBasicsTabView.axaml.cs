using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Writersword.Modules.Characters.ViewModels;
using Writersword.Modules.Characters.ViewModels.Tabs;
using Writersword.Modules.Characters.Views;
using Writersword.Modules.Characters.Views.Avatars;
using Writersword.Modules.Characters.Models;

namespace Writersword.Modules.Characters.Views.Card.Tabs
{
    public partial class CharacterBasicsTabView : UserControl
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterBasicsTabView>();

        public CharacterBasicsTabView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;

            // Блоки, свёрнутые в этом сеансе, остаются свёрнутыми и у следующей
            // открытой карточки.
            ApplyStoredCollapsedBlocks();

            // Перенос плитки ведётся на уровне всей карточки: во время
            // переноса порядок меняется на лету, и список пересоздаёт плитки —
            // указатель, захваченный самой плиткой, при этом теряется вместе
            // с ней, и перенос обрывается. Обработчик туннельный, как в списке
            // персонажей: он получает событие раньше прокрутки, поэтому она
            // не уводит галерею во время переноса.
            AddHandler(PointerPressedEvent, OnCardPointerPressed, RoutingStrategies.Tunnel);
            AddHandler(PointerMovedEvent, OnCardPointerMoved, RoutingStrategies.Tunnel);
            AddHandler(PointerReleasedEvent, OnCardPointerReleased, RoutingStrategies.Tunnel);

            // Колесо во время переноса тоже листает галерею. Обработчик на
            // карточке, а не на самой галерее: курсор с картинкой может уйти
            // за её пределы, и событие туда уже не придёт.
            AddHandler(PointerWheelChangedEvent, OnCardPointerWheel, RoutingStrategies.Tunnel);
        }

        // Нажатие тоже ловится на карточке, а не на плитке: обработчик плитки
        // получает событие последним, и любой узел между ней и корнем может
        // событие погасить — тогда перенос не начнётся вовсе. Тем же порядком
        // работает список персонажей.
        private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            ClearTilePress();

            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

            var tile = FindTileFromSource(e.Source as Visual);
            if (tile?.DataContext is not CharacterGalleryItemViewModel item) return;
            if (item.IsAddTile || item.IsPlaceholder) return;

            _tilePressOrigin = e.GetPosition(this);
            _pressedTileRef = item.ImageRef;

            _logger.Debug("[GalleryDrag] press on {Ref}", item.ImageRef);
        }

        /// <summary>Плитка галереи, внутри которой лежит источник события.</summary>
        private static Panel? FindTileFromSource(Visual? source)
        {
            for (var v = source; v != null; v = v.GetVisualParent())
                if (v is Panel panel && panel.Classes.Contains("tile"))
                    return panel;

            return null;
        }

        private void OnCardPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_tilePressOrigin is not { } origin) return;
            if (_pressedTileRef is not { } imageRef) return;
            if (DataContext is not CharacterBasicsTabViewModel vm) return;

            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                if (_galleryDragging) CancelGalleryDrag(e);
                else ClearTilePress();
                return;
            }

            var position = e.GetPosition(this);

            if (!_galleryDragging)
            {
                var dx = position.X - origin.X;
                var dy = position.Y - origin.Y;
                if (Math.Sqrt(dx * dx + dy * dy) < LabelDragThreshold) return;

                var tile = FindTileByRef(imageRef);
                if (tile == null)
                {
                    _logger.Debug("[GalleryDrag] tile for {Ref} not found", imageRef);
                    return;
                }

                // Призрак снимается с плитки до того, как она уйдёт из сетки:
                // после старта её место занимает копия, и брать размер и
                // картинку будет уже не с чего.
                ShowGalleryGhost(tile, position);

                if (!vm.BeginGalleryDrag(imageRef))
                {
                    _logger.Debug("[GalleryDrag] begin rejected for {Ref}", imageRef);
                    HideGalleryGhost();
                    ClearTilePress();
                    return;
                }

                _logger.Debug("[GalleryDrag] started at index {Index} of {Count}",
                    vm.GalleryPlaceholderIndex, vm.GalleryImageCount);

                _galleryDragging = true;
                _lastGalleryPreviewTick = Environment.TickCount64;
                _lastGalleryDragPos = position;

                // Прокрутка берётся по имени, а не поиском по дереву: вокруг
                // галереи есть и другие прокручиваемые области, и ближайшая
                // к плитке не обязательно та, которую надо двигать.
                _galleryScroll = this.FindControl<ScrollViewer>("GalleryScroll");
                StartGalleryAutoScroll();

                // Указатель захватывается карточкой, а не плиткой: во время
                // переноса плитки пересоздаются, и захват ушёл бы вместе с той,
                // за которой он числился.
                e.Pointer.Capture(this);
                return;
            }

            _lastGalleryDragPos = position;
            MoveGalleryGhost(position);
            UpdateGalleryAutoScrollVelocity(position);

            UpdateGalleryPreview(position, vm);
        }

        /// <summary>
        /// Пересчёт места вставки. Троттлинг общий на все источники движения —
        /// указатель, автопрокрутку и колесо: перестановка тянет за собой
        /// синхронную раскладку, и на каждом кадре её делать незачем.
        /// </summary>
        private void UpdateGalleryPreview(Point position, CharacterBasicsTabViewModel vm)
        {
            var now = Environment.TickCount64;
            if (now - _lastGalleryPreviewTick < GalleryPreviewThrottleMs) return;
            _lastGalleryPreviewTick = now;

            var target = ComputeGalleryTargetIndex(position, vm);
            var current = vm.GalleryPlaceholderIndex;
            if (target == current) return;

            var before = SnapshotTilePositions();
            vm.UpdateGalleryDrag(target);

            _logger.Debug("[GalleryDrag] {From} -> {To}, now {Now}, tiles {Tiles}",
                current, target, vm.GalleryPlaceholderIndex, before.Count);

            BeginTileFlip(before);
        }

        // ── прокрутка галереи во время переноса ───────────────────────────
        // Картинку несут к краю — галерея едет сама, как страница в браузере.
        // Едет только она: карточка под ней при переносе стоит на месте,
        // иначе уезжает вся вкладка, а нужен ряд картинок.

        private ScrollViewer? _galleryScroll;
        private DispatcherTimer? _galleryAutoScrollTimer;
        private double _galleryAutoScrollVel;
        private Point _lastGalleryDragPos;

        private void StartGalleryAutoScroll()
        {
            if (_galleryAutoScrollTimer == null)
            {
                _galleryAutoScrollTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(16)
                };
                _galleryAutoScrollTimer.Tick += OnGalleryAutoScrollTick;
            }

            _galleryAutoScrollVel = 0;
            _galleryAutoScrollTimer.Start();
        }

        private void StopGalleryAutoScroll()
        {
            _galleryAutoScrollTimer?.Stop();
            _galleryAutoScrollVel = 0;
        }

        /// <summary>
        /// Скорость тем выше, чем ближе курсор к краю галереи. У самого края
        /// она наибольшая, в середине — ноль. За пределами галереи — тоже
        /// наибольшая: картинку унесли за край, значит листать надо.
        /// </summary>
        private void UpdateGalleryAutoScrollVelocity(Point position)
        {
            _galleryAutoScrollVel = 0;
            if (_galleryScroll == null) return;

            var topLeft = _galleryScroll.TranslatePoint(new Point(0, 0), this);
            if (topLeft is not { } origin) return;

            const double zone = 60.0;
            const double maxSpeed = 18.0;

            double top = origin.Y;
            double bottom = top + _galleryScroll.Bounds.Height;

            if (position.Y < top + zone)
                _galleryAutoScrollVel = -maxSpeed * Math.Clamp((top + zone - position.Y) / zone, 0, 1);
            else if (position.Y > bottom - zone)
                _galleryAutoScrollVel = maxSpeed * Math.Clamp((position.Y - (bottom - zone)) / zone, 0, 1);
        }

        private void OnGalleryAutoScrollTick(object? sender, EventArgs e)
        {
            if (!_galleryDragging) return;
            if (Math.Abs(_galleryAutoScrollVel) < 0.5) return;
            if (DataContext is not CharacterBasicsTabViewModel vm) return;

            if (!ScrollGalleryBy(_galleryAutoScrollVel)) return;

            MoveGalleryGhost(_lastGalleryDragPos);
            UpdateGalleryPreview(_lastGalleryDragPos, vm);
        }

        /// <summary>Сдвиг галереи. Ложь — упёрлись в край или ехать нечем.</summary>
        private bool ScrollGalleryBy(double delta)
        {
            if (_galleryScroll == null) return false;

            var offset = _galleryScroll.Offset;
            double maxY = Math.Max(0, _galleryScroll.Extent.Height - _galleryScroll.Viewport.Height);
            double newY = Math.Clamp(offset.Y + delta, 0, maxY);

            if (Math.Abs(newY - offset.Y) < 0.1) return false;

            _galleryScroll.Offset = new Vector(offset.X, newY);
            return true;
        }

        private void OnCardPointerWheel(object? sender, PointerWheelEventArgs e)
        {
            if (!_galleryDragging) return;
            if (DataContext is not CharacterBasicsTabViewModel vm) return;

            ScrollGalleryBy(-e.Delta.Y * 60.0);

            var position = e.GetPosition(this);
            _lastGalleryDragPos = position;
            MoveGalleryGhost(position);
            UpdateGalleryPreview(position, vm);

            // Событие дальше не идёт: во время переноса колесо листает галерею,
            // а карточка под ней остаётся на месте.
            e.Handled = true;
        }

        private void OnCardPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!_galleryDragging)
            {
                ClearTilePress();
                return;
            }

            if (DataContext is CharacterBasicsTabViewModel vm)
                vm.CommitGalleryDrag();

            FinishGalleryDrag(e);
        }

        private void CancelGalleryDrag(PointerEventArgs e)
        {
            if (DataContext is CharacterBasicsTabViewModel vm)
                vm.CancelGalleryDrag();

            FinishGalleryDrag(e);
        }

        private void FinishGalleryDrag(PointerEventArgs e)
        {
            _galleryDragging = false;

            StopGalleryAutoScroll();
            e.Pointer.Capture(null);
            HideGalleryGhost();
            ResetTileTransforms();
            ClearTilePress();
        }

        /// <summary>Плитка, показывающая эту картинку.</summary>
        private Control? FindTileByRef(string imageRef) =>
            GalleryTiles().FirstOrDefault(t =>
                t.DataContext is CharacterGalleryItemViewModel item &&
                !item.IsAddTile &&
                string.Equals(item.ImageRef, imageRef, StringComparison.Ordinal));

        // Значок на месте пустого описания: щелчок по нему ставит курсор
        // в само поле. Поле лежит под значком, и без этого щелчок пришёлся бы
        // в пустоту.
        // ── Сворачивание блоков ───────────────────────────────────────────

        // Ключи свёрнутых блоков (Tag блока в разметке). Общие для всех карточек
        // и живут до конца сеанса: свернул «Классификацию» у одного персонажа —
        // она свёрнута и у следующего, которого откроешь.
        private static readonly HashSet<string> CollapsedBlocks = new(StringComparer.Ordinal);

        /// <summary>Щелчок по заголовку блока сворачивает его или разворачивает.</summary>
        private void OnBlockHeaderTapped(object? sender, TappedEventArgs e)
        {
            if (sender is not Control header) return;

            var block = FindBlockOf(header);
            if (block is null) return;

            bool collapse = !block.Classes.Contains("collapsed");
            block.Classes.Set("collapsed", collapse);

            if (block.Tag is string key)
            {
                if (collapse) CollapsedBlocks.Add(key);
                else CollapsedBlocks.Remove(key);
            }

            e.Handled = true;
        }

        /// <summary>Блок, которому принадлежит заголовок.</summary>
        private static Border? FindBlockOf(Control header)
        {
            for (var node = header.GetLogicalParent(); node is not null; node = node.GetLogicalParent())
                if (node is Border border && border.Classes.Contains("block"))
                    return border;

            return null;
        }

        private void ApplyStoredCollapsedBlocks()
        {
            if (CollapsedBlocks.Count == 0) return;

            foreach (var border in this.GetLogicalDescendants().OfType<Border>())
            {
                if (!border.Classes.Contains("block")) continue;
                if (border.Tag is string key && CollapsedBlocks.Contains(key))
                    border.Classes.Set("collapsed", true);
            }
        }

        // ── Шапка, уезжающая вправо ───────────────────────────────────────
        //
        // Шапка одна и та же в обоих положениях. Развёрнутая стоит во всю ширину
        // карточки поверх стопки, стопка начинается с отступа на её высоту.
        // Когда стопку прокрутили настолько, что шапка закрывает уже блоки, а не
        // пустое место над ними, шапка переезжает карточкой в правую колонку над
        // галереей: сужается к правому краю, опускается на поле колонки,
        // скругляется и обводится рамкой, а имя с описанием уходят под аватар.
        // Все её поля при этом остаются живыми. Переезд идёт по времени, а не по
        // прокрутке, и назад — так же.

        // Ширина шапки-карточки: правая колонка 284 без своего правого поля 20.
        private const double CompactHeaderWidth = 264;

        // Положение шапки-карточки: на верхнем поле правой колонки, вровень с галереей.
        private static readonly Thickness CompactHeaderMargin = new(0, 16, 20, 0);

        // Поля шапки: в карточке по бокам уже, иначе имени почти не остаётся места.
        // Верхнее поле одинаковое: от него считается свисание закладки группы.
        private static readonly Thickness ExpandedHeaderPadding = new(20, 14, 20, 14);
        private static readonly Thickness CompactHeaderPadding = new(12, 14, 12, 14);

        private static readonly Thickness ExpandedHeaderBorder = new(0, 0, 0, 1);
        private static readonly Thickness CompactHeaderBorder = new(1);

        private const double CompactHeaderCornerRadius = 8;

        // Отступ имени от аватара: справа от него в развёрнутой шапке и под ним
        // в карточке.
        private static readonly Thickness ExpandedTitleMargin = new(10, 0, 10, 0);
        private static readonly Thickness CompactTitleMargin = new(0, 2, 0, 0);

        // Кольцо аватара стоит на холсте не по центру: слева от него дуга с
        // кнопками цвета и настроек. Середина кольца — 72 при ширине холста 114,
        // то есть на 15 правее середины холста. Правое поле в 30 сдвигает холст
        // на 15 влево, и кольцо встаёт ровно по центру карточки.
        private static readonly Thickness CompactAvatarMargin = new(0, 0, 30, 0);

        // Просвет между шапкой-карточкой и галереей.
        private const double CompactGap = 12;

        // Верхнее поле стопки под развёрнутой шапкой.
        private const double BodyTopMargin = 16;

        // Шапка переезжает не раньше, чем стопку сдвинули хотя бы на столько:
        // у короткой карточки иначе хватало бы дрогнуть колесом.
        private const double CompactMinEnterOffset = 24;

        // Насколько выше точки переезда надо подняться, чтобы шапка вернулась.
        // Разрыв не даёт ей метаться туда-сюда на границе.
        private const double CompactLeaveBackOff = 32;

        private static readonly TimeSpan HeaderMoveDuration = TimeSpan.FromMilliseconds(350);

        // Высота развёрнутой шапки: от неё отступ стопки и точка переезда. В
        // карточке шапка выше — имя стоит под аватаром, — но стопке до этого
        // дела нет: её отступ не меняется, чтобы прокрутка не прыгала.
        private double _expandedHeaderHeight;

        // Высота шапки-карточки: от неё место над галереей.
        private double _compactHeaderHeight;

        private bool _headerCompact;

        // Пока идёт переезд, размеры шапки меняет переход, и отвечать на них
        // пересчётом раскладки нельзя — он оборвал бы переход на полпути.
        private bool _headerMoving;

        // Номер переезда: завершение старого переезда, начатого до смены
        // направления, не должно трогать новый.
        private int _headerMoveVersion;

        private readonly TranslateTransform _avatarShift = new();
        private readonly TranslateTransform _titleShift = new();

        private void OnCardRootSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (_headerMoving) return;

            // Ширина развёрнутой шапки идёт за окном без перехода: тянуть её
            // с запаздыванием за краем окна незачем.
            ApplyHeaderLayout();
        }

        private void OnCardHeaderSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (_headerMoving) return;

            double height = e.NewSize.Height;

            if (_headerCompact)
            {
                // В карточке высота меняется, когда появляется или пропадает
                // описание: место над галереей идёт следом, без перехода.
                if (Math.Abs(height - _compactHeaderHeight) < 0.5) return;
                _compactHeaderHeight = height;
                ApplyHeaderLayout();
                return;
            }

            if (Math.Abs(height - _expandedHeaderHeight) < 0.5) return;

            _expandedHeaderHeight = height;

            // Отступ стопки — полная высота развёрнутой шапки в любом её
            // положении: высота прокрутки не меняется при переезде, и смещение
            // не прыгает. Когда шапка переезжает, этот отступ уже прокручен и не
            // виден.
            var stack = this.FindControl<StackPanel>("BodyStack");
            if (stack != null)
            {
                var m = stack.Margin;
                stack.Margin = new Thickness(m.Left, _expandedHeaderHeight + BodyTopMargin, m.Right, m.Bottom);
            }

            ApplyHeaderLayout();
            UpdateHeaderState();
        }

        private void OnBodyScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            UpdateHeaderState();

            if (sender is not ScrollViewer scroll) return;

            // Прокрутка, которую надо вернуть, ставится, как только карточка
            // разложилась достаточно; до этого вьюмодели не сообщается —
            // иначе промежуточный ноль затёр бы запомненное место.
            if (_pendingScrollY.HasValue)
            {
                TryApplyPendingScroll(scroll);
                return;
            }

            if (DataContext is CharacterBasicsTabViewModel vm)
                vm.BodyScrollY = scroll.Offset.Y;
        }

        // ── Возврат прокрутки ─────────────────────────────────────────────
        //
        // Место в карточке восстанавливается после перезапуска и пересоздания
        // модуля. Разделы анкет собираются не сразу, и высота стопки растёт
        // несколько проходов раскладки, поэтому прокрутка ставится по мере
        // роста — но не дольше короткого окна: после него это уже прокрутка
        // автора, а не восстановление.

        private static readonly TimeSpan PendingScrollWindow = TimeSpan.FromSeconds(1.5);

        private double? _pendingScrollY;
        private DateTime _pendingScrollSince;
        private CharacterBasicsTabViewModel? _scrollVm;

        private void OnPendingScrollRequested()
        {
            if (_scrollVm is null) return;

            var value = _scrollVm.TakePendingScroll();
            if (!value.HasValue) return;

            _pendingScrollY = value;
            _pendingScrollSince = DateTime.UtcNow;

            var scroll = this.FindControl<ScrollViewer>("BodyScroll");
            if (scroll != null)
                Dispatcher.UIThread.Post(() => TryApplyPendingScroll(scroll), DispatcherPriority.Loaded);
        }

        private void TryApplyPendingScroll(ScrollViewer scroll)
        {
            if (!_pendingScrollY.HasValue) return;

            var target = _pendingScrollY.Value;
            var max = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);

            if (max >= target - 0.5)
            {
                _pendingScrollY = null;
                scroll.Offset = new Vector(scroll.Offset.X, target);
                return;
            }

            if (DateTime.UtcNow - _pendingScrollSince > PendingScrollWindow)
            {
                // Карточка стала короче, чем была: встаём так низко, как можно.
                _pendingScrollY = null;
                scroll.Offset = new Vector(scroll.Offset.X, max);
                return;
            }

            scroll.Offset = new Vector(scroll.Offset.X, max);
        }

        private void UpdateHeaderState()
        {
            var scroll = this.FindControl<ScrollViewer>("BodyScroll");
            if (scroll is null || _expandedHeaderHeight <= 0) return;

            double spare = scroll.Extent.Height - scroll.Viewport.Height;
            double offset = scroll.Offset.Y;

            // Точка переезда — когда отступ под шапкой прокручен целиком. У
            // короткой карточки столько не прокрутить, и тогда переезд — в самом
            // конце прокрутки.
            double enterAt = Math.Max(CompactMinEnterOffset, Math.Min(_expandedHeaderHeight, spare - 1));

            if (!_headerCompact)
            {
                if (spare > CompactMinEnterOffset && offset >= enterAt)
                    SetHeaderCompact(true);
            }
            else
            {
                double leaveAt = Math.Max(0, enterAt - CompactLeaveBackOff);
                if (offset <= leaveAt)
                    SetHeaderCompact(false);
            }
        }

        /// <summary>
        /// Переезд шапки. Раскладка внутри неё меняется сразу — имя встаёт под
        /// аватар или обратно рядом, — а глазу это показывается переходом:
        /// аватар и имя стартуют с прежних мест и доезжают до новых, шапка
        /// плавно меняет ширину, высоту, поля и рамку.
        /// </summary>
        private void SetHeaderCompact(bool compact)
        {
            if (_headerCompact == compact) return;

            var root = this.FindControl<Grid>("CardRoot");
            var header = this.FindControl<Border>("CardHeader");
            var avatar = this.FindControl<Canvas>("HeaderAvatar");
            var title = this.FindControl<StackPanel>("HeaderTitle");
            var spacer = this.FindControl<Border>("SideHeaderSpacer");
            if (root is null || header is null || avatar is null || title is null) return;

            double rootWidth = root.Bounds.Width;
            if (rootWidth <= 0) return;

            int version = ++_headerMoveVersion;
            _headerMoving = true;

            // Прежнее состояние: откуда стартуют переходы. Текущий сдвиг
            // аватара и имени учитывается — переезд мог смениться обратным
            // посреди пути.
            // Значения снимаются до того, как переходы выключены: без них
            // свойства сразу встали бы на конечные значения прошлого переезда.
            double fromWidth = header.Bounds.Width;
            double fromHeight = header.Bounds.Height;
            Thickness fromMargin = header.Margin;
            Thickness fromPadding = header.Padding;
            Thickness fromBorder = header.BorderThickness;
            CornerRadius fromCorner = header.CornerRadius;
            Point? avatarFrom = avatar.TranslatePoint(new Point(0, 0), header);
            Point? titleFrom = title.TranslatePoint(new Point(0, 0), header);
            header.Transitions = null;

            _headerCompact = compact;
            root.Classes.Set("compact", compact);
            ApplyHeaderPlacement();

            // Конечная высота шапки: меряется в новой раскладке и новой ширине.
            // Размеры ставятся конечными только на время замера и тут же
            // возвращаются — переходы ещё выключены, поэтому ничего не мигнёт.
            double toWidth = compact ? Math.Min(CompactHeaderWidth, rootWidth) : rootWidth;
            Thickness toMargin = compact ? CompactHeaderMargin : default;
            Thickness toPadding = compact ? CompactHeaderPadding : ExpandedHeaderPadding;
            Thickness toBorder = compact ? CompactHeaderBorder : ExpandedHeaderBorder;
            CornerRadius toCorner = compact ? new CornerRadius(CompactHeaderCornerRadius) : default;

            header.Height = double.NaN;
            header.Width = toWidth;
            header.Margin = toMargin;
            header.Padding = toPadding;
            header.BorderThickness = toBorder;
            header.Measure(new Size(rootWidth, double.PositiveInfinity));
            double toHeight = header.DesiredSize.Height - toMargin.Top - toMargin.Bottom;

            if (compact) _compactHeaderHeight = toHeight;

            header.Width = fromWidth;
            header.Height = fromHeight;
            header.Margin = fromMargin;
            header.Padding = fromPadding;
            header.BorderThickness = fromBorder;
            header.CornerRadius = fromCorner;

            header.Transitions = CreateHeaderTransitions();
            header.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
            header.Width = toWidth;
            header.Height = toHeight;
            header.Margin = toMargin;
            header.Padding = toPadding;
            header.BorderThickness = toBorder;
            header.CornerRadius = toCorner;

            if (spacer != null)
            {
                spacer.Transitions = CreateSpacerTransitions();
                spacer.Height = compact ? toHeight + CompactGap : _expandedHeaderHeight;
            }

            // Аватар и имя: новое место известно только после раскладки, поэтому
            // она проводится сразу, до отрисовки, и сдвиг к прежнему месту
            // ставится в том же кадре — глазу скачок не виден.
            header.UpdateLayout();
            StartShiftFrom(avatar, _avatarShift, avatarFrom, header);
            StartShiftFrom(title, _titleShift, titleFrom, header);

            DispatcherTimer.RunOnce(() =>
            {
                if (version != _headerMoveVersion) return;
                FinishHeaderMove();
            }, HeaderMoveDuration + TimeSpan.FromMilliseconds(40));
        }

        /// <summary>
        /// Конец переезда: переходы снимаются, высота шапки снова идёт по
        /// содержимому — описание можно добавить или стереть, и шапка
        /// подстроится сама.
        /// </summary>
        private void FinishHeaderMove()
        {
            var header = this.FindControl<Border>("CardHeader");
            var spacer = this.FindControl<Border>("SideHeaderSpacer");

            if (header != null)
            {
                header.Transitions = null;
                header.Height = double.NaN;
            }

            if (spacer != null)
                spacer.Transitions = null;

            _avatarShift.Transitions = null;
            _titleShift.Transitions = null;
            _avatarShift.X = 0;
            _avatarShift.Y = 0;
            _titleShift.X = 0;
            _titleShift.Y = 0;

            _headerMoving = false;

            ApplyHeaderLayout();
            UpdateHeaderState();
        }

        /// <summary>
        /// Раскладка шапки без перехода — при смене размера окна или высоты
        /// шапки и по окончании переезда.
        /// </summary>
        private void ApplyHeaderLayout()
        {
            var root = this.FindControl<Grid>("CardRoot");
            var header = this.FindControl<Border>("CardHeader");
            var spacer = this.FindControl<Border>("SideHeaderSpacer");
            if (root is null || header is null) return;

            double rootWidth = root.Bounds.Width;
            if (rootWidth <= 0) return;

            ApplyHeaderPlacement();

            header.Transitions = null;
            header.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
            header.Width = _headerCompact ? Math.Min(CompactHeaderWidth, rootWidth) : rootWidth;
            header.Margin = _headerCompact ? CompactHeaderMargin : default;
            header.Padding = _headerCompact ? CompactHeaderPadding : ExpandedHeaderPadding;
            header.BorderThickness = _headerCompact ? CompactHeaderBorder : ExpandedHeaderBorder;
            header.CornerRadius = _headerCompact
                ? new CornerRadius(CompactHeaderCornerRadius)
                : default;

            if (spacer != null)
            {
                spacer.Transitions = null;
                spacer.Height = _headerCompact
                    ? _compactHeaderHeight + CompactGap
                    : _expandedHeaderHeight;
            }
        }

        /// <summary>
        /// Места аватара, имени и закладки группы внутри шапки. Развёрнутая:
        /// аватар слева, имя справа от него. Карточка: аватар по центру, имя
        /// под ним во всю ширину, закладка у правого края карточки.
        /// </summary>
        private void ApplyHeaderPlacement()
        {
            var avatar = this.FindControl<Canvas>("HeaderAvatar");
            var title = this.FindControl<StackPanel>("HeaderTitle");
            var flag = this.FindControl<Panel>("HeaderFlag");

            if (avatar != null)
            {
                Grid.SetRow(avatar, 0);
                Grid.SetColumn(avatar, 0);
                Grid.SetColumnSpan(avatar, _headerCompact ? 2 : 1);
                avatar.HorizontalAlignment = _headerCompact
                    ? Avalonia.Layout.HorizontalAlignment.Center
                    : Avalonia.Layout.HorizontalAlignment.Stretch;
                avatar.Margin = _headerCompact ? CompactAvatarMargin : default;
            }

            if (title != null)
            {
                Grid.SetRow(title, _headerCompact ? 1 : 0);
                Grid.SetColumn(title, _headerCompact ? 0 : 1);
                Grid.SetColumnSpan(title, _headerCompact ? 2 : 1);
                title.HorizontalAlignment = _headerCompact
                    ? Avalonia.Layout.HorizontalAlignment.Stretch
                    : Avalonia.Layout.HorizontalAlignment.Left;
                title.Margin = _headerCompact ? CompactTitleMargin : ExpandedTitleMargin;
            }

            if (flag != null)
            {
                Grid.SetRow(flag, 0);
                Grid.SetColumn(flag, _headerCompact ? 0 : 1);
                Grid.SetColumnSpan(flag, _headerCompact ? 2 : 1);
            }
        }

        /// <summary>
        /// Сдвинуть элемент туда, где он был до смены раскладки, и дать ему
        /// доехать до нового места.
        /// </summary>
        private static void StartShiftFrom(Control element, TranslateTransform shift, Point? from, Visual container)
        {
            if (!ReferenceEquals(element.RenderTransform, shift))
                element.RenderTransform = shift;

            shift.Transitions = null;

            if (!from.HasValue) return;

            // Новое место без сдвига: текущее положение минус висящий сдвиг.
            var now = element.TranslatePoint(new Point(0, 0), container);
            if (!now.HasValue) return;

            double baseX = now.Value.X - shift.X;
            double baseY = now.Value.Y - shift.Y;

            shift.X = from.Value.X - baseX;
            shift.Y = from.Value.Y - baseY;

            var easing = new Avalonia.Animation.Easings.CubicEaseInOut();
            shift.Transitions = new Avalonia.Animation.Transitions
            {
                new Avalonia.Animation.DoubleTransition
                {
                    Property = TranslateTransform.XProperty,
                    Duration = HeaderMoveDuration,
                    Easing = easing
                },
                new Avalonia.Animation.DoubleTransition
                {
                    Property = TranslateTransform.YProperty,
                    Duration = HeaderMoveDuration,
                    Easing = easing
                }
            };

            shift.X = 0;
            shift.Y = 0;
        }

        private static Avalonia.Animation.Transitions CreateHeaderTransitions()
        {
            var easing = new Avalonia.Animation.Easings.CubicEaseInOut();

            return new Avalonia.Animation.Transitions
            {
                new Avalonia.Animation.DoubleTransition
                {
                    Property = Avalonia.Layout.Layoutable.WidthProperty,
                    Duration = HeaderMoveDuration,
                    Easing = easing
                },
                new Avalonia.Animation.DoubleTransition
                {
                    Property = Avalonia.Layout.Layoutable.HeightProperty,
                    Duration = HeaderMoveDuration,
                    Easing = easing
                },
                new Avalonia.Animation.ThicknessTransition
                {
                    Property = Avalonia.Layout.Layoutable.MarginProperty,
                    Duration = HeaderMoveDuration,
                    Easing = easing
                },
                new Avalonia.Animation.ThicknessTransition
                {
                    Property = Border.PaddingProperty,
                    Duration = HeaderMoveDuration,
                    Easing = easing
                },
                new Avalonia.Animation.ThicknessTransition
                {
                    Property = Border.BorderThicknessProperty,
                    Duration = HeaderMoveDuration,
                    Easing = easing
                },
                new Avalonia.Animation.CornerRadiusTransition
                {
                    Property = Border.CornerRadiusProperty,
                    Duration = HeaderMoveDuration,
                    Easing = easing
                }
            };
        }

        private static Avalonia.Animation.Transitions CreateSpacerTransitions()
        {
            return new Avalonia.Animation.Transitions
            {
                new Avalonia.Animation.DoubleTransition
                {
                    Property = Avalonia.Layout.Layoutable.HeightProperty,
                    Duration = HeaderMoveDuration,
                    Easing = new Avalonia.Animation.Easings.CubicEaseInOut()
                }
            };
        }

        /// <summary>
        /// Колесо над правой колонкой, которой самой листать нечего, листает стопку
        /// слева. Раньше обе колонки прокручивались вместе, и колесо над галереей
        /// двигало всю карточку — эта привычка сохраняется.
        /// </summary>
        private void OnSideWheel(object? sender, PointerWheelEventArgs e)
        {
            if (e.Handled) return;

            var body = this.FindControl<ScrollViewer>("BodyScroll");
            if (body is null) return;

            const double WheelStepPx = 50;
            double max = Math.Max(0, body.Extent.Height - body.Viewport.Height);
            double y = Math.Clamp(body.Offset.Y - e.Delta.Y * WheelStepPx, 0, max);
            body.Offset = new Vector(body.Offset.X, y);
            e.Handled = true;
        }

        private void OnSubtitleIconClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            this.FindControl<TextBox>("ShortDescriptionBox")?.Focus();
        }

        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            if (_scrollVm != null)
                _scrollVm.PendingScrollRequested -= OnPendingScrollRequested;
            _scrollVm = null;
            _pendingScrollY = null;

            if (DataContext is not CharacterBasicsTabViewModel vm) return;

            _scrollVm = vm;
            vm.PendingScrollRequested += OnPendingScrollRequested;
            OnPendingScrollRequested();

            vm.RequestPickerOpen = async () =>
            {
                if (vm.AvatarService == null) return null;

                // Выбор аватара — оверлей по центру модуля (как редактор цвета),
                // а не отдельное системное окно. Кнопок Upload/Delete под аватаром
                // больше нет: удаление доступно кнопкой внутри пикера, действие
                // передаётся только когда аватар есть.
                var host = this.FindAncestorOfType<CharactersModuleView>();
                var overlay = host?.FindControl<CharacterAvatarPickerOverlay>("AvatarPickerOverlayControl");
                if (overlay != null)
                {
                    Action? deleteAction = string.IsNullOrEmpty(vm.AvatarPath)
                        ? null
                        : () => vm.DeleteAvatarCommand.Execute().Subscribe();
                    return await overlay.ShowAsync(vm.AvatarService, vm.CharacterId, deleteAction);
                }

                // Запасной путь, если вью показана вне модуля: прежнее окно.
                var window = TopLevel.GetTopLevel(this) as Window;
                if (window == null) return null;
                return await CharacterAvatarPickerWindow.ShowAsync(
                    window, vm.AvatarService, vm.CharacterId);
            };

        }

        // Enter в поле имени под аватаром: имя сохраняется немедленно, в обход
        // задержки автосейва карточки, и поле теряет фокус — визуально ввод
        // зафиксирован. Привязка Text обновляет вьюмодель на каждый символ,
        // поэтому дополнительной синхронизации текста не требуется.
        // В используемой версии Avalonia у IFocusManager нет ClearFocus,
        // поэтому фокус переводится явно на корень вкладки.
        private void OnNameTitleKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;

            if (DataContext is CharacterBasicsTabViewModel vm)
                vm.RequestImmediateSave();

            Focusable = true;
            Focus();
        }

        // Настройки карточки (кольцо, вид аватара, толщина рамки) — то же окно,
        // что у карточек основного списка. Персист идёт через вью-модель строки
        // списка: её сеттеры дёргают колбэки модуля. Поэтому окно открывается
        // для строки текущего персонажа, а после OK кольцо и закладка
        // синхронизируются обратно в открытую карточку — иначе автосейв
        // карточки перезаписал бы их прежними значениями.
        private void OnCardSettingsClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (DataContext is not CharacterBasicsTabViewModel vm) return;

            var host = this.FindAncestorOfType<CharactersModuleView>();
            var overlay = host?.FindControl<CardSettingsOverlay>("CardSettingsOverlayControl");
            if (overlay is null) return;

            if (host!.DataContext is not CharactersViewModel moduleVm) return;
            var item = moduleVm.FindListItem(vm.CharacterId);
            if (item is null) return;

            overlay.ShowFor(item, moduleVm, () =>
            {
                vm.AvatarRing = item.AvatarRing;
                vm.GroupBookmark = item.GroupBookmark;
            });
        }

        // Enter в поле нового имени: добавить в список и очистить поле.
        private void OnNewNameKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if (sender is not TextBox box) return;
            if (DataContext is not CharacterBasicsTabViewModel vm) return;

            vm.AddNameCommand.Execute(box.Text ?? string.Empty).Subscribe();
            box.Text = string.Empty;
        }

        // Стрелка в чипе имени: сделать это имя отображаемым. Прежнее
        // отображаемое встаёт на его место в списке и не теряется.
        private void OnNameMakePrimaryClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not Control c || c.DataContext is not Writersword.Modules.Characters.Models.CharacterNameEntry entry) return;
            if (DataContext is not CharacterBasicsTabViewModel vm) return;

            vm.MakePrimaryName(entry.Id);
            e.Handled = true;
        }

        // Щелчок по чипу имени — правка: имя возвращается в поле ввода вместе
        // с пометкой и убирается из списка. Отредактировал, нажал Enter —
        // вернулось на место. Отдельного редактора под одно поле нет: ввод
        // остаётся потоковым.
        private void OnNameChipClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not Control c || c.DataContext is not Writersword.Modules.Characters.Models.CharacterNameEntry entry) return;
            if (DataContext is not CharacterBasicsTabViewModel vm) return;

            var box = this.FindControl<TextBox>("NewNameBox");
            if (box != null)
            {
                box.Text = string.IsNullOrWhiteSpace(entry.Note)
                    ? entry.Value
                    : $"{entry.Value} — {entry.Note}";
                box.CaretIndex = box.Text.Length;
                box.Focus();
            }

            vm.RemoveName(entry.Id);
            e.Handled = true;
        }

        // Крестик в чипе имени.
        private void OnNameRemoveClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not Control c || c.DataContext is not Writersword.Modules.Characters.Models.CharacterNameEntry entry) return;
            if (DataContext is not CharacterBasicsTabViewModel vm) return;

            vm.RemoveName(entry.Id);
            e.Handled = true;
        }

        // Enter в поле нового тега: добавить и очистить поле для следующего.
        // Поле — AutoCompleteBox с подсказкой уже заведённых тегов, поэтому
        // текст читается с него, а не с TextBox.
        private void OnNewTagKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if (sender is not AutoCompleteBox box) return;
            if (DataContext is not CharacterBasicsTabViewModel vm) return;

            // Пока открыт список подсказок, Enter выбирает из него — добавлять
            // тег в этот момент значит добавить недонабранное слово.
            if (box.IsDropDownOpen) return;

            e.Handled = true;
            vm.AddTagCommand.Execute(box.Text ?? string.Empty).Subscribe();
            box.Text = string.Empty;

            // Новый тег сразу попадает в подсказки: следующий персонаж
            // получит его без перезагрузки проекта.
            vm.ReloadKnownTags();
        }

        // Enter в поле новой метки. Если метка с таким именем уже есть
        // в проекте, вьюмодель подхватит её целиком — со значком, цветом
        // и эффектом; иначе заведёт новую с настройками по умолчанию.
        private void OnNewLabelKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if (sender is not AutoCompleteBox box) return;
            if (DataContext is not CharacterBasicsTabViewModel vm) return;

            // Пока открыт список подсказок, Enter выбирает из него.
            if (box.IsDropDownOpen) return;

            e.Handled = true;
            vm.AddLabelCommand.Execute(box.Text ?? string.Empty).Subscribe();
            box.Text = string.Empty;
            vm.ReloadKnownLabels();
        }

        // Общая правка меняет метки у других персонажей прямо в модели, а
        // строки списка держат свои копии — без этого вызова карточки
        // остальных персонажей показывали бы прежний значок до перезагрузки
        // проекта.
        private void RefreshListLabels()
        {
            var host = this.FindAncestorOfType<CharactersModuleView>();
            if (host?.DataContext is CharactersViewModel moduleVm)
                moduleVm.RefreshLabelsFromModel();
        }

        // Редактор метки хостится в CharactersModuleView поверх содержимого,
        // как окно настроек карточки.
        private LabelEditorOverlay? FindLabelEditor()
        {
            var host = this.FindAncestorOfType<CharactersModuleView>();
            return host?.FindControl<LabelEditorOverlay>("LabelEditorOverlayControl");
        }

        // Enter в поле группового обращения: правило для выбранной папки.
        private void OnGroupAddressKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;

            if (sender is not TextBox box) return;
            if (DataContext is not CharacterBasicsTabViewModel vm) return;

            var folderBox = this.FindControl<ComboBox>("GroupAddressFolderBox");
            if (folderBox?.SelectedItem is not Writersword.Modules.Characters.Models.CharacterFolder folder) return;

            vm.AddGroupAddress(folder.Id, box.Text ?? string.Empty);
            box.Text = string.Empty;
        }

        private void OnGroupAddressRemoveClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;

            if (sender is not Control c ||
                c.DataContext is not Writersword.Modules.Characters.Models.CharacterGroupAddress item) return;
            if (DataContext is not CharacterBasicsTabViewModel vm) return;

            vm.RemoveGroupAddress(item.Id);
        }

        // ── Перетаскивание чипов меток ────────────────────────────────────
        // Порядок меток задавался только стрелками ‹ › внутри чипа: на десятке
        // меток это утомительно. Стрелки остаются — на трёх-четырёх они
        // быстрее.

        private const double LabelDragThreshold = 6.0;

        private Point? _labelPressOrigin;
        private string? _pressedLabelId;
        private PointerPressedEventArgs? _labelPressArgs;

        private void OnLabelChipPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            ClearLabelPress();

            if (sender is not Control chip) return;
            if (chip.DataContext is not Writersword.Modules.Characters.Models.CharacterLabel label) return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

            _labelPressOrigin = e.GetPosition(this);
            _pressedLabelId = label.Id;
            _labelPressArgs = e;
        }

        private async void OnLabelChipPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_labelPressOrigin is not { } origin) return;
            if (_pressedLabelId is not { } labelId) return;
            if (_labelPressArgs is not { } pressArgs) return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

            var current = e.GetPosition(this);
            var dx = current.X - origin.X;
            var dy = current.Y - origin.Y;
            if (Math.Sqrt(dx * dx + dy * dy) < LabelDragThreshold) return;

            // Порог пройден — дальше это перетаскивание, а не щелчок,
            // открывающий редактор метки.
            ClearLabelPress();

            var dataTransfer = new DataTransfer();
            dataTransfer.Add(DataTransferItem.Create(CharacterDragFormats.LabelId, labelId));

            try
            {
                await DragDrop.DoDragDropAsync(pressArgs, dataTransfer, DragDropEffects.Move);
            }
            catch (Exception)
            {
                // Перетаскивание может прервать система — порядок просто
                // не меняется.
            }
        }

        private void OnLabelChipPointerReleased(object? sender, PointerReleasedEventArgs e)
            => ClearLabelPress();

        private void ClearLabelPress()
        {
            _labelPressOrigin = null;
            _pressedLabelId = null;
            _labelPressArgs = null;
        }

        private void OnLabelChipDragOver(object? sender, DragEventArgs e)
        {
            e.DragEffects = e.DataTransfer.Contains(CharacterDragFormats.LabelId)
                ? DragDropEffects.Move
                : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnLabelChipDrop(object? sender, DragEventArgs e)
        {
            e.Handled = true;

            if (sender is not Control chip) return;
            if (chip.DataContext is not Writersword.Modules.Characters.Models.CharacterLabel target) return;
            if (DataContext is not CharacterBasicsTabViewModel vm) return;

            var draggedId = e.DataTransfer.TryGetValue(CharacterDragFormats.LabelId);
            if (string.IsNullOrEmpty(draggedId)) return;

            vm.MoveLabelTo(draggedId, target.Id);
        }

        // Клик по чипу метки — правка существующей: Id и порядок сохраняются,
        // результат заменяет метку в коллекции (UpsertLabel).
        private void OnLabelChipClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not Control c || c.DataContext is not Writersword.Modules.Characters.Models.CharacterLabel label) return;
            if (DataContext is not CharacterBasicsTabViewModel vm) return;

            // Вид метки общий всегда — признака «применить ко всем» у
            // редактора больше нет: правишь «Ранен» здесь, он меняется у всех,
            // у кого стоит. Поэтому и строки списка перечитываются всегда.
            FindLabelEditor()?.ShowFor(label, updated =>
            {
                vm.UpsertLabel(updated, asGlobal: true);
                RefreshListLabels();
            });
            e.Handled = true;
        }

        // Кнопка «Добавить метку» — создание через полный редактор.
        private void OnAddLabelClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not CharacterBasicsTabViewModel vm) return;

            FindLabelEditor()?.ShowFor(null, created =>
            {
                vm.UpsertLabel(created, asGlobal: true);
                RefreshListLabels();
            });
            e.Handled = true;
        }

        // Стрелки порядка в чипе: влево/вправо на одну позицию.
        private void OnLabelMoveLeftClick(object? sender, RoutedEventArgs e)
        {
            MoveLabelFromChip(sender, -1);
            e.Handled = true;
        }

        private void OnLabelMoveRightClick(object? sender, RoutedEventArgs e)
        {
            MoveLabelFromChip(sender, +1);
            e.Handled = true;
        }

        private void MoveLabelFromChip(object? sender, int delta)
        {
            if (sender is not Control c || c.DataContext is not Writersword.Modules.Characters.Models.CharacterLabel label) return;
            if (DataContext is not CharacterBasicsTabViewModel vm) return;
            vm.MoveLabel(label.Id, delta);
        }

        // Крестик в чипе метки. Обработчик вместо каст-биндинга к вьюмодели:
        // каст типа в шаблоне разрешается в рантайме и роняет вью.
        private void OnLabelRemoveClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not Control c || c.DataContext is not Writersword.Modules.Characters.Models.CharacterLabel label) return;
            if (DataContext is not CharacterBasicsTabViewModel vm) return;

            vm.RemoveLabelCommand.Execute(label.Id).Subscribe();
            e.Handled = true;
        }

        // Крестик в чипе тега — та же история, что у меток.
        private void OnTagRemoveClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not Control c || c.DataContext is not string tag) return;
            if (DataContext is not CharacterBasicsTabViewModel vm) return;

            vm.RemoveTagCommand.Execute(tag).Subscribe();
            e.Handled = true;
        }

        // Добавление картинок в галерею. Несколько файлов за раз: образы
        // обычно приносят пачкой, а не по одному.
        private async void OnGalleryAddClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (DataContext is not CharacterBasicsTabViewModel vm) return;

            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage == null) return;

            try
            {
                var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Картинки персонажа",
                    AllowMultiple = true,
                    FileTypeFilter = new[] { FilePickerFileTypes.ImageAll }
                });

                if (files == null || files.Count == 0) return;

                foreach (var file in files)
                {
                    await using var stream = await file.OpenReadAsync();
                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer);

                    await vm.AddGalleryImageAsync(buffer.ToArray(), file.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Gallery add failed");
            }
        }

        // ── Перетаскивание в галерее ──────────────────────────────────────
        // Картинки принимаются извне — из проводника, браузера, чего угодно,
        // что отдаёт файл. Плитки переставляются между собой тем же приёмом,
        // что карточки персонажей в списке: порог сдвига, свой формат данных.

        private Point? _tilePressOrigin;
        private string? _pressedTileRef;

        // Перенос идёт вручную, а не системным перетаскиванием: система
        // забирает указатель себе и своего превью под курсором не рисует.
        // Порядок работы взят у карточек персонажей в списке: картинка
        // уходит из сетки, вместо неё встаёт тусклая копия-место, а сама
        // картинка летит под курсором. Соседи сдвигаются данными, а вью
        // доигрывает их перемещение приёмом FLIP — измеряет положение до
        // перестановки, после перестановки ставит разницу сдвигом и ведёт
        // его к нулю переходом.
        private const long GalleryPreviewThrottleMs = 60;

        private bool _galleryDragging;
        private long _lastGalleryPreviewTick;

        private void ClearTilePress()
        {
            _tilePressOrigin = null;
            _pressedTileRef = null;
        }

        /// <summary>Плитки галереи в порядке обхода дерева.</summary>
        private IEnumerable<Panel> GalleryTiles() =>
            this.GetVisualDescendants().OfType<Panel>().Where(t => t.Classes.Contains("tile"));

        /// <summary>
        /// Сетка галереи — общая система координат для замеров. Именно она, а
        /// не карточка: сетка прокручивается вместе с плитками, и прокрутка
        /// во время переноса не порождает ложных перемещений.
        /// </summary>
        private Control? GalleryPanel() =>
            GalleryTiles().FirstOrDefault()?.GetVisualAncestors().OfType<ItemsControl>().FirstOrDefault();

        /// <summary>
        /// Положение плиток до перестановки. Меряем вместе с текущим сдвигом:
        /// плитка в середине перехода не должна начинать доезд заново.
        /// </summary>
        private Dictionary<string, Point> SnapshotTilePositions()
        {
            var result = new Dictionary<string, Point>();
            var root = GalleryPanel();
            if (root == null) return result;

            foreach (var tile in GalleryTiles())
            {
                if (tile.DataContext is not CharacterGalleryItemViewModel item) continue;
                if (result.ContainsKey(item.ImageRef)) continue;

                var pt = tile.TranslatePoint(new Point(0, 0), root);
                if (pt.HasValue) result[item.ImageRef] = pt.Value;
            }

            return result;
        }

        /// <summary>
        /// Доигрывает перемещение плиток после перестановки: разница между
        /// прежним и новым положением ставится сдвигом, а следующим кадром
        /// сводится к нулю — плитка едет, а не прыгает.
        /// </summary>
        private void BeginTileFlip(Dictionary<string, Point> before)
        {
            if (before.Count == 0) return;

            var root = GalleryPanel();
            if (root == null) return;

            // Раскладка прогоняется здесь же: отложенный замер вытесняется
            // потоком событий указателя и при непрерывном переносе не успевает
            // отработать — отсюда и берётся ощущение, что анимации нет.
            UpdateLayout();

            var pending = new List<(TranslateTransform tt, double dx, double dy)>(before.Count);

            foreach (var tile in GalleryTiles())
            {
                if (tile.DataContext is not CharacterGalleryItemViewModel item) continue;
                if (!before.TryGetValue(item.ImageRef, out var old)) continue;
                if (tile.RenderTransform is not TranslateTransform tt) continue;

                // Чистое положение по раскладке меряется без текущего сдвига,
                // поэтому он временно снимается — но без перехода, иначе снятие
                // само превратится в анимацию.
                double keepX = tt.X, keepY = tt.Y;
                var saved = tt.Transitions;
                tt.Transitions = null;
                tt.X = 0.0;
                tt.Y = 0.0;

                var now = tile.TranslatePoint(new Point(0, 0), root);
                if (!now.HasValue)
                {
                    tt.X = keepX;
                    tt.Y = keepY;
                    tt.Transitions = saved;
                    continue;
                }

                double dx = old.X - now.Value.X;
                double dy = old.Y - now.Value.Y;

                if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5)
                {
                    tt.X = keepX;
                    tt.Y = keepY;
                    tt.Transitions = saved;
                    continue;
                }

                tt.X = dx;
                tt.Y = dy;
                tt.Transitions = saved;
                pending.Add((tt, dx, dy));
            }

            if (pending.Count == 0) return;

            // Доезд к нулю — следующим кадром и с приоритетом отрисовки: он выше
            // ввода, поэтому непрерывным переносом его не вытесняет. Сбрасываем
            // только тот сдвиг, который сами и поставили: если плитку успел
            // подхватить следующий шаг, доведёт его собственный вызов.
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var (tt, dx, dy) in pending)
                    if (tt.X == dx && tt.Y == dy) { tt.X = 0.0; tt.Y = 0.0; }
            }, DispatcherPriority.Render);
        }

        private void ResetTileTransforms()
        {
            foreach (var tile in GalleryTiles())
            {
                if (tile.RenderTransform is not TranslateTransform tt) continue;
                if (tt.X == 0.0 && tt.Y == 0.0) continue;

                var saved = tt.Transitions;
                tt.Transitions = null;
                tt.X = 0.0;
                tt.Y = 0.0;
                tt.Transitions = saved;
            }
        }

        /// <summary>
        /// Место, куда встанет картинка, если её отпустить сейчас. Считается
        /// по действительной геометрии плиток, а не по расчётному размеру
        /// ячейки: место вставки держится строки курсора, переход на другую
        /// строку — только движением курсора по вертикали. Правее последней
        /// плитки нижней строки — конец галереи, иначе последнее место
        /// оказалось бы недостижимым.
        /// </summary>
        private int ComputeGalleryTargetIndex(Point position, CharacterBasicsTabViewModel vm)
        {
            var cells = new List<(int idx, double cx, double cy)>();
            var rowYs = new List<double>();
            double tileSide = 0;

            foreach (var tile in GalleryTiles())
            {
                if (tile.DataContext is not CharacterGalleryItemViewModel item) continue;
                if (item.IsAddTile) continue;
                if (tile.Bounds.Width <= 1 || tile.Bounds.Height <= 1) continue;

                var index = vm.Gallery.IndexOf(item);
                if (index < 0) continue;

                var center = tile.TranslatePoint(
                    new Point(tile.Bounds.Width / 2.0, tile.Bounds.Height / 2.0), this);
                if (!center.HasValue) continue;

                // Центр берётся без текущего сдвига: пока плитка едет, её
                // видимое положение к раскладке отношения не имеет.
                double offX = 0, offY = 0;
                if (tile.RenderTransform is TranslateTransform tt) { offX = tt.X; offY = tt.Y; }

                double cx = center.Value.X - offX;
                double cy = center.Value.Y - offY;

                cells.Add((index, cx, cy));
                if (tile.Bounds.Height > tileSide) tileSide = tile.Bounds.Height;
                if (!rowYs.Any(y => Math.Abs(y - cy) <= 4)) rowYs.Add(cy);
            }

            if (cells.Count == 0) return vm.GalleryPlaceholderIndex;

            rowYs.Sort();
            double rowPitch = double.MaxValue;
            for (int i = 1; i < rowYs.Count; i++)
            {
                double d = rowYs[i] - rowYs[i - 1];
                if (d > 1 && d < rowPitch) rowPitch = d;
            }
            if (rowPitch == double.MaxValue) rowPitch = tileSide > 1 ? tileSide : 100;
            double halfRow = rowPitch / 2.0;

            var row = cells.Where(c => Math.Abs(c.cy - position.Y) <= halfRow).ToList();
            if (row.Count == 0)
            {
                double nearestY = cells.OrderBy(c => Math.Abs(c.cy - position.Y)).First().cy;
                row = cells.Where(c => Math.Abs(c.cy - nearestY) <= halfRow).ToList();
            }

            var nearest = row.OrderBy(c => Math.Abs(c.cx - position.X)).First();
            int target = nearest.idx;

            bool hasRowBelow = cells.Any(c => c.cy > position.Y + halfRow);
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
                if (colPitch == double.MaxValue) colPitch = tileSide > 1 ? tileSide : 100;

                if (position.X > right.cx + colPitch / 2.0)
                    target = right.idx + 1;
            }

            return Math.Clamp(target, 0, Math.Max(0, vm.GalleryImageCount - 1));
        }

        private void ShowGalleryGhost(Control tile, Point position)
        {
            var canvas = this.FindControl<Canvas>("GalleryGhostCanvas");
            var ghost = this.FindControl<Border>("GalleryGhost");
            var image = this.FindControl<Image>("GalleryGhostImage");
            if (canvas == null || ghost == null || image == null) return;

            if (tile.DataContext is CharacterGalleryItemViewModel item)
                image.Source = item.Preview;

            // Призрак того же размера, что плитка — перенос читается как
            // перекладывание самой картинки, а не абстрактного значка.
            ghost.Width = tile.Bounds.Width;
            ghost.Height = tile.Bounds.Height;

            canvas.IsVisible = true;
            MoveGalleryGhost(position);
        }

        private void MoveGalleryGhost(Point position)
        {
            var ghost = this.FindControl<Border>("GalleryGhost");
            if (ghost == null) return;

            Canvas.SetLeft(ghost, position.X - ghost.Width / 2.0);
            Canvas.SetTop(ghost, position.Y - ghost.Height / 2.0);
        }

        private void HideGalleryGhost()
        {
            var canvas = this.FindControl<Canvas>("GalleryGhostCanvas");
            if (canvas != null) canvas.IsVisible = false;
        }

        // Колесо над галереей крутит галерею, а не всю карточку. Событие
        // гасится только когда внутри действительно есть что прокручивать:
        // иначе колесо над короткой галереей не делало бы ничего, и пришлось
        // бы уводить курсор в сторону, чтобы продолжить листать карточку.
        private void OnGalleryWheel(object? sender, PointerWheelEventArgs e)
        {
            if (sender is not ScrollViewer scroll) return;
            if (scroll.Extent.Height <= scroll.Viewport.Height) return;

            e.Handled = true;
        }

        // Системное перетаскивание осталось только для файлов извне: перенос
        // плиток между собой идёт вручную, ради призрака под курсором.
        private void OnGalleryDragOver(object? sender, DragEventArgs e)
        {
            e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
                ? DragDropEffects.Copy
                : DragDropEffects.None;

            e.Handled = true;
        }

        private async void OnGalleryDrop(object? sender, DragEventArgs e)
        {
            e.Handled = true;

            if (DataContext is not CharacterBasicsTabViewModel vm) return;

            // Файлы извне: берём всё, что удалось прочитать как поток.
            var files = e.DataTransfer.TryGetFiles();
            if (files == null) return;

            foreach (var file in files)
            {
                if (file is not IStorageFile storageFile) continue;

                try
                {
                    await using var stream = await storageFile.OpenReadAsync();
                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer);

                    await vm.AddGalleryImageAsync(buffer.ToArray(), storageFile.Name);
                }
                catch (Exception ex)
                {
                    // Бросить могут что угодно — папку, ярлык, недоступный файл.
                    _logger.Error(ex, "Gallery drop failed: {Name}", storageFile.Name);
                }
            }
        }

        // Пункты контекстного меню плитки галереи. DataContext пункта — та же
        // картинка, что у плитки: меню объявлено внутри её шаблона.
        private void OnGalleryUseAsAvatarClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is not Control c || c.DataContext is not CharacterGalleryItemViewModel item) return;
            if (DataContext is not CharacterBasicsTabViewModel vm) return;

            vm.UseAsAvatar(item.ImageRef);
        }

        private void OnGalleryRemoveClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is not Control c || c.DataContext is not CharacterGalleryItemViewModel item) return;
            if (DataContext is not CharacterBasicsTabViewModel vm) return;

            vm.RemoveGalleryImage(item.ImageRef);
        }

        // Выбор набора в раскрывшемся списке подключает его к карточке.
        // Список закрывается сам: подключение — законченное действие, держать
        // меню открытым незачем.
        private void OnAttachAnketaClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;

            if (sender is not Control c) return;
            if (c.DataContext is not Writersword.Modules.Characters.Models.CharacterAnketa anketa) return;
            if (DataContext is not CharacterBasicsTabViewModel vm) return;

            this.FindControl<Button>("AttachAnketaButton")?.Flyout?.Hide();
            vm.AttachAnketa(anketa.Id);
        }

        /// <summary>
        /// Шаблон из того же списка: подключает все свои анкеты разом.
        /// Уже подключённые остаются на местах — шаблон добавляет
        /// недостающие разделы, а не пересобирает карточку заново.
        /// </summary>
        private void OnApplyTemplateClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;

            if (sender is not Control c) return;
            if (c.DataContext is not Writersword.Modules.Characters.Models.CharacterTemplate template) return;
            if (DataContext is not CharacterBasicsTabViewModel vm) return;

            this.FindControl<Button>("AttachAnketaButton")?.Flyout?.Hide();
            vm.ApplyTemplate(template.Id);
        }

        /// <summary>
        /// Клик по кругляшку шкалы выставляет значение. Обработчик, а не
        /// команда с параметром: каст типа вьюмодели внутри шаблона
        /// разрешается в рантайме и роняет вью.
        ///
        /// Переехал сюда вместе с полями анкет: они показываются в «Общем»,
        /// а прежняя вкладка параметров осталась только хранилищем значений.
        /// </summary>
        private void OnScaleDotClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not Control c) return;
            if (c.DataContext is not CharacterScaleDotViewModel dot) return;

            // Владелец кругляшка — параметр: у списка точек DataContext строки
            // списка полей.
            FindParameterOwner(c)?.SetFromDot(dot.Value);
            e.Handled = true;
        }

        private static CharacterParameterItemViewModel? FindParameterOwner(Control start)
        {
            var current = start.Parent;
            while (current is not null)
            {
                if (current.DataContext is CharacterParameterItemViewModel item) return item;
                current = current.Parent;
            }
            return null;
        }

        /// <summary>Забыть недавние. Сами анкеты и подключённое остаются.</summary>
        private void OnClearRecentAnketasClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            (DataContext as CharacterBasicsTabViewModel)?.ClearRecentAnketas();
        }

        // Крестик на чипе набора: набор перестаёт числиться в составе карточки,
        // значения полей при этом остаются.
        private void OnDetachAnketaClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not Control c || c.DataContext is not Writersword.Modules.Characters.Models.CharacterAnketa anketa) return;
            if (DataContext is not CharacterBasicsTabViewModel vm) return;

            vm.DetachAnketa(anketa.Id);
            e.Handled = true;
        }

        // Быстрая отметка «Мёртв»: добавляет встроенную метку. Кнопка исчезает,
        // как только метка появилась — дальше персонаж управляется её чипом,
        // и одно и то же состояние не показывается двумя разными органами.
        private void OnMarkDeadClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is CharacterBasicsTabViewModel vm) vm.IsDead = true;
            e.Handled = true;
        }
    }
}