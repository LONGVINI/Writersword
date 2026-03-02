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
using Writersword.Src.Core.Interfaces.Services.Input;
using Writersword.ViewModels.Components;

namespace Writersword.Views.Components
{
    public partial class MenuBarView : UserControl
    {
        private readonly ILogger<MenuBarView> _logger;
        private readonly IHotKeyService _hotKeyService;
        private WrapPanel? _wrapPanel;

        public MenuBarView()
        {
            _logger = App.Services.GetService<ILogger<MenuBarView>>()!;
            _hotKeyService = App.Services.GetRequiredService<IHotKeyService>();
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            _hotKeyService.HotKeysChanged += OnHotKeysChanged;

            foreach (var item in MainMenu.Items)
            {
                if (item is MenuItem topLevel)
                    topLevel.SubmenuOpened += OnTopLevelMenuOpened;
            }

            MainMenu.TemplateApplied += OnMenuTemplateApplied;

            _logger.LogDebug("MenuBarView created");
        }

        private void OnMenuTemplateApplied(object? sender, TemplateAppliedEventArgs e)
        {
            var itemsPresenter = e.NameScope.Find<ItemsPresenter>("PART_ItemsPresenter");
            if (itemsPresenter == null) return;

            itemsPresenter.Loaded += (_, _) =>
            {
                _wrapPanel = itemsPresenter.Panel as WrapPanel;
                _logger.LogDebug("WrapPanel found: {Found}", _wrapPanel != null);

                if (_wrapPanel != null)
                {
                    // Invalidate только при изменении высоты (перенос строк).
                    // Изменение ширины не трогаем — цикла нет.
                    _wrapPanel.SizeChanged += (_, ev) =>
                    {
                        if (Math.Abs(ev.NewSize.Height - ev.PreviousSize.Height) > 0.5)
                        {
                            _logger.LogDebug("WrapPanel height changed: {Old} -> {New}", ev.PreviousSize.Height, ev.NewSize.Height);
                            InvalidateMeasure();
                        }
                    };
                }
            };
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var result = base.MeasureOverride(availableSize);

            if (_wrapPanel != null && _wrapPanel.Bounds.Height > 0)
            {
                var overhead = MainMenu.Padding.Top + MainMenu.Padding.Bottom;
                var h = _wrapPanel.Bounds.Height + overhead;
                _logger.LogDebug("MeasureOverride: {W}x{H}", availableSize.Width, h);
                return new Size(result.Width, h);
            }

            _logger.LogDebug("MeasureOverride: {W}x{H} (base)", availableSize.Width, result.Height);
            return result;
        }

        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            if (DataContext is MenuBarViewModel vm)
            {
                vm.OpenRecentProjectCommand.Subscribe(_ =>
                {
                    Dispatcher.UIThread.Post(() => MainMenu.Close());
                });
            }

            UpdateAllGestures();
        }

        private void OnHotKeysChanged()
        {
            Dispatcher.UIThread.Post(UpdateAllGestures);
        }

        private void OnTopLevelMenuOpened(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (sender is not MenuItem topLevel) return;

            Dispatcher.UIThread.Post(() =>
            {
                AlignGestureText(topLevel);
            }, DispatcherPriority.Render);
        }

        private void AlignGestureText(MenuItem parent)
        {
            foreach (var item in parent.Items)
            {
                if (item is not MenuItem mi) continue;

                var layoutRoot = mi.GetTemplateChildren()
                    .OfType<Border>()
                    .FirstOrDefault(b => b.Name == "PART_LayoutRoot");

                if (layoutRoot?.Child is not Grid grid) continue;

                var gestureText = mi.GetTemplateChildren()
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

        private void UpdateAllGestures()
        {
            UpdateGesturesRecursive(MainMenu);
        }

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
                    {
                        try { menuItem.InputGesture = KeyGesture.Parse(gestureStr); }
                        catch { }
                    }
                }

                if (menuItem.Items.Count > 0)
                    UpdateGesturesRecursive(menuItem);
            }
        }

        private string BuildGestureString(string hotKeyId)
        {
            var hotKey = _hotKeyService.GetHotKey(hotKeyId);
            if (hotKey == null || hotKey.ActiveGestures.Count == 0)
                return string.Empty;

            var first = hotKey.ActiveGestures[0];
            return first.IsSingle ? first.FirstStep.ToString() : string.Join(" -> ", first.Steps);
        }
    }
}