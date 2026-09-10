using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using System;
using System.Collections.Generic;
using System.Globalization;
using Writersword.Modules.TextEditor.Models.Settings;
using Writersword.Modules.TextEditor.Models.Styles;
// Псевдоним, а не using: у контрола есть своё свойство Resources, и без
// него имя Resources в этом классе означает словарь ресурсов Avalonia.
using Strings = Writersword.Modules.TextEditor.Resources.TextEditorStrings;

namespace Writersword.Modules.TextEditor.Views.Dialogs
{
    /// <summary>
    /// Окно «Табуляция»: точная правка позиций абзаца числами.
    ///
    /// Линейка отвечает за повседневную работу — прицелился и поставил, — но у неё есть
    /// предел: попасть мышью в ровные 12,5 см нельзя, а именно ровные значения и нужны,
    /// когда столбцы должны совпасть в нескольких абзацах. Здесь позиция вводится числом,
    /// и здесь же видно весь набор разом.
    ///
    /// Окно работает с копией набора и отдаёт её целиком по «ОК». Правка по месту была бы
    /// короче, но каждое нажатие «Установить» перестраивало бы рукопись, и отказаться от
    /// сделанного стало бы нечем.
    /// </summary>
    public partial class TabSettingsOverlay : UserControl
    {
        private Border _scrim = null!;
        private ListBox _stopsList = null!;
        private TextBlock _emptyHint = null!;
        private TextBox _positionBox = null!;
        private TextBox _defaultStepBox = null!;

        private RadioButton _alignLeft = null!;
        private RadioButton _alignCenter = null!;
        private RadioButton _alignRight = null!;
        private RadioButton _alignDecimal = null!;

        private RadioButton _leaderNone = null!;
        private RadioButton _leaderDots = null!;
        private RadioButton _leaderDashes = null!;
        private RadioButton _leaderLine = null!;

        private readonly List<TabStop> _stops = new();
        private RulerUnits _units = RulerUnits.Centimeters;
        private double _defaultStepPt = 35.4;

        // Пока список перезаполняется, выбор в нём меняется сам собой. Без этого признака
        // каждая перерисовка списка затирала бы поля ввода тем, что оказалось выбрано.
        private bool _suspendSelection;

        /// <summary>
        /// Готовый набор и шаг по умолчанию в пунктах. Зовётся только по «ОК»: закрытие
        /// крестиком, Escape или щелчком мимо окна — это отказ, и менять рукопись он не должен.
        /// </summary>
        public Action<List<TabStop>, double>? Applied { get; set; }

        public TabSettingsOverlay()
        {
            InitializeComponent();
            IsVisible = false;

            _scrim = this.FindControl<Border>("Scrim")!;
            _stopsList = this.FindControl<ListBox>("StopsList")!;
            _emptyHint = this.FindControl<TextBlock>("EmptyHint")!;
            _positionBox = this.FindControl<TextBox>("PositionBox")!;
            _defaultStepBox = this.FindControl<TextBox>("DefaultStepBox")!;

            _alignLeft = this.FindControl<RadioButton>("AlignLeftBtn")!;
            _alignCenter = this.FindControl<RadioButton>("AlignCenterBtn")!;
            _alignRight = this.FindControl<RadioButton>("AlignRightBtn")!;
            _alignDecimal = this.FindControl<RadioButton>("AlignDecimalBtn")!;

            _leaderNone = this.FindControl<RadioButton>("LeaderNoneBtn")!;
            _leaderDots = this.FindControl<RadioButton>("LeaderDotsBtn")!;
            _leaderDashes = this.FindControl<RadioButton>("LeaderDashesBtn")!;
            _leaderLine = this.FindControl<RadioButton>("LeaderLineBtn")!;

            this.FindControl<Button>("SetBtn")!.Click += OnSet;
            this.FindControl<Button>("RemoveBtn")!.Click += OnRemove;
            this.FindControl<Button>("ClearAllBtn")!.Click += OnClearAll;
            this.FindControl<Button>("OkBtn")!.Click += OnOk;
            this.FindControl<Button>("CancelBtn")!.Click += OnCancel;
            this.FindControl<Button>("CloseBtn")!.Click += OnCancel;

            _stopsList.SelectionChanged += OnStopSelected;
            _scrim.PointerPressed += OnScrimPressed;
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            TopLevel.GetTopLevel(this)?.AddHandler(KeyDownEvent, OnOverlayKeyDown, RoutingStrategies.Tunnel);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            TopLevel.GetTopLevel(this)?.RemoveHandler(KeyDownEvent, OnOverlayKeyDown);
            base.OnDetachedFromVisualTree(e);
        }

        private void OnOverlayKeyDown(object? sender, KeyEventArgs e)
        {
            if (!IsVisible) return;
            if (e.Key == Key.Escape) { IsVisible = false; e.Handled = true; }
        }

        /// <summary>
        /// Открывает окно на наборе абзаца под кареткой. Единицы приходят из линейки:
        /// человек только что смотрел на неё, и просить его пересчитать сантиметры в
        /// дюймы ради одного поля незачем.
        /// </summary>
        public void Show(IReadOnlyList<TabStop> stops, double defaultStepPt, RulerUnits units)
        {
            _units = units;
            _defaultStepPt = defaultStepPt > 0 ? defaultStepPt : 35.4;

            _stops.Clear();
            foreach (var stop in stops) _stops.Add(stop.Clone());
            _stops.Sort(static (a, b) => a.PositionPt.CompareTo(b.PositionPt));

            _defaultStepBox.Text = FormatUnits(_defaultStepPt);
            _positionBox.Text = string.Empty;

            SetAlignment(TabAlignment.Left);
            SetLeader(TabLeaderStyle.None);

            RebuildList(-1);

            IsVisible = true;
            _positionBox.Focus();
        }

        // ── Список ────────────────────────────────────────────────────────

        private void RebuildList(int selectIndex)
        {
            _suspendSelection = true;
            try
            {
                var rows = new List<string>(_stops.Count);
                foreach (var stop in _stops) rows.Add(DescribeStop(stop));

                _stopsList.ItemsSource = rows;
                _stopsList.SelectedIndex = selectIndex >= 0 && selectIndex < rows.Count
                    ? selectIndex
                    : -1;

                _emptyHint.IsVisible = rows.Count == 0;
            }
            finally
            {
                _suspendSelection = false;
            }
        }

        private string DescribeStop(TabStop stop)
        {
            string align = stop.Alignment switch
            {
                TabAlignment.Center => Strings.Tab_AlignCenter,
                TabAlignment.Right => Strings.Tab_AlignRight,
                TabAlignment.Decimal => Strings.Tab_AlignDecimal,
                _ => Strings.Tab_AlignLeft
            };

            string leader = stop.Leader switch
            {
                TabLeaderStyle.Dots => Strings.Tab_LeaderDots,
                TabLeaderStyle.Dashes => Strings.Tab_LeaderDashes,
                TabLeaderStyle.Line => Strings.Tab_LeaderLine,
                _ => string.Empty
            };

            string text = FormatUnits(stop.PositionPt) + " " + UnitSuffix() + "   " + align;
            if (leader.Length > 0) text += "   " + leader;
            return text;
        }

        private void OnStopSelected(object? sender, SelectionChangedEventArgs e)
        {
            if (_suspendSelection) return;

            int idx = _stopsList.SelectedIndex;
            if (idx < 0 || idx >= _stops.Count) return;

            var stop = _stops[idx];
            _positionBox.Text = FormatUnits(stop.PositionPt);
            SetAlignment(stop.Alignment);
            SetLeader(stop.Leader);
        }

        // ── Кнопки ────────────────────────────────────────────────────────

        /// <summary>
        /// Ставит позицию или правит уже стоящую. Одна кнопка на оба случая по той же
        /// причине, что и в Word: вводя число, человек называет место, а не выбирает
        /// между «добавить» и «изменить» — если позиция там уже есть, речь о ней.
        /// </summary>
        private void OnSet(object? sender, RoutedEventArgs e)
        {
            if (!TryReadPt(_positionBox.Text, out double positionPt)) return;
            if (positionPt < 0) return;

            var alignment = ReadAlignment();
            var leader = ReadLeader();

            // Одна и та же точка не может нести два разных выравнивания: попадание в
            // существующую позицию правит её, а не кладёт вторую поверх.
            const double SamePointPt = 0.5;
            for (int i = 0; i < _stops.Count; i++)
            {
                if (Math.Abs(_stops[i].PositionPt - positionPt) <= SamePointPt)
                {
                    _stops[i].PositionPt = positionPt;
                    _stops[i].Alignment = alignment;
                    _stops[i].Leader = leader;
                    RebuildList(i);
                    return;
                }
            }

            _stops.Add(new TabStop
            {
                PositionPt = positionPt,
                Alignment = alignment,
                Leader = leader
            });
            _stops.Sort(static (a, b) => a.PositionPt.CompareTo(b.PositionPt));

            int selected = _stops.FindIndex(s => Math.Abs(s.PositionPt - positionPt) <= SamePointPt);
            RebuildList(selected);
        }

        private void OnRemove(object? sender, RoutedEventArgs e)
        {
            int idx = _stopsList.SelectedIndex;
            if (idx < 0 || idx >= _stops.Count) return;

            _stops.RemoveAt(idx);
            RebuildList(Math.Min(idx, _stops.Count - 1));
        }

        private void OnClearAll(object? sender, RoutedEventArgs e)
        {
            _stops.Clear();
            RebuildList(-1);
        }

        private void OnOk(object? sender, RoutedEventArgs e)
        {
            double stepPt = TryReadPt(_defaultStepBox.Text, out double parsed) && parsed > 1
                ? parsed
                : _defaultStepPt;

            var result = new List<TabStop>(_stops.Count);
            foreach (var stop in _stops) result.Add(stop.Clone());

            IsVisible = false;
            Applied?.Invoke(result, stepPt);
        }

        private void OnCancel(object? sender, RoutedEventArgs e) => IsVisible = false;

        private void OnScrimPressed(object? sender, PointerPressedEventArgs e) => IsVisible = false;

        // ── Переключатели ─────────────────────────────────────────────────

        private void SetAlignment(TabAlignment alignment)
        {
            _alignLeft.IsChecked = alignment == TabAlignment.Left;
            _alignCenter.IsChecked = alignment == TabAlignment.Center;
            _alignRight.IsChecked = alignment == TabAlignment.Right;
            _alignDecimal.IsChecked = alignment == TabAlignment.Decimal;
        }

        private TabAlignment ReadAlignment()
        {
            if (_alignCenter.IsChecked == true) return TabAlignment.Center;
            if (_alignRight.IsChecked == true) return TabAlignment.Right;
            if (_alignDecimal.IsChecked == true) return TabAlignment.Decimal;
            return TabAlignment.Left;
        }

        private void SetLeader(TabLeaderStyle leader)
        {
            _leaderNone.IsChecked = leader == TabLeaderStyle.None;
            _leaderDots.IsChecked = leader == TabLeaderStyle.Dots;
            _leaderDashes.IsChecked = leader == TabLeaderStyle.Dashes;
            _leaderLine.IsChecked = leader == TabLeaderStyle.Line;
        }

        private TabLeaderStyle ReadLeader()
        {
            if (_leaderDots.IsChecked == true) return TabLeaderStyle.Dots;
            if (_leaderDashes.IsChecked == true) return TabLeaderStyle.Dashes;
            if (_leaderLine.IsChecked == true) return TabLeaderStyle.Line;
            return TabLeaderStyle.None;
        }

        // ── Единицы ───────────────────────────────────────────────────────

        private double UnitInMm() => _units == RulerUnits.Inches ? 25.4 : 10.0;

        private string UnitSuffix() => _units == RulerUnits.Inches ? "\"" : "см";

        private string FormatUnits(double pt)
        {
            double value = pt * 25.4 / 72.0 / UnitInMm();
            return value.ToString("0.##", CultureInfo.CurrentCulture);
        }

        /// <summary>
        /// Читает число в единицах линейки и отдаёт пункты. Разделитель принимается любой:
        /// набирая на цифровой клавиатуре, человек ставит точку, а язык интерфейса ждёт
        /// запятую — отказывать ему из-за этого не за что.
        /// </summary>
        private bool TryReadPt(string? text, out double pt)
        {
            pt = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;

            string cleaned = text.Trim()
                .Replace("\"", string.Empty)
                .Replace("см", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim()
                .Replace(',', '.');

            if (!double.TryParse(cleaned, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double value))
                return false;

            pt = value * UnitInMm() * 72.0 / 25.4;
            return true;
        }
    }
}
