using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Writersword.Core.Interfaces.WorkFlows;
using Writersword.Core.Models.Project;
using Writersword.Core.Services;
using Writersword.Infrastructure.Converters;

namespace Writersword.Styles.UserControls
{
    /// <summary>
    /// Внутри-приложенческий оверлей редактора цвета. Живёт в составе модуля,
    /// затемняет и блокирует только его область, не создаёт окно ОС. Тянется
    /// по высоте под размер модуля (середина прокручивается).
    /// Режимы выбора: HSV-квадрат + полоса оттенка, соты, цветовое колесо и
    /// вкладка ручного ввода значений (HEX / RGB / HSL / HSV). Плюс пипетка с
    /// экрана и пользовательская палитра проекта с перетаскиванием образцов.
    /// </summary>
    public partial class ColorEditorOverlay : UserControl
    {
        private const double SvSize = 200;
        private const double HueLen = 200;
        private const double WheelSize = 200;
        private const double WheelRadius = 100;

        private readonly IScreenColorPicker _eyedropper = ScreenColorPicker.Create();
        private bool _syncing;
        private bool _ring;
        private bool _ringsAllState;
        // Редактируется карточка группы: показывается галка закладки-ленточки и
        // сама закладка в превью; состояние возвращается в результате редактора.
        private bool _isGroupCard;
        private bool _bookmark = true;
        private TaskCompletionSource<ColorEditResult?>? _tcs;

        // Текущий цвет редактора и его HSV-представление (оттенок сохраняется
        // при уходе насыщенности в ноль, чтобы полоса не прыгала на красный).
        private Color _current;
        private double _h, _s, _v;

        // Альфа-канал текущего цвета. Внутренние контролы (квадрат, колесо,
        // ползунки RGB/HSL/HSV) работают в непрозрачном RGB и альфу сохраняют;
        // задаётся ползунком A и 8-значным HEX (#AARRGGBB). Полностью
        // прозрачное значение — это «без цвета».
        private byte _alpha = 255;

        // ── История изменений основного цвета (Ctrl+Z / Ctrl+Y) ──────────
        // Каждое изменение цвета (ползунки RGB/HSL/HSV, точка на квадрате и
        // колесе, полоса оттенка, соты, HEX, поля значений, пипетка, свотчи)
        // попадает в историю. Границей записи служит жест: пока кнопка мыши
        // зажата (таскание точки или ползунка), изменения сливаются в одну
        // запись, отпускание закрывает её — следующее изменение начинает новую.
        // Для изменений без жеста (клавиатура, поток от генератора) действует
        // короткое окно слияния по времени.
        private sealed class ColorSnap
        {
            public Color Value;
            public long Seq;
        }

        private readonly List<ColorSnap> _colorUndo = new();
        private readonly List<ColorSnap> _colorRedo = new();
        private const int MaxColorHistory = 100;
        private const int ColorMergeMs = 500;
        private bool _restoringColor;
        private long _lastColorPushTick;
        private bool _colorGestureActive;

        private bool _showPreview;
        private bool _previewCollapsed;

        private bool _svDrag, _hueDrag, _wheelDrag;
        private bool _honeycombBuilt, _wheelBuilt;

        // Ячейки сот и текущая подсвеченная (контур вокруг выбранного цвета).
        private readonly List<Polygon> _honeyCells = new();
        private Polygon? _honeySelected;

        // Пользовательская палитра проекта (закреплённые цвета). Источник истины —
        // ProjectFile.ProjectPinnedColors; эта коллекция — представление для биндинга.
        public ObservableCollection<string> Palette { get; } = new();

        private bool _palettePressed, _paletteDragging;
        private int _paletteDragIndex = -1;
        private string? _paletteDragHex;
        private bool _paletteDirty;

        // Перетаскивание свотча «Мои цвета» по принципу карточек/палитр (2D-сетка).
        private Border? _swElem;
        private Control? _swCell;
        private ItemsControl? _swList;
        private IPointer? _swPointer;
        private int _swTarget = -1;
        private int _swColumns = 1;
        private double _swCellW, _swCellH;
        private Avalonia.Animation.Transitions? _swSavedTransitions;
        private Avalonia.Threading.DispatcherTimer? _swHoldTimer;

        // ── Шум: поле случайных цветов с зумом по клику ───────────────────
        // Битмап малого разрешения растягивается до окна с интерполяцией —
        // получаются мягкие цветовые «облака», как в референсе.
        private const int NoiseRes = 64;
        private const double NoiseView = 220;
        private WriteableBitmap? _noiseBmp;
        private double[]? _noiseR, _noiseG, _noiseB;   // значения 0..255 для билинейной выборки
        private string _noisePreset = "rainbow";
        private bool _noiseBuilt;
        private readonly Random _noiseRng = new();
        private ScaleTransform? _noiseScaleT;
        private TranslateTransform? _noiseTransT;
        private Border? _noiseSolid;                   // слой однотонной заливки поверх поля

        // Текущее и целевое состояние камеры + параметры анимации перехода.
        private double _nScale = 1, _nTx, _nTy;
        private double _nScaleStart = 1, _nTxStart, _nTyStart;
        private double _nScaleTarget = 1, _nTxTarget, _nTyTarget;
        private double _nAnimT;
        private double _nAnimStep = 0.08;              // прирост за тик = 16мс / длительность
        private Avalonia.Threading.DispatcherTimer? _noiseTimer;

        // Цвет, выбранный кликом: показываем его только в конце наезда (без спойлера).
        private Color _nPending;
        private bool _nHasPending;

        // Полоса-редактор градиента под цветами.
        private GradientStripEditor? _gradientStrip;
        private bool _gradientEnabled;
        // Градиент включён автоматически кликом по образцу-градиенту, а не галкой:
        // последующий выбор простого цвета выключает его обратно. Ручное включение
        // галкой пометку не ставит — тогда простой цвет режим не сбрасывает.
        private bool _gradientAutoEnabled;
        private bool _settingGrad;

        public ColorEditorOverlay()
        {
            InitializeComponent();
            IsVisible = false;

            // Границы жеста изменения цвета для истории Ctrl+Z: пока кнопка мыши
            // зажата — изменения сливаются в одну запись, отпускание (или потеря
            // захвата) закрывает её. Обработчики туннельные и ловят и уже
            // обработанные события — стандартные ползунки гасят их сами.
            AddHandler(PointerPressedEvent, OnColorGesturePressed,
                RoutingStrategies.Tunnel, handledEventsToo: true);
            AddHandler(PointerReleasedEvent, OnColorGestureReleased,
                RoutingStrategies.Tunnel, handledEventsToo: true);
            AddHandler(PointerCaptureLostEvent, OnColorGestureCaptureLost,
                RoutingStrategies.Tunnel, handledEventsToo: true);

            // Клик/протяжка по дорожке градиентного ползунка: значение моментально
            // переходит под курсор и сразу тянется одним движением.
            foreach (var name in new[]
            {
                "SliderR", "SliderG", "SliderB", "SliderA",
                "SliderAHoney", "SliderANoise",
                "SlR", "SlG", "SlB", "SlA",
                "SlHslH", "SlHslS", "SlHslL",
                "SlHsvH", "SlHsvS", "SlHsvV"
            })
            {
                var sl = this.FindControl<Slider>(name);
                if (sl is null) continue;
                sl.AddHandler(InputElement.PointerPressedEvent, OnGradSliderPressed, RoutingStrategies.Tunnel);
                sl.AddHandler(InputElement.PointerMovedEvent, OnGradSliderMoved, RoutingStrategies.Tunnel);
                sl.AddHandler(InputElement.PointerReleasedEvent, OnGradSliderReleased, RoutingStrategies.Tunnel);
            }

            // Связь менеджера палитр с редактором: клик по образцу ставит цвет,
            // «+» берёт текущий цвет редактора.
            var pm = this.FindControl<PaletteManagerView>("PalettesPanel");
            if (pm is not null)
            {
                pm.ColorPicked = SelectFromCode;
                pm.CurrentColorProvider = () =>
                    (_gradientEnabled ? _gradientStrip?.BuildSpec().ToCode() : null)
                    ?? $"#{_current.R:X2}{_current.G:X2}{_current.B:X2}";
                pm.ActiveChanged += UpdateActivePalettePlate;
                UpdateActivePalettePlate();
            }

            // Полоса градиента: выбор чипа-стопа загружает его цвет в основной выбор.
            _gradientStrip = this.FindControl<GradientStripEditor>("GradientStrip");
            if (_gradientStrip is not null)
            {
                _gradientStrip.ActiveStopSelected += SelectFromHex;
                _gradientStrip.SpecChanged += OnStripSpecChanged;
            }

            // После Ctrl+Z/Ctrl+Y показываем вкладку, где произошло изменение —
            // иначе откат в невидимой секции остаётся незамеченным.
            var palettesPanel = this.FindControl<PaletteManagerView>("PalettesPanel");
            if (palettesPanel is not null)
                palettesPanel.HistoryApplied += OnPaletteHistoryApplied;

            // По умолчанию открыта вкладка «Мои цвета».
            SetCollectionTab("my");

            // Высота панели не должна превышать высоту модуля — иначе при сжатии
            // окна редактор обрезается. Середина (ScrollViewer) прокручивается.
            this.GetObservable(BoundsProperty).Subscribe(b =>
            {
                if (b.Width <= 0) return;
                bool wide = ApplyPanelMetrics(b.Width, b.Height);
                // Перенос панелей во время прохода раскладки роняет ContentPresenter
                // (NRE), поэтому при изменении размеров откладываем на следующий тик.
                if (_isWide != wide)
                {
                    _isWide = wide;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => SetTwoColumn(wide));
                }
            });
        }

        // Перехват Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z вешаем на окно, пока редактор живёт
        // в дереве; реагируем только когда редактор открыт (IsVisible).
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            TopLevel.GetTopLevel(this)?.AddHandler(
                KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            TopLevel.GetTopLevel(this)?.RemoveHandler(KeyDownEvent, OnEditorKeyDown);
            base.OnDetachedFromVisualTree(e);
        }

        // Начало/конец жеста (нажатие и отпускание кнопки мыши в редакторе).
        private void OnColorGesturePressed(object? sender, PointerPressedEventArgs e)
            => _colorGestureActive = true;

        private void OnColorGestureReleased(object? sender, PointerReleasedEventArgs e)
            => EndColorGesture();

        private void OnColorGestureCaptureLost(object? sender, PointerCaptureLostEventArgs e)
            => EndColorGesture();

        // Отпустили мышку — запись жеста закрыта: следующее изменение цвета
        // попадёт в отдельную запись истории, а не сольётся с предыдущей.
        private void EndColorGesture()
        {
            _colorGestureActive = false;
            _lastColorPushTick = 0;
        }

        // Запоминает состояние ДО изменения цвета; продолжение жеста (или поток
        // изменений в пределах окна слияния) лишь обновляет номер записи в общей
        // хронологии, чтобы маршрутизация Ctrl+Z считала цвет последним изменением.
        private void PushColorUndo(Color next)
        {
            if (_restoringColor || next == _current) return;
            long now = Environment.TickCount64;
            _colorRedo.Clear();
            if (_colorUndo.Count > 0 && _lastColorPushTick != 0
                && (_colorGestureActive || now - _lastColorPushTick <= ColorMergeMs))
            {
                _colorUndo[_colorUndo.Count - 1].Seq = UndoClock.Next();
                _lastColorPushTick = now;
                return;
            }
            _colorUndo.Add(new ColorSnap { Value = _current, Seq = UndoClock.Next() });
            if (_colorUndo.Count > MaxColorHistory) _colorUndo.RemoveAt(0);
            _lastColorPushTick = now;
        }

        private long LastColorUndoSeq =>
            _colorUndo.Count > 0 ? _colorUndo[_colorUndo.Count - 1].Seq : -1;
        private long LastColorRedoSeq =>
            _colorRedo.Count > 0 ? _colorRedo[_colorRedo.Count - 1].Seq : -1;

        // Откат цвета: встречная запись для повтора наследует номер отменяемой.
        // Сброс времени слияния — чтобы следующее изменение не приклеилось к
        // записи, оставшейся до отката.
        private void ColorUndo()
        {
            if (_colorUndo.Count == 0) return;
            var snap = _colorUndo[_colorUndo.Count - 1];
            _colorUndo.RemoveAt(_colorUndo.Count - 1);
            _colorRedo.Add(new ColorSnap { Value = _current, Seq = snap.Seq });
            _lastColorPushTick = 0;
            _restoringColor = true;
            try { SetColor(snap.Value); }
            finally { _restoringColor = false; }
        }

        private void ColorRedo()
        {
            if (_colorRedo.Count == 0) return;
            var snap = _colorRedo[_colorRedo.Count - 1];
            _colorRedo.RemoveAt(_colorRedo.Count - 1);
            _colorUndo.Add(new ColorSnap { Value = _current, Seq = snap.Seq });
            _lastColorPushTick = 0;
            _restoringColor = true;
            try { SetColor(snap.Value); }
            finally { _restoringColor = false; }
        }

        // Отмена/повтор изменений палитр. В текстовом поле (переименование, ввод HEX)
        // не перехватываем — там работает обычная отмена текста.
        private void OnEditorKeyDown(object? sender, KeyEventArgs e)
        {
            if (!IsVisible) return;
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            bool undo = e.Key == Key.Z && !shift;
            bool redo = e.Key == Key.Y || (e.Key == Key.Z && shift);
            if (!undo && !redo) return;

            // Скрытое поле (например, погасший ввод имени палитры) может продолжать
            // держать фокус — текстовым полем считаем только реально видимое, иначе
            // Ctrl+Z уходит в невидимый TextBox и «не срабатывает».
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            if (focused is TextBox tb && tb.IsEffectivelyVisible)
            {
                // В текстовых полях самого редактора (имя палитры, HEX и т.п.)
                // работает обычная текстовая отмена — событие не трогаем. Поле
                // вне редактора — гасим: фон меняться не должен.
                if (!this.IsVisualAncestorOf(tb))
                    e.Handled = true;
                return;
            }

            // Пока редактор открыт, Ctrl+Z/Ctrl+Y полностью изолированы от
            // подложки: событие гасится всегда, даже когда откатывать нечего.
            // Иначе нажатие проваливается в модуль под редактором (например,
            // список персонажей) и незаметно откатывает операции там.
            e.Handled = true;

            // Отмена и повтор идут в единой хронологии трёх историй: полосы
            // градиента, палитр и основного цвета (см. UndoClock). Откатывается
            // изменение с наибольшим номером; повторяется отменённое последним,
            // то есть с наименьшим. Побочные смены цвета при откате градиента и
            // палитр (выбор активного стопа и т.п.) в историю цвета не пишутся —
            // на время отката поднимается _restoringColor.
            var pm = this.FindControl<PaletteManagerView>("PalettesPanel");
            if (undo)
            {
                long g = _gradientStrip is not null && _gradientStrip.CanUndo
                    ? _gradientStrip.LastUndoSeq : -1;
                long p = pm is not null && pm.CanUndo ? pm.LastUndoSeq : -1;
                long c = LastColorUndoSeq;
                if (g < 0 && p < 0 && c < 0) return;
                if (g >= p && g >= c)
                {
                    _restoringColor = true;
                    try { _gradientStrip!.Undo(); }
                    finally { _restoringColor = false; }
                }
                else if (p >= c)
                {
                    _restoringColor = true;
                    try { pm!.Undo(); }
                    finally { _restoringColor = false; }
                }
                else ColorUndo();
            }
            else
            {
                long g = _gradientStrip is not null && _gradientStrip.CanRedo
                    ? _gradientStrip.LastRedoSeq : long.MaxValue;
                long p = pm is not null && pm.CanRedo ? pm.LastRedoSeq : long.MaxValue;
                long c = _colorRedo.Count > 0 ? LastColorRedoSeq : long.MaxValue;
                if (g == long.MaxValue && p == long.MaxValue && c == long.MaxValue) return;
                if (g <= p && g <= c)
                {
                    _restoringColor = true;
                    try { _gradientStrip!.Redo(); }
                    finally { _restoringColor = false; }
                }
                else if (p <= c)
                {
                    _restoringColor = true;
                    try { pm!.Redo(); }
                    finally { _restoringColor = false; }
                }
                else ColorRedo();
            }
        }

        /// <summary>
        /// Показывает редактор поверх модуля. Возвращает выбранный HEX или null при отмене.
        /// </summary>
        public Task<ColorEditResult?> ShowAsync(string hex, bool showPreview,
            Bitmap? image, string? name, string? fallback, bool ringEnabled, bool ringsAllState,
            bool isGroup = false, bool bookmarkEnabled = true)
        {
            _tcs?.TrySetResult(null);
            _tcs = new TaskCompletionSource<ColorEditResult?>();

            _showPreview = showPreview;
            _previewCollapsed = false;
            var preview = this.FindControl<Control>("PreviewPanel");
            if (preview is not null) preview.IsVisible = showPreview;
            var previewToggle = this.FindControl<Button>("PreviewToggle");
            if (previewToggle is not null) previewToggle.IsVisible = showPreview;

            var ringSection = this.FindControl<Control>("RingSection");
            if (ringSection is not null) ringSection.IsVisible = showPreview;
            var ringConfirm = this.FindControl<Control>("RingConfirmPanel");
            if (ringConfirm is not null) ringConfirm.IsVisible = false;

            var eye = this.FindControl<Button>("EyedropperButton");
            if (eye is not null) eye.IsEnabled = _eyedropper.IsSupported;

            // Превью реальной карточки: картинка/значок и имя.
            var img = this.FindControl<Image>("PreviewAvatarImage");
            if (img is not null) img.Source = image;
            var fb = this.FindControl<TextBlock>("PreviewFallbackText");
            if (fb is not null)
            {
                fb.Text = string.IsNullOrEmpty(fallback) ? "?" : fallback;
                fb.IsVisible = image is null;
            }
            var nm = this.FindControl<TextBlock>("PreviewNameText");
            if (nm is not null) nm.Text = string.IsNullOrWhiteSpace(name) ? string.Empty : name;

            _ring = ringEnabled;
            _ringsAllState = ringsAllState;
            _paletteDirty = false;
            var ringCheck = this.FindControl<CheckBox>("RingCheck");
            if (ringCheck is not null) ringCheck.IsChecked = ringEnabled;

            // Настройка закладки-ленточки доступна только для карточек групп.
            _isGroupCard = isGroup;
            _bookmark = bookmarkEnabled;
            var bookmarkCheck = this.FindControl<CheckBox>("BookmarkCheck");
            if (bookmarkCheck is not null)
            {
                bookmarkCheck.IsVisible = showPreview && isGroup;
                bookmarkCheck.IsChecked = bookmarkEnabled;
            }
            // Подпись кнопки переключателя: если у всех включено — «убрать у всех», иначе «включить у всех».
            var ringAllBtn = this.FindControl<Button>("RingAllButton");
            if (ringAllBtn is not null)
                ringAllBtn.Content = ringsAllState
                    ? SharedStrings.ColorEditor_RingNone
                    : SharedStrings.ColorEditor_RingAll;

            BuildHoneycomb();
            BuildWheel();
            LoadPalette();
            SetTab(0);
            this.FindControl<PaletteManagerView>("PalettesPanel")?.Refresh();
            SetCollectionTab(_collectionTab);

            // Входное значение может быть обычным hex либо кодом градиента —
            // грузим в полосу, а в основной выбор ставим цвет активного стопа.
            // Историю полосы чистим на случай, если прошлый сеанс редактора
            // завершился в обход обычного закрытия (палитры чистит Refresh выше).
            var spec = GradientSpec.Parse(hex);
            _gradientStrip?.ClearHistory();
            _gradientStrip?.Load(spec);
            SetGradientEnabled(!spec.IsSolid);
            // Стартовое состояние задано входным значением, автопометки нет:
            // выбор простого цвета сам по себе градиент не выключит.
            _gradientAutoEnabled = false;

            Color c;
            try { c = Color.Parse(spec.SolidHex); }
            catch { c = Color.FromRgb(0x60, 0x7D, 0x8B); }

            SetColor(c);

            // Начальная установка цвета при открытии — не действие пользователя:
            // история очищается уже после неё, чтобы первый Ctrl+Z не откатывал
            // на цвет из прошлого сеанса редактора.
            ClearUndoHistory();

            // Панель всегда видима (на случай, если прошлые версии оставили Opacity=0).
            // Раскладку колонок под ширину модуля доложит наблюдатель Bounds — перенос
            // панелей делается на следующем тике, что безопасно.
            var editorPanel = this.FindControl<Border>("EditorPanel");
            if (editorPanel is not null) editorPanel.Opacity = 1;

            IsVisible = true;
            return _tcs.Task;
        }

        // Размеры панели редактора под доступную область; возвращает, нужна ли
        // двухколоночная раскладка (сам перенос панелей — SetTwoColumn — отдельно).
        private bool ApplyPanelMetrics(double width, double height)
        {
            bool wide = width >= 820;
            var panel = this.FindControl<Border>("EditorPanel");
            if (panel is not null)
            {
                panel.MaxHeight = Math.Max(220, height - 48);
                panel.MaxWidth = Math.Min(wide ? 760 : 460, Math.Max(120, width - 48));
                panel.Width = wide ? 720 : 440;

                var lbl = this.FindControl<TextBlock>("EyedropperLabel");
                if (lbl is not null) lbl.IsVisible = panel.MaxWidth > 340;
            }
            return wide;
        }

        // applyAll: null — кольцо только для этого; true — кольца всем; false — убрать у всех.
        private void CompleteEditor(bool? applyAll)
        {
            if (_paletteDirty) SaveActiveDocument();
            var result = new ColorEditResult
            {
                // С неполной альфой отдаём 8-значный HEX (#AARRGGBB); альфа 0 —
                // это «без цвета». Конвертеры кистей проекта понимают альфу.
                Hex = _current.A < 255
                    ? $"#{_current.A:X2}{_current.R:X2}{_current.G:X2}{_current.B:X2}"
                    : $"#{_current.R:X2}{_current.G:X2}{_current.B:X2}",
                Code = (_gradientEnabled && _gradientStrip is not null)
                    ? _gradientStrip.BuildSpec().ToCode() : null,
                Ring = _ring,
                Bookmark = _bookmark,
                ApplyAll = applyAll
            };
            ClearUndoHistory();
            IsVisible = false;
            var tcs = _tcs;
            _tcs = null;
            tcs?.TrySetResult(result);
        }

        private void CompleteCancel()
        {
            // Палитра применяется сразу (через «+»), поэтому сохраняем её и при отмене.
            if (_paletteDirty) SaveActiveDocument();
            ClearUndoHistory();
            IsVisible = false;
            var tcs = _tcs;
            _tcs = null;
            tcs?.TrySetResult(null);
        }

        // Закрытие редактора безвозвратно очищает его историю Ctrl+Z (палитры и
        // градиент). Истории других модулей (например, текстового редактора) это
        // не затрагивает — у них собственные стеки.
        private void ClearUndoHistory()
        {
            _gradientStrip?.ClearHistory();
            this.FindControl<PaletteManagerView>("PalettesPanel")?.ClearHistory();
            _colorUndo.Clear();
            _colorRedo.Clear();
            _lastColorPushTick = 0;
            _colorGestureActive = false;
        }

        // Палитра живёт в ProjectFile, но её изменение не помечает проект «грязным»,
        // поэтому при правке палитры сохраняем документ явно.
        private static void SaveActiveDocument()
        {
            try
            {
                var tab = CoreServices.GetService<ITabCollection>()?.ActiveTab;
                var workflow = CoreServices.GetService<IProjectWorkflow>();
                if (tab is not null && workflow is not null)
                    _ = workflow.SaveDocumentAsync(tab, showNotification: false);
            }
            catch { }
        }

        // ── Применение/отрисовка цвета ────────────────────────────────────

        // Цвет из внешнего источника (RGB, HEX, соты, палитра, пипетка): пересчёт
        // HSV. Альфа берётся из пришедшего цвета (8-значный HEX и «без цвета»
        // задают её явно, обычный 6-значный выбор возвращает непрозрачность).
        private void SetColor(Color c)
        {
            _alpha = c.A;
            var (h, s, v) = RgbToHsv(c);
            if (s > 1e-4) _h = h;
            _s = s;
            _v = v;
            Render(c);
        }

        // Сохраняет текущую альфу при выборе из внутренних контролов RGB/HSL/HSV.
        private Color WithAlpha(Color c) => Color.FromArgb(_alpha, c.R, c.G, c.B);

        // Цвет из внутреннего источника (квадрат, оттенок, колесо): HSV не трогаем.
        private void ApplyHsv() => Render(HsvToRgb(_h, _s, _v));

        private void Render(Color c)
        {
            // Итоговый цвет всегда несёт текущую альфу: внутренние источники
            // отдают непрозрачный RGB, альфа живёт отдельным каналом.
            c = Color.FromArgb(_alpha, c.R, c.G, c.B);
            PushColorUndo(c);
            _syncing = true;
            try
            {
                _current = c;
                _gradientStrip?.SetActiveColor(c);

                var sw = this.FindControl<Border>("PreviewSwatch");
                if (sw is not null) sw.Background = new SolidColorBrush(c);

                // HEX: с альфой — 8 значений (#AARRGGBB), непрозрачный — обычные 6.
                var rgbHex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                var hexStr = c.A < 255 ? $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}" : rgbHex;
                var hb = this.FindControl<TextBox>("HexBox");
                if (hb is not null) hb.Text = hexStr;
                HighlightHoneycomb(rgbHex);

                // Ползунки RGB + альфа (вкладка «Спектр»)
                var sr = this.FindControl<Slider>("SliderR"); if (sr is not null) sr.Value = c.R;
                var sg = this.FindControl<Slider>("SliderG"); if (sg is not null) sg.Value = c.G;
                var sb = this.FindControl<Slider>("SliderB"); if (sb is not null) sb.Value = c.B;
                var sa = this.FindControl<Slider>("SliderA"); if (sa is not null) sa.Value = c.A;

                // Альфа-ползунки остальных вкладок (Соты / Колесо / Шум); альфа
                // вкладки «Значения» (SlA) идёт вместе с остальными ползунками
                // этой вкладки внутри UpdateGradients.
                var saHoney = this.FindControl<Slider>("SliderAHoney"); if (saHoney is not null) saHoney.Value = c.A;
                var saWheel = this.FindControl<Slider>("SliderAWheel"); if (saWheel is not null) saWheel.Value = c.A;
                var saNoise = this.FindControl<Slider>("SliderANoise"); if (saNoise is not null) saNoise.Value = c.A;

                // Градиентные ползунки (значения + цвет треков).
                UpdateGradients(c);
                var lr = this.FindControl<TextBlock>("LabelR"); if (lr is not null) lr.Text = c.R.ToString();
                var lg = this.FindControl<TextBlock>("LabelG"); if (lg is not null) lg.Text = c.G.ToString();
                var lb = this.FindControl<TextBlock>("LabelB"); if (lb is not null) lb.Text = c.B.ToString();
                var la = this.FindControl<TextBlock>("LabelA"); if (la is not null) la.Text = c.A.ToString();
                var laHoney = this.FindControl<TextBlock>("LabelAHoney"); if (laHoney is not null) laHoney.Text = c.A.ToString();
                var laWheel = this.FindControl<TextBlock>("LabelAWheel"); if (laWheel is not null) laWheel.Text = c.A.ToString();
                var laNoise = this.FindControl<TextBlock>("LabelANoise"); if (laNoise is not null) laNoise.Text = c.A.ToString();

                // Текстовые поля (вкладка «Значения»)
                SetText("TxtR", c.R.ToString(CultureInfo.InvariantCulture));
                SetText("TxtG", c.G.ToString(CultureInfo.InvariantCulture));
                SetText("TxtB", c.B.ToString(CultureInfo.InvariantCulture));
                SetText("TxtA", c.A.ToString(CultureInfo.InvariantCulture));
                var (hl_h, hl_s, hl_l) = RgbToHsl(c);
                SetText("TxtHslH", ((int)Math.Round(hl_h)).ToString(CultureInfo.InvariantCulture));
                SetText("TxtHslS", ((int)Math.Round(hl_s * 100)).ToString(CultureInfo.InvariantCulture));
                SetText("TxtHslL", ((int)Math.Round(hl_l * 100)).ToString(CultureInfo.InvariantCulture));
                SetText("TxtHsvH", ((int)Math.Round(_h)).ToString(CultureInfo.InvariantCulture));
                SetText("TxtHsvS", ((int)Math.Round(_s * 100)).ToString(CultureInfo.InvariantCulture));
                SetText("TxtHsvV", ((int)Math.Round(_v * 100)).ToString(CultureInfo.InvariantCulture));

                // Квадрат и полоса оттенка
                var hueLayer = this.FindControl<Border>("SvHueLayer");
                if (hueLayer is not null) hueLayer.Background = new SolidColorBrush(HsvToRgb(_h, 1, 1));

                var svThumb = this.FindControl<Border>("SvThumb");
                if (svThumb is not null)
                {
                    Canvas.SetLeft(svThumb, _s * SvSize - 8);
                    Canvas.SetTop(svThumb, (1 - _v) * SvSize - 8);
                }
                var hueThumb = this.FindControl<Border>("HueThumb");
                if (hueThumb is not null)
                {
                    Canvas.SetLeft(hueThumb, -2);
                    Canvas.SetTop(hueThumb, _h / 360.0 * HueLen - 3);
                }

                // Колесо
                var wheelDim = this.FindControl<Ellipse>("WheelDim");
                if (wheelDim is not null) wheelDim.Opacity = 1 - _v;
                var wheelVal = this.FindControl<Slider>("WheelValue");
                if (wheelVal is not null) wheelVal.Value = _v * 100;
                var wheelThumb = this.FindControl<Border>("WheelThumb");
                if (wheelThumb is not null)
                {
                    double ang = _h * Math.PI / 180.0;
                    double tx = WheelRadius + Math.Sin(ang) * (_s * WheelRadius);
                    double ty = WheelRadius - Math.Cos(ang) * (_s * WheelRadius);
                    Canvas.SetLeft(wheelThumb, tx - 8);
                    Canvas.SetTop(wheelThumb, ty - 8);
                }

                UpdatePreviewVisual();
            }
            finally
            {
                _syncing = false;
            }
        }

        private void SetText(string name, string value)
        {
            var t = this.FindControl<TextBox>(name);
            if (t is not null) t.Text = value;
        }

        private void UpdateCardPreview(IBrush brush)
        {
            var border = this.FindControl<Border>("PreviewCardBorder");
            if (border is not null) border.BorderBrush = brush;
            var avatar = this.FindControl<Ellipse>("PreviewAvatar");
            if (avatar is not null) avatar.Fill = brush;
            var ring = this.FindControl<Border>("PreviewRing");
            if (ring is not null)
            {
                ring.BorderBrush = brush;
                ring.IsVisible = _ring;
            }
            var bookmark = this.FindControl<Avalonia.Controls.Shapes.Path>("PreviewBookmark");
            if (bookmark is not null)
            {
                bookmark.IsVisible = _isGroupCard && _bookmark;
                bookmark.Fill = brush;
            }
        }

        private void OnBookmarkCheckChanged(object? sender, RoutedEventArgs e)
        {
            _bookmark = (sender as CheckBox)?.IsChecked == true;
            UpdatePreviewVisual();
        }

        private void OnRingCheckChanged(object? sender, RoutedEventArgs e)
        {
            var ringCheck = this.FindControl<CheckBox>("RingCheck");
            _ring = ringCheck?.IsChecked == true;
            var ring = this.FindControl<Border>("PreviewRing");
            if (ring is not null) ring.IsVisible = _ring;
        }

        private void OnTogglePreview(object? sender, RoutedEventArgs e)
        {
            _previewCollapsed = !_previewCollapsed;
            var prev = this.FindControl<Control>("PreviewPanel");
            if (prev is not null) prev.IsVisible = _showPreview && !_previewCollapsed;
        }

        private void OnRingAllClick(object? sender, RoutedEventArgs e)
        {
            var p = this.FindControl<Control>("RingConfirmPanel");
            if (p is not null) p.IsVisible = true;
        }

        private void OnRingConfirmCancel(object? sender, RoutedEventArgs e)
        {
            var p = this.FindControl<Control>("RingConfirmPanel");
            if (p is not null) p.IsVisible = false;
        }

        // Подтверждение: переключает состояние «у всех» (вкл↔выкл) и закрывает редактор.
        private void OnConfirmRingApply(object? sender, RoutedEventArgs e) => CompleteEditor(!_ringsAllState);

        // Резервный обработчик скрытой кнопки (на случай возврата двухкнопочного режима).
        private void OnConfirmRingRemove(object? sender, RoutedEventArgs e) => CompleteEditor(false);

        private void SelectFromHex(string hex)
        {
            try { SetColor(Color.Parse(hex)); }
            catch { }
        }

        // Клик по сохранённому образцу: если это код градиента — грузим его в полосу
        // и включаем режим градиента, иначе ведём себя как обычный выбор цвета.
        private void SelectFromCode(string code)
        {
            var spec = GradientSpec.Parse(code);
            if (!spec.IsSolid)
            {
                _gradientStrip?.Load(spec);
                // Выключенный градиент образец-градиент включает «взаймы»:
                // помечаем, чтобы выбор простого цвета вернул всё как было.
                if (!_gradientEnabled) _gradientAutoEnabled = true;
                SetGradientEnabled(true);
            }
            else if (_gradientAutoEnabled)
            {
                // Градиент включался только автоматически — выбор простого цвета
                // возвращает редактор в одноцветный режим.
                _gradientAutoEnabled = false;
                SetGradientEnabled(false);
            }
            try { SetColor(Color.Parse(spec.SolidHex)); }
            catch { }
            UpdatePreviewVisual();
        }

        // Включение/выключение режима градиента: полоса блокируется, а на сохранение
        // и в превью уходит сплошной цвет.
        private void SetGradientEnabled(bool on)
        {
            _gradientEnabled = on;
            var chk = this.FindControl<CheckBox>("GradientEnableCheck");
            if (chk is not null)
            {
                _settingGrad = true;
                chk.IsChecked = on;
                _settingGrad = false;
            }
            if (_gradientStrip is not null) _gradientStrip.IsEnabled = on;
            UpdatePreviewVisual();
        }

        private void OnGradientEnableChanged(object? sender, RoutedEventArgs e)
        {
            if (_settingGrad) return;
            _gradientEnabled = (sender as CheckBox)?.IsChecked == true;
            // Галка — явное решение пользователя: автопометка снимается, выбор
            // простого цвета режим больше не переключает.
            _gradientAutoEnabled = false;
            if (_gradientStrip is not null) _gradientStrip.IsEnabled = _gradientEnabled;
            UpdatePreviewVisual();
        }

        private void OnStripSpecChanged() => UpdatePreviewVisual();

        // Текущая кисть для превью: градиент из полосы либо сплошной цвет.
        private IBrush CurrentBrush()
        {
            if (_gradientEnabled && _gradientStrip is not null)
                return GradientBrushFactory.ToBrush(_gradientStrip.BuildSpec());
            return new SolidColorBrush(_current);
        }

        private void UpdatePreviewVisual()
        {
            var brush = CurrentBrush();
            var sw = this.FindControl<Border>("PreviewSwatch");
            if (sw is not null) sw.Background = brush;
            UpdateCardPreview(brush);
        }

        // ── Вкладки ───────────────────────────────────────────────────────

        private void OnTabSpectrum(object? sender, RoutedEventArgs e) => SetTab(0);
        private void OnTabHoneycomb(object? sender, RoutedEventArgs e) => SetTab(1);
        private void OnTabWheel(object? sender, RoutedEventArgs e) => SetTab(2);
        private void OnTabValues(object? sender, RoutedEventArgs e) => SetTab(3);
        private void OnTabPalettes(object? sender, RoutedEventArgs e) => SetTab(4);
        private void OnTabNoise(object? sender, RoutedEventArgs e) => SetTab(5);

        // Вкладки коллекций: Мои цвета / Стандартные / Палитры.
        private string _collectionTab = "my";

        private void OnColMy(object? sender, RoutedEventArgs e) => SetCollectionTab("my");
        private void OnColStd(object? sender, RoutedEventArgs e) => SetCollectionTab("std");
        private void OnColPal(object? sender, RoutedEventArgs e) => SetCollectionTab("pal");

        // Откат/повтор истории палитр: переключаемся на вкладку той секции, где
        // изменение произошло (стандартные цвета или палитры), чтобы результат
        // был виден сразу.
        private void OnPaletteHistoryApplied(bool std)
        {
            var tab = std ? "std" : "pal";
            if (_collectionTab != tab) SetCollectionTab(tab);
        }

        private void SetCollectionTab(string tab)
        {
            _collectionTab = tab;
            var my = this.FindControl<StackPanel>("MyColorsSection");
            var pm = this.FindControl<PaletteManagerView>("PalettesPanel");
            if (my is not null) my.IsVisible = tab == "my";
            if (pm is not null)
            {
                pm.IsVisible = tab != "my";
                if (tab == "std") pm.ShowSection("standard");
                else if (tab == "pal") pm.ShowSection("palettes");
            }
            SetTabActive(this.FindControl<Button>("ColMyBtn"), tab == "my");
            SetTabActive(this.FindControl<Button>("ColStdBtn"), tab == "std");
            SetTabActive(this.FindControl<Button>("ColPalBtn"), tab == "pal");
        }

        private static void SetTabActive(Button? b, bool on)
        {
            if (b is null) return;
            if (on) { if (!b.Classes.Contains("active")) b.Classes.Add("active"); }
            else b.Classes.Remove("active");
        }

        // Широко — коллекции (вкладки, Мои цвета, палитры, кольцо) переносим в
        // правую колонку рядом с пикером; узко — возвращаем под пикер в столбик.
        private bool? _isWide;

        private void SetTwoColumn(bool wide)
        {
            var pickerStack = this.FindControl<StackPanel>("PickerStack");
            var rightHost = this.FindControl<Control>("RightHost");
            var rightPinned = this.FindControl<StackPanel>("RightPinned");
            var rightStack = this.FindControl<StackPanel>("RightStack");
            if (pickerStack is null || rightHost is null || rightPinned is null || rightStack is null) return;

            var tabs = this.FindControl<Border>("CollTabs");
            var my = this.FindControl<StackPanel>("MyColorsSection");
            var pm = this.FindControl<PaletteManagerView>("PalettesPanel");
            var ring = this.FindControl<StackPanel>("RingSection");

            // Снимаем контрол с любого текущего родителя и кладём в нужный список.
            void Place(Control? c, Avalonia.Controls.Controls dest)
            {
                if (c is null) return;
                pickerStack.Children.Remove(c);
                rightPinned.Children.Remove(c);
                rightStack.Children.Remove(c);
                if (!dest.Contains(c)) dest.Add(c);
            }

            if (wide)
            {
                // Вкладки закреплены сверху правой колонки, прокручивается только контент.
                Place(tabs, rightPinned.Children);
                Place(my, rightStack.Children);
                Place(pm, rightStack.Children);
                Place(ring, rightStack.Children);
            }
            else
            {
                Place(tabs, pickerStack.Children);
                Place(my, pickerStack.Children);
                Place(pm, pickerStack.Children);
                Place(ring, pickerStack.Children);
            }
            rightHost.IsVisible = wide;
            UpdateActivePalettePlate();
        }

        // Индекс текущей вкладки пикера (Спектр/Соты/Колесо/Значения/Шум). Нужен,
        // чтобы UpdateGradients пересобирал тяжёлые треки-градиенты только для
        // видимой сейчас вкладки, а не для всех сразу на каждое движение мыши.
        private int _activeTab;

        private void SetTab(int index)
        {
            _activeTab = index;

            ShowPanel("SpectrumPanel", index == 0);
            ShowPanel("HoneycombPanel", index == 1);
            ShowPanel("WheelPanel", index == 2);
            ShowPanel("ValuesPanel", index == 3);
            ShowPanel("NoisePanel", index == 5);

            ToggleClass(this.FindControl<Button>("TabSpectrumBtn"), "active", index == 0);
            ToggleClass(this.FindControl<Button>("TabHoneycombBtn"), "active", index == 1);
            ToggleClass(this.FindControl<Button>("TabWheelBtn"), "active", index == 2);
            ToggleClass(this.FindControl<Button>("TabValuesBtn"), "active", index == 3);
            ToggleClass(this.FindControl<Button>("TabNoiseBtn"), "active", index == 5);

            // Шум генерируем лениво — при первом показе вкладки.
            if (index == 5 && !_noiseBuilt) { _noiseBuilt = true; BuildNoise(); }

            // Треки-градиенты пересобираются только для видимой вкладки (см.
            // UpdateGradients) — при переключении досчитываем их для только что
            // показанной, иначе она осталась бы с состоянием от прошлой вкладки.
            UpdateGradients(_current);
        }

        private void ShowPanel(string name, bool visible)
        {
            var c = this.FindControl<Control>(name);
            if (c is not null) c.IsVisible = visible;
        }

        private static void ToggleClass(Button? b, string cls, bool on)
        {
            if (b is null) return;
            if (on) { if (!b.Classes.Contains(cls)) b.Classes.Add(cls); }
            else b.Classes.Remove(cls);
        }

        // ── SV-квадрат ────────────────────────────────────────────────────

        private void OnSvPressed(object? sender, PointerPressedEventArgs e)
        {
            _svDrag = true;
            e.Pointer.Capture(sender as IInputElement);
            UpdateSv(e.GetPosition(sender as Visual));
            e.Handled = true;
        }

        private void OnSvMoved(object? sender, PointerEventArgs e)
        {
            if (_svDrag) UpdateSv(e.GetPosition(sender as Visual));
        }

        private void OnSvReleased(object? sender, PointerReleasedEventArgs e)
        {
            _svDrag = false;
            e.Pointer.Capture(null);
        }

        private void UpdateSv(Point p)
        {
            _s = Math.Clamp(p.X / SvSize, 0, 1);
            _v = Math.Clamp(1 - p.Y / SvSize, 0, 1);
            ApplyHsv();
        }

        // ── Полоса оттенка ────────────────────────────────────────────────

        private void OnHuePressed(object? sender, PointerPressedEventArgs e)
        {
            _hueDrag = true;
            e.Pointer.Capture(sender as IInputElement);
            UpdateHue(e.GetPosition(sender as Visual));
            e.Handled = true;
        }

        private void OnHueMoved(object? sender, PointerEventArgs e)
        {
            if (_hueDrag) UpdateHue(e.GetPosition(sender as Visual));
        }

        private void OnHueReleased(object? sender, PointerReleasedEventArgs e)
        {
            _hueDrag = false;
            e.Pointer.Capture(null);
        }

        private void UpdateHue(Point p)
        {
            _h = Math.Clamp(p.Y / HueLen, 0, 1) * 360;
            ApplyHsv();
        }

        // ── Цветовое колесо ───────────────────────────────────────────────

        private void BuildWheel()
        {
            if (_wheelBuilt) return;
            var img = this.FindControl<Image>("WheelImage");
            if (img is null) return;

            int size = (int)WheelSize;
            double r = WheelRadius;
            var wb = new WriteableBitmap(
                new PixelSize(size, size), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Unpremul);

            using (var fb = wb.Lock())
            {
                int stride = fb.RowBytes;
                var row = new byte[stride];
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        double dx = x + 0.5 - r;
                        double dy = y + 0.5 - r;
                        double dist = Math.Sqrt(dx * dx + dy * dy);
                        int o = x * 4;
                        if (dist <= r)
                        {
                            double ang = Math.Atan2(dx, -dy) * 180.0 / Math.PI;
                            if (ang < 0) ang += 360;
                            var col = HsvToRgb(ang, dist / r, 1);
                            byte a = 255;
                            if (dist > r - 1) a = (byte)Math.Clamp((r - dist) * 255.0, 0, 255);
                            row[o] = col.B; row[o + 1] = col.G; row[o + 2] = col.R; row[o + 3] = a;
                        }
                        else
                        {
                            row[o] = 0; row[o + 1] = 0; row[o + 2] = 0; row[o + 3] = 0;
                        }
                    }
                    Marshal.Copy(row, 0, IntPtr.Add(fb.Address, y * stride), stride);
                }
            }

            img.Source = wb;
            _wheelBuilt = true;
        }

        private void OnWheelPressed(object? sender, PointerPressedEventArgs e)
        {
            _wheelDrag = true;
            e.Pointer.Capture(sender as IInputElement);
            UpdateWheel(e.GetPosition(sender as Visual));
            e.Handled = true;
        }

        private void OnWheelMoved(object? sender, PointerEventArgs e)
        {
            if (_wheelDrag) UpdateWheel(e.GetPosition(sender as Visual));
        }

        private void OnWheelReleased(object? sender, PointerReleasedEventArgs e)
        {
            _wheelDrag = false;
            e.Pointer.Capture(null);
        }

        private void UpdateWheel(Point p)
        {
            double dx = p.X - WheelRadius;
            double dy = p.Y - WheelRadius;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            double ang = Math.Atan2(dx, -dy) * 180.0 / Math.PI;
            if (ang < 0) ang += 360;
            _h = ang;
            _s = Math.Clamp(dist / WheelRadius, 0, 1);
            ApplyHsv();
        }

        private void OnWheelValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (_syncing) return;
            _v = Math.Clamp(e.NewValue / 100.0, 0, 1);
            ApplyHsv();
        }

        // ── Шум ───────────────────────────────────────────────────────────

        // Генерирует поле случайных цветов выбранного набора и сбрасывает камеру.
        private void BuildNoise()
        {
            int n = NoiseRes;
            _noiseR = new double[n * n];
            _noiseG = new double[n * n];
            _noiseB = new double[n * n];
            _noiseBmp = new WriteableBitmap(
                new PixelSize(n, n), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Unpremul);

            IReadOnlyList<string>? pal = _noisePreset == "palette" ? ActivePaletteColors() : null;

            using (var fb = _noiseBmp.Lock())
            {
                int stride = fb.RowBytes;
                var row = new byte[stride];
                for (int y = 0; y < n; y++)
                {
                    for (int x = 0; x < n; x++)
                    {
                        var col = NoiseColor(pal);
                        int idx = y * n + x;
                        _noiseR[idx] = col.R; _noiseG[idx] = col.G; _noiseB[idx] = col.B;
                        int o = x * 4;
                        row[o] = col.B; row[o + 1] = col.G; row[o + 2] = col.R; row[o + 3] = 255;
                    }
                    Marshal.Copy(row, 0, IntPtr.Add(fb.Address, y * stride), stride);
                }
            }

            var img = this.FindControl<Image>("NoiseImage");
            if (img is not null)
            {
                img.Source = _noiseBmp;
                if (img.RenderTransform is TransformGroup g)
                {
                    foreach (var ch in g.Children)
                    {
                        if (ch is ScaleTransform st) _noiseScaleT = st;
                        else if (ch is TranslateTransform tt) _noiseTransT = tt;
                    }
                }
            }
            _noiseSolid ??= this.FindControl<Border>("NoiseSolid");
            ResetNoiseView();
        }

        // Цвет одного пикселя по выбранному набору.
        private Color NoiseColor(IReadOnlyList<string>? pal)
        {
            double R() => _noiseRng.NextDouble();
            switch (_noisePreset)
            {
                case "skin":
                    return HsvToRgb(18 + R() * 26, 0.25 + R() * 0.40, 0.55 + R() * 0.40);
                case "pastel":
                    return HsvToRgb(R() * 360, 0.18 + R() * 0.27, 0.85 + R() * 0.15);
                case "gray":
                {
                    byte v = (byte)_noiseRng.Next(0, 256);
                    return Color.FromRgb(v, v, v);
                }
                case "neon":
                    return HsvToRgb(R() * 360, 0.90 + R() * 0.10, 0.95 + R() * 0.05);
                case "palette":
                    if (pal is { Count: > 0 })
                    {
                        try { return Color.Parse(pal[_noiseRng.Next(pal.Count)]); }
                        catch { }
                    }
                    return HsvToRgb(R() * 360, 0.6 + R() * 0.4, 0.7 + R() * 0.3);
                default: // rainbow
                    return HsvToRgb(R() * 360, 0.6 + R() * 0.4, 0.7 + R() * 0.3);
            }
        }

        private IReadOnlyList<string>? ActivePaletteColors() =>
            this.FindControl<PaletteManagerView>("PalettesPanel")?.ActivePaletteColors;

        private void OnNoisePresetChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cb && cb.SelectedItem is ComboBoxItem it && it.Tag is string key)
            {
                _noisePreset = key;
                if (_noiseBuilt) BuildNoise();   // перегенерировать уже показанное поле
            }
        }

        private void OnNoiseRegen(object? sender, RoutedEventArgs e)
        {
            if (_noiseBuilt) BuildNoise();
        }

        // Возврат камеры к исходному виду (плавно).
        private void OnNoiseReset(object? sender, RoutedEventArgs e)
        {
            _nHasPending = false;
            if (_noiseSolid is not null) _noiseSolid.Opacity = 0;
            _nScaleStart = _nScale; _nTxStart = _nTx; _nTyStart = _nTy;
            _nScaleTarget = 1; _nTxTarget = 0; _nTyTarget = 0;
            StartNoiseAnim(300);
        }

        // Клик по полю: запоминаем цвет точки и за пару секунд приближаемся к ней
        // «до конца» — пока этот цвет не заполнит весь квадрат. Сам цвет выдаётся
        // только в конце наезда, чтобы не было спойлера.
        private void OnNoisePressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Visual v) return;
            var p = e.GetPosition(v);

            // Точка в локальных координатах изображения (до трансформации).
            double localX = (p.X - _nTx) / _nScale;
            double localY = (p.Y - _nTy) / _nScale;

            _nPending = SampleNoiseAt(localX, localY);
            _nHasPending = true;

            // Готовим слой однотонной заливки (проявится к концу наезда).
            if (_noiseSolid is not null)
            {
                _noiseSolid.Background = new SolidColorBrush(_nPending);
                _noiseSolid.Opacity = 0;
            }

            double s2 = NoiseRes * 2.2;                 // приближаемся глубоко — почти один пиксель
            double imgSize = NoiseView * s2;
            _nScaleStart = _nScale; _nTxStart = _nTx; _nTyStart = _nTy;
            _nScaleTarget = s2;
            _nTxTarget = Math.Clamp(NoiseView / 2 - localX * s2, NoiseView - imgSize, 0);
            _nTyTarget = Math.Clamp(NoiseView / 2 - localY * s2, NoiseView - imgSize, 0);
            StartNoiseAnim(2000);                        // ~2 секунды плавного наезда
            e.Handled = true;
        }

        // Билинейная выборка цвета поля в точке (в локальных координатах изображения).
        private Color SampleNoiseAt(double localX, double localY)
        {
            if (_noiseR is null || _noiseG is null || _noiseB is null) return _current;

            double bx = Math.Clamp(localX / NoiseView * NoiseRes - 0.5, 0, NoiseRes - 1.0001);
            double by = Math.Clamp(localY / NoiseView * NoiseRes - 0.5, 0, NoiseRes - 1.0001);
            int x0 = (int)Math.Floor(bx), y0 = (int)Math.Floor(by);
            int x1 = Math.Min(x0 + 1, NoiseRes - 1), y1 = Math.Min(y0 + 1, NoiseRes - 1);
            double fx = bx - x0, fy = by - y0;

            double Sample(double[] ch)
            {
                double top = ch[y0 * NoiseRes + x0] * (1 - fx) + ch[y0 * NoiseRes + x1] * fx;
                double bot = ch[y1 * NoiseRes + x0] * (1 - fx) + ch[y1 * NoiseRes + x1] * fx;
                return top * (1 - fy) + bot * fy;
            }

            byte r = (byte)Math.Clamp(Sample(_noiseR), 0, 255);
            byte g = (byte)Math.Clamp(Sample(_noiseG), 0, 255);
            byte b = (byte)Math.Clamp(Sample(_noiseB), 0, 255);
            return Color.FromRgb(r, g, b);
        }

        private void StartNoiseAnim(double durationMs)
        {
            _nAnimT = 0;
            _nAnimStep = durationMs <= 0 ? 1 : 16.0 / durationMs;
            if (_noiseTimer is null)
            {
                _noiseTimer = new Avalonia.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(16)
                };
                _noiseTimer.Tick += OnNoiseTick;
            }
            _noiseTimer.Start();
        }

        private void OnNoiseTick(object? sender, EventArgs e)
        {
            _nAnimT += _nAnimStep;
            // ease-in-out cubic — мягкий старт и мягкая остановка.
            double x = _nAnimT >= 1 ? 1 : _nAnimT;
            double t = x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;
            _nScale = _nScaleStart + (_nScaleTarget - _nScaleStart) * t;
            _nTx = _nTxStart + (_nTxTarget - _nTxStart) * t;
            _nTy = _nTyStart + (_nTyTarget - _nTyStart) * t;
            ApplyNoiseTransform();

            // К концу наезда плавно проявляем сплошную заливку — поле становится однотонным.
            if (_noiseSolid is not null)
                _noiseSolid.Opacity = _nHasPending ? Math.Clamp((t - 0.6) / 0.4, 0, 1) : 0;

            if (_nAnimT >= 1)
            {
                _noiseTimer?.Stop();
                if (_nHasPending) { _nHasPending = false; SetColor(_nPending); }
            }
        }

        private void ResetNoiseView()
        {
            _noiseTimer?.Stop();
            _nHasPending = false;
            if (_noiseSolid is not null) _noiseSolid.Opacity = 0;
            _nScale = _nScaleTarget = _nScaleStart = 1;
            _nTx = _nTxTarget = _nTxStart = 0;
            _nTy = _nTyTarget = _nTyStart = 0;
            ApplyNoiseTransform();
        }

        private void ApplyNoiseTransform()
        {
            if (_noiseScaleT is not null) { _noiseScaleT.ScaleX = _nScale; _noiseScaleT.ScaleY = _nScale; }
            if (_noiseTransT is not null) { _noiseTransT.X = _nTx; _noiseTransT.Y = _nTy; }
        }

        // ── Соты ──────────────────────────────────────────────────────────

        private void BuildHoneycomb()
        {
            if (_honeycombBuilt) return;
            var canvas = this.FindControl<Canvas>("HoneycombCanvas");
            if (canvas is null) return;
            canvas.Children.Clear();
            _honeyCells.Clear();
            _honeySelected = null;

            const double r = 11;
            const int cols = 12;
            const int hueRows = 7;
            double w = Math.Sqrt(3) * r;
            double rowH = 1.5 * r;

            for (int rowi = 0; rowi < hueRows; rowi++)
            {
                double l = 0.82 - rowi * (0.62 / (hueRows - 1));
                for (int col = 0; col < cols; col++)
                {
                    double hue = col * (360.0 / cols);
                    AddHex(canvas, rowi, col, r, HslToRgb(hue, 0.82, l));
                }
            }
            for (int col = 0; col < cols; col++)
            {
                double g = 1.0 - col / (double)(cols - 1);
                byte v = (byte)Math.Round(g * 255);
                AddHex(canvas, hueRows, col, r, Color.FromRgb(v, v, v));
            }

            int totalRows = hueRows + 1;
            canvas.Width = cols * w + w / 2 + 4;
            canvas.Height = totalRows * rowH + r + 4;
            _honeycombBuilt = true;
        }

        private void AddHex(Canvas canvas, int row, int col, double r, Color color)
        {
            double w = Math.Sqrt(3) * r;
            double rowH = 1.5 * r;
            double offset = (row % 2 == 1) ? w / 2 : 0;
            double cx = col * w + w / 2 + offset + 2;
            double cy = row * rowH + r + 2;

            var poly = new Polygon
            {
                Points = HexPoints(cx, cy, r),
                Fill = new SolidColorBrush(color),
                Stroke = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)),
                StrokeThickness = 1,
                Tag = $"#{color.R:X2}{color.G:X2}{color.B:X2}",
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            poly.PointerPressed += OnHoneycombCellPressed;
            canvas.Children.Add(poly);
            _honeyCells.Add(poly);
        }

        // Подсвечивает ячейку сот, чей цвет совпадает с текущим (контур), остальные сбрасывает.
        private void HighlightHoneycomb(string hex)
        {
            Polygon? match = null;
            foreach (var p in _honeyCells)
                if (p.Tag is string t && string.Equals(t, hex, StringComparison.OrdinalIgnoreCase))
                {
                    match = p;
                    break;
                }

            if (ReferenceEquals(match, _honeySelected)) return;

            if (_honeySelected is not null)
            {
                _honeySelected.Stroke = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0));
                _honeySelected.StrokeThickness = 1;
                _honeySelected.ZIndex = 0;
            }
            if (match is not null)
            {
                match.Stroke = Brushes.White;
                match.StrokeThickness = 3;
                match.ZIndex = 5;
            }
            _honeySelected = match;
        }

        private static IList<Point> HexPoints(double cx, double cy, double r)
        {
            double hw = Math.Sqrt(3) / 2 * r;
            return new List<Point>
            {
                new Point(cx, cy - r),
                new Point(cx + hw, cy - r / 2),
                new Point(cx + hw, cy + r / 2),
                new Point(cx, cy + r),
                new Point(cx - hw, cy + r / 2),
                new Point(cx - hw, cy - r / 2),
            };
        }

        private void OnHoneycombCellPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is Polygon p && p.Tag is string hex) SelectFromHex(hex);
            e.Handled = true;
        }

        // ── Пользовательская палитра (drag-reorder + добавление/удаление) ──

        private void LoadPalette()
        {
            Palette.Clear();
            var proj = CurrentProject;
            if (proj is null) return;
            foreach (var c in proj.ProjectPinnedColors) Palette.Add(Normalize(c));
        }

        private void PersistPalette()
        {
            var proj = CurrentProject;
            if (proj is null) return;
            proj.ProjectPinnedColors.Clear();
            foreach (var c in Palette) proj.ProjectPinnedColors.Add(Normalize(c));
        }

        private int IndexOfPalette(string hex)
        {
            var n = Normalize(hex);
            for (int i = 0; i < Palette.Count; i++)
                if (Normalize(Palette[i]) == n) return i;
            return -1;
        }

        private void RemovePaletteColor(string hex)
        {
            var i = IndexOfPalette(hex);
            if (i >= 0) { Palette.RemoveAt(i); PersistPalette(); _paletteDirty = true; }
        }

        // Крестик на свотче «Моих цветов» — удалить именно этот цвет.
        private void OnRemoveMyColor(object? sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.DataContext is string hex)
                RemovePaletteColor(hex);
            e.Handled = true;
        }

        // Текущий выбор как код: градиент (grad|...) при включённом режиме, иначе hex.
        private string CurrentCode()
            => (_gradientEnabled ? _gradientStrip?.BuildSpec().ToCode() : null)
               ?? $"#{_current.R:X2}{_current.G:X2}{_current.B:X2}";

        private void OnAddCurrentClick(object? sender, RoutedEventArgs e)
        {
            var code = CurrentCode();
            if (IndexOfPalette(code) < 0)
            {
                Palette.Add(Normalize(code));
                PersistPalette();
                _paletteDirty = true;
            }
        }

        private void OnPalettePressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Border b || b.DataContext is not string hex) return;

            var props = e.GetCurrentPoint(b).Properties;
            if (props.IsRightButtonPressed)
            {
                RemovePaletteColor(hex);
                e.Handled = true;
                return;
            }

            _palettePressed = true;
            _paletteDragging = false;
            _paletteDragHex = hex;
            _paletteDragIndex = IndexOfPalette(hex);
            _swTarget = _paletteDragIndex;
            _swElem = b;
            _swPointer = e.Pointer;
            _swList = (b as Visual)?.FindAncestorOfType<ItemsControl>();
            _swCell = _swList?.ContainerFromIndex(_paletteDragIndex);

            // Удержание 80 мс -> старт драга; быстрый клик остаётся выбором цвета.
            _swHoldTimer?.Stop();
            _swHoldTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _swHoldTimer.Tick += OnPaletteHoldTick;
            _swHoldTimer.Start();
            e.Handled = true;
        }

        private void OnPaletteHoldTick(object? sender, EventArgs e)
        {
            _swHoldTimer?.Stop();
            if (!_palettePressed || _paletteDragging || _swElem is null) return;
            _paletteDragging = true;
            _swPointer?.Capture(_swElem);
            if (_swCell is not null) _swCell.ZIndex = 100;
            _swElem.Opacity = 0.9;
            if (_swList is not null) _swList.IsHitTestVisible = false;
            if (_swElem.RenderTransform is TranslateTransform tt)
            {
                _swSavedTransitions = tt.Transitions;
                tt.Transitions = null;
            }
            _swTarget = _paletteDragIndex;

            if (_swList is not null)
            {
                var cont = _swList.ContainerFromIndex(_paletteDragIndex);
                if (cont is not null) { _swCellW = cont.Bounds.Width; _swCellH = cont.Bounds.Height; }
                _swColumns = PaletteColumns(_swList);
            }
        }

        private static int PaletteColumns(ItemsControl items)
        {
            double minY = double.MaxValue;
            foreach (var c in items.GetRealizedContainers())
                if (c.Bounds.Y < minY) minY = c.Bounds.Y;
            int cols = 0;
            foreach (var c in items.GetRealizedContainers())
                if (Math.Abs(c.Bounds.Y - minY) < 1.0) cols++;
            return Math.Max(1, cols);
        }

        private void OnPaletteMoved(object? sender, PointerEventArgs e)
        {
            if (!_palettePressed || !_paletteDragging || _swElem is null) return;
            var items = (sender as Visual)?.FindAncestorOfType<ItemsControl>();
            if (items is null) return;
            ApplyPaletteDrag(items, e.GetPosition(items));
        }

        // Призрак держится под курсором (X и Y); цель — ближайшая ячейка; соседи
        // разъезжаются с учётом колонок (перенос на строки работает).
        private void ApplyPaletteDrag(ItemsControl items, Point pointer)
        {
            if (_swElem is null) return;

            var dragCont = items.ContainerFromIndex(_paletteDragIndex) as Visual;
            if (dragCont is not null && _swElem.RenderTransform is TranslateTransform tt)
            {
                var tl = dragCont.TranslatePoint(new Point(0, 0), items) ?? new Point();
                tt.X = pointer.X - (tl.X + dragCont.Bounds.Width / 2.0);
                tt.Y = pointer.Y - (tl.Y + dragCont.Bounds.Height / 2.0);
            }

            int target = NearestPaletteCell(items, pointer);
            if (target < 0) return;
            if (target != _swTarget)
            {
                _swTarget = target;
                ShiftPaletteCells(items);
            }
        }

        private static int NearestPaletteCell(ItemsControl items, Point p)
        {
            int best = -1;
            double bestD = double.MaxValue;
            foreach (var cont in items.GetRealizedContainers())
            {
                var tl = (cont as Visual)?.TranslatePoint(new Point(0, 0), items) ?? new Point();
                double cx = tl.X + cont.Bounds.Width / 2.0;
                double cy = tl.Y + cont.Bounds.Height / 2.0;
                double dx = cx - p.X, dy = cy - p.Y;
                double d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; best = items.IndexFromContainer(cont); }
            }
            return best;
        }

        private void ShiftPaletteCells(ItemsControl items)
        {
            int cols = Math.Max(1, _swColumns);
            foreach (var cont in items.GetRealizedContainers())
            {
                var sw = PaletteSwatchOf(cont);
                if (sw is null || ReferenceEquals(sw, _swElem)) continue;
                int i = items.IndexFromContainer(cont);
                int ni = i;
                if (_swTarget > _paletteDragIndex && i > _paletteDragIndex && i <= _swTarget) ni = i - 1;
                else if (_swTarget < _paletteDragIndex && i >= _swTarget && i < _paletteDragIndex) ni = i + 1;

                double tx = 0, ty = 0;
                if (ni != i)
                {
                    tx = (ni % cols - i % cols) * _swCellW;
                    ty = (ni / cols - i / cols) * _swCellH;
                }
                if (sw.RenderTransform is TranslateTransform tt) { tt.X = tx; tt.Y = ty; }
            }
        }

        private static Border? PaletteSwatchOf(Control cont)
        {
            var child = (cont as ContentPresenter)?.Child ?? cont;
            if (child is Border bd) return bd;
            if (child is Panel p)
                foreach (var ch in p.Children)
                    if (ch is Border b) return b;
            return null;
        }

        private void OnPaletteReleased(object? sender, PointerReleasedEventArgs e)
        {
            _swHoldTimer?.Stop();
            if (!_palettePressed) return;
            _palettePressed = false;

            if (_paletteDragging)
            {
                _swPointer?.Capture(null);
                var items = (sender as Visual)?.FindAncestorOfType<ItemsControl>();
                int target = _swTarget;

                // Снимаем смещения без анимации (иначе соседи дёргаются на фиксации),
                // переходы вернём на следующий тик.
                var restore = new List<(TranslateTransform t, Avalonia.Animation.Transitions? saved)>();
                if (items is not null)
                    foreach (var cont in items.GetRealizedContainers())
                    {
                        var sw = PaletteSwatchOf(cont);
                        if (sw?.RenderTransform is TranslateTransform t)
                        {
                            var saved = ReferenceEquals(sw, _swElem) ? _swSavedTransitions : t.Transitions;
                            t.Transitions = null;
                            t.X = 0;
                            t.Y = 0;
                            restore.Add((t, saved));
                        }
                    }

                if (_swElem is not null) _swElem.Opacity = 1;
                if (_swCell is not null) _swCell.ZIndex = 0;

                if (target >= 0 && _paletteDragIndex >= 0 && target != _paletteDragIndex)
                {
                    Palette.Move(_paletteDragIndex, target);
                    PersistPalette();
                    _paletteDirty = true;
                }
                else if (_paletteDragHex is string held)
                {
                    // Зажал дольше порога, но не перенёс (слот не сменился) — это
                    // клик: выбираем цвет, иначе чуть затянутое нажатие по свотчу
                    // «не прожималось».
                    SelectFromCode(held);
                }

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    foreach (var (t, saved) in restore) t.Transitions = saved;
                });
            }
            else if (_paletteDragHex is string h)
            {
                SelectFromCode(h);   // быстрый клик — выбрать цвет или применить градиент
            }

            if (_swList is not null) _swList.IsHitTestVisible = true;

            _paletteDragging = false;
            _paletteDragIndex = -1;
            _paletteDragHex = null;
            _swTarget = -1;
            _swElem = null;
            _swCell = null;
            _swList = null;
            _swPointer = null;
        }

        // Закреплённая плашка активной палитры: «+» добавляет текущий цвет без прокрутки.
        private void OnPlateAdd(object? sender, RoutedEventArgs e)
            => this.FindControl<PaletteManagerView>("PalettesPanel")?.AddCurrentColor();

        private void UpdateActivePalettePlate()
        {
            var pm = this.FindControl<PaletteManagerView>("PalettesPanel");
            var plate = this.FindControl<Border>("ActivePalettePlate");
            var name = this.FindControl<TextBlock>("PlateName");
            if (pm is null || plate is null) return;
            // В широком режиме палитры и так справа — плашка не нужна.
            plate.IsVisible = pm.HasActivePalette && _isWide != true;
            if (name is not null)
                name.Text = string.IsNullOrWhiteSpace(pm.ActivePaletteName) ? "—" : pm.ActivePaletteName;
        }

        // ── RGB / HEX / HSL / HSV (ручной ввод цифрами) ───────────────────

        private void OnRgbSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (_syncing) return;
            var sr = this.FindControl<Slider>("SliderR");
            var sg = this.FindControl<Slider>("SliderG");
            var sb = this.FindControl<Slider>("SliderB");
            SetColor(WithAlpha(Color.FromRgb(
                (byte)(sr?.Value ?? 0),
                (byte)(sg?.Value ?? 0),
                (byte)(sb?.Value ?? 0))));
        }

        // Ползунок альфы: меняет только канал прозрачности текущего цвета. Общий
        // обработчик для альфа-ползунков всех вкладок (Спектр, Соты, Колесо,
        // Значения, Шум) — читает значение из события, а не по имени контрола.
        private void OnAlphaSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (_syncing) return;
            _alpha = (byte)Math.Clamp(e.NewValue, 0, 255);
            ApplyHsv();
        }

        // ── Градиентные ползунки вкладки «Значения» ───────────────────────

        private void OnValRgbSlider(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (_syncing) return;
            SetColor(WithAlpha(Color.FromRgb(
                (byte)SliderVal("SlR"), (byte)SliderVal("SlG"), (byte)SliderVal("SlB"))));
        }

        private void OnValHslSlider(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (_syncing) return;
            SetColor(WithAlpha(HslToRgb(
                SliderVal("SlHslH"), SliderVal("SlHslS") / 100.0, SliderVal("SlHslL") / 100.0)));
        }

        private void OnValHsvSlider(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (_syncing) return;
            _h = SliderVal("SlHsvH");
            _s = Math.Clamp(SliderVal("SlHsvS") / 100.0, 0, 1);
            _v = Math.Clamp(SliderVal("SlHsvV") / 100.0, 0, 1);
            ApplyHsv();
        }

        private double SliderVal(string name) => this.FindControl<Slider>(name)?.Value ?? 0;

        private Slider? _gradDrag;

        // Нажатие по дорожке: значение моментально переходит под курсор, указатель
        // захватывается и протяжка идёт тем же движением. Нажатие по самому ползунку
        // тоже захватывается — поведение единое из любой точки.
        private void OnGradSliderPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Slider s) return;
            if (!e.GetCurrentPoint(s).Properties.IsLeftButtonPressed) return;
            _gradDrag = s;
            e.Pointer.Capture(s);
            SetGradValueFromPointer(s, e.GetPosition(s));
            e.Handled = true;
        }

        private void OnGradSliderMoved(object? sender, PointerEventArgs e)
        {
            if (_gradDrag is null || !ReferenceEquals(sender, _gradDrag)) return;
            SetGradValueFromPointer(_gradDrag, e.GetPosition(_gradDrag));
            e.Handled = true;
        }

        private void OnGradSliderReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_gradDrag is null) return;
            _gradDrag = null;
            e.Pointer.Capture(null);
            e.Handled = true;
        }

        private static void SetGradValueFromPointer(Slider s, Point p)
        {
            double w = s.Bounds.Width;
            if (w <= 0) return;
            const double thumb = 16;
            double frac = Math.Clamp((p.X - thumb / 2) / Math.Max(1, w - thumb), 0, 1);
            s.Value = s.Minimum + frac * (s.Maximum - s.Minimum);
        }

        private void SetSliderVal(string name, double v)
        {
            var s = this.FindControl<Slider>(name);
            if (s is not null) s.Value = v;
        }

        private void SetSliderBg(string name, IBrush b)
        {
            var s = this.FindControl<Slider>(name);
            if (s is not null) s.Background = b;
        }

        // Обновляет значения и градиенты-треки всех градиентных ползунков под текущий цвет.
        //
        // Значения (числа) ставятся всегда — они дешёвые. А вот пересборка
        // треков-градиентов (аллокация LinearGradientBrush + перерисовка) —
        // самая тяжёлая часть перерисовки при перетаскивании, и она нужна
        // только той вкладке, которую сейчас видно. Скрытым вкладкам это
        // не нужно вообще: при перетаскивании, например, SV-квадрата на
        // «Спектре» треки скрытой вкладки «Значения» больше не пересобираются
        // на каждый пиксель. При переключении вкладки (SetTab) UpdateGradients
        // зовётся заново — показанная вкладка сразу получает актуальные треки.
        private void UpdateGradients(Color c)
        {
            var (lh, ls, ll) = RgbToHsl(c);

            SetSliderVal("SlR", c.R); SetSliderVal("SlG", c.G); SetSliderVal("SlB", c.B); SetSliderVal("SlA", c.A);
            SetSliderVal("SlHslH", lh); SetSliderVal("SlHslS", ls * 100); SetSliderVal("SlHslL", ll * 100);
            SetSliderVal("SlHsvH", _h); SetSliderVal("SlHsvS", _s * 100); SetSliderVal("SlHsvV", _v * 100);

            // Трек альфы: от полностью прозрачного к непрозрачному текущему RGB.
            // Общий для вкладок с альфа-ползунком — ниже красит только свою.
            var aGrad = Grad(Color.FromArgb(0, c.R, c.G, c.B), Color.FromArgb(255, c.R, c.G, c.B));

            switch (_activeTab)
            {
                case 0: // Спектр
                {
                    var rGrad = Grad(Color.FromRgb(0, c.G, c.B), Color.FromRgb(255, c.G, c.B));
                    var gGrad = Grad(Color.FromRgb(c.R, 0, c.B), Color.FromRgb(c.R, 255, c.B));
                    var bGrad = Grad(Color.FromRgb(c.R, c.G, 0), Color.FromRgb(c.R, c.G, 255));
                    SetSliderBg("SliderR", rGrad); SetSliderBg("SliderG", gGrad); SetSliderBg("SliderB", bGrad);
                    SetSliderBg("SliderA", aGrad);
                    break;
                }
                case 1: // Соты
                    SetSliderBg("SliderAHoney", aGrad);
                    break;
                case 2: // Колесо — альфа там простой вертикальный ползунок без трека-градиента.
                    break;
                case 3: // Значения
                {
                    var rGrad = Grad(Color.FromRgb(0, c.G, c.B), Color.FromRgb(255, c.G, c.B));
                    var gGrad = Grad(Color.FromRgb(c.R, 0, c.B), Color.FromRgb(c.R, 255, c.B));
                    var bGrad = Grad(Color.FromRgb(c.R, c.G, 0), Color.FromRgb(c.R, c.G, 255));
                    SetSliderBg("SlR", rGrad); SetSliderBg("SlG", gGrad); SetSliderBg("SlB", bGrad);
                    SetSliderBg("SlA", aGrad);

                    SetSliderBg("SlHslH", HRainbow());
                    SetSliderBg("SlHslS", Grad(HslToRgb(lh, 0, ll), HslToRgb(lh, 1, ll)));
                    SetSliderBg("SlHslL", Grad(HslToRgb(lh, ls, 0), HslToRgb(lh, ls, 0.5), HslToRgb(lh, ls, 1)));

                    SetSliderBg("SlHsvH", HRainbow());
                    SetSliderBg("SlHsvS", Grad(HsvToRgb(_h, 0, _v), HsvToRgb(_h, 1, _v)));
                    SetSliderBg("SlHsvV", Grad(HsvToRgb(_h, _s, 0), HsvToRgb(_h, _s, 1)));
                    break;
                }
                case 5: // Шум
                    SetSliderBg("SliderANoise", aGrad);
                    break;
            }
        }

        private static LinearGradientBrush Clone(LinearGradientBrush src)
        {
            var b = new LinearGradientBrush { StartPoint = src.StartPoint, EndPoint = src.EndPoint };
            foreach (var s in src.GradientStops) b.GradientStops.Add(new GradientStop(s.Color, s.Offset));
            return b;
        }

        private static LinearGradientBrush Grad(params Color[] stops)
        {
            var b = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative)
            };
            if (stops.Length == 1)
                b.GradientStops.Add(new GradientStop(stops[0], 0));
            else
                for (int i = 0; i < stops.Length; i++)
                    b.GradientStops.Add(new GradientStop(stops[i], (double)i / (stops.Length - 1)));
            return b;
        }

        private static LinearGradientBrush HRainbow()
        {
            var b = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative)
            };
            double[] hs = { 0, 60, 120, 180, 240, 300, 360 };
            for (int i = 0; i < hs.Length; i++)
                b.GradientStops.Add(new GradientStop(HsvToRgb(hs[i], 1, 1), (double)i / (hs.Length - 1)));
            return b;
        }

        private void OnRgbKey(object? sender, KeyEventArgs e) { if (e.Key == Key.Enter) CommitRgb(); }
        private void OnRgbCommit(object? sender, RoutedEventArgs e) => CommitRgb();

        private void CommitRgb()
        {
            if (_syncing) return;
            int r = ReadInt("TxtR", 0, 255);
            int g = ReadInt("TxtG", 0, 255);
            int b = ReadInt("TxtB", 0, 255);
            SetColor(WithAlpha(Color.FromRgb((byte)r, (byte)g, (byte)b)));
        }

        private void OnHslKey(object? sender, KeyEventArgs e) { if (e.Key == Key.Enter) CommitHsl(); }
        private void OnHslCommit(object? sender, RoutedEventArgs e) => CommitHsl();

        private void CommitHsl()
        {
            if (_syncing) return;
            double h = ReadInt("TxtHslH", 0, 360);
            double s = ReadInt("TxtHslS", 0, 100) / 100.0;
            double l = ReadInt("TxtHslL", 0, 100) / 100.0;
            SetColor(WithAlpha(HslToRgb(h, s, l)));
        }

        private void OnHsvKey(object? sender, KeyEventArgs e) { if (e.Key == Key.Enter) CommitHsv(); }
        private void OnHsvCommit(object? sender, RoutedEventArgs e) => CommitHsv();

        private void CommitHsv()
        {
            if (_syncing) return;
            _h = ReadInt("TxtHsvH", 0, 360);
            _s = ReadInt("TxtHsvS", 0, 100) / 100.0;
            _v = ReadInt("TxtHsvV", 0, 100) / 100.0;
            ApplyHsv();
        }

        // Числовое поле альфы вкладки «Значения» (TxtA рядом с SlA).
        private void OnAlphaKey(object? sender, KeyEventArgs e) { if (e.Key == Key.Enter) CommitAlpha(); }
        private void OnAlphaCommit(object? sender, RoutedEventArgs e) => CommitAlpha();

        private void CommitAlpha()
        {
            if (_syncing) return;
            _alpha = (byte)ReadInt("TxtA", 0, 255);
            ApplyHsv();
        }

        private int ReadInt(string name, int min, int max)
        {
            var t = this.FindControl<TextBox>(name);
            var s = (t?.Text ?? string.Empty).Trim();
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                return Math.Clamp(v, min, max);
            return min;
        }

        private void OnHexCommit(object? sender, RoutedEventArgs e) => CommitHex();

        private void OnHexKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) CommitHex();
        }

        private void CommitHex()
        {
            if (_syncing) return;
            var hb = this.FindControl<TextBox>("HexBox");
            if (hb?.Text is not string t || string.IsNullOrWhiteSpace(t)) return;
            try { SetColor(Color.Parse(t)); }
            catch { }
        }

        // ── Пипетка ───────────────────────────────────────────────────────

        private async void OnEyedropperClick(object? sender, RoutedEventArgs e)
        {
            if (!_eyedropper.IsSupported) return;

            // Прячем оверлей, чтобы он не попал в снимок экрана, и даём кадр на перерисовку.
            var owner = TopLevel.GetTopLevel(this);
            IsVisible = false;
            await Task.Delay(60);
            Color? picked = null;
            try { picked = await _eyedropper.PickAsync(owner); }
            catch { picked = null; }
            IsVisible = true;

            if (picked is not null) SetColor(picked.Value);
        }

        // ── Кнопки ────────────────────────────────────────────────────────

        private void OnOkClick(object? sender, RoutedEventArgs e) => CompleteEditor(null);

        private void OnCancelClick(object? sender, RoutedEventArgs e) => CompleteCancel();

        // Крестик = закрыть и применить выбранный цвет (удобнее: выбрал и закрыл).
        // Явный отказ — только через кнопку «Отмена».
        private void OnCloseClick(object? sender, RoutedEventArgs e) => CompleteEditor(null);

        // ── Преобразования цвета и доступ к проекту ───────────────────────

        private static ProjectFile? CurrentProject =>
            CoreServices.GetService<ITabCollection>()?.ActiveTab?.Context?.Project;

        private static string Normalize(string? hex) => (hex ?? string.Empty).Trim().ToUpperInvariant();

        private static (double h, double s, double v) RgbToHsv(Color c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double d = max - min;

            double h = 0;
            if (d > 1e-6)
            {
                if (max == r) h = 60 * (((g - b) / d) % 6);
                else if (max == g) h = 60 * (((b - r) / d) + 2);
                else h = 60 * (((r - g) / d) + 4);
            }
            if (h < 0) h += 360;

            double s = max <= 1e-6 ? 0 : d / max;
            double v = max;
            return (h, s, v);
        }

        private static (double h, double s, double l) RgbToHsl(Color c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double d = max - min;
            double l = (max + min) / 2;

            double h = 0, s = 0;
            if (d > 1e-6)
            {
                s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
                if (max == r) h = 60 * (((g - b) / d) % 6);
                else if (max == g) h = 60 * (((b - r) / d) + 2);
                else h = 60 * (((r - g) / d) + 4);
                if (h < 0) h += 360;
            }
            return (h, s, l);
        }

        private static Color HsvToRgb(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60.0 % 2) - 1));
            double m = v - c;
            double r = 0, g = 0, b = 0;
            if (h < 60) { r = c; g = x; }
            else if (h < 120) { r = x; g = c; }
            else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; }
            else if (h < 300) { r = x; b = c; }
            else { r = c; b = x; }
            return Color.FromRgb(
                (byte)Math.Round((r + m) * 255),
                (byte)Math.Round((g + m) * 255),
                (byte)Math.Round((b + m) * 255));
        }

        private static Color HslToRgb(double h, double s, double l)
        {
            h = ((h % 360) + 360) % 360;
            double c = (1 - Math.Abs(2 * l - 1)) * s;
            double x = c * (1 - Math.Abs((h / 60.0 % 2) - 1));
            double m = l - c / 2;
            double r = 0, g = 0, b = 0;
            if (h < 60) { r = c; g = x; }
            else if (h < 120) { r = x; g = c; }
            else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; }
            else if (h < 300) { r = x; b = c; }
            else { r = c; b = x; }
            return Color.FromRgb(
                (byte)Math.Round((r + m) * 255),
                (byte)Math.Round((g + m) * 255),
                (byte)Math.Round((b + m) * 255));
        }
    }

    /// <summary>
    /// Результат редактора цвета: выбранный HEX, состояние кольца вокруг аватара
    /// и флаг «применить кольцо ко всем персонажам».
    /// </summary>
    public sealed class ColorEditResult
    {
        public string Hex { get; init; } = string.Empty;

        // Полный код выбранного значения: обычный hex для одноцвета либо код
        // градиента "grad|...". Для одноцвета совпадает с Hex.
        public string? Code { get; init; }

        public bool Ring { get; init; }
        // Закладка-ленточка карточки группы (имеет смысл только когда
        // редактировалась группа).
        public bool Bookmark { get; init; } = true;
        public bool? ApplyAll { get; init; }
    }
}
