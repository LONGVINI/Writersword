using Avalonia;
#if DEBUG
using Avalonia.Diagnostics;
#endif
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Interfaces.Services.Input;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Interfaces.Services.UI;
using Writersword.Core.Interfaces.WorkFlows;
using Writersword.Core.Models.Backup;
using Writersword.Resources.Localization;
using Writersword.ViewModels;
using Writersword.Views.Components.MenuBar;

namespace Writersword.Views
{
    public partial class MainWindowView : Window, ITabSnapshotPresenter
    {
        private readonly ILogger<MainWindowView> _logger;
        private bool _isClosing = false;
        private CancellationTokenSource? _paddingDebounce;

        private WndProcDelegate? _wndProcDelegate;
        private IntPtr _originalWndProc = IntPtr.Zero;

        private double _titleBarContentWidth = 300.0;

        // Невидимый focusable элемент — приёмник фокуса при кликах на нефокусируемые области.
        // Нужен потому что Window.Focusable == false в Avalonia 11 и Focus() на окне не работает.
        private Panel? _focusSink;

        // ── Снапшоты вкладок (мгновенное переключение в стиле браузера) ──
        // При уходе с вкладки захватывается последний отрисованный кадр её
        // док-области; при возврате кадр мгновенно показывается поверх контента,
        // пока реальные модули прогружаются (включая случай выгруженной из памяти
        // вкладки), и скрывается по готовности. Один кадр ~8 МБ на вкладку —
        // на порядки дешевле живых модулей в памяти.
        private readonly System.Collections.Generic.Dictionary<object, Avalonia.Media.Imaging.RenderTargetBitmap> _tabSnapshots = new();
        private Image? _tabSnapshotOverlay;
        private DispatcherTimer? _snapshotHideTimer;
        private DateTime _snapshotShownAt;
        private static readonly TimeSpan SnapshotMinShowTime = TimeSpan.FromMilliseconds(150);
        // Потолок жизни оверлея небольшой: залипший поверх контента кадр хуже,
        // чем кратко видимый плейсхолдер под ним.
        private static readonly TimeSpan SnapshotMaxShowTime = TimeSpan.FromSeconds(3);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [SupportedOSPlatform("windows")]
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ReleaseCapture();

        [SupportedOSPlatform("windows")]
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [SupportedOSPlatform("windows")]
        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [SupportedOSPlatform("windows")]
        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [SupportedOSPlatform("windows")]
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint uFlags);

        [SupportedOSPlatform("windows")]
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr hWnd, out Win32Rect lpRect);

        [SupportedOSPlatform("windows")]
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [SupportedOSPlatform("windows")]
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

        [SupportedOSPlatform("windows")]
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [SupportedOSPlatform("windows")]
        [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [SupportedOSPlatform("windows")]
        [DllImport("user32.dll")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [SupportedOSPlatform("windows")]
        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct Win32Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int cbSize;
            public Win32Rect rcMonitor;
            public Win32Rect rcWork;
            public uint dwFlags;
        }

        private const uint WM_NCCALCSIZE = 0x0083;
        private const uint WM_NCHITTEST = 0x0084;
        private const int HTCLIENT = 1;
        private const int HTCAPTION = 2;
        private const int HTLEFT = 10;
        private const int HTRIGHT = 11;
        private const int HTTOP = 12;
        private const int HTTOPLEFT = 13;
        private const int HTTOPRIGHT = 14;
        private const int HTBOTTOM = 15;
        private const int HTBOTTOMLEFT = 16;
        private const int HTBOTTOMRIGHT = 17;
        private const int GWL_STYLE = -16;
        private const int GWLP_WNDPROC = -4;
        private const int WS_THICKFRAME = 0x00040000;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
        private const int SM_CXFRAME = 32;
        private const int SM_CYFRAME = 33;
        private const int SM_CXPADDEDBORDER = 92;
        private const double AnomalousPaddingThreshold = 100.0;

        private const int TitleBarHeight = 32;
        private const int WindowButtonsWidth = 138;
        private const int ResizeBorderSize = 8;

        // ── ITabSnapshotPresenter ─────────────────────────────────────────

        /// <summary>
        /// Панель главной области (Grid.Row=4): содержит DockControl первым ребёнком.
        /// </summary>
        private Panel? GetDockHostPanel()
            => this.FindControl<Panel>("NotificationContainer")?.Parent as Panel;

        // Кадр захватывается в половинном разрешении: RenderTargetBitmap синхронно
        // перерисовывает всё визуальное дерево дока на UI-потоке, и в полном
        // разрешении на большом документе это давало ощутимое провисание в момент
        // переключения. Для мимолётного кадра-заглушки половинного разрешения
        // достаточно, а стоимость рендера и память падают в четыре раза.
        private const double SnapshotResolutionFactor = 0.5;

        public void CaptureTabSnapshot(object tab)
        {
            try
            {
                var host = GetDockHostPanel();
                var dockControl = host?.Children.Count > 0 ? host.Children[0] as Control : null;
                if (dockControl == null || dockControl.Bounds.Width < 1 || dockControl.Bounds.Height < 1)
                    return;

                // Кадр не захватывается, пока контент вкладки не готов (виден
                // плейсхолдер загрузки, идёт прогрев раскладки или ещё показан
                // чужой снапшот-оверлей): при быстрых переключениях захват ловил
                // полупостроенный layout, и эта испорченная «фотография» потом
                // показывалась поверх реального контента при возврате на вкладку.
                // Прежний (корректный) кадр вкладки при пропуске сохраняется.
                if (!IsTabContentReady() || _tabSnapshotOverlay?.IsVisible == true)
                {
                    _logger.LogDebug("Tab snapshot capture skipped: content not ready");
                    return;
                }

                var captureStopwatch = System.Diagnostics.Stopwatch.StartNew();

                double scale = RenderScaling * SnapshotResolutionFactor;
                var pixelSize = new PixelSize(
                    Math.Max(1, (int)(dockControl.Bounds.Width * scale)),
                    Math.Max(1, (int)(dockControl.Bounds.Height * scale)));

                var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(
                    pixelSize, new Vector(96 * scale, 96 * scale));
                bitmap.Render(dockControl);

                if (_tabSnapshots.TryGetValue(tab, out var old))
                    old.Dispose();
                _tabSnapshots[tab] = bitmap;

                captureStopwatch.Stop();
                _logger.LogDebug("Tab snapshot captured: {Size} in {ElapsedMs}ms",
                    pixelSize, captureStopwatch.ElapsedMilliseconds);
                if (captureStopwatch.ElapsedMilliseconds > 50)
                {
                    _logger.LogWarning("Tab snapshot capture took {ElapsedMs}ms on UI thread",
                        captureStopwatch.ElapsedMilliseconds);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tab snapshot capture failed");
            }
        }

        public void ShowTabSnapshot(object tab)
        {
            try
            {
                if (!_tabSnapshots.TryGetValue(tab, out var bitmap))
                {
                    // Для этой вкладки кадра нет (первое открытие) — убираем оверлей,
                    // оставшийся от предыдущего показа, иначе он показывал бы чужой кадр.
                    HideTabSnapshotOverlay();
                    return;
                }

                var host = GetDockHostPanel();
                if (host == null) return;

                // Кадр другой геометрии (окно или панель изменили размер, либо захват
                // случился в вырожденный момент) не показываем и выбрасываем:
                // Stretch=Fill растянул бы его в «сломанный экранчик» поверх контента.
                var dockControl = host.Children.Count > 0 ? host.Children[0] as Control : null;
                if (dockControl != null && dockControl.Bounds.Width >= 1)
                {
                    int expectedW = Math.Max(1,
                        (int)(dockControl.Bounds.Width * RenderScaling * SnapshotResolutionFactor));
                    int expectedH = Math.Max(1,
                        (int)(dockControl.Bounds.Height * RenderScaling * SnapshotResolutionFactor));

                    bool geometryMatches =
                        Math.Abs(bitmap.PixelSize.Width - expectedW) <= Math.Max(4, expectedW / 20)
                        && Math.Abs(bitmap.PixelSize.Height - expectedH) <= Math.Max(4, expectedH / 20);

                    if (!geometryMatches)
                    {
                        _logger.LogDebug(
                            "Tab snapshot discarded (geometry mismatch): {Actual} vs expected {ExpectedW}x{ExpectedH}",
                            bitmap.PixelSize, expectedW, expectedH);
                        ForgetTabSnapshot(tab);
                        HideTabSnapshotOverlay();
                        return;
                    }
                }

                EnsureSnapshotOverlay(host);

                _tabSnapshotOverlay!.Source = bitmap;
                _tabSnapshotOverlay.IsVisible = true;
                _snapshotShownAt = DateTime.UtcNow;
                StartSnapshotHidePolling();

                _logger.LogDebug("Tab snapshot shown");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tab snapshot show failed");
                HideTabSnapshotOverlay();
            }
        }

        public void ForgetTabSnapshot(object tab)
        {
            if (!_tabSnapshots.TryGetValue(tab, out var bitmap))
                return;

            if (_tabSnapshotOverlay != null && ReferenceEquals(_tabSnapshotOverlay.Source, bitmap))
            {
                _tabSnapshotOverlay.IsVisible = false;
                _tabSnapshotOverlay.Source = null;
            }

            _tabSnapshots.Remove(tab);
            bitmap.Dispose();
        }

        private void EnsureSnapshotOverlay(Panel host)
        {
            if (_tabSnapshotOverlay != null) return;

            // ZIndex 900: поверх DockControl, но ниже NotificationContainer (1000).
            // IsHitTestVisible=false — клики проходят сквозь кадр к реальному контенту.
            _tabSnapshotOverlay = new Image
            {
                Stretch = Avalonia.Media.Stretch.Fill,
                IsHitTestVisible = false,
                ZIndex = 900,
                IsVisible = false
            };
            host.Children.Add(_tabSnapshotOverlay);
        }

        private void StartSnapshotHidePolling()
        {
            if (_snapshotHideTimer == null)
            {
                _snapshotHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                _snapshotHideTimer.Tick += (_, _) =>
                {
                    var elapsed = DateTime.UtcNow - _snapshotShownAt;
                    if (elapsed < SnapshotMinShowTime) return;

                    if (elapsed >= SnapshotMaxShowTime || IsTabContentReady())
                        HideTabSnapshotOverlay();
                };
            }
            _snapshotHideTimer.Start();
        }

        /// <summary>
        /// Готов ли реальный контент под оверлеем: нет видимых плейсхолдеров
        /// загрузки модулей и ни один текстовый канвас не прогревает раскладку.
        /// </summary>
        private bool IsTabContentReady()
        {
            if (Writersword.Modules.TextEditor.Document.DocumentCanvas.ActiveWarmupCount > 0)
                return false;

            var host = GetDockHostPanel();
            if (host == null) return true;

            return !host.GetVisualDescendants()
                .OfType<Writersword.Infrastructure.Dock.ModuleLoadingPlaceholder>()
                .Any(p => p.IsEffectivelyVisible);
        }

        private void HideTabSnapshotOverlay()
        {
            _snapshotHideTimer?.Stop();
            if (_tabSnapshotOverlay != null)
                _tabSnapshotOverlay.IsVisible = false;
        }

        // ── Оверлей «отпустите, чтобы открыть вкладку» ────────────────────
        // Показывается на время перетаскивания вкладки: контент не грузится
        // вообще, пока кнопка мыши не отпущена, — а пользователь видит
        // анимацию ожидания вместо застывшего контента предыдущей вкладки.
        // Анимированный ProgressBar здесь допустим: оверлей живёт только
        // на время жеста перетаскивания.
        private Border? _tabDragPendingOverlay;

        public void ShowTabDragPending()
        {
            var host = GetDockHostPanel();
            if (host == null) return;

            if (_tabDragPendingOverlay == null)
            {
                // ZIndex 950: поверх снапшота вкладки (900), ниже уведомлений (1000).
                _tabDragPendingOverlay = new Border
                {
                    Background = Avalonia.Media.Brushes.Transparent,
                    IsHitTestVisible = false,
                    ZIndex = 950,
                    IsVisible = false,
                    Child = new StackPanel
                    {
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        Spacing = 12,
                        Children =
                        {
                            new ProgressBar
                            {
                                IsIndeterminate = true,
                                Width = 160,
                                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
                            },
                            new TextBlock
                            {
                                Text = Strings.TabBar_ReleaseToOpen,
                                Opacity = 0.7,
                                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
                            }
                        }
                    }
                };
                host.Children.Add(_tabDragPendingOverlay);
            }

            // Текст обновляется на случай смены языка между показами.
            if (_tabDragPendingOverlay.Child is StackPanel panel
                && panel.Children.Count > 1
                && panel.Children[1] is TextBlock label)
            {
                label.Text = Strings.TabBar_ReleaseToOpen;
            }

            _tabDragPendingOverlay.IsVisible = true;
        }

        public void HideTabDragPending()
        {
            if (_tabDragPendingOverlay != null)
                _tabDragPendingOverlay.IsVisible = false;
        }

        public MainWindowView()
        {
            _logger = App.Services.GetService<ILogger<MainWindowView>>()!;

            InitializeComponent();

            _focusSink = this.FindControl<Panel>("FocusSink");

#if DEBUG
            // Инспектор визуального дерева, F12. Нужен, чтобы не гадать по логам,
            // что за контрол оказался на экране: наводишь — видишь его тип, предков
            // и привязки.
            //
            // Инспектор приезжает пакетом ProDiagnostics — он лежал в зависимостях,
            // но его никогда не вызывали. Сборка внутри пакета называется
            // Avalonia.Diagnostics, отсюда и пространство имён; одноимённого пакета
            // на nuget.org под Avalonia 12 не существует, ставить его не надо.
            //
            // Сочетание по умолчанию — F12, задавать отдельно нечего.
            this.AttachDevTools();
#endif

            StartPointerProbe();

            this.Opened += (s, e) =>
            {
                _logger.LogDebug("MainWindowView opened - DataContext: {DataContextType}", DataContext?.GetType().Name);

                // Регистрируем окно как презентер снапшотов вкладок:
                // MainWindowViewModel вызывает захват/показ кадров при переключениях.
                if (DataContext is MainWindowViewModel snapshotVm)
                    snapshotVm.TabSnapshotPresenter = this;

                // Диагностика производительности рендера — выключена. Для включения
                // заменить None на нужный набор флагов, например:
                //   Avalonia.Rendering.RendererDebugOverlays.Fps
                //   | Avalonia.Rendering.RendererDebugOverlays.DirtyRects
                //   | Avalonia.Rendering.RendererDebugOverlays.LayoutTimeGraph
                //   | Avalonia.Rendering.RendererDebugOverlays.RenderTimeGraph;
                this.RendererDiagnostics.DebugOverlays =
                    Avalonia.Rendering.RendererDebugOverlays.None;

                EnsureThickFrameAndSubclass();
                if (WindowState == WindowState.Maximized)
                    ScheduleMaximizedPadding();

                // После того как визуальное дерево построено — подписываемся на LayoutUpdated
                // MenuBarView для отслеживания фактической ширины пунктов меню.
                // Это нужно для WM_NCHITTEST: область правее пунктов меню должна быть
                // HTCAPTION (перетаскивание окна), а не HTCLIENT.
                var menuBarView = this.FindDescendantOfType<MenuBarView>();
                if (menuBarView != null)
                {
                    menuBarView.LayoutUpdated += (_, _) =>
                    {
                        var wrapPanel = menuBarView.GetVisualDescendants()
                            .OfType<WrapPanel>()
                            .FirstOrDefault();

                        if (wrapPanel == null) return;

                        double maxRight = 0;
                        foreach (Control child in wrapPanel.Children)
                        {
                            if (child.IsVisible)
                                maxRight = Math.Max(maxRight, child.Bounds.Right);
                        }

                        if (maxRight > 0)
                            _titleBarContentWidth = menuBarView.Bounds.X + maxRight;
                    };
                }
            };

            Closing += OnClosing;

            this.AddHandler(
                KeyDownEvent,
                OnKeyDown,
                Avalonia.Interactivity.RoutingStrategies.Tunnel
            );

            // Глобальный обработчик: снимает фокус с любого TextBox при клике вне него.
            // Регистрируется один раз на уровне окна и покрывает всё приложение.
            this.AddHandler(
                PointerPressedEvent,
                OnGlobalPointerPressed,
                Avalonia.Interactivity.RoutingStrategies.Tunnel
            );

            InitializeTitleBar();
        }

        // Туннельный обработчик на уровне окна — ловит все клики раньше дочерних элементов.
        // Если сфокусирован TextBox и клик произошёл вне него — фокусируем FocusSink,
        // что гарантированно триггерит LostFocus на TextBox.
        // Window.Focusable == false в Avalonia 11, поэтому Focus() на окне не работает.
        private void OnGlobalPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            var focused = FocusManager?.GetFocusedElement();
            if (focused is not TextBox focusedBox) return;

            Visual? src = e.Source as Visual;
            while (src != null)
            {
                if (ReferenceEquals(src, focusedBox)) return;
                src = src.GetVisualParent();
            }

            // Уводим фокус в FocusSink — это триггерит LostFocus на TextBox.
            // Если источник клика сам focusable (кнопка и т.д.), Avalonia
            // дополнительно переведёт фокус на него в ходе обработки события.
            _focusSink?.Focus();
        }

        /// <summary>
        /// Выставляет WS_THICKFRAME для Aero Snap и субклассирует WndProc.
        /// </summary>
        private void EnsureThickFrameAndSubclass()
        {
            if (!OperatingSystem.IsWindows()) return;

            var hwnd = TryGetPlatformHandle()?.Handle;
            if (!hwnd.HasValue || hwnd.Value == IntPtr.Zero) return;

            var style = GetWindowLong(hwnd.Value, GWL_STYLE);

            if ((style & WS_THICKFRAME) == 0)
            {
                SetWindowLong(hwnd.Value, GWL_STYLE, style | WS_THICKFRAME);
                _logger.LogDebug("EnsureThickFrameAndSubclass: WS_THICKFRAME set, 0x{Old:X8} -> 0x{New:X8}",
                    style, style | WS_THICKFRAME);
            }

            if (_originalWndProc == IntPtr.Zero)
            {
                _wndProcDelegate = CustomWndProc;
                var newProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
                _originalWndProc = SetWindowLongPtr(hwnd.Value, GWLP_WNDPROC, newProc);
                _logger.LogDebug("EnsureThickFrameAndSubclass: WndProc subclassed, original=0x{Orig:X}", _originalWndProc);
            }

            SetWindowPos(hwnd.Value, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
        }

        [SupportedOSPlatform("windows")]
        private IntPtr CustomWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_NCCALCSIZE && wParam != IntPtr.Zero)
            {
                // При максимизации явно ограничиваем клиентскую область рабочей зоной монитора.
                // Это исключает перекрытие панели задач без ручного расчёта Padding/Margin.
                // При обычном состоянии возвращаем нулевой размер NC-области (убираем системные рамки).
                if (WindowState == WindowState.Maximized)
                {
                    var hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                    if (hMonitor != IntPtr.Zero)
                    {
                        var mi = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
                        if (GetMonitorInfo(hMonitor, ref mi))
                        {
                            Marshal.WriteInt32(lParam, 0, mi.rcWork.Left);
                            Marshal.WriteInt32(lParam, 4, mi.rcWork.Top);
                            Marshal.WriteInt32(lParam, 8, mi.rcWork.Right);
                            Marshal.WriteInt32(lParam, 12, mi.rcWork.Bottom);

                            _logger.LogDebug(
                                "WM_NCCALCSIZE maximized: rcWork=({L},{T},{R},{B})",
                                mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom);

                            return IntPtr.Zero;
                        }
                    }
                }

                return IntPtr.Zero;
            }

            if (msg == WM_NCHITTEST)
            {
                if (!GetWindowRect(hwnd, out var winRect))
                    return CallWindowProc(_originalWndProc, hwnd, msg, wParam, lParam);

                long lparam = lParam.ToInt64();
                int screenX = (int)(short)(lparam & 0xFFFF);
                int screenY = (int)(short)((lparam >> 16) & 0xFFFF);

                int relX = screenX - winRect.Left;
                int relY = screenY - winRect.Top;
                int winW = winRect.Right - winRect.Left;
                int winH = winRect.Bottom - winRect.Top;

                uint dpi = GetDpiForWindow(hwnd);
                double scaling = dpi > 0 ? dpi / 96.0 : 1.0;

                int resizeBorderPx = (int)(ResizeBorderSize * scaling);

                if (WindowState != WindowState.Maximized)
                {
                    bool onLeft = relX < resizeBorderPx;
                    bool onRight = relX > winW - resizeBorderPx;
                    bool onTop = relY < resizeBorderPx;
                    bool onBottom = relY > winH - resizeBorderPx;

                    if (onTop && onLeft) return new IntPtr(HTTOPLEFT);
                    if (onTop && onRight) return new IntPtr(HTTOPRIGHT);
                    if (onBottom && onLeft) return new IntPtr(HTBOTTOMLEFT);
                    if (onBottom && onRight) return new IntPtr(HTBOTTOMRIGHT);
                    if (onTop) return new IntPtr(HTTOP);
                    if (onBottom) return new IntPtr(HTBOTTOM);
                    if (onLeft) return new IntPtr(HTLEFT);
                    if (onRight) return new IntPtr(HTRIGHT);
                }

                int titleBarHeightPx = (int)(TitleBarHeight * scaling);
                int windowButtonsPx = (int)(WindowButtonsWidth * scaling);
                int contentWidthPx = (int)(_titleBarContentWidth * scaling);

                if (relY < titleBarHeightPx)
                {
                    if (relX >= winW - windowButtonsPx)
                        return new IntPtr(HTCLIENT);
                    if (relX < contentWidthPx)
                        return new IntPtr(HTCLIENT);
                    return new IntPtr(HTCAPTION);
                }

                return new IntPtr(HTCLIENT);
            }

            return CallWindowProc(_originalWndProc, hwnd, msg, wParam, lParam);
        }

        /// <summary>Инициализация кнопок заголовка.</summary>
        private void InitializeTitleBar()
        {
            var minimizeButton = this.FindControl<Button>("MinimizeButton");
            if (minimizeButton != null)
                minimizeButton.Click += (_, _) => WindowState = WindowState.Minimized;

            var maximizeButton = this.FindControl<Button>("MaximizeButton");
            if (maximizeButton != null)
                maximizeButton.Click += (_, _) => ToggleMaximize();

            var closeButton = this.FindControl<Button>("CloseButton");
            if (closeButton != null)
                closeButton.Click += (_, _) => Close();

            PropertyChanged += OnWindowPropertyChanged;
            PositionChanged += OnWindowPositionChanged;
        }

        private void ToggleMaximize()
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void OnWindowPositionChanged(object? sender, PixelPointEventArgs e)
        {
            if (WindowState == WindowState.Maximized)
                ScheduleMaximizedPadding();
        }

        private void ScheduleMaximizedPadding()
        {
            _paddingDebounce?.Cancel();
            _paddingDebounce?.Dispose();
            _paddingDebounce = null;

            var cts = new CancellationTokenSource();
            _paddingDebounce = cts;

            DispatcherTimer.RunOnce(() =>
            {
                if (cts.IsCancellationRequested) return;
                if (WindowState != WindowState.Maximized) return;
                ApplyMaximizedPadding();
            }, TimeSpan.FromMilliseconds(150));
        }

        private void ApplyMaximizedPadding()
        {
            if (OperatingSystem.IsWindows())
            {
                ApplyMaximizedPaddingWin32();
                return;
            }

            ApplyMaximizedPaddingAvalonia();
        }

        /// <summary>
        /// На Windows клиентская область при максимизации уже ограничена рабочей зоной
        /// через WM_NCCALCSIZE, поэтому никакой компенсации через Padding/Margin не нужно.
        /// Сбрасываем любые ранее выставленные отступы и применяем корректировку кнопок.
        /// </summary>
        [SupportedOSPlatform("windows")]
        private void ApplyMaximizedPaddingWin32()
        {
            Padding = new Thickness(0);
            ApplyRootGridBottomMargin(0);
            ApplyButtonsPadding();

            _logger.LogDebug("ApplyMaximizedPaddingWin32: padding cleared, WM_NCCALCSIZE handles work area");
        }

        private void ApplyMaximizedPaddingAvalonia()
        {
            var screen = Screens.ScreenFromWindow(this);
            if (screen == null)
            {
                _logger.LogWarning("ApplyMaximizedPaddingAvalonia: screen not found, padding not applied");
                return;
            }

            var windowBounds = Bounds;
            var workArea = screen.WorkingArea;
            var scaling = screen.Scaling;

            var workLeft = workArea.X / scaling;
            var workTop = workArea.Y / scaling;
            var workRight = (workArea.X + workArea.Width) / scaling;
            var workBottom = (workArea.Y + workArea.Height) / scaling;

            var windowPosition = Position;
            var winLeft = windowPosition.X / scaling;
            var winTop = windowPosition.Y / scaling;
            var winRight = winLeft + windowBounds.Width;
            var winBottom = winTop + windowBounds.Height;

            var padBottom = Math.Max(0, winBottom - workBottom);

            // Window.Padding.Bottom в Avalonia 12 конфликтует с внутренним OffScreenMargin
            // при ExtendClientAreaToDecorationsHint=True — нижний отступ либо игнорируется,
            // либо суммируется с OffScreenMargin и контент уходит под панель задач.
            // Решение: left/top/right — через Window.Padding (работают корректно),
            // bottom — через Margin корневого Grid (обходит конфликт с OffScreenMargin).
            Padding = new Thickness(
                Math.Max(0, workLeft - winLeft),
                Math.Max(0, workTop - winTop),
                Math.Max(0, winRight - workRight),
                0);

            ApplyRootGridBottomMargin(padBottom);
            ApplyButtonsPadding();
        }

        /// <summary>
        /// Применяет нижний отступ к корневому Grid вместо Window.Padding.Bottom.
        /// В Avalonia 12 с ExtendClientAreaToDecorationsHint=True Window.Padding.Bottom
        /// конфликтует с внутренним OffScreenMargin — контент уходит под панель задач.
        /// </summary>
        private void ApplyRootGridBottomMargin(double bottom)
        {
            if (this.Content is Grid rootGrid)
                rootGrid.Margin = new Thickness(0, 0, 0, bottom);
        }

        private void ApplyButtonsPadding()
        {
            var buttonPanel = this.FindControl<StackPanel>("WindowButtonsPanel");
            if (buttonPanel == null) return;
            buttonPanel.Margin = new Thickness(0, -1, 0, 0);
        }

        private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property != WindowStateProperty) return;

            if (WindowState == WindowState.Maximized)
                ScheduleMaximizedPadding();
            else
            {
                Padding = new Thickness(0);
                ApplyRootGridBottomMargin(0);
                ApplyButtonsPadding();
            }

            var maximizeIcon = this.FindControl<Rectangle>("MaximizeIcon");
            var restoreIcon = this.FindControl<Canvas>("RestoreIcon");

            if (maximizeIcon != null)
                maximizeIcon.IsVisible = WindowState != WindowState.Maximized;

            if (restoreIcon != null)
                restoreIcon.IsVisible = WindowState == WindowState.Maximized;
        }

        private async void OnClosing(object? sender, CancelEventArgs e)
        {
            if (_isClosing)
            {
                e.Cancel = false;
                return;
            }

            e.Cancel = true;
            _isClosing = true;

            _paddingDebounce?.Cancel();
            _paddingDebounce?.Dispose();
            _paddingDebounce = null;

            _logger.LogDebug("OnClosing started");

            if (DataContext is not MainWindowViewModel vm)
            {
                if (Application.Current?.ApplicationLifetime
                    is IClassicDesktopStyleApplicationLifetime lifetime)
                {
                    lifetime.Shutdown(0);
                }
                return;
            }

            var tabCollection = App.Services.GetRequiredService<ITabCollection>();
            var projectWorkflow = App.Services.GetRequiredService<IProjectWorkflow>();
            var dialogService = App.Services.GetRequiredService<IDialogService>();

            _logger.LogDebug("Open tabs count: {Count}", tabCollection.Tabs.Count());

            if (tabCollection.Tabs.Count() == 0)
            {
                _logger.LogDebug("No tabs open, showing welcome screen");
                _isClosing = false;
                await App.ShowWelcomeScreen(this);
                return;
            }

            var activeTab = tabCollection.ActiveTab;
            if (activeTab != null && !string.IsNullOrEmpty(activeTab.FilePath))
            {
                try
                {
                    // Кеш пишется только когда есть что спасать. Безусловная запись при
                    // каждом закрытии складывала в .wsasd то, что вернули живые модули,
                    // включая пустой документ модуля, который своих данных не получил.
                    // При следующем запуске такой кеш оказывается новее ZIP, проходит
                    // сравнение как «данные идентичны» и подставляется вместо проекта.
                    bool hasUnsaved = await projectWorkflow.HasUnsavedChanges(activeTab);

                    if (hasUnsaved)
                    {
                        var stateCollector = App.Services.GetRequiredService<IModuleStateCollectorService>();
                        var cacheService = App.Services.GetRequiredService<IZipCacheService>();

                        var activeModules = vm.GetActiveModules();
                        var (customData, sessionData) = stateCollector.CollectAllData(activeModules);

                        if (customData.Count > 0)
                        {
                            var project = activeTab.GetProject();
                            await cacheService.SaveCacheAsync(activeTab.FilePath, project.Id, customData, sessionData);
                            activeTab.MarkAsModified();
                            _logger.LogDebug("Active tab cached: {Count} modules", customData.Count);
                        }
                    }
                    else
                    {
                        _logger.LogDebug("Active tab has no unsaved changes, cache not written: {Title}", activeTab.Title);
                    }

                    if (activeTab.Workspace != null)
                    {
                        await activeTab.Workspace.SaveWorkspaceAsync();
                        _logger.LogDebug("Workspace saved for: {Title}", activeTab.Title);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error caching active tab on close");
                }
            }

            var settingsService = App.Services.GetRequiredService<ISettingsService>();
            var openPaths = tabCollection.Tabs
                .Select(t => t.FilePath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct()
                .ToList();
            settingsService.SaveOpenProjects(openPaths!);
            _logger.LogDebug("Saved {Count} open projects", openPaths.Count);

            var tabs = tabCollection.Tabs.ToList();

            foreach (var tab in tabs)
            {
                if (!await projectWorkflow.HasUnsavedChanges(tab))
                    continue;

                _logger.LogDebug("Tab {Title} has unsaved changes", tab.Title);

                var result = await dialogService.ShowMessageAsync(
                    Strings.Dialog_UnsavedChanges_Title,
                    $"{Strings.Dialog_UnsavedChanges_Document} \"{tab.Title}\" {Strings.Dialog_UnsavedChanges_HasUnsaved}\n\n{Strings.Dialog_UnsavedChanges_Message}",
                    MessageBoxType.Question,
                    MessageBoxButtons.YesNoCancel
                );

                if (result == MessageBoxResult.Cancel)
                {
                    _logger.LogDebug("Closing cancelled by user");
                    _isClosing = false;
                    return;
                }

                if (result == MessageBoxResult.Yes)
                {
                    bool saved = await projectWorkflow.SaveDocumentAsync(tab);
                    if (!saved)
                    {
                        _logger.LogWarning("Save failed for {Title}", tab.Title);
                        _isClosing = false;
                        return;
                    }
                }
                else if (result == MessageBoxResult.No)
                {
                    if (!string.IsNullOrEmpty(tab.FilePath))
                    {
                        var cacheService = App.Services.GetRequiredService<IZipCacheService>();
                        // Task.Run: DeleteCache вызывает _fileLock.Wait() на UI-потоке.
                        // Если фоновый авто-сейв держит лок — дедлок при закрытии.
                        await Task.Run(() => cacheService.DeleteCache(tab.FilePath));
                    }
                }
            }

            // Точка восстановления при закрытии: для тех, кто держит приложение
            // открытым сутками, это единственный надёжный ритм. Снимается с
            // файла на диске, поэтому идёт после всех сохранений выше.
            try
            {
                var backupService = App.Services.GetRequiredService<IBackupService>();

                foreach (var path in openPaths)
                {
                    if (!string.IsNullOrEmpty(path))
                        await backupService.CreateSnapshotAsync(path, BackupTrigger.AppClose);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Backup point on close failed");
            }

            // Финальная чистка кешей. Идёт последней — после всех записей .wsasd
            // при закрытии. Кеш, совпадающий с ZIP, ничего не восстанавливает, но
            // при следующем запуске становится источником данных вместо проекта:
            // allData в SaveDocumentAsync стартует именно с него.
            // Здесь, а не в ShutdownRequested: событие поднимает TryShutdown, а
            // отсюда вызывается Shutdown(0), который его не поднимает.
            try
            {
                await projectWorkflow.CleanupCachesAsync(openPaths!);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cache cleanup on close failed");
            }

            _logger.LogInformation("OnClosing finished - shutting down");

            if (Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown(0);
            }
        }

        // РАЗБОР ЗАЛИПАНИЯ. Плоскость модулей перестаёт принимать ввод, а меню и
        // кнопки воркмодов продолжают работать. UI-поток при этом жив — значит
        // дело не в зависании, а в захвате указателя: перетаскивание в Dock
        // началось и не завершилось, весь ввод уходит захватившему элементу.
        //
        // Пишем нажатия, отпускания и потерю захвата на туннельной фазе, до того
        // как их кто-либо обработает. Нажатие без парного отпускания — и виновник
        // назван вместе с элементом, который его съел.
        private void StartPointerProbe()
        {
            var log = Serilog.Log.ForContext("SourceContext", "PointerProbe");

            AddHandler(PointerPressedEvent, (_, e) =>
            {
                log.Debug("PRESSED over {Source}, capture held by {Captured}",
                    Describe(e.Source), Describe(e.Pointer.Captured));

                _probeLastCaptured = e.Pointer.Captured is null ? null : Describe(e.Pointer.Captured);
            }, RoutingStrategies.Tunnel, handledEventsToo: true);

            AddHandler(PointerReleasedEvent, (_, e) =>
            {
                log.Debug("RELEASED over {Source}, capture held by {Captured}",
                    Describe(e.Source), Describe(e.Pointer.Captured));

                // Состояние сбрасывается здесь, а не только в PointerMoved.
                // Иначе после отпускания в поле остаётся значение с последнего
                // движения мыши, и таймер до следующего шевеления печатает
                // залипание там, где захват давно освобождён.
                _probeLastCaptured = e.Pointer.Captured is null ? null : Describe(e.Pointer.Captured);
            }, RoutingStrategies.Tunnel, handledEventsToo: true);

            AddHandler(PointerCaptureLostEvent, (_, e) =>
            {
                log.Debug("CAPTURE LOST by {Source}", Describe(e.Source));
                _probeLastCaptured = null;
            }, RoutingStrategies.Tunnel, handledEventsToo: true);

            // Раз в секунду — держит ли кто-то захват. Если строка повторяется, а
            // отпускания не было, значит указатель залип именно там.
            var timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            timer.Tick += (_, _) =>
            {
                var captured = _probeLastCaptured;
                if (captured != null)
                    log.Debug("capture still held by {Captured}", captured);
            };
            timer.Start();
            _probeTimer = timer;

            AddHandler(PointerMovedEvent, (_, e) =>
            {
                _probeLastCaptured = e.Pointer.Captured is null ? null : Describe(e.Pointer.Captured);
            }, RoutingStrategies.Tunnel, handledEventsToo: true);
        }

        private DispatcherTimer? _probeTimer;
        private string? _probeLastCaptured;

        private static string Describe(object? o)
        {
            if (o is null) return "none";
            if (o is not Control c) return o.GetType().Name;

            // Один тип контрола ни о чём не говорит: ContentPresenter в приложении
            // сотни. Нужна цепочка предков — по ней видно, чей он и в каком окне.
            var parts = new List<string>();
            Visual? v = c;
            for (int i = 0; i < 6 && v is not null; i++)
            {
                if (v is Control ctl)
                    parts.Add(ctl.GetType().Name
                        + (string.IsNullOrEmpty(ctl.Name) ? "" : " #" + ctl.Name));
                else
                    parts.Add(v.GetType().Name);

                v = v.GetVisualParent();
            }

            // Корень отдельно: всплывающий список живёт в своём окне PopupRoot,
            // и по нему сразу видно, пришло событие из ленты или из списка.
            var root = TopLevel.GetTopLevel(c)?.GetType().Name ?? "?";
            return string.Join(" < ", parts) + "  [root: " + root + "]";
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
#if DEBUG
            _logger.LogDebug("KeyDown: Key={Key}, Modifiers={Modifiers}, Handled={Handled}", e.Key, e.KeyModifiers, e.Handled);
#endif

            var hotKeyService = App.Services.GetRequiredService<IHotKeyService>();
            var gesture = new KeyGesture(e.Key, e.KeyModifiers);
            var focusedModuleType = GetFocusedModuleType();

            if (hotKeyService.HandleKeyPress(gesture, focusedModuleType))
            {
                _logger.LogDebug("KeyDown: handled by HotKeyService, Key={Key}", e.Key);
                e.Handled = true;
                return;
            }

            if (focusedModuleType != null && DataContext is MainWindowViewModel vm)
            {
                var activeTab = vm.TabBar.ActiveTab;
                var module = activeTab?.ModuleContext.GetModule(focusedModuleType)
                    as IUndoableModule;
                if (module?.BlockedNativeGestures.Any(g =>
                    g.Key == e.Key && g.KeyModifiers == e.KeyModifiers) == true)
                {
                    e.Handled = true;
                }
            }
        }

        private string? GetFocusedModuleType()
        {
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;

            while (focused != null)
            {
                if (focused.Tag is string moduleType && !string.IsNullOrEmpty(moduleType))
                    return moduleType;

                focused = focused.Parent as Control;
            }

            return null;
        }
    }
}