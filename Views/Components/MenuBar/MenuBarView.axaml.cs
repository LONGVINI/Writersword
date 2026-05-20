using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using Writersword.Core.Interfaces.Services.Input;
using Writersword.ViewModels.Components.MenuBar;

namespace Writersword.Views.Components.MenuBar
{
    public partial class MenuBarView : UserControl
    {
        private readonly IHotKeyService _hotKeyService;
        private WrapPanel? _wrapPanel;
        private double _baseWrapHeight = -1;

        public MenuBarView()
        {
            _hotKeyService = App.Services.GetRequiredService<IHotKeyService>();
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            _hotKeyService.HotKeysChanged += OnHotKeysChanged;

            foreach (var item in MainMenu.Items)
                if (item is MenuItem topLevel)
                    topLevel.SubmenuOpened += OnTopLevelMenuOpened;

            MainMenu.TemplateApplied += OnMenuTemplateApplied;
            SizeChanged += OnMenuBarViewSizeChanged;
        }

        // ── WrapPanel / resize ─────────────────────────────────────────────────

        private void OnMenuTemplateApplied(object? sender, TemplateAppliedEventArgs e)
        {
            var itemsPresenter = e.NameScope.Find<ItemsPresenter>("PART_ItemsPresenter");
            if (itemsPresenter == null) return;

            itemsPresenter.Loaded += (_, _) =>
            {
                _wrapPanel = itemsPresenter.Panel as WrapPanel;
            };
        }

        private void OnMenuBarViewSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (_wrapPanel == null || !e.WidthChanged) return;

            var availableWidth = e.NewSize.Width - MainMenu.Padding.Left - MainMenu.Padding.Right;
            if (availableWidth <= 0) return;

            _wrapPanel.Measure(new Size(availableWidth, double.PositiveInfinity));
            var desiredH = _wrapPanel.DesiredSize.Height;

            if (Math.Abs(desiredH - MainMenu.Bounds.Height) > 0.5)
            {
                MainMenu.Height = desiredH;
            }
        }

        private void OnWrapPanelLayoutUpdated(object? sender, EventArgs e)
        {
            if (_wrapPanel == null || _wrapPanel.Bounds.Width <= 0) return;

            _wrapPanel.Measure(new Size(_wrapPanel.Bounds.Width, double.PositiveInfinity));
            var desiredH = _wrapPanel.DesiredSize.Height;

            if (_baseWrapHeight < 0)
            {
                _baseWrapHeight = desiredH;
            }

            if (Math.Abs(desiredH - MainMenu.Height) > 0.5)
            {
                MainMenu.Height = desiredH;
            }
        }

        private void OnWrapPanelSizeChanged(object? sender, SizeChangedEventArgs e)
        {

            if (_baseWrapHeight < 0)
            {
                _baseWrapHeight = e.NewSize.Height;
                return;
            }

            if (Math.Abs(e.NewSize.Height - e.PreviousSize.Height) < 0.5) return;

            var extraRows = Math.Max(0, Math.Round((e.NewSize.Height - _baseWrapHeight) / _baseWrapHeight));
            var newMenuHeight = 32 + extraRows * 32;

            MainMenu.Height = newMenuHeight;
        }

        // ── DataContext / HotKeys ──────────────────────────────────────────────

        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            if (DataContext is MenuBarViewModel vm)
                vm.OpenRecentProjectCommand.Subscribe(_ =>
                    Dispatcher.UIThread.Post(() => MainMenu.Close()));

            UpdateAllGestures();
        }

        private void OnHotKeysChanged() =>
            Dispatcher.UIThread.Post(UpdateAllGestures);

        // ── Gesture alignment ─────────────────────────────────────────────────

        private void OnTopLevelMenuOpened(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (sender is not MenuItem topLevel) return;
            Dispatcher.UIThread.Post(() => AlignGestureText(topLevel), DispatcherPriority.Render);
        }

        private void AlignGestureText(MenuItem parent)
        {
            foreach (var item in parent.Items)
            {
                if (item is not MenuItem mi) continue;

                var layoutRoot = mi.GetTemplateDescendants()
                    .OfType<Border>()
                    .FirstOrDefault(b => b.Name == "PART_LayoutRoot");

                if (layoutRoot?.Child is not Grid grid) continue;

                var gestureText = mi.GetTemplateDescendants()
                    .OfType<TextBlock>()
                    .FirstOrDefault(t => t.Name == "PART_InputGestureText");

                if (gestureText == null) continue;

                gestureText.HorizontalAlignment = HorizontalAlignment.Right;

                if (mi.Items.Count == 0 && grid.ColumnDefinitions.Count > 4)
                {
                    grid.ColumnDefinitions[4].Width = GridLength.Auto;
                    grid.ColumnDefinitions[4].MinWidth = 0;
                    grid.ColumnDefinitions[4].MaxWidth = 0;
                    Grid.SetColumnSpan(gestureText, 2);
                }
            }
        }

        // ── UpdateGestures ────────────────────────────────────────────────────

        private void UpdateAllGestures() => UpdateGesturesRecursive(MainMenu);

        private void UpdateGesturesRecursive(ItemsControl parent)
        {
            foreach (var item in parent.Items)
            {
                if (item is not MenuItem menuItem) continue;

                if (!string.IsNullOrEmpty(menuItem.Name) &&
                    menuItem.Name.StartsWith("HotKey_", StringComparison.Ordinal))
                {
                    var gestureStr = BuildGestureString(menuItem.Name);
                    if (!string.IsNullOrEmpty(gestureStr))
                        try { menuItem.InputGesture = KeyGesture.Parse(gestureStr); } catch { }
                }

                if (menuItem.Items.Count > 0)
                    UpdateGesturesRecursive(menuItem);
            }
        }

        private string BuildGestureString(string hotKeyId)
        {
            var hotKey = _hotKeyService.GetHotKey(hotKeyId);
            if (hotKey == null || hotKey.ActiveGestures.Count == 0) return string.Empty;

            var first = hotKey.ActiveGestures[0];
            return first.IsSingle ? first.FirstStep.ToString() : string.Join(" -> ", first.Steps);
        }
    }
}