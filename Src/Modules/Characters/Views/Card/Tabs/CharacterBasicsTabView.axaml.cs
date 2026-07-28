using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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

namespace Writersword.Modules.Characters.Views.Card.Tabs
{
    public partial class CharacterBasicsTabView : UserControl
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterBasicsTabView>();

        public CharacterBasicsTabView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;

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

        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            if (DataContext is not CharacterBasicsTabViewModel vm) return;

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

            FindLabelEditor()?.ShowFor(label, updated => vm.UpsertLabel(updated));
            e.Handled = true;
        }

        // Кнопка «Добавить метку» — создание через полный редактор.
        private void OnAddLabelClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not CharacterBasicsTabViewModel vm) return;

            FindLabelEditor()?.ShowFor(null, created => vm.UpsertLabel(created));
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

        // Выбор набора в списке подключает его к карточке. Выбор сразу
        // сбрасывается: список работает кнопкой «добавить», а не показывает
        // текущее состояние — состояние показывают чипы выше.
        private void OnAttachAnketaSelected(object? sender, SelectionChangedEventArgs e)
        {
            if (sender is not ComboBox box) return;
            if (box.SelectedItem is not Writersword.Modules.Characters.Models.CharacterAnketa anketa) return;
            if (DataContext is not CharacterBasicsTabViewModel vm) return;

            box.SelectedItem = null;
            vm.AttachAnketa(anketa.Id);
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