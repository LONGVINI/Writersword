using System;
using System.Globalization;
using System.Windows.Input;
using ReactiveUI;
using Writersword.Modules.TextEditor.Contracts;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.ViewModels.Toolbar
{
    /// <summary>
    /// Вкладка «Диаграмма»: вид, группировка рядов, элементы (название, легенда,
    /// подписи, сетка, рамка, названия осей), наборы цветов, обмен строк и столбцов и
    /// окно данных. Видна, пока выделена диаграмма.
    ///
    /// Каждая кнопка собирает новую модель диаграммы из выделенной и отдаёт её
    /// документу одним шагом отмены: модель после создания не меняется, так что
    /// кэш нарисованных диаграмм узнаёт правку сам.
    /// </summary>
    public sealed class RibbonChartTabViewModel : ReactiveObject
    {
        private readonly ITextEditorCommandTarget _target;

        private ChartKind _currentKind;
        private ChartGrouping _currentGrouping;
        private ChartLegendPosition _currentLegend;
        private bool _showTitle;
        private bool _showDataLabels;
        private bool _showGridlines;
        private bool _showBorder;
        private bool _showAxisTitles;
        private bool _hasGrouping;
        private bool _hasAxes;
        private bool _canSwitchRowColumn;

        public RibbonChartTabViewModel(ITextEditorCommandTarget target)
        {
            _target = target;

            EditDataCommand = ReactiveCommand.Create(() => _target.EditSelectedChart());
            SwitchRowColumnCommand = ReactiveCommand.Create(() => Apply("Строка/столбец диаграммы", c => c.Transposed()));

            SetKindCommand = ReactiveCommand.Create<string?>(name =>
            {
                if (!Enum.TryParse<ChartKind>(name, out var kind)) return;
                Apply("Вид диаграммы", c => c.WithKind(kind));
            });

            SetGroupingCommand = ReactiveCommand.Create<string?>(name =>
            {
                if (!Enum.TryParse<ChartGrouping>(name, out var grouping)) return;
                Apply("Группировка рядов диаграммы", c =>
                {
                    var next = c.Clone();
                    if (!next.HasGrouping) return next;
                    next.Grouping = grouping;
                    if (next.Kind is ChartKind.Column or ChartKind.Bar)
                    {
                        var defaults = ChartModel.CreateDefault(next.Kind);
                        next.GapWidthPercent = grouping == ChartGrouping.Standard ? defaults.GapWidthPercent : 150;
                        next.OverlapPercent = grouping == ChartGrouping.Standard ? defaults.OverlapPercent : 100;
                    }
                    return next;
                });
            });

            SetLegendCommand = ReactiveCommand.Create<string?>(name =>
            {
                if (!Enum.TryParse<ChartLegendPosition>(name, out var position)) return;
                Apply("Легенда диаграммы", c =>
                {
                    var next = c.Clone();
                    next.Legend = position;
                    return next;
                });
            });

            SetColorsCommand = ReactiveCommand.Create<string?>(index =>
            {
                if (!int.TryParse(index, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) return;
                if (i < 0 || i >= ChartModel.ColorSchemes.Count) return;
                var colors = ChartModel.ColorSchemes[i].Colors;
                Apply("Цвета диаграммы", c => c.WithColors(colors));
            });

            ToggleTitleCommand = ReactiveCommand.Create(() => Apply("Название диаграммы", c =>
            {
                var next = c.Clone();
                next.ShowTitle = !next.ShowTitle;
                return next;
            }));

            ToggleDataLabelsCommand = ReactiveCommand.Create(() => Apply("Подписи данных", c =>
            {
                var next = c.Clone();
                next.ShowDataLabels = !next.ShowDataLabels;
                return next;
            }));

            ToggleGridlinesCommand = ReactiveCommand.Create(() => Apply("Сетка диаграммы", c =>
            {
                var next = c.Clone();
                next.ShowGridlines = !next.ShowGridlines;
                return next;
            }));

            ToggleBorderCommand = ReactiveCommand.Create(() => Apply("Рамка диаграммы", c =>
            {
                var next = c.Clone();
                next.ShowBorder = !next.ShowBorder;
                return next;
            }));

            // Названия осей: у диаграммы без названий ставятся заготовки, как «Название
            // оси» у Word; поправить их текст — в окне диаграммы. Нажатие при
            // названиях убирает оба.
            ToggleAxisTitlesCommand = ReactiveCommand.Create(() => Apply("Названия осей", c =>
            {
                var next = c.Clone();
                bool any = !string.IsNullOrWhiteSpace(next.CategoryAxisTitle) || !string.IsNullOrWhiteSpace(next.ValueAxisTitle);
                if (any)
                {
                    next.CategoryAxisTitle = null;
                    next.ValueAxisTitle = null;
                }
                else
                {
                    next.CategoryAxisTitle = "Название оси";
                    next.ValueAxisTitle = "Название оси";
                }
                return next;
            }));
        }

        public ICommand EditDataCommand { get; }
        public ICommand SwitchRowColumnCommand { get; }
        public ICommand SetKindCommand { get; }
        public ICommand SetGroupingCommand { get; }
        public ICommand SetLegendCommand { get; }
        public ICommand SetColorsCommand { get; }
        public ICommand ToggleTitleCommand { get; }
        public ICommand ToggleDataLabelsCommand { get; }
        public ICommand ToggleGridlinesCommand { get; }
        public ICommand ToggleBorderCommand { get; }
        public ICommand ToggleAxisTitlesCommand { get; }

        public ChartKind CurrentKind
        {
            get => _currentKind;
            private set => this.RaiseAndSetIfChanged(ref _currentKind, value);
        }

        public ChartGrouping CurrentGrouping
        {
            get => _currentGrouping;
            private set => this.RaiseAndSetIfChanged(ref _currentGrouping, value);
        }

        public ChartLegendPosition CurrentLegend
        {
            get => _currentLegend;
            private set => this.RaiseAndSetIfChanged(ref _currentLegend, value);
        }

        public bool ShowTitle
        {
            get => _showTitle;
            private set => this.RaiseAndSetIfChanged(ref _showTitle, value);
        }

        public bool ShowDataLabels
        {
            get => _showDataLabels;
            private set => this.RaiseAndSetIfChanged(ref _showDataLabels, value);
        }

        public bool ShowGridlines
        {
            get => _showGridlines;
            private set => this.RaiseAndSetIfChanged(ref _showGridlines, value);
        }

        public bool ShowBorder
        {
            get => _showBorder;
            private set => this.RaiseAndSetIfChanged(ref _showBorder, value);
        }

        public bool ShowAxisTitles
        {
            get => _showAxisTitles;
            private set => this.RaiseAndSetIfChanged(ref _showAxisTitles, value);
        }

        /// <summary>У вида есть группировка: рядом, с накоплением, нормированная.</summary>
        public bool HasGrouping
        {
            get => _hasGrouping;
            private set => this.RaiseAndSetIfChanged(ref _hasGrouping, value);
        }

        /// <summary>У вида есть оси: сетка и названия осей имеют смысл.</summary>
        public bool HasAxes
        {
            get => _hasAxes;
            private set => this.RaiseAndSetIfChanged(ref _hasAxes, value);
        }

        /// <summary>Строки и столбцы можно поменять: у точечной и пузырьковой категорий нет.</summary>
        public bool CanSwitchRowColumn
        {
            get => _canSwitchRowColumn;
            private set => this.RaiseAndSetIfChanged(ref _canSwitchRowColumn, value);
        }

        /// <summary>Стиль или экспресс-макет из галереи: одна правка — один шаг отмены.</summary>
        public void ApplyPreset(ChartPresets.Preset preset, string what)
            => Apply(what + " диаграммы: " + preset.Name, preset.Apply);

        /// <summary>Набор цветов: готовый или свой.</summary>
        public void ApplyColors(System.Collections.Generic.IReadOnlyList<string> colors)
            => Apply("Цвета диаграммы", c => c.WithColors(colors));

        /// <summary>Возвращает клавиатуру листу — после закрытия меню и галерей ленты.</summary>
        public void FocusEditor() => _target.FocusEditor();

        /// <summary>Выделенная диаграмма: по ней меню «Добавить элемент» ставит флажки.</summary>
        public ChartModel? SelectedChart => _target.GetSelectedChart();

        /// <summary>
        /// Пункт меню «Добавить элемент»: код вида «что.как» — оси, названия осей,
        /// название, метки данных, таблица данных, линии сетки. Одна правка — один шаг
        /// отмены.
        /// </summary>
        public void ApplyElement(string code)
        {
            if (string.IsNullOrEmpty(code)) return;
            int dot = code.IndexOf('.');
            string group = dot < 0 ? code : code[..dot];
            string value = dot < 0 ? string.Empty : code[(dot + 1)..];

            switch (group)
            {
                case "axis":
                    Apply("Оси диаграммы", c =>
                    {
                        var next = c.Clone();
                        if (value == "cat") next.HideCategoryAxis = !next.HideCategoryAxis;
                        else next.HideValueAxis = !next.HideValueAxis;
                        return next;
                    });
                    break;

                case "atitle":
                    Apply("Названия осей", c =>
                    {
                        var next = c.Clone();
                        if (value == "cat")
                            next.CategoryAxisTitle = string.IsNullOrWhiteSpace(next.CategoryAxisTitle) ? "Название оси" : null;
                        else
                            next.ValueAxisTitle = string.IsNullOrWhiteSpace(next.ValueAxisTitle) ? "Название оси" : null;
                        return next;
                    });
                    break;

                case "title":
                    Apply("Название диаграммы", c =>
                    {
                        var next = c.Clone();
                        next.ShowTitle = value != "none";
                        return next;
                    });
                    break;

                case "labels":
                    Apply("Метки данных", c =>
                    {
                        var next = c.Clone();
                        if (value == "none")
                        {
                            next.ShowDataLabels = false;
                        }
                        else
                        {
                            next.ShowDataLabels = true;
                            next.DataLabelPosition = Enum.TryParse<ChartLabelPosition>(value, out var position) ? position : ChartLabelPosition.Auto;
                        }
                        return next;
                    });
                    break;

                case "table":
                    Apply("Таблица данных", c =>
                    {
                        var next = c.Clone();
                        next.ShowDataTable = value != "none";
                        next.HideDataTableKeys = value == "nokeys";
                        return next;
                    });
                    break;

                case "grid":
                    Apply("Линии сетки", c =>
                    {
                        var next = c.Clone();
                        switch (value)
                        {
                            case "major": next.ShowGridlines = !next.ShowGridlines; break;
                            case "minor": next.ShowMinorGridlines = !next.ShowMinorGridlines; break;
                            case "catmajor": next.ShowCategoryGridlines = !next.ShowCategoryGridlines; break;
                            case "catminor": next.ShowCategoryMinorGridlines = !next.ShowCategoryMinorGridlines; break;
                        }
                        return next;
                    });
                    break;

                case "err":
                    Apply("Пределы погрешностей", c =>
                    {
                        var next = c.Clone();
                        foreach (var series in next.Series)
                        {
                            if (!ChartModel.KindHasTrendline(next.KindOf(series))) continue;
                            series.ErrorBars = Enum.TryParse<ChartErrorBarType>(value, out var type)
                                ? new ChartErrorBars { Type = type, Value = ChartErrorBars.DefaultValue(type) }
                                : null;
                        }
                        return next;
                    });
                    break;

                case "trend":
                    Apply("Линия тренда", c =>
                    {
                        var next = c.Clone();
                        foreach (var series in next.Series)
                        {
                            if (!ChartModel.KindHasTrendline(next.KindOf(series))) continue;
                            series.Trendline = value switch
                            {
                                "none" => null,
                                "forecast" => new ChartTrendline { Type = ChartTrendlineType.Linear, Forward = 2 },
                                _ => Enum.TryParse<ChartTrendlineType>(value, out var type) ? new ChartTrendline { Type = type } : null
                            };
                        }
                        return next;
                    });
                    break;

                case "lines":
                    Apply("Линии диаграммы", c =>
                    {
                        var next = c.Clone();
                        switch (value)
                        {
                            case "none": next.ShowDropLines = false; next.ShowHighLowLines = false; break;
                            case "drop": next.ShowDropLines = !next.ShowDropLines; break;
                            case "hilow": next.ShowHighLowLines = !next.ShowHighLowLines; break;
                        }
                        return next;
                    });
                    break;

                case "updown":
                    Apply("Полосы повышения и понижения", c =>
                    {
                        var next = c.Clone();
                        next.ShowUpDownBars = value == "show";
                        return next;
                    });
                    break;

                case "legend":
                    if (Enum.TryParse<ChartLegendPosition>(value, out var legend))
                        Apply("Легенда диаграммы", c =>
                        {
                            var next = c.Clone();
                            next.Legend = legend;
                            return next;
                        });
                    break;
            }
        }

        /// <summary>Читает выделенную диаграмму и обновляет состояние кнопок.</summary>
        public void SyncFromTarget()
        {
            var chart = _target.GetSelectedChart();
            if (chart is null) return;

            CurrentKind = chart.Kind;
            CurrentGrouping = chart.Grouping;
            CurrentLegend = chart.Legend;
            ShowTitle = chart.ShowTitle;
            ShowDataLabels = chart.ShowDataLabels;
            ShowGridlines = chart.ShowGridlines;
            ShowBorder = chart.ShowBorder;
            ShowAxisTitles = !string.IsNullOrWhiteSpace(chart.CategoryAxisTitle) || !string.IsNullOrWhiteSpace(chart.ValueAxisTitle);
            HasGrouping = chart.HasGrouping;
            HasAxes = chart.HasAxes;
            CanSwitchRowColumn = !chart.IsXY;
        }

        /// <summary>
        /// Предпросмотр на выделенной диаграмме, пока указатель на образце галереи: правка
        /// показывается в листе, но не применяется. null — снять предпросмотр.
        /// </summary>
        public void PreviewChart(Func<ChartModel, ChartModel>? change)
        {
            var chart = _target.GetSelectedChart();
            _target.PreviewSelectedChart(chart is not null && change is not null ? change(chart) : null);
        }

        private void Apply(string description, Func<ChartModel, ChartModel> change)
        {
            var chart = _target.GetSelectedChart();
            if (chart is null) return;

            var next = change(chart);
            _target.ApplySelectedChart(next, description);
            SyncFromTarget();

            // Клавиатура — снова листу: Ctrl+Z сразу отменяет нажатое.
            _target.FocusEditor();
        }
    }
}
