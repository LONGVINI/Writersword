using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Writersword.Core.Interfaces.Services;
using Writersword.Resources.Localization;
using Writersword.Core.Interfaces.Services.Input;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Interfaces.Services.UI;
using Writersword.Core.Interfaces.WorkFlows;
using Writersword.ViewModels;
using Writersword.Core.Interfaces.Modules;
using Writersword.Views.Components.MenuBar;

namespace Writersword.Views
{
    public partial class MainWindowView : Window
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

        public MainWindowView()
        {
            _logger = App.Services.GetService<ILogger<MainWindowView>>()!;

            InitializeComponent();

            _focusSink = this.FindControl<Panel>("FocusSink");

            this.Opened += (s, e) =>
            {
                _logger.LogDebug("MainWindowView opened - DataContext: {DataContextType}", DataContext?.GetType().Name);
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

        // ── Глобальный анфокус TextBox ─────────────────────────────────────
        // Туннельный обработчик на уровне окна — ловит все клики раньше дочерних элементов.
        // Если сфокусирован TextBox и клик произошёл вне него — фокусируем FocusSink,
        // что гарантированно триггерит LostFocus на TextBox.
        // Window.Focusable == false в Avalonia 11, поэтому Focus() на окне не работает.

        private void OnGlobalPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            var focused = FocusManager?.GetFocusedElement();
            if (focused is not TextBox focusedBox) return;

            // Клик внутри самого TextBox — не трогаем.
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
                return IntPtr.Zero;

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
                var hwnd = TryGetPlatformHandle()?.Handle;
                if (hwnd.HasValue && hwnd.Value != IntPtr.Zero)
                {
                    ApplyMaximizedPaddingWin32(hwnd.Value);
                    return;
                }
            }

            ApplyMaximizedPaddingAvalonia();
        }

        [SupportedOSPlatform("windows")]
        private void ApplyMaximizedPaddingWin32(IntPtr hwnd)
        {
            if (!GetWindowRect(hwnd, out var winRect))
            {
                _logger.LogWarning("ApplyMaximizedPaddingWin32: GetWindowRect failed, falling back to Avalonia");
                ApplyMaximizedPaddingAvalonia();
                return;
            }

            var hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (hMonitor == IntPtr.Zero)
            {
                _logger.LogWarning("ApplyMaximizedPaddingWin32: MonitorFromWindow returned null, falling back to Avalonia");
                ApplyMaximizedPaddingAvalonia();
                return;
            }

            var mi = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(hMonitor, ref mi))
            {
                _logger.LogWarning("ApplyMaximizedPaddingWin32: GetMonitorInfo failed, falling back to Avalonia");
                ApplyMaximizedPaddingAvalonia();
                return;
            }

            var avaloniaScreen = Screens.All.FirstOrDefault(s =>
                Math.Abs(s.Bounds.X - mi.rcMonitor.Left) < 10 &&
                Math.Abs(s.Bounds.Y - mi.rcMonitor.Top) < 10);

            var scaling = avaloniaScreen?.Scaling ?? 1.0;

            var padLeft = Math.Max(0.0, (mi.rcWork.Left - winRect.Left) / scaling);
            var padTop = Math.Max(0.0, (mi.rcWork.Top - winRect.Top) / scaling);
            var padRight = Math.Max(0.0, (winRect.Right - mi.rcWork.Right) / scaling);
            var padBottom = Math.Max(0.0, (winRect.Bottom - mi.rcWork.Bottom) / scaling);

            _logger.LogDebug(
                "ApplyMaximizedPaddingWin32: winRect=({WL},{WT},{WR},{WB}), rcWork=({ML},{MT},{MR},{MB}), scaling={S}, padding={PL},{PT},{PR},{PB}",
                winRect.Left, winRect.Top, winRect.Right, winRect.Bottom,
                mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom,
                scaling, padLeft, padTop, padRight, padBottom);

            if (padLeft > AnomalousPaddingThreshold || padRight > AnomalousPaddingThreshold)
            {
                int frameX = GetSystemMetrics(SM_CXFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER);
                int frameY = GetSystemMetrics(SM_CYFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER);

                int correctX = mi.rcMonitor.Left - frameX;
                int correctY = mi.rcMonitor.Top - frameY;
                int correctW = (mi.rcMonitor.Right - mi.rcMonitor.Left) + frameX * 2;
                int correctH = (mi.rcMonitor.Bottom - mi.rcMonitor.Top) + frameY * 2;

                _logger.LogWarning(
                    "ApplyMaximizedPaddingWin32: anomalous padding detected, correcting position to ({X},{Y},{W},{H})",
                    correctX, correctY, correctW, correctH);

                SetWindowPos(hwnd, IntPtr.Zero, correctX, correctY, correctW, correctH,
                    SWP_NOZORDER | SWP_FRAMECHANGED);

                if (!GetWindowRect(hwnd, out winRect))
                {
                    _logger.LogWarning("ApplyMaximizedPaddingWin32: GetWindowRect after correction failed");
                    return;
                }

                padLeft = Math.Max(0.0, (mi.rcWork.Left - winRect.Left) / scaling);
                padTop = Math.Max(0.0, (mi.rcWork.Top - winRect.Top) / scaling);
                padRight = Math.Max(0.0, (winRect.Right - mi.rcWork.Right) / scaling);
                padBottom = Math.Max(0.0, (winRect.Bottom - mi.rcWork.Bottom) / scaling);

                _logger.LogDebug(
                    "ApplyMaximizedPaddingWin32: corrected padding={PL},{PT},{PR},{PB}",
                    padLeft, padTop, padRight, padBottom);
            }

            // Window.Padding.Bottom в Avalonia 12 конфликтует с внутренним OffScreenMargin
            // при ExtendClientAreaToDecorationsHint=True — нижний отступ либо игнорируется,
            // либо суммируется с OffScreenMargin и контент уходит под панель задач.
            // Решение: left/top/right — через Window.Padding (работают корректно),
            // bottom — через Margin корневого Grid (обходит конфликт с OffScreenMargin).
            Padding = new Thickness(padLeft, padTop, padRight, 0);
            ApplyRootGridBottomMargin(padBottom);
            ApplyButtonsPadding();
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

            _logger.LogDebug("Open tabs count: {Count}", tabCollection.Tabs.Count);

            if (tabCollection.Tabs.Count == 0)
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
                        cacheService.DeleteCache(tab.FilePath);
                    }
                }
            }

            _logger.LogInformation("OnClosing finished - shutting down");

            if (Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown(0);
            }
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