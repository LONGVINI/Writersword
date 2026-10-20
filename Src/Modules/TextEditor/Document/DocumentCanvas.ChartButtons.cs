using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using SkiaSharp;
using Writersword.Infrastructure.Behaviours;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Views.Dialogs;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Три кнопки сбоку от выделенной диаграммы, как у Word: «+» — элементы диаграммы
    /// флажками, кисть — стили и цвета, воронка — фильтр рядов и категорий.
    ///
    /// Кнопки стоят столбиком справа от верхнего правого угла, снаружи рамки; панель
    /// каждой открывается у указателя и правит диаграмму сразу, шагом отмены на
    /// каждое изменение.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        private const float ChartSideSizePt = 15f;
        private const float ChartSideSpacingPt = 4f;
        private const float ChartSideGapPt = ImageHandleHalfPt + 3f;

        private const string ChartSidePlusIcon = "M19 13h-6v6h-2v-6H5v-2h6V5h2v6h6v2z";
        private const string ChartSideBrushIcon = "M7 14c-1.66 0-3 1.34-3 3 0 1.31-1.16 2-2 2 .92 1.22 2.49 2 4 2 2.21 0 4-1.79 4-4 0-1.66-1.34-3-3-3zm13.71-9.37l-1.34-1.34a.996.996 0 0 0-1.41 0L9 12.25 11.75 15l8.96-8.96a.996.996 0 0 0 0-1.41z";
        private const string ChartSideFilterIcon = "M4.25 5.61C6.27 8.2 10 13 10 13v6c0 .55.45 1 1 1h2c.55 0 1-.45 1-1v-6s3.72-4.8 5.74-7.39A.998.998 0 0 0 18.95 4H5.04c-.83 0-1.3.95-.79 1.61z";

        private const string ChartSideGearIcon = "M19.14 12.94a7.07 7.07 0 0 0 0-1.88l2.03-1.58a.5.5 0 0 0 .12-.64l-1.92-3.32a.5.5 0 0 0-.61-.22l-2.39.96a7.03 7.03 0 0 0-1.62-.94l-.36-2.54a.5.5 0 0 0-.5-.42h-3.84a.5.5 0 0 0-.5.42l-.36 2.54c-.59.24-1.13.56-1.62.94l-2.39-.96a.5.5 0 0 0-.61.22L2.71 8.84a.5.5 0 0 0 .12.64l2.03 1.58a7.07 7.07 0 0 0 0 1.88l-2.03 1.58a.5.5 0 0 0-.12.64l1.92 3.32c.13.22.39.31.61.22l2.39-.96c.49.38 1.03.7 1.62.94l.36 2.54c.05.24.25.42.5.42h3.84c.25 0 .45-.18.5-.42l.36-2.54c.59-.24 1.13-.56 1.62-.94l2.39.96c.22.09.48 0 .61-.22l1.92-3.32a.5.5 0 0 0-.12-.64l-2.03-1.58zM12 15.5a3.5 3.5 0 1 1 0-7 3.5 3.5 0 0 1 0 7z";

        private static readonly string[] ChartSideIcons = { ChartSidePlusIcon, ChartSideBrushIcon, ChartSideFilterIcon };

        /// <summary>Прямоугольник боковой кнопки диаграммы index (0 — «+», 1 — кисть, 2 — фильтр) в координатах картинки до поворота.</summary>
        private static SKRect ChartSideButtonRect(float right, float top, int index)
        {
            float y = top + index * (ChartSideSizePt + ChartSideSpacingPt);
            return new SKRect(right + ChartSideGapPt, y, right + ChartSideGapPt + ChartSideSizePt, y + ChartSideSizePt);
        }

        /// <summary>Какая боковая кнопка выделенной диаграммы под точкой: 0–2 или -1.</summary>
        private int HitTestChartSideButton(float xPt, float yPt)
        {
            if (_selectedImage is not { IsChart: true } || IsEditingBlocked || _imageCropMode) return -1;

            float pageShiftXPt = GetPageShiftXPt();
            foreach (var ie in _images)
            {
                if (!ReferenceEquals(ie.Block, _selectedImage)) continue;

                float left = ie.XPt + pageShiftXPt;
                float right = left + ie.WidthPt;
                float top = ie.Ypt;
                float cx = (left + right) / 2f;
                float cy = top + ie.HeightPt / 2f;
                var (lx, ly) = RotatePointAround(xPt, yPt, cx, cy, -(float)ie.Block.RotationDeg);
                for (int i = 0; i < ChartSideIcons.Length; i++)
                {
                    var rect = ChartSideButtonRect(right, top, i);
                    rect.Inflate(1.5f, 1.5f);
                    if (rect.Contains(lx, ly)) return i;
                }
                return -1;
            }
            return -1;
        }

        /// <summary>Боковые кнопки: белые квадратики в цвет рамки выделения, со значками.</summary>
        private void DrawChartSideButtons(SKCanvas canvas, float right, float top)
        {
            using var lines = new SKPaint
            {
                Color = _paintImageSelection.Color,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1f,
                IsAntialias = true
            };
            using var ink = new SKPaint { Color = _paintImageSelection.Color, Style = SKPaintStyle.Fill, IsAntialias = true };

            for (int i = 0; i < ChartSideIcons.Length; i++)
            {
                var rect = ChartSideButtonRect(right, top, i);
                canvas.DrawRoundRect(rect, 2f, 2f, _paintImageHandleFill);
                canvas.DrawRoundRect(rect, 2f, 2f, lines);

                using var icon = SKPath.ParseSvgPathData(ChartSideIcons[i]);
                if (icon is null) continue;
                float inner = ChartSideSizePt - 4f;
                canvas.Save();
                canvas.Translate(rect.Left + 2f, rect.Top + 2f);
                canvas.Scale(inner / 24f);
                canvas.DrawPath(icon, ink);
                canvas.Restore();
            }
        }

        // ── Панели ────────────────────────────────────────────────────────

        /// <summary>Открывает у указателя панель боковой кнопки index для диаграммы chartImage.</summary>
        private void ShowChartSidePanel(ImageBlock chartImage, int index)
        {
            if (DocVm is null || !ReferenceEquals(_selectedImage, chartImage) || chartImage.Chart is null) return;

            var flyout = new Flyout { Placement = PlacementMode.Pointer, ShowMode = FlyoutShowMode.Standard };
            // Панель держит фокус сама: после пересборки флажков фокус не теряется, и
            // Ctrl+Z из панели доходит до её обработчика.
            // Курсор свой: всплывашка наследует его от листа, а там текстовая каретка.
            var host = new Border { Padding = new Thickness(10, 8), MinWidth = 230, Focusable = true, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Arrow) };
            flyout.Content = host;

            void Rebuild()
            {
                if (chartImage.Chart is not { } chart) { flyout.Hide(); return; }
                host.Child = index switch
                {
                    0 => ElementsPanel(chart, Rebuild, flyout),
                    1 => StylePanel(chart, flyout),
                    _ => FilterPanel(chart, flyout)
                };
                if (flyout.IsOpen) host.Focus();
            }

            Rebuild();

            // Отмена и повтор прямо из панели: панель — всплывающее окно, и клавиши
            // из него до листа сами не доходят. Панель после отмены показывает
            // вернувшееся состояние.
            host.AddHandler(KeyDownEvent, (_, e) =>
            {
                bool ctrl = (e.KeyModifiers & Avalonia.Input.KeyModifiers.Control) != 0;
                bool shift = (e.KeyModifiers & Avalonia.Input.KeyModifiers.Shift) != 0;
                if (!ctrl || e.Source is TextBox) return;

                if (e.Key == Avalonia.Input.Key.Z && !shift) { DocVm?.Undo(); e.Handled = true; }
                else if (e.Key == Avalonia.Input.Key.Y || (e.Key == Avalonia.Input.Key.Z && shift)) { DocVm?.Redo(); e.Handled = true; }
                else return;

                if (index == 0) Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild, Avalonia.Threading.DispatcherPriority.Background);
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

            // Панель закрылась — клавиатура снова у листа.
            // Окно диаграммы, открытое из панели, фокус оставляет себе.
            flyout.Closed += (_, _) => ClearChartPreview();
            flyout.Closed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (ChartEditorOverlay.FindHost() is { IsVisible: true }) return;
                Focus();
            }, Avalonia.Threading.DispatcherPriority.Input);

            flyout.ShowAt(this, true);
        }

        /// <summary>Правит выделенную диаграмму: копия, правка, один шаг отмены.</summary>
        private void ApplyChartChange(string description, Action<ChartModel> change)
        {
            if (DocVm?.GetSelectedChart() is not { } chart) return;
            var next = chart.Clone();
            change(next);
            DocVm.ApplySelectedChart(next, description);
        }

        private static TextBlock PanelTitle(string text, Control owner)
        {
            var title = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
            title.Bind(TextBlock.ForegroundProperty, owner.GetResourceObservable("TextPrimaryBrush"));
            return title;
        }

        /// <summary>«Элементы диаграммы»: флажки, как у кнопки «+» в Word.</summary>
        private Control ElementsPanel(ChartModel chart, Action rebuild, Flyout flyout)
        {
            var panel = new StackPanel { Spacing = 2 };
            panel.Children.Add(PanelTitle("Элементы диаграммы", this));

            void Item(string text, bool isChecked, bool visible, string description, Action<ChartModel, bool> change)
            {
                if (!visible) return;
                var check = new CheckBox { Content = text, IsChecked = isChecked, FontSize = 12 };
                check.IsCheckedChanged += (_, _) =>
                {
                    bool on = check.IsChecked == true;
                    ApplyChartChange(description, c => change(c, on));
                    rebuild();
                };
                panel.Children.Add(check);
            }

            bool axes = chart.HasAxes;
            var analytics = chart.Series.Where(s => ChartModel.KindHasTrendline(chart.KindOf(s))).ToList();

            Item("Оси", axes && !chart.HideCategoryAxis && !chart.HideValueAxis, axes, "Оси диаграммы", (c, on) =>
            {
                c.HideCategoryAxis = !on;
                c.HideValueAxis = !on;
            });
            Item("Названия осей", !string.IsNullOrWhiteSpace(chart.CategoryAxisTitle) || !string.IsNullOrWhiteSpace(chart.ValueAxisTitle),
                axes && chart.Kind != ChartKind.Radar, "Названия осей", (c, on) =>
                {
                    c.CategoryAxisTitle = on ? (string.IsNullOrWhiteSpace(c.CategoryAxisTitle) ? "Название оси" : c.CategoryAxisTitle) : null;
                    c.ValueAxisTitle = on ? (string.IsNullOrWhiteSpace(c.ValueAxisTitle) ? "Название оси" : c.ValueAxisTitle) : null;
                });
            Item("Название диаграммы", chart.ShowTitle, true, "Название диаграммы", (c, on) => c.ShowTitle = on);
            Item("Метки данных", chart.ShowDataLabels, true, "Метки данных", (c, on) => c.ShowDataLabels = on);
            Item("Таблица данных", chart.ShowDataTable, chart.CanShowDataTable, "Таблица данных", (c, on) => c.ShowDataTable = on);
            Item("Пределы погрешностей", analytics.Count > 0 && analytics.All(s => s.ErrorBars is not null), analytics.Count > 0,
                "Пределы погрешностей", (c, on) =>
                {
                    foreach (var series in c.Series)
                        if (ChartModel.KindHasTrendline(c.KindOf(series)))
                            series.ErrorBars = on ? series.ErrorBars ?? new ChartErrorBars { Type = ChartErrorBarType.StandardError } : null;
                });
            Item("Линии сетки", chart.ShowGridlines, axes, "Линии сетки", (c, on) => c.ShowGridlines = on);
            Item("Легенда", chart.Legend != ChartLegendPosition.None, true, "Легенда диаграммы",
                (c, on) => c.Legend = on ? ChartLegendPosition.Bottom : ChartLegendPosition.None);
            Item("Линия тренда", analytics.Count > 0 && analytics.All(s => s.Trendline is not null), analytics.Count > 0,
                "Линия тренда", (c, on) =>
                {
                    foreach (var series in c.Series)
                        if (ChartModel.KindHasTrendline(c.KindOf(series)))
                            series.Trendline = on ? series.Trendline ?? new ChartTrendline() : null;
                });
            Item("Полосы повышения и понижения", chart.ShowUpDownBars, chart.HasLineSeries && chart.Series.Count >= 2,
                "Полосы повышения и понижения", (c, on) => c.ShowUpDownBars = on);

            var more = new Button { Classes = { "ghost" }, Content = "Другие параметры…", FontSize = 12, Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(6, 3) };
            TooltipBehavior.SetTip(more, "Окно диаграммы: все настройки элементов, места подписей, сетки по отдельности, анализ");
            var chartImage = _selectedImage;
            more.Click += (_, _) =>
            {
                flyout.Hide();
                if (chartImage is not null) DocVm?.RequestChartEdit(chartImage);
            };
            panel.Children.Add(more);
            return panel;
        }

        /// <summary>«Стиль» и «Цвет»: галерея стилей с образцами и наборы цветов, как у кисти Word.</summary>
        private Control StylePanel(ChartModel chart, Flyout flyout)
        {
            var panel = new StackPanel { Spacing = 6 };
            var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            var styleTab = new ToggleButton { Classes = { ChartEditorOverlay.AccentToggleClass }, Content = "Стиль", IsChecked = true, FontSize = 12, Padding = new Thickness(10, 3) };
            var colorTab = new ToggleButton { Classes = { ChartEditorOverlay.AccentToggleClass }, Content = "Цвет", FontSize = 12, Padding = new Thickness(10, 3) };
            ChartEditorOverlay.AddAccentToggleStyles(panel.Styles);
            tabs.Children.Add(styleTab);
            tabs.Children.Add(colorTab);
            panel.Children.Add(tabs);

            var body = new ContentControl();
            panel.Children.Add(body);

            Control Styles()
            {
                var gallery = new ChartPresetGallery(chart, ChartPresets.Styles, 2, 108, ChartUserPresetSource.Styles);
                gallery.Hovered += preset =>
                {
                    if (preset is null) { SetChartPreview(null); return; }
                    if (DocVm?.GetSelectedChart() is { } current) SetChartPreview(preset.Apply(current));
                };
                gallery.Chosen += preset =>
                {
                    ClearChartPreview();
                    if (DocVm?.GetSelectedChart() is { } current)
                        DocVm.ApplySelectedChart(preset.Apply(current), "Стиль диаграммы: " + preset.Name);
                    flyout.Hide();
                };
                return new ScrollViewer
                {
                    Content = gallery,
                    MaxHeight = 380,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    AllowAutoHide = false,
                    Padding = new Thickness(0, 0, 14, 0)
                };
            }

            Control Colors()
            {
                var list = new StackPanel { Spacing = 1 };
                void Row(string name, IReadOnlyList<string> palette)
                {
                    var swatches = new StackPanel { Orientation = Orientation.Horizontal };
                    for (int i = 0; i < Math.Min(palette.Count, 10); i++)
                    {
                        swatches.Children.Add(new Border { Width = 11, Height = 13, Background = Writersword.Modules.TextEditor.Views.Dialogs.ChartEditorOverlay.SwatchBrush(palette[i]) });
                    }
                    var content = new StackPanel { Orientation = Orientation.Horizontal };
                    content.Children.Add(new Border { CornerRadius = new CornerRadius(3), ClipToBounds = true, Child = swatches });
                    content.Children.Add(new TextBlock { Text = name, FontSize = 12, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                    var button = new Button
                    {
                        Classes = { "ghost" },
                        Content = content,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                        Padding = new Thickness(6, 4)
                    };
                    var colors = palette;
                    button.PointerEntered += (_, _) =>
                    {
                        if (DocVm?.GetSelectedChart() is { } current) SetChartPreview(current.WithColors(colors));
                    };
                    button.PointerExited += (_, _) => SetChartPreview(null);
                    button.Click += (_, _) =>
                    {
                        ClearChartPreview();
                        if (DocVm?.GetSelectedChart() is { } current)
                            DocVm.ApplySelectedChart(current.WithColors(colors), "Цвета диаграммы");
                        flyout.Hide();
                    };
                    list.Children.Add(button);
                }

                void Section(string title)
                {
                    var label = new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeight.SemiBold, Margin = new Thickness(6, 6, 6, 2) };
                    label.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("TextMutedBrush"));
                    list.Children.Add(label);
                }

                // Свои наборы правятся в окне диаграммы: кнопки открывают его сразу на них,
                // как в меню «Цвета» на ленте.
                var chartImage = _selectedImage;
                void Action(string text, string icon, string tip, string description, ChartEditorStartAction start)
                {
                    var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    content.Children.Add(new PathIcon { Width = 13, Height = 13, Data = StreamGeometry.Parse(icon) });
                    content.Children.Add(new TextBlock { Text = text, FontSize = 12, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                    var button = new Button
                    {
                        Classes = { "ghost" },
                        Content = content,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                        Padding = new Thickness(6, 4)
                    };
                    TooltipBehavior.SetTip(button, tip);
                    TooltipBehavior.SetDescription(button, description);
                    button.Click += (_, _) =>
                    {
                        ClearChartPreview();
                        flyout.Hide();
                        if (chartImage is null || DocVm is null) return;
                        ChartEditorOverlay.PendingStart = start;
                        DocVm.RequestChartEdit(chartImage);
                    };
                    list.Children.Add(button);
                }

                Section("Готовые наборы");
                foreach (var (name, colors) in ChartModel.ColorSchemes) Row(name, colors);
                var own = Services.ChartColorSets.Load();
                if (own.Count > 0)
                {
                    Section("Свои наборы");
                    foreach (var set in own)
                        Row(string.IsNullOrWhiteSpace(set.Name) ? "Свой набор" : set.Name, set.Colors);
                }

                var divider = new Border { Height = 1, Margin = new Thickness(6, 4, 6, 3) };
                divider.Bind(Border.BackgroundProperty, this.GetResourceObservable("BorderSubtleBrush"));
                list.Children.Add(divider);
                Action("Новый набор из этой диаграммы", ChartSidePlusIcon, "Новый набор",
                    "Завести свой набор из нынешних цветов диаграммы и сразу открыть его настройку: имя, цвета, их порядок и число.",
                    ChartEditorStartAction.NewColorSet);
                if (own.Count > 0)
                    Action("Настроить свои наборы…", ChartSideGearIcon, "Настроить свои наборы",
                        "Окно своих наборов: имя, цвета по одному, их порядок и число, удаление.",
                        ChartEditorStartAction.ColorSets);

                return new ScrollViewer
                {
                    Content = list,
                    MaxHeight = 380,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    AllowAutoHide = false,
                    Padding = new Thickness(0, 0, 14, 0)
                };
            }

            body.Content = Styles();
            styleTab.Click += (_, _) => { styleTab.IsChecked = true; colorTab.IsChecked = false; body.Content = Styles(); };
            colorTab.Click += (_, _) => { colorTab.IsChecked = true; styleTab.IsChecked = false; body.Content = Colors(); };
            return panel;
        }

        /// <summary>
        /// Фильтр: флажки рядов и категорий (у круговой — долей). Снятый флажок прячет
        /// ряд или категорию с диаграммы, данные остаются в таблице. «Применить» —
        /// одним шагом отмены.
        /// </summary>
        private Control FilterPanel(ChartModel chart, Flyout flyout)
        {
            var panel = new StackPanel { Spacing = 2 };
            panel.Children.Add(PanelTitle("Фильтр", this));

            var seriesChecks = new List<CheckBox>();
            var categoryChecks = new List<CheckBox>();

            TextBlock Section(string text)
            {
                var label = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 2) };
                label.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("TextMutedBrush"));
                return label;
            }

            var lists = new StackPanel { Spacing = 1 };
            if (!chart.IsCircular)
            {
                lists.Children.Add(Section("Ряды"));
                for (int s = 0; s < chart.Series.Count; s++)
                {
                    string name = string.IsNullOrWhiteSpace(chart.Series[s].Name) ? "Ряд " + (s + 1) : chart.Series[s].Name;
                    var check = new CheckBox { Content = name, IsChecked = !chart.Series[s].Hidden, FontSize = 12 };
                    seriesChecks.Add(check);
                    lists.Children.Add(check);
                }
            }

            if (!chart.IsXY)
            {
                lists.Children.Add(Section(chart.IsCircular ? "Доли" : "Категории"));
                var hidden = new HashSet<int>(chart.HiddenCategories ?? new List<int>());
                for (int c = 0; c < chart.CategoryCount; c++)
                {
                    var check = new CheckBox { Content = chart.CategoryLabel(c), IsChecked = !hidden.Contains(c), FontSize = 12 };
                    categoryChecks.Add(check);
                    lists.Children.Add(check);
                }
            }

            panel.Children.Add(new ScrollViewer
            {
                Content = lists,
                MaxHeight = 320,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                AllowAutoHide = false,
                Padding = new Thickness(0, 0, 14, 0)
            });

            var apply = new Button { Classes = { "primary" }, Content = "Применить", FontSize = 12, Padding = new Thickness(12, 4) };
            apply.Click += (_, _) =>
            {
                // Хотя бы один ряд и одна категория остаются: пустой диаграммы не бывает.
                bool anySeries = seriesChecks.Count == 0 || seriesChecks.Any(c => c.IsChecked == true);
                bool anyCategory = categoryChecks.Count == 0 || categoryChecks.Any(c => c.IsChecked == true);
                if (!anySeries || !anyCategory) return;

                ApplyChartChange("Фильтр диаграммы", c =>
                {
                    for (int s = 0; s < seriesChecks.Count && s < c.Series.Count; s++)
                        c.Series[s].Hidden = seriesChecks[s].IsChecked != true;
                    var hiddenNow = new List<int>();
                    for (int i = 0; i < categoryChecks.Count; i++)
                        if (categoryChecks[i].IsChecked != true) hiddenNow.Add(i);
                    c.HiddenCategories = hiddenNow.Count > 0 ? hiddenNow : null;
                });
                flyout.Hide();
            };

            var chartImage = _selectedImage;
            var data = new Button { Classes = { "ghost" }, Content = "Выбрать данные…", FontSize = 12, Padding = new Thickness(8, 4) };
            TooltipBehavior.SetTip(data, "Окно диаграммы: таблица данных");
            data.Click += (_, _) =>
            {
                flyout.Hide();
                if (chartImage is not null) DocVm?.RequestChartEdit(chartImage);
            };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
            buttons.Children.Add(apply);
            buttons.Children.Add(data);
            panel.Children.Add(buttons);

            var note = new TextBlock
            {
                Text = "Снятый флажок прячет ряд или категорию с диаграммы; данные остаются в таблице. В .docx уходит видимое.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 240,
                Margin = new Thickness(0, 6, 0, 0)
            };
            note.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("TextMutedBrush"));
            panel.Children.Add(note);
            return panel;
        }
    }
}
