using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.ViewModels.Toolbar;
using Writersword.Styles.UserControls;

namespace Writersword.Modules.TextEditor.Views.Toolbar.Tabs
{
    public partial class RibbonChartTab : UserControl
    {
        private RibbonScrollContainer? _scrollContainer;
        private static readonly Serilog.ILogger _log = Serilog.Log.ForContext<RibbonChartTab>();

        public RibbonChartTab()
        {
            InitializeComponent();
            SizeChanged += OnSizeChanged;
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _scrollContainer = this.FindControl<RibbonScrollContainer>("ScrollContainer");

            if (_menuWired) return;
            _menuWired = true;

            // Закрылось меню или галерея — клавиатура снова у листа: без этого Ctrl+Z
            // уходил во всплывающее окно и отмена не срабатывала, пока не щёлкнешь в текст.
            // Пункт «Другие параметры…» открывает окно диаграммы — фокус тогда его.
            void ReturnFocus() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (Dialogs.ChartEditorOverlay.FindHost() is { IsVisible: true }) return;
                (DataContext as RibbonChartTabViewModel)?.FocusEditor();
            }, Avalonia.Threading.DispatcherPriority.Input);

            if (this.FindControl<Button>("AddElementButton")?.Flyout is MenuFlyout menu)
            {
                menu.Opening += (_, _) => BuildAddElementMenu(menu);
                menu.Opened += (_, _) => _log.Debug("[CHARTMENU] «Добавить элемент» открыто: пунктов {Count}", menu.Items.Count);
                menu.Closed += (_, _) =>
                {
                    _log.Debug("[CHARTMENU] «Добавить элемент» закрыто");
                    ReturnFocus();
                };
            }

            if (this.FindControl<Button>("QuickLayoutButton")?.Flyout is Flyout layouts)
            {
                layouts.Opening += (_, _) => BuildPresetFlyout(layouts, ChartPresets.Layouts, Dialogs.ChartUserPresetSource.Layouts, "Экспресс-макет");
                layouts.Opened += (_, _) => Widen(layouts);
                layouts.Closed += (_, _) => { (DataContext as RibbonChartTabViewModel)?.PreviewChart(null); ReturnFocus(); };
            }

            if (this.FindControl<Button>("StylesButton")?.Flyout is Flyout styles)
            {
                styles.Opening += (_, _) => BuildPresetFlyout(styles, ChartPresets.Styles, Dialogs.ChartUserPresetSource.Styles, "Стиль");
                styles.Opened += (_, _) => Widen(styles);
                styles.Closed += (_, _) => { (DataContext as RibbonChartTabViewModel)?.PreviewChart(null); ReturnFocus(); };
            }

            if (this.FindControl<Button>("ColorsButton")?.Flyout is Flyout colors)
            {
                colors.Opening += (_, _) => BuildColorsFlyout(colors);
                colors.Closed += (_, _) => { (DataContext as RibbonChartTabViewModel)?.PreviewChart(null); ReturnFocus(); };
            }
        }

        // ── Галереи стилей, макетов и цветов ─────────────────────────────

        /// <summary>
        /// Галерея образцов: уменьшенные копии выделенной диаграммы с каждым пресетом,
        /// ниже — свои образцы и плитка сохранения своего.
        /// </summary>
        private void BuildPresetFlyout(Flyout flyout, System.Collections.Generic.IReadOnlyList<ChartPresets.Preset> presets,
            Dialogs.ChartUserPresetSource user, string what)
        {
            if (DataContext is not RibbonChartTabViewModel vm || vm.SelectedChart is not { } chart)
            {
                // Пустое окошко всплывало серой полоской под кнопкой.
                flyout.Content = null;
                Avalonia.Threading.Dispatcher.UIThread.Post(flyout.Hide);
                return;
            }

            var gallery = new Dialogs.ChartPresetGallery(chart, presets, 4, 120, user);
            gallery.Hovered += preset => vm.PreviewChart(preset is null ? null : preset.Apply);
            gallery.Chosen += preset =>
            {
                vm.PreviewChart(null);
                flyout.Hide();
                vm.ApplyPreset(preset, what);
            };
            flyout.Content = new ScrollViewer
            {
                Content = gallery,
                MaxHeight = 540,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                AllowAutoHide = false,
                Padding = new Thickness(0, 0, 14, 0)
            };
            Widen(flyout);
        }

        /// <summary>
        /// Снимает с окошка галереи предельную ширину темы (456): четыре образца в ряд
        /// шире, и правый столбец обрезался. Окошко создаётся при первом показе, поэтому
        /// ширина снимается и при открытии, и после него (стиль chartGallery в разметке
        /// делает то же заранее).
        /// </summary>
        private static void Widen(Flyout flyout)
        {
            if ((flyout.Content as Control)?.Parent is FlyoutPresenter presenter)
                presenter.MaxWidth = double.PositiveInfinity;
        }

        /// <summary>Наборы цветов: готовые и свои (из окна диаграммы), строкой образцов с именем.</summary>
        private void BuildColorsFlyout(Flyout flyout)
        {
            if (DataContext is not RibbonChartTabViewModel vm || vm.SelectedChart is null)
            {
                flyout.Content = null;
                Avalonia.Threading.Dispatcher.UIThread.Post(flyout.Hide);
                return;
            }

            var list = new StackPanel { Spacing = 1, MinWidth = 260 };

            void Section(string title)
            {
                var label = new TextBlock { Text = title, FontSize = 11, FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(8, 6, 8, 2) };
                label.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("TextMutedBrush"));
                list.Children.Add(label);
            }

            void Row(string name, System.Collections.Generic.IReadOnlyList<string> palette)
            {
                var swatches = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
                int shown = Math.Min(palette.Count, 10);
                for (int i = 0; i < shown; i++)
                {
                    swatches.Children.Add(new Border { Width = 12, Height = 14, Background = Writersword.Modules.TextEditor.Views.Dialogs.ChartEditorOverlay.SwatchBrush(palette[i]) });
                }
                var text = new TextBlock { Text = name, FontSize = 12, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                var content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
                content.Children.Add(new Border { CornerRadius = new CornerRadius(3), ClipToBounds = true, Child = swatches });
                content.Children.Add(text);

                var button = new Button
                {
                    Classes = { "ghost" },
                    Content = content,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                    Padding = new Thickness(8, 5)
                };
                var colors = palette;
                button.PointerEntered += (_, _) => vm.PreviewChart(c => c.WithColors(colors));
                button.PointerExited += (_, _) => vm.PreviewChart(null);
                button.Click += (_, _) =>
                {
                    vm.PreviewChart(null);
                    flyout.Hide();
                    vm.ApplyColors(colors);
                };
                list.Children.Add(button);
            }

            Section("Готовые наборы");
            foreach (var (name, colors) in ChartModel.ColorSchemes) Row(name, colors);

            var own = Services.ChartColorSets.Load();
            if (own.Count > 0)
            {
                Section("Свои наборы");
                foreach (var set in own) Row(string.IsNullOrWhiteSpace(set.Name) ? "Свой набор" : set.Name, set.Colors);
            }

            // Свои наборы правятся в окне диаграммы: кнопки открывают его сразу на них.
            Button Action(string text, string icon, string tip, string description, Dialogs.ChartEditorStartAction start)
            {
                var content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
                content.Children.Add(new Avalonia.Controls.PathIcon { Width = 13, Height = 13, Data = Avalonia.Media.StreamGeometry.Parse(icon) });
                content.Children.Add(new TextBlock { Text = text, FontSize = 12, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                var button = new Button
                {
                    Classes = { "ghost" },
                    Content = content,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                    Padding = new Thickness(8, 5)
                };
                Writersword.Infrastructure.Behaviours.TooltipBehavior.SetTip(button, tip);
                Writersword.Infrastructure.Behaviours.TooltipBehavior.SetDescription(button, description);
                button.Click += (_, _) =>
                {
                    vm.PreviewChart(null);
                    flyout.Hide();
                    Dialogs.ChartEditorOverlay.PendingStart = start;
                    vm.EditDataCommand.Execute(null);
                };
                return button;
            }

            var divider = new Border { Height = 1, Margin = new Thickness(8, 4, 8, 3) };
            divider.Bind(Border.BackgroundProperty, this.GetResourceObservable("BorderSubtleBrush"));
            list.Children.Add(divider);
            list.Children.Add(Action("Новый набор из этой диаграммы", "M11 5h2v6h6v2h-6v6h-2v-6H5v-2h6z",
                "Новый набор", "Завести свой набор из нынешних цветов диаграммы и сразу открыть его настройку: имя, цвета, их порядок и число.",
                Dialogs.ChartEditorStartAction.NewColorSet));
            if (own.Count > 0)
            {
                list.Children.Add(Action("Настроить свои наборы…",
                    "M19.14 12.94a7.07 7.07 0 0 0 0-1.88l2.03-1.58a.5.5 0 0 0 .12-.64l-1.92-3.32a.5.5 0 0 0-.61-.22l-2.39.96a7.03 7.03 0 0 0-1.62-.94l-.36-2.54a.5.5 0 0 0-.5-.42h-3.84a.5.5 0 0 0-.5.42l-.36 2.54c-.59.24-1.13.56-1.62.94l-2.39-.96a.5.5 0 0 0-.61.22L2.71 8.84a.5.5 0 0 0 .12.64l2.03 1.58a7.07 7.07 0 0 0 0 1.88l-2.03 1.58a.5.5 0 0 0-.12.64l1.92 3.32c.13.22.39.31.61.22l2.39-.96c.49.38 1.03.7 1.62.94l.36 2.54c.05.24.25.42.5.42h3.84c.25 0 .45-.18.5-.42l.36-2.54c.59-.24 1.13-.56 1.62-.94l2.39.96c.22.09.48 0 .61-.22l1.92-3.32a.5.5 0 0 0-.12-.64l-2.03-1.58zM12 15.5a3.5 3.5 0 1 1 0-7 3.5 3.5 0 0 1 0 7z",
                    "Настроить свои наборы", "Окно своих наборов: имя, цвета по одному, их порядок и число, удаление.",
                    Dialogs.ChartEditorStartAction.ColorSets));
            }

            flyout.Content = new ScrollViewer
            {
                Content = list,
                MaxHeight = 460,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                AllowAutoHide = false,
                Padding = new Thickness(0, 0, 14, 0)
            };
        }

        private bool _menuWired;

        // ── «Добавить элемент» ────────────────────────────────────────────

        /// <summary>
        /// Меню «Добавить элемент», как у Word: оси, названия осей, название, метки
        /// данных, таблица данных, линии сетки и легенда. Собирается при каждом
        /// открытии — флажки и доступные пункты зависят от выделенной диаграммы.
        /// </summary>
        private void BuildAddElementMenu(MenuFlyout menu)
        {
            menu.Items.Clear();
            _log.Debug("[CHARTMENU] Сборка «Добавить элемент»: диаграмма выделена {Has}",
                (DataContext as RibbonChartTabViewModel)?.SelectedChart is not null);
            if (DataContext is not RibbonChartTabViewModel vm || vm.SelectedChart is not { } chart)
            {
                // Пустое меню всплывало серой полоской под кнопкой.
                Avalonia.Threading.Dispatcher.UIThread.Post(menu.Hide);
                return;
            }

            bool bar = chart.Kind == ChartKind.Bar;
            bool radar = chart.Kind == ChartKind.Radar;
            bool axes = chart.HasAxes;
            string categoryDirection = bar ? "вертикальная" : "горизонтальная";
            string valueDirection = bar ? "горизонтальная" : "вертикальная";

            MenuItem Check(string header, bool isChecked, string code, bool enabled = true)
            {
                var item = new MenuItem
                {
                    Header = header,
                    ToggleType = MenuItemToggleType.CheckBox,
                    IsChecked = isChecked,
                    IsEnabled = enabled
                };
                item.Click += (_, _) => vm.ApplyElement(code);
                return item;
            }

            MenuItem Radio(string header, bool isChecked, string code, bool enabled = true)
            {
                var item = new MenuItem
                {
                    Header = header,
                    ToggleType = MenuItemToggleType.Radio,
                    IsChecked = isChecked,
                    IsEnabled = enabled
                };
                item.Click += (_, _) => vm.ApplyElement(code);
                return item;
            }

            MenuItem Other(string header)
            {
                var item = new MenuItem { Header = header };
                item.Click += (_, _) => vm.EditDataCommand.Execute(null);
                return item;
            }

            MenuItem Sub(string header, bool enabled, params MenuItem[] items)
            {
                var sub = new MenuItem { Header = header, IsEnabled = enabled };
                foreach (var item in items) sub.Items.Add(item);
                return sub;
            }

            menu.Items.Add(Sub("Оси", axes,
                Check("Основная " + categoryDirection, !chart.HideCategoryAxis, "axis.cat"),
                Check("Основная " + valueDirection, !chart.HideValueAxis, "axis.val")));

            menu.Items.Add(Sub("Названия осей", axes && !radar,
                Check("Основная " + categoryDirection, !string.IsNullOrWhiteSpace(chart.CategoryAxisTitle), "atitle.cat"),
                Check("Основная " + valueDirection, !string.IsNullOrWhiteSpace(chart.ValueAxisTitle), "atitle.val")));

            menu.Items.Add(Sub("Название диаграммы", true,
                Radio("Нет", !chart.ShowTitle, "title.none"),
                Radio("Над диаграммой", chart.ShowTitle, "title.above")));

            var grouping = chart.HasGrouping ? chart.Grouping : ChartGrouping.Standard;
            var positions = ChartModel.LabelPositionsFor(chart.Kind, grouping);
            var current = chart.LabelPositionFor(chart.Kind);
            var labels = new System.Collections.Generic.List<MenuItem> { Radio("Нет", !chart.ShowDataLabels, "labels.none") };
            if (positions.Count == 0)
                labels.Add(Radio("Показать", chart.ShowDataLabels, "labels.Auto"));
            else
                foreach (var position in positions)
                    labels.Add(Radio(ChartModel.LabelPositionName(position),
                        chart.ShowDataLabels && (current == position || (current == ChartLabelPosition.Auto && position == positions.FirstOrDefault(p => p == DefaultPositionOf(chart)))),
                        "labels." + position));
            menu.Items.Add(Sub("Метки данных", true, labels.ToArray()));

            menu.Items.Add(Sub("Таблица данных", chart.CanShowDataTable,
                Radio("Нет", !chart.ShowDataTable, "table.none"),
                Radio("С ключами легенды", chart.ShowDataTable && !chart.HideDataTableKeys, "table.keys"),
                Radio("Без ключей легенды", chart.ShowDataTable && chart.HideDataTableKeys, "table.nokeys")));

            string valueLines = bar ? "вертикальные" : "горизонтальные";
            string categoryLines = bar ? "горизонтальные" : "вертикальные";
            menu.Items.Add(Sub("Линии сетки", axes,
                Check(radar ? "Основные" : "Основные " + valueLines, chart.ShowGridlines, "grid.major"),
                Check("Основные " + categoryLines, chart.ShowCategoryGridlines, "grid.catmajor", !radar),
                Check("Вспомогательные " + valueLines, chart.ShowMinorGridlines, "grid.minor", !radar),
                Check("Вспомогательные " + categoryLines, chart.ShowCategoryMinorGridlines, "grid.catminor", !radar)));

            // Аналитика — как у Word: ко всем рядам, которые её допускают; тонкая
            // настройка по рядам — в окне диаграммы, карточка «Анализ».
            bool analytics = chart.CanHaveAnalytics;
            var withAnalytics = chart.Series.Where(s => ChartModel.KindHasTrendline(chart.KindOf(s))).ToList();
            ChartErrorBarType? errorType = withAnalytics.Count > 0 && withAnalytics.All(s => s.ErrorBars is not null)
                && withAnalytics.Select(s => s.ErrorBars!.Type).Distinct().Count() == 1 ? withAnalytics[0].ErrorBars!.Type : null;
            bool noErrors = withAnalytics.All(s => s.ErrorBars is null);
            menu.Items.Add(Sub("Пределы погрешностей", analytics,
                Radio("Нет", noErrors, "err.none"),
                Radio("Стандартная погрешность", errorType == ChartErrorBarType.StandardError, "err.StandardError"),
                Radio("Процент", errorType == ChartErrorBarType.Percentage, "err.Percentage"),
                Radio("Стандартное отклонение", errorType == ChartErrorBarType.StandardDeviation, "err.StandardDeviation"),
                Other("Другие параметры…")));

            ChartTrendline? trend = withAnalytics.Count > 0 && withAnalytics.All(s => s.Trendline is not null) ? withAnalytics[0].Trendline : null;
            bool noTrend = withAnalytics.All(s => s.Trendline is null);
            menu.Items.Add(Sub("Линия тренда", analytics,
                Radio("Нет", noTrend, "trend.none"),
                Radio("Линейная", trend is { Type: ChartTrendlineType.Linear, Forward: 0 }, "trend.Linear"),
                Radio("Экспоненциальная", trend?.Type == ChartTrendlineType.Exponential, "trend.Exponential"),
                Radio("Линейный прогноз", trend is { Type: ChartTrendlineType.Linear } && trend.Forward > 0, "trend.forecast"),
                Radio("Скользящее среднее", trend?.Type == ChartTrendlineType.MovingAverage, "trend.MovingAverage"),
                Other("Другие параметры…")));

            bool lines = chart.HasLineSeries || chart.Kind == ChartKind.Area;
            menu.Items.Add(Sub("Линии", lines,
                Radio("Нет", !chart.ShowDropLines && !chart.ShowHighLowLines, "lines.none"),
                Check("Линии проекции", chart.ShowDropLines, "lines.drop"),
                Check("Линии максимума и минимума", chart.ShowHighLowLines, "lines.hilow", chart.HasLineSeries && chart.Series.Count >= 2)));

            menu.Items.Add(Sub("Полосы повышения и понижения", chart.HasLineSeries && chart.Series.Count >= 2,
                Radio("Нет", !chart.ShowUpDownBars, "updown.none"),
                Radio("Полосы повышения и понижения", chart.ShowUpDownBars, "updown.show")));

            menu.Items.Add(Sub("Легенда", true,
                Radio("Нет", chart.Legend == ChartLegendPosition.None, "legend.None"),
                Radio("Справа", chart.Legend == ChartLegendPosition.Right, "legend.Right"),
                Radio("Сверху", chart.Legend == ChartLegendPosition.Top, "legend.Top"),
                Radio("Слева", chart.Legend == ChartLegendPosition.Left, "legend.Left"),
                Radio("Снизу", chart.Legend == ChartLegendPosition.Bottom, "legend.Bottom")));
        }

        /// <summary>Место подписей, которое диаграмма рисует, когда своё не задано.</summary>
        private static ChartLabelPosition DefaultPositionOf(ChartModel chart) => chart.Kind switch
        {
            ChartKind.Column or ChartKind.Bar => chart.HasGrouping && chart.Grouping != ChartGrouping.Standard
                ? ChartLabelPosition.Center
                : ChartLabelPosition.OutsideEnd,
            ChartKind.Line or ChartKind.Scatter => ChartLabelPosition.Above,
            ChartKind.Bubble => ChartLabelPosition.Center,
            ChartKind.Pie => ChartLabelPosition.BestFit,
            _ => ChartLabelPosition.Auto
        };

        // Группы вкладки не сворачиваются: что не влезло по ширине, листается стрелками.
        private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (_scrollContainer is null) return;
            _scrollContainer.ArrowsVisible = true;
            _scrollContainer.NotifySizeChanged();
        }
    }
}
