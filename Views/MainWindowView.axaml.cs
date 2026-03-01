using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using Writersword.Core.Interfaces.Services;
using Writersword.Resources.Localization;
using Writersword.Src.Core.Interfaces.Services.Input;
using Writersword.Src.Core.Interfaces.Services.Storage;
using Writersword.Src.Core.Interfaces.Services.UI;
using Writersword.Src.Core.Interfaces.WorkFlows;
using Writersword.ViewModels;

namespace Writersword.Views
{
    /// <summary>
    /// Главное окно приложения
    /// </summary>
    public partial class MainWindowView : Window
    {
        private readonly ILogger<MainWindowView> _logger;
        private bool _isClosing = false;
        private CancellationTokenSource? _paddingDebounce;

        public MainWindowView()
        {
            _logger = App.Services.GetService<ILogger<MainWindowView>>()!;

            InitializeComponent();

            this.Opened += (s, e) =>
            {
                _logger.LogDebug("MainWindowView opened - DataContext: {DataContextType}", DataContext?.GetType().Name);

                if (WindowState == WindowState.Maximized)
                    ScheduleMaximizedPadding();
            };

            Closing += OnClosing;
            KeyDown += OnKeyDown;

            InitializeTitleBar();
        }

        /// <summary>
        /// Инициализация кнопок и перетаскивания кастомного заголовка окна
        /// </summary>
        private void InitializeTitleBar()
        {
            this.AddHandler(
                InputElement.PointerPressedEvent,
                OnTitleBarPointerPressed,
                Avalonia.Interactivity.RoutingStrategies.Tunnel
            );

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

        /// <summary>
        /// Перетаскивание окна — срабатывает на Window через tunnel,
        /// игнорирует клики по кнопкам и элементам меню,
        /// срабатывает только в зоне заголовка (32px сверху)
        /// </summary>
        private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

            var pos = e.GetCurrentPoint(this).Position;
            if (pos.Y > 32) return;

            var source = e.Source as Control;
            while (source != null)
            {
                if (source is Button) return;
                source = source.Parent as Control;
            }

            BeginMoveDrag(e);
        }

        private void ToggleMaximize()
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        /// <summary>
        /// Срабатывает при каждом изменении позиции окна.
        /// Windows при переходе FullScreen → Maximized двигает окно в несколько шагов,
        /// поэтому откладываем вычисление паддинга до стабилизации позиции.
        /// </summary>
        private void OnWindowPositionChanged(object? sender, PixelPointEventArgs e)
        {
            if (WindowState == WindowState.Maximized)
                ScheduleMaximizedPadding();
        }

        /// <summary>
        /// Откладывает вычисление паддинга на 150мс после последнего события позиции.
        /// Если за это время пришло новое событие — предыдущее отменяется.
        /// Гарантирует что паддинг считается только по финальной позиции окна.
        /// </summary>
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

        /// <summary>
        /// Вычисляет и применяет точный padding для максимизированного borderless-окна.
        /// Windows при максимизации NoChrome-окна намеренно выдвигает его за края экрана
        /// на величину равную ширине системной рамки. Паддинг вычисляется динамически
        /// через сравнение реальных Bounds окна с WorkingArea экрана с учётом DPI.
        /// Фон Window прозрачный, поэтому overflow зона не видна на соседних мониторах.
        /// </summary>
        private void ApplyMaximizedPadding()
        {
            var screen = Screens.ScreenFromWindow(this);
            if (screen == null)
            {
                _logger.LogWarning("ApplyMaximizedPadding: screen not found, using fallback 8px");
                Padding = new Thickness(8);
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

            var padLeft = Math.Max(0, workLeft - winLeft);
            var padTop = Math.Max(0, workTop - winTop);
            var padRight = Math.Max(0, winRight - workRight);
            var padBottom = Math.Max(0, winBottom - workBottom);

            var padding = new Thickness(padLeft, padTop, padRight, padBottom);
            Padding = padding;

            _logger.LogDebug(
                "ApplyMaximizedPadding: workArea={WorkArea}, window=({WinLeft},{WinTop},{WinRight},{WinBottom}), padding={Padding}",
                workArea, winLeft, winTop, winRight, winBottom, padding);
        }

        /// <summary>
        /// Обновляет padding и иконку кнопки максимизации при изменении состояния окна.
        /// </summary>
        private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property != WindowStateProperty) return;

            if (WindowState == WindowState.Maximized)
                ScheduleMaximizedPadding();
            else
                Padding = new Thickness(0);

            var maximizeIcon = this.FindControl<Rectangle>("MaximizeIcon");
            var restoreIcon = this.FindControl<Canvas>("RestoreIcon");

            if (maximizeIcon != null)
                maximizeIcon.IsVisible = WindowState != WindowState.Maximized;

            if (restoreIcon != null)
                restoreIcon.IsVisible = WindowState == WindowState.Maximized;
        }

        /// <summary>
        /// Обработчик попытки закрытия главного окна
        /// Проверяет несохранённые изменения в каждой вкладке и предлагает сохранить
        /// </summary>
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
                _logger.LogDebug("No tabs, showing welcome");
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
                    _logger.LogError(ex, "Error caching active tab");
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
                {
                    _logger.LogDebug("Tab {Title} - no changes", tab.Title);
                    continue;
                }

                _logger.LogDebug("Tab {Title} has unsaved changes", tab.Title);

                var result = await dialogService.ShowMessageAsync(
                    Strings.Dialog_UnsavedChanges_Title,
                    $"{Strings.Dialog_UnsavedChanges_Document} \"{tab.Title}\" {Strings.Dialog_UnsavedChanges_HasUnsaved}\n\n{Strings.Dialog_UnsavedChanges_Message}",
                    MessageBoxType.Question,
                    MessageBoxButtons.YesNoCancel
                );

                _logger.LogDebug("User choice for {Title}: {Result}", tab.Title, result);

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
                    _logger.LogDebug("Tab saved: {Title}", tab.Title);
                }
                else if (result == MessageBoxResult.No)
                {
                    if (!string.IsNullOrEmpty(tab.FilePath))
                    {
                        var cacheService = App.Services.GetRequiredService<IZipCacheService>();
                        cacheService.DeleteCache(tab.FilePath);
                        _logger.LogDebug("Cache deleted for: {Title}", tab.Title);
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

        /// <summary>
        /// Обработчик нажатия клавиш
        /// Определяет moduleType модуля в фокусе и передаёт в HotKeyService
        /// </summary>
        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            var hotKeyService = App.Services.GetRequiredService<IHotKeyService>();
            var gesture = new KeyGesture(e.Key, e.KeyModifiers);

            var focusedModuleType = GetFocusedModuleType();

            if (hotKeyService.HandleKeyPress(gesture, focusedModuleType))
                e.Handled = true;
        }

        /// <summary>
        /// Определить moduleType модуля который сейчас в фокусе
        /// Поднимается по дереву фокуса до Control у которого Tag содержит moduleType
        /// </summary>
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