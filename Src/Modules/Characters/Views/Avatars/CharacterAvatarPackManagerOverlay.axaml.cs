using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Platform.Storage;
using Serilog;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Writersword.Modules.Characters.ViewModels.Avatars;
using Writersword.Src.Modules.Characters.Resources;

namespace Writersword.Modules.Characters.Views.Avatars
{
    /// <summary>
    /// Папки с аватарками. Живёт на уровне модуля, показывается поверх выбора
    /// аватарки и несёт свой скрим.
    ///
    /// Размеры панели ограничиваются под размер модуля тем же способом, что у
    /// редактора цвета: наблюдатель Bounds ставит панели MaxWidth и MaxHeight,
    /// а середина окна прокручивается.
    ///
    /// Окно принимает брошенные картинки — они ложатся в выбранную папку, — а
    /// кнопка приёма архива принимает брошенный ZIP.
    /// </summary>
    public partial class CharacterAvatarPackManagerOverlay : UserControl
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterAvatarPackManagerOverlay>();

        private static readonly FilePickerFileType ZipFileType =
            new("ZIP") { Patterns = new[] { "*.zip" } };

        private static readonly FilePickerFileType ImageFileType =
            new(CharactersStrings.FilePicker_ImagesFilter)
            {
                Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.webp" },
                MimeTypes = new[] { "image/jpeg", "image/png", "image/webp" }
            };

        // Перетаскивание плиток. Два одинаковых по устройству списка — лента
        // папок и содержимое выбранной папки, — поэтому механика одна на оба, а
        // различия вынесены в замыкания: где искать плитки, что считать
        // порядком и куда его записывать.
        private readonly TileDragController _packDrag;
        private readonly TileDragController _itemDrag;

        public CharacterAvatarPackManagerOverlay()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;

            _packDrag = new TileDragController(
                owner: this,
                tileClass: "packTile",
                host: () => this.FindControl<ItemsRepeater>("PacksHost"),
                scroll: () => this.FindControl<ScrollViewer>("PacksScroll"),
                items: () => Model?.Packs,
                move: (from, to) => Model?.Packs.Move(from, to),
                // Порядок папок хранится опознавателями и один на все области:
                // встроенную папку тоже можно поставить туда, где она нужна.
                canReorder: () => Model != null,
                ghost: item => (item as CharacterAvatarPackManagerPackViewModel)?.Cover,
                setDragging: (item, value) =>
                {
                    if (item is CharacterAvatarPackManagerPackViewModel pack) pack.IsDragging = value;
                },
                commit: (before, after) => Model?.CommitPackOrder(
                    before.OfType<CharacterAvatarPackManagerPackViewModel>().Select(p => p.PackId).ToList(),
                    after.OfType<CharacterAvatarPackManagerPackViewModel>().Select(p => p.PackId).ToList()),
                // Лента папок — один ряд с прокруткой вбок: и подхват соседней
                // плитки, и автопрокрутка у краёв считаются по горизонтали.
                horizontal: true,
                // Папка выбирается щелчком по плитке: кнопки под ней больше нет,
                // а плитка — это и есть папка.
                click: item =>
                {
                    if (Model != null && item is CharacterAvatarPackManagerPackViewModel pack)
                        Model.SelectedPack = pack;
                });

            _itemDrag = new TileDragController(
                owner: this,
                tileClass: "packItem",
                host: () => this.FindControl<ItemsRepeater>("PackItemsHost"),
                scroll: () => this.FindControl<ScrollViewer>("PackItemsScroll"),
                items: () => Model?.SelectedPack?.Items,
                move: (from, to) => Model?.SelectedPack?.Items.Move(from, to),
                // Встроенные паки лежат в ресурсах сборки: порядок картинок им
                // записывать некуда, и тянуть плитку значило бы обещать
                // перестановку, которой не будет.
                canReorder: () => Model?.SelectedPack?.CanReorder == true,
                ghost: item => (item as CharacterAvatarPackManagerItemViewModel)?.Thumbnail,
                setDragging: (item, value) =>
                {
                    if (item is CharacterAvatarPackManagerItemViewModel tile) tile.IsDragging = value;
                },
                commit: (before, after) =>
                {
                    var pack = Model?.SelectedPack;
                    if (Model == null || pack == null) return;

                    Model.CommitItemOrder(
                        pack.PackId,
                        before.OfType<CharacterAvatarPackManagerItemViewModel>().Select(i => i.FileName).ToList(),
                        after.OfType<CharacterAvatarPackManagerItemViewModel>().Select(i => i.FileName).ToList());
                },
                horizontal: false,
                // Щелчок по картинке выделяет её, и она становится тем, что
                // уберёт Delete. Повторный щелчок по той же снимает выделение.
                click: item =>
                {
                    if (Model != null && item is CharacterAvatarPackManagerItemViewModel tile)
                        Model.SelectItem(tile);
                });

            // Перетаскивание плиток ловится туннелем на корне окна — так же,
            // как перетаскивание карточек в списке персонажей: обработчик должен
            // отработать раньше прокрутки и раньше кнопок на самой плитке.
            AddHandler(PointerPressedEvent, OnTilePointerPressed, RoutingStrategies.Tunnel);
            AddHandler(PointerMovedEvent, OnTilePointerMoved, RoutingStrategies.Tunnel);
            AddHandler(PointerReleasedEvent, OnTilePointerReleased, RoutingStrategies.Tunnel);

            // Закрыть окно можно не только крестиком, поэтому доводим убранное
            // до хранилища по самому факту скрытия: пока окно на экране, ни один
            // файл не тронут, а как только оно ушло — отменять уже нечем.
            this.GetObservable(IsVisibleProperty).Subscribe(visible =>
            {
                if (!visible && DataContext is CharacterAvatarPackManagerViewModel vm)
                    vm.ApplyChanges();
            });

            // Панель не должна вылезать за модуль: при сжатом окне она иначе
            // обрезается по краям вместе с кнопками.
            this.GetObservable(BoundsProperty).Subscribe(b =>
            {
                if (b.Width <= 0) return;
                ApplyPanelMetrics(b.Width, b.Height);
            });

            // Ширина сетки картинок известна только после раскладки, поэтому
            // столбцы разводятся по её итогу — см. ApplyItemGridSpacing.
            //
            // Слушаются оба размера: Bounds меняется при перемере окна, а
            // Viewport — когда полоса прокрутки появляется или уходит и
            // отбирает у содержимого свою ширину. По одному только Bounds
            // расчёт заставал Viewport ещё нулевым.
            var itemsScroll = this.FindControl<ScrollViewer>("PackItemsScroll");
            itemsScroll?.GetObservable(BoundsProperty)
                .Subscribe(_ => ApplyItemGridSpacing());
            itemsScroll?.GetObservable(ScrollViewer.ViewportProperty)
                .Subscribe(_ => ApplyItemGridSpacing());
        }

        // ── Ширина столбцов сетки картинок ────────────────────────────────
        //
        // Раскладка берёт целое число столбцов по 76, а остаток — меньше
        // столбца — копила одной дырой у правого края: при ширине окна около
        // 740 в ленту влезало восемь столбцов, и семь десятков точек висели
        // пустотой. Растяжение ячеек средствами самой раскладки этого не
        // исправило, поэтому остаток раздаётся вручную: он делится поровну
        // между промежутками столбцов, и сетка приходит ровно к щели под
        // полосу прокрутки при любой ширине окна.

        /// <summary>Ячейка: плитка 68 плюс её отступ 4 с каждой стороны.</summary>
        private const double ItemCellSide = 76.0;

        private void ApplyItemGridSpacing()
        {
            var scroll = this.FindControl<ScrollViewer>("PackItemsScroll");
            var host = this.FindControl<ItemsRepeater>("PackItemsHost");
            if (scroll is null || host is null) return;
            if (host.Layout is not UniformGridLayout current) return;

            // Ширина берётся у окна прокрутки, а не у самого ScrollViewer:
            // полоса стоит всегда (AllowAutoHide="False"), место под себя
            // занимает сама, и Viewport — это ровно то, что остаётся
            // содержимому. Раньше здесь стояла ширина всего ScrollViewer минус
            // ещё четырнадцать «под полосу», и вместе с таким же полем у самой
            // сетки место под полосу вычиталось трижды: сорок точек уходило в
            // никуда, и вместе с законным остатком у правого края набиралась
            // дыра в целую плитку.
            var available = scroll.Viewport.Width;
            if (available <= 0) available = scroll.Bounds.Width;
            if (available <= 0) return;

            var columns = (int)Math.Floor(available / ItemCellSide);
            if (columns < 1) return;

            // Один столбец разводить нечем, и делить на ноль промежутков тоже.
            var spacing = 0.0;
            if (columns > 1)
            {
                // Точка запаса: при впритык совпавшей ширине округление могло бы
                // отнять у раскладки последний столбец, и сетка прыгала бы на
                // один столбец туда-сюда при каждом перемере.
                var leftover = available - columns * ItemCellSide - 1;
                if (leftover > 0) spacing = leftover / (columns - 1);
            }

            if (Math.Abs(current.MinColumnSpacing - spacing) <= 0.5) return;

            // Раскладка ставится новая, а не правится на месте: смена
            // MinColumnSpacing у уже работающей раскладки до репитера не
            // доходила — сетка оставалась с прежним шагом, и вся эта
            // арифметика ничего не меняла на экране. Присвоение Layout
            // репитер пересобирает наверняка.
            host.Layout = new UniformGridLayout
            {
                Orientation = Orientation.Horizontal,
                MinItemWidth = ItemCellSide,
                MinItemHeight = ItemCellSide,
                MinColumnSpacing = spacing,
                MinRowSpacing = 0,
                ItemsStretch = UniformGridLayoutItemsStretch.None
            };
        }

        // ── Колесо над лентой папок ───────────────────────────────────────
        //
        // Лента едет вбок, а колесо у мыши крутится вверх-вниз, и своего
        // горизонтального колеса у большинства мышей нет. Обычное колесо здесь
        // и двигает ленту вбок: другого способа доехать до дальней папки, кроме
        // как тащить полосу прокрутки, иначе не остаётся.
        private void OnPacksWheel(object? sender, PointerWheelEventArgs e)
        {
            if (sender is not ScrollViewer scroll) return;

            var max = Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width);
            if (max <= 0) return;

            // Шаг в половину плитки: лента невысокая, и целая плитка за щелчок
            // проматывала бы её слишком крупно.
            const double step = 46.0;

            // Колесо от себя двигает ленту влево, к себе — вправо: то же
            // направление, что у вертикальных списков, только по другой оси.
            var delta = (Math.Abs(e.Delta.Y) > Math.Abs(e.Delta.X) ? e.Delta.Y : e.Delta.X) * step;
            var next = Math.Clamp(scroll.Offset.X - delta, 0, max);

            if (Math.Abs(next - scroll.Offset.X) < 0.1) return;

            scroll.Offset = new Vector(next, scroll.Offset.Y);
            e.Handled = true;
        }

        private CharacterAvatarPackManagerViewModel? Model
            => DataContext as CharacterAvatarPackManagerViewModel;

        // Ctrl+Z / Ctrl+Y окна. Своя история, отдельная от истории модуля:
        // убранные картинки и папки возвращаются, назначенный переезд
        // отменяется, порядок откатывается — пока окно открыто. Обработчик
        // висит на окне туннелем, как у редактора цвета, и пока менеджер
        // показан, событие гасится всегда — иначе отмена проваливается в модуль
        // под ним и незаметно откатывает операции там.
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            TopLevel.GetTopLevel(this)?.AddHandler(
                KeyDownEvent, OnManagerKeyDown, RoutingStrategies.Tunnel);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            TopLevel.GetTopLevel(this)?.RemoveHandler(KeyDownEvent, OnManagerKeyDown);
            base.OnDetachedFromVisualTree(e);
        }

        private void OnManagerKeyDown(object? sender, KeyEventArgs e)
        {
            if (!IsVisible) return;

            // Delete убирает выделенное — папку или картинку, смотря что
            // нажали последним (см. DeleteSelection). В поле имени клавиша
            // остаётся собой и стирает букву: там выделено не то.
            if (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None)
            {
                var typing = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
                if (typing is TextBox) return;

                if (Model is { } target && target.DeleteSelection()) e.Handled = true;
                return;
            }

            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

            var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            var undo = e.Key == Key.Z && !shift;
            var redo = e.Key == Key.Y || (e.Key == Key.Z && shift);
            if (!undo && !redo) return;

            // В полях ввода самого окна (имя папки) работает обычная текстовая
            // отмена — событие не трогаем. Поле снаружи гасим: правки в окне
            // отменяться не должны чужой историей.
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            if (focused is TextBox box && box.IsEffectivelyVisible)
            {
                if (!this.IsVisualAncestorOf(box)) e.Handled = true;
                return;
            }

            e.Handled = true;

            if (Model is not { } vm) return;
            if (undo) vm.Undo(); else vm.Redo();
        }

        /// <summary>
        /// Enter в поле имени — «я закончил»: имя уходит в сеанс окна сразу, не
        /// дожидаясь задержки, а поле отпускает фокус. Пока фокус в поле, Ctrl+Z
        /// достаётся тексту, а не окну, и вернуть им предыдущее действие нельзя.
        /// </summary>
        private void OnPackNameKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if (Model is not { } vm) return;

            e.Handled = true;
            vm.CommitRenameNow();

            // Фокус уходит на само окно: следующее нажатие клавиш относится к
            // папкам, а не к тексту.
            Focus();
        }

        /// <summary>
        /// Папку завели — ставим курсор в поле имени и выделяем его целиком.
        /// Имя у новой папки временное, и первое, что с ней делают, — дают своё:
        /// набрать его нужно сразу, не возвращаясь мышью к полю.
        /// </summary>
        private void OnPackCreated()
        {
            // Через очередь: лента в этот момент пересобирается, и поле получает
            // имя новой папки уже после того, как выбор дойдёт до него.
            Dispatcher.UIThread.Post(() =>
            {
                var box = this.FindControl<TextBox>("PackNameBox");
                if (box is null || !box.IsEffectivelyEnabled) return;

                box.Focus();
                box.SelectAll();
            }, DispatcherPriority.Background);
        }

        private void ApplyPanelMetrics(double width, double height)
        {
            var panel = this.FindControl<Border>("ManagerPanel");
            if (panel is null) return;

            // Верхний предел подобран под девять столбцов плиток: 744 минус
            // рамка, поля прокрутки и дорожка самой полосы прокрутки оставляют
            // ровно 9 × 76. Ниже предела окно сжимается вместе с модулем, а
            // раскладка сама уменьшает число столбцов — плитки не обрезаются и
            // не наезжают на полосу.
            const double maxPanelWidth = 744.0;

            // Высота задаётся, а не выводится из содержимого. Иначе окно
            // меняло размер на каждый выбор папки: в одной шесть картинок, в
            // соседней пятьсот, и панель то съёживалась, то распахивалась —
            // прямо под курсором, которым только что попали по плитке. Теперь
            // окно стоит на месте, а лишнее содержимое прокручивается внутри.
            const double maxPanelHeight = 640.0;

            var panelWidth = Math.Min(maxPanelWidth, Math.Max(160, width - 48));
            var panelHeight = Math.Min(maxPanelHeight, Math.Max(320, height - 48));

            panel.MaxWidth = panelWidth;
            panel.Width = panelWidth;
            panel.MaxHeight = panelHeight;
            panel.Height = panelHeight;
        }

        // Окно живёт дольше своей модели: пикер заводит новую при каждом
        // открытии. Подписку на прежнюю снимаем, иначе после третьего открытия
        // на одно создание папки приходилось бы три попытки поставить курсор.
        private CharacterAvatarPackManagerViewModel? _boundModel;

        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            if (_boundModel != null) _boundModel.PackCreated -= OnPackCreated;
            _boundModel = null;

            if (Model is not { } vm) return;

            _boundModel = vm;
            vm.PackCreated += OnPackCreated;

            vm.RequestZipImportPicker = async () =>
            {
                var window = TopLevel.GetTopLevel(this) as Window;
                if (window == null) return null;
                var files = await window.StorageProvider.OpenFilePickerAsync(
                    new FilePickerOpenOptions
                    {
                        Title = "Выберите ZIP-архив с папкой аватарок",
                        AllowMultiple = false,
                        FileTypeFilter = new[] { ZipFileType }
                    });
                return files.Count > 0 ? files[0].Path.LocalPath : null;
            };

            vm.RequestZipExportPicker = async (packName) =>
            {
                var window = TopLevel.GetTopLevel(this) as Window;
                if (window == null) return null;
                var file = await window.StorageProvider.SaveFilePickerAsync(
                    new FilePickerSaveOptions
                    {
                        Title = "Сохранить папку аватарок как ZIP",
                        SuggestedFileName = $"{packName}.zip",
                        FileTypeChoices = new[] { ZipFileType }
                    });
                return file?.Path.LocalPath;
            };

            vm.RequestImagePicker = async () =>
            {
                var result = new List<CharacterPickedImage>();

                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return result;

                var files = await topLevel.StorageProvider.OpenFilePickerAsync(
                    new FilePickerOpenOptions
                    {
                        Title = CharactersStrings.FilePicker_SelectImageTitle,
                        AllowMultiple = true,
                        FileTypeFilter = new[] { ImageFileType }
                    });

                // Отдаём не содержимое, а способ его прочитать: сотни выбранных
                // фотографий иначе оказались бы в памяти все разом.
                foreach (var file in files)
                {
                    var picked = file;
                    result.Add(new CharacterPickedImage(picked.Name, async () =>
                    {
                        try
                        {
                            await using var stream = await picked.OpenReadAsync();
                            using var buffer = new MemoryStream();
                            await stream.CopyToAsync(buffer);
                            return buffer.ToArray();
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex, "Reading picked image failed: {Name}", picked.Name);
                            return null;
                        }
                    }));
                }

                return result;
            };
        }

        // ── Перетаскивание плиток ─────────────────────────────────────────
        //
        // Лента папок и содержимое выбранной папки тащатся одинаково, поэтому
        // событие сначала предлагается ленте папок, а если под курсором была не
        // она — содержимому. Оба обработчика молчат, пока у них нет своей
        // плитки под курсором.

        private void OnTilePointerPressed(object? sender, PointerPressedEventArgs e)
        {
            _packDrag.Reset();
            _itemDrag.Reset();

            if (_packDrag.TryPress(e)) return;
            _itemDrag.TryPress(e);
        }

        private void OnTilePointerMoved(object? sender, PointerEventArgs e)
        {
            _packDrag.Move(e);
            _itemDrag.Move(e);
        }

        private void OnTilePointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            _packDrag.Release(e);
            _itemDrag.Release(e);
        }

        // ── Призрак под курсором ──────────────────────────────────────────
        //
        // Один на обе ленты: тащат их одинаково, и второй призрак отличался бы
        // от первого только тем, что его пришлось бы держать в разметке дважды.

        private Canvas? _ghostCanvas;
        private Border? _ghostBorder;
        private Image? _ghostImage;

        private const double GhostSize = 68.0;

        private void ShowGhost(Bitmap? picture, Point pos)
        {
            EnsureGhost();
            if (_ghostCanvas is null) return;

            if (_ghostImage is not null) _ghostImage.Source = picture;

            MoveGhost(pos);
            _ghostCanvas.IsVisible = true;
        }

        private void MoveGhost(Point pos)
        {
            if (_ghostBorder is null) return;
            Canvas.SetLeft(_ghostBorder, pos.X - GhostSize / 2.0);
            Canvas.SetTop(_ghostBorder, pos.Y - GhostSize / 2.0);
        }

        private void HideGhost()
        {
            if (_ghostCanvas is not null) _ghostCanvas.IsVisible = false;
            if (_ghostImage is not null) _ghostImage.Source = null;
        }

        private void EnsureGhost()
        {
            _ghostCanvas ??= this.FindControl<Canvas>("DragGhostCanvas");
            _ghostBorder ??= this.FindControl<Border>("DragGhostBorder");
            _ghostImage ??= this.FindControl<Image>("DragGhostImage");
        }

        /// <summary>
        /// Перетаскивание плиток одной ленты.
        ///
        /// Устроено как перетаскивание карточек персонажей: тот же порог сдвига,
        /// та же пауза удержания (быстрый щелчок остаётся щелчком и доходит до
        /// кнопок плитки), тот же призрак под курсором и та же автопрокрутка у
        /// краёв.
        ///
        /// Место вставки показывает сама плитка: она остаётся в ленте и едет по
        /// ней вслед за курсором, а картинку с неё на это время убирают.
        /// Отдельного пустого места, как у карточек, тут не заводится — плитки
        /// одинаковые и стоят плотной сеткой, и лишний пустой квадрат читался бы
        /// как ещё одна плитка.
        ///
        /// Новый порядок ложится в сеанс окна: пока окно открыто, в хранилище
        /// ничего не уходит, Ctrl+Z возвращает, закрытие записывает.
        /// </summary>
        private sealed class TileDragController
        {
            private const double DragThreshold = 8.0;
            private const long DragHoldDelayMs = 90;

            // Пересчёт места вставки не чаще ~16 раз в секунду. Призрак летит за
            // мышью каждый кадр — это дёшево, двигается одна координата, — а
            // перестановка в коллекции влечёт полный проход раскладки всей ленты.
            private const long ReorderThrottleMs = 60;

            private readonly CharacterAvatarPackManagerOverlay _owner;
            private readonly string _tileClass;
            private readonly Func<ItemsRepeater?> _host;
            private readonly Func<ScrollViewer?> _scroll;
            private readonly Func<IList?> _items;
            private readonly Action<int, int> _move;
            private readonly Func<bool> _canReorder;
            private readonly Func<object, Bitmap?> _ghost;
            private readonly Action<object, bool> _setDragging;
            private readonly Action<List<object>, List<object>> _commit;
            private readonly bool _horizontal;
            private readonly Action<object>? _click;

            private object? _candidate;
            private List<object> _before = new();
            private Panel? _pickedTile;
            private Point _dragStartPoint;
            private long _pressTick;
            private long _lastReorderTick;
            private bool _isDragging;
            private bool _hasPointerCapture;
            private Point _lastDragPos;

            private DispatcherTimer? _autoScrollTimer;
            private double _autoScrollVel;

            public TileDragController(
                CharacterAvatarPackManagerOverlay owner,
                string tileClass,
                Func<ItemsRepeater?> host,
                Func<ScrollViewer?> scroll,
                Func<IList?> items,
                Action<int, int> move,
                Func<bool> canReorder,
                Func<object, Bitmap?> ghost,
                Action<object, bool> setDragging,
                Action<List<object>, List<object>> commit,
                bool horizontal,
                Action<object>? click)
            {
                _horizontal = horizontal;
                _owner = owner;
                _tileClass = tileClass;
                _host = host;
                _scroll = scroll;
                _items = items;
                _move = move;
                _canReorder = canReorder;
                _ghost = ghost;
                _setDragging = setDragging;
                _commit = commit;
                _click = click;
            }

            /// <summary>Забыть кандидата, не трогая уже начатое перетаскивание.</summary>
            public void Reset()
            {
                if (_isDragging) return;
                ClearPicked();
                _candidate = null;
            }

            /// <summary>
            /// Плитка этой ленты под курсором. false — нажали не сюда, событие
            /// достаётся соседней ленте.
            /// </summary>
            public bool TryPress(PointerPressedEventArgs e)
            {
                if (!e.GetCurrentPoint(_owner).Properties.IsLeftButtonPressed) return false;

                var tile = FindTile(e.Source as Visual, _tileClass);
                if (tile?.DataContext is not { } item) return false;

                var list = _items();
                if (list == null || !list.Contains(item)) return false;

                _candidate = item;
                _dragStartPoint = e.GetPosition(_owner);
                _pressTick = Environment.TickCount64;

                // Указатель здесь не захватываем: при простом щелчке захват на
                // окне подавил бы Click кнопок плитки. Захват берётся в момент
                // реального старта перетаскивания.
                SetPicked(tile);
                return true;
            }

            public void Move(PointerEventArgs e)
            {
                if (_candidate is not { } candidate) return;

                if (!e.GetCurrentPoint(_owner).Properties.IsLeftButtonPressed)
                {
                    End(e.Pointer);
                    return;
                }

                var pos = e.GetPosition(_owner);

                if (!_isDragging)
                {
                    var delta = pos - _dragStartPoint;
                    var moved = Math.Abs(delta.X) >= DragThreshold || Math.Abs(delta.Y) >= DragThreshold;

                    // Движение раньше паузы удержания — это щелчок или прокрутка:
                    // отпускаем кандидата, чтобы нажатие дошло до кнопок плитки.
                    if (moved && Environment.TickCount64 - _pressTick < DragHoldDelayMs)
                    {
                        ClearPicked();
                        _candidate = null;
                        return;
                    }

                    if (!moved) return;

                    if (!_canReorder())
                    {
                        ClearPicked();
                        _candidate = null;
                        return;
                    }

                    _isDragging = true;
                    e.Pointer.Capture(_owner);
                    _hasPointerCapture = true;
                    ClearPicked();

                    _before = Snapshot();
                    _setDragging(candidate, true);

                    _owner.ShowGhost(_ghost(candidate), pos);
                    StartAutoScroll();
                    _lastDragPos = pos;
                    _lastReorderTick = Environment.TickCount64;
                }
                else
                {
                    _lastDragPos = pos;
                    _owner.MoveGhost(pos);
                    UpdateAutoScrollVelocity(pos);

                    var now = Environment.TickCount64;
                    if (now - _lastReorderTick >= ReorderThrottleMs)
                    {
                        _lastReorderTick = now;
                        UpdateOrder(pos);
                    }
                }
            }

            public void Release(PointerReleasedEventArgs e)
            {
                if (_candidate is not { } clicked) return;

                if (!_isDragging)
                {
                    ClearPicked();
                    _candidate = null;
                    _click?.Invoke(clicked);
                    return;
                }

                End(e.Pointer);

                // Отпускание после перетаскивания не должно доходить до кнопок под
                // курсором: плитку положили на место, а не нажали на неё.
                e.Handled = true;
            }

            private void End(IPointer? pointer)
            {
                var item = _candidate;
                var wasDragging = _isDragging;

                if (_hasPointerCapture) pointer?.Capture(null);
                _hasPointerCapture = false;
                _isDragging = false;
                _candidate = null;

                ClearPicked();
                _owner.HideGhost();
                StopAutoScroll();

                if (item is not null) _setDragging(item, false);
                if (!wasDragging) return;

                _commit(_before, Snapshot());
            }

            private List<object> Snapshot()
            {
                var list = _items();
                var result = new List<object>();
                if (list == null) return result;

                foreach (var entry in list)
                    if (entry != null) result.Add(entry);

                return result;
            }

            /// <summary>
            /// Подвинуть перетаскиваемую плитку туда, где сейчас курсор. Двигаем
            /// саму плитку в коллекции: лента перестраивается, и порядок на экране
            /// в любой момент равен тому, который запишется при отпускании.
            /// </summary>
            private void UpdateOrder(Point pos)
            {
                var list = _items();
                if (list is null || _candidate is null) return;

                var from = list.IndexOf(_candidate);
                if (from < 0) return;

                var target = TargetIndexAt(pos, from, list);
                if (target < 0 || target == from) return;

                _move(from, target);
            }

            private int TargetIndexAt(Point pos, int from, IList list)
            {
                var host = _host();
                if (host is null) return -1;

                var lastEdge = double.MinValue;

                foreach (var tile in host.GetVisualDescendants().OfType<Panel>())
                {
                    if (!tile.Classes.Contains(_tileClass)) continue;
                    if (tile.DataContext is not { } item) continue;

                    var topLeft = tile.TranslatePoint(new Point(0, 0), _owner);
                    if (topLeft is null) continue;

                    var rect = new Rect(topLeft.Value, tile.Bounds.Size);

                    // Край, за которым начинается пустое место после последней
                    // плитки: у сетки это низ нижнего ряда, у ленты — правый
                    // край последней плитки.
                    var edge = _horizontal ? rect.Right : rect.Bottom;
                    if (edge > lastEdge) lastEdge = edge;

                    if (!rect.Contains(pos)) continue;

                    var index = list.IndexOf(item);
                    if (index < 0) return -1;

                    // Правая половина плитки означает «встать после неё». Без этого
                    // соседнюю плитку было бы не обойти: чтобы уехать вправо на одну
                    // позицию, курсор пришлось бы заводить за середину следующей.
                    if (pos.X > rect.Center.X) index++;
                    if (index > from) index--;

                    return Math.Clamp(index, 0, list.Count - 1);
                }

                // За последней плиткой — в конец. Промежутки между плитками
                // концом не считаются: там курсор оказывается по дороге, и лента
                // прыгала бы на каждом переходе между рядами.
                var reached = _horizontal ? pos.X : pos.Y;
                if (lastEdge > double.MinValue && reached > lastEdge)
                    return list.Count - 1;

                return -1;
            }

            private static Panel? FindTile(Visual? source, string tileClass)
                => source?.GetSelfAndVisualAncestors()
                    .OfType<Panel>()
                    .FirstOrDefault(p => p.Classes.Contains(tileClass));

            private void SetPicked(Panel tile)
            {
                ClearPicked();
                _pickedTile = tile;
                if (!tile.Classes.Contains("picked")) tile.Classes.Add("picked");
            }

            private void ClearPicked()
            {
                if (_pickedTile is null) return;
                _pickedTile.Classes.Remove("picked");
                _pickedTile = null;
            }

            // ── Автопрокрутка у краёв ─────────────────────────────────────
            //
            // Лента длиннее своей рамки, и без прокрутки плитку с последнего
            // ряда нельзя было бы утащить в первый.

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
            }

            private void UpdateAutoScrollVelocity(Point pos)
            {
                _autoScrollVel = 0;

                var scroll = _scroll();
                if (scroll is null) return;

                var topLeft = scroll.TranslatePoint(new Point(0, 0), _owner);
                if (topLeft is null) return;

                // Сетка картинок прокручивается вниз, лента папок — вбок:
                // считаем по той стороне, вдоль которой список едет.
                var start = _horizontal ? topLeft.Value.X : topLeft.Value.Y;
                var end = start + (_horizontal ? scroll.Bounds.Width : scroll.Bounds.Height);
                var along = _horizontal ? pos.X : pos.Y;

                const double zone = 40.0;
                const double maxSpeed = 18.0;

                if (along < start + zone)
                    _autoScrollVel = -maxSpeed * Math.Clamp((start + zone - along) / zone, 0, 1);
                else if (along > end - zone)
                    _autoScrollVel = maxSpeed * Math.Clamp((along - (end - zone)) / zone, 0, 1);
            }

            private void OnAutoScrollTick(object? sender, EventArgs e)
            {
                if (!_isDragging || Math.Abs(_autoScrollVel) < 0.5) return;

                var scroll = _scroll();
                if (scroll is null) return;

                var offset = scroll.Offset;

                var max = _horizontal
                    ? Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width)
                    : Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);

                var current = _horizontal ? offset.X : offset.Y;
                var next = Math.Clamp(current + _autoScrollVel, 0, max);
                if (Math.Abs(next - current) < 0.1) return;

                scroll.Offset = _horizontal
                    ? new Vector(next, offset.Y)
                    : new Vector(offset.X, next);

                // Лента уехала — под курсором теперь другая плитка.
                _owner.MoveGhost(_lastDragPos);

                var now = Environment.TickCount64;
                if (now - _lastReorderTick >= ReorderThrottleMs)
                {
                    _lastReorderTick = now;
                    UpdateOrder(_lastDragPos);
                }
            }
        }

        // ── Приём картинок всем окном ─────────────────────────────────────

        private void OnManagerDragOver(object? sender, DragEventArgs e)
        {
            var accepts = e.DataTransfer.Contains(DataFormat.File);
            e.DragEffects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
            SetDropTarget(accepts);
            e.Handled = true;
        }

        private void OnManagerDragLeave(object? sender, DragEventArgs e)
        {
            SetDropTarget(false);
            e.Handled = true;
        }

        private async void OnManagerDrop(object? sender, DragEventArgs e)
        {
            e.Handled = true;
            SetDropTarget(false);

            if (Model is not { } vm) return;

            var files = e.DataTransfer.TryGetFiles();
            if (files == null) return;

            // В папку кладут запас, поэтому берётся вся брошенная пачка, а не
            // одна картинка: здесь у выбора нет единственного результата.
            foreach (var file in files)
            {
                if (file is not IStorageFile storageFile) continue;
                if (!CharacterAvatarPickerOverlay.IsDroppableImage(storageFile.Name)) continue;

                try
                {
                    await using var stream = await storageFile.OpenReadAsync();
                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer);

                    await vm.HandleDroppedImageAsync(buffer.ToArray(), storageFile.Name);
                }
                catch (Exception ex)
                {
                    // Бросить могут что угодно — папку, ярлык, недоступный файл.
                    _logger.Error(ex, "Pack manager drop failed: {Name}", storageFile.Name);
                }
            }
        }

        // ── Приём архива кнопкой ──────────────────────────────────────────
        //
        // Кнопка приёма ZIP объявлена приёмником сама. Событие Drop всплывает,
        // и обработчик кнопки успевает пометить его обработанным раньше, чем
        // до него доберётся обработчик всего окна — иначе архив ушёл бы в
        // общий разбор, где ждут только картинки.

        private void OnZipDragOver(object? sender, DragEventArgs e)
        {
            var accepts = e.DataTransfer.Contains(DataFormat.File);
            e.DragEffects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
            SetZipArmed(accepts);
            SetDropTarget(false);
            e.Handled = true;
        }

        private void OnZipDragLeave(object? sender, DragEventArgs e)
        {
            SetZipArmed(false);
            e.Handled = true;
        }

        private async void OnZipDrop(object? sender, DragEventArgs e)
        {
            e.Handled = true;
            SetZipArmed(false);
            SetDropTarget(false);

            if (Model is not { } vm) return;

            var files = e.DataTransfer.TryGetFiles();
            if (files == null) return;

            foreach (var file in files)
            {
                if (file is not IStorageFile storageFile) continue;

                var path = storageFile.Path.LocalPath;
                if (string.IsNullOrEmpty(path)) continue;
                if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;

                try { await vm.HandleDroppedZipAsync(path); }
                catch (Exception ex) { _logger.Error(ex, "Zip drop failed: {Path}", path); }

                return;
            }

            vm.StatusMessage = "На эту кнопку бросают ZIP-архив с папкой аватарок.";
        }

        private void SetZipArmed(bool value)
        {
            var button = this.FindControl<Button>("ImportZipButton");
            if (button == null) return;

            if (value)
            {
                if (!button.Classes.Contains("dropArmed")) button.Classes.Add("dropArmed");
            }
            else
            {
                button.Classes.Remove("dropArmed");
            }
        }

        private void SetDropTarget(bool value)
        {
            if (Model is { } vm) vm.IsDropTarget = value;
        }

        // Скрим блокирует модуль, но окно не закрывает — как в редакторе цвета.
        private void OnScrimPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;
    }
}
