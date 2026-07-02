using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Writersword.Modules.TextEditor.Models.Styles;
using StyleAlignment = Writersword.Modules.TextEditor.Models.Styles.TextAlignment;

namespace Writersword.Modules.TextEditor.Views.Dialogs
{
    /// <summary>
    /// Окно «Абзац»: выравнивание, отступы, интервалы и междустрочный с живым образцом.
    /// Единицы отступов и интервалов переключаются (сантиметры / пункты). В модель всё пишется
    /// в пунктах. Уровень структуры в окне не редактируется (он есть в риббоне) и сохраняется
    /// неизменным. Результат — ParagraphProperties через Close(result); null при отмене.
    /// </summary>
    public partial class ParagraphDialog : Window
    {
        private const double PtPerCm = 28.3464567;

        // 0 — сантиметры, 1 — пункты.
        private int _unit;
        private int _initialOutline;
        private bool _loading;

        private ComboBox _unitCombo = null!;
        private ComboBox _alignmentCombo = null!;
        private NumericUpDown _leftIndentBox = null!;
        private NumericUpDown _rightIndentBox = null!;
        private ComboBox _firstLineModeCombo = null!;
        private NumericUpDown _firstLineValueBox = null!;
        private NumericUpDown _spaceBeforeBox = null!;
        private NumericUpDown _spaceAfterBox = null!;
        private ComboBox _lineSpacingCombo = null!;
        private NumericUpDown _lineSpacingValueBox = null!;
        private TextBlock _previewSample = null!;

        private TextBlock _leftLabel = null!;
        private TextBlock _rightLabel = null!;
        private TextBlock _firstLineLabel = null!;
        private TextBlock _beforeLabel = null!;
        private TextBlock _afterLabel = null!;

        public ParagraphDialog()
        {
            InitializeComponent();
        }

        public ParagraphDialog(ParagraphProperties current) : this()
        {
            _unitCombo = this.FindControl<ComboBox>("UnitCombo")!;
            _alignmentCombo = this.FindControl<ComboBox>("AlignmentCombo")!;
            _leftIndentBox = this.FindControl<NumericUpDown>("LeftIndentBox")!;
            _rightIndentBox = this.FindControl<NumericUpDown>("RightIndentBox")!;
            _firstLineModeCombo = this.FindControl<ComboBox>("FirstLineModeCombo")!;
            _firstLineValueBox = this.FindControl<NumericUpDown>("FirstLineValueBox")!;
            _spaceBeforeBox = this.FindControl<NumericUpDown>("SpaceBeforeBox")!;
            _spaceAfterBox = this.FindControl<NumericUpDown>("SpaceAfterBox")!;
            _lineSpacingCombo = this.FindControl<ComboBox>("LineSpacingCombo")!;
            _lineSpacingValueBox = this.FindControl<NumericUpDown>("LineSpacingValueBox")!;
            _previewSample = this.FindControl<TextBlock>("PreviewSample")!;

            _leftLabel = this.FindControl<TextBlock>("LeftLabel")!;
            _rightLabel = this.FindControl<TextBlock>("RightLabel")!;
            _firstLineLabel = this.FindControl<TextBlock>("FirstLineLabel")!;
            _beforeLabel = this.FindControl<TextBlock>("BeforeLabel")!;
            _afterLabel = this.FindControl<TextBlock>("AfterLabel")!;

            var okBtn = this.FindControl<Button>("OkBtn")!;
            var cancelBtn = this.FindControl<Button>("CancelBtn")!;
            var closeBtn = this.FindControl<Button>("CloseBtn");
            okBtn.Click += OnOk;
            cancelBtn.Click += OnCancel;
            if (closeBtn is not null)
                closeBtn.Click += OnCancel;

            _unitCombo.SelectionChanged += OnUnitChanged;
            _alignmentCombo.SelectionChanged += OnAnyChanged;
            _firstLineModeCombo.SelectionChanged += OnModeChanged;
            _lineSpacingCombo.SelectionChanged += OnModeChanged;

            _leftIndentBox.ValueChanged += OnAnyValueChanged;
            _rightIndentBox.ValueChanged += OnAnyValueChanged;
            _firstLineValueBox.ValueChanged += OnAnyValueChanged;
            _spaceBeforeBox.ValueChanged += OnAnyValueChanged;
            _spaceAfterBox.ValueChanged += OnAnyValueChanged;
            _lineSpacingValueBox.ValueChanged += OnAnyValueChanged;

            LoadFrom(current);
            ApplyUnitFormat();
            UpdateEnabledStates();
            UpdatePreview();
        }

        // ── Единицы ───────────────────────────────────────────────────────

        private string UnitSuffix => _unit == 0 ? "см" : "пт";

        private double DisplayToPt(decimal? v)
        {
            double d = (double)(v ?? 0m);
            return _unit == 0 ? d * PtPerCm : d;
        }

        private double PtToDisplay(double pt) => _unit == 0 ? pt / PtPerCm : pt;

        private void ApplyUnitFormat()
        {
            decimal increment = _unit == 0 ? 0.25m : 1m;
            string format = _unit == 0 ? "0.##" : "0.#";

            foreach (var box in new[] { _leftIndentBox, _rightIndentBox, _firstLineValueBox, _spaceBeforeBox, _spaceAfterBox })
            {
                box.Increment = increment;
                box.FormatString = format;
            }

            _leftLabel.Text = $"Слева ({UnitSuffix})";
            _rightLabel.Text = $"Справа ({UnitSuffix})";
            _firstLineLabel.Text = $"на ({UnitSuffix})";
            _beforeLabel.Text = $"Перед ({UnitSuffix})";
            _afterLabel.Text = $"После ({UnitSuffix})";
        }

        private void OnUnitChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            int newUnit = _unitCombo.SelectedIndex < 0 ? 0 : _unitCombo.SelectedIndex;
            if (newUnit == _unit) return;

            _loading = true;
            foreach (var box in new[] { _leftIndentBox, _rightIndentBox, _firstLineValueBox, _spaceBeforeBox, _spaceAfterBox })
            {
                double pt = _unit == 0 ? (double)(box.Value ?? 0m) * PtPerCm : (double)(box.Value ?? 0m);
                double disp = newUnit == 0 ? pt / PtPerCm : pt;
                box.Value = (decimal)Math.Round(disp, 2);
            }
            _unit = newUnit;
            _loading = false;

            ApplyUnitFormat();
            UpdatePreview();
        }

        // ── Загрузка / сбор ───────────────────────────────────────────────

        private void LoadFrom(ParagraphProperties p)
        {
            _loading = true;

            _initialOutline = p.OutlineLevel;
            _unit = 0;
            _unitCombo.SelectedIndex = 0;

            _alignmentCombo.SelectedIndex = (int)(p.Alignment ?? StyleAlignment.Left);

            _leftIndentBox.Value = (decimal)PtToDisplay(p.LeftIndent ?? 0);
            _rightIndentBox.Value = (decimal)PtToDisplay(p.RightIndent ?? 0);

            double fl = p.FirstLineIndent ?? 0;
            if (fl > 0)
            {
                _firstLineModeCombo.SelectedIndex = 1;
                _firstLineValueBox.Value = (decimal)PtToDisplay(fl);
            }
            else if (fl < 0)
            {
                _firstLineModeCombo.SelectedIndex = 2;
                _firstLineValueBox.Value = (decimal)PtToDisplay(-fl);
            }
            else
            {
                _firstLineModeCombo.SelectedIndex = 0;
                _firstLineValueBox.Value = 0m;
            }

            _spaceBeforeBox.Value = (decimal)PtToDisplay(p.SpaceBefore ?? 0);
            _spaceAfterBox.Value = (decimal)PtToDisplay(p.SpaceAfter ?? 0);

            var rule = p.LineSpacingRule ?? LineSpacingRule.Auto;
            double val = p.LineSpacingValue ?? 1.0;
            if (rule == LineSpacingRule.Exact)
            {
                _lineSpacingCombo.SelectedIndex = 4;
                _lineSpacingValueBox.Value = (decimal)val;
            }
            else if (rule == LineSpacingRule.AtLeast)
            {
                _lineSpacingCombo.SelectedIndex = 5;
                _lineSpacingValueBox.Value = (decimal)val;
            }
            else
            {
                if (Math.Abs(val - 1.0) < 0.001) _lineSpacingCombo.SelectedIndex = 0;
                else if (Math.Abs(val - 1.5) < 0.001) _lineSpacingCombo.SelectedIndex = 1;
                else if (Math.Abs(val - 2.0) < 0.001) _lineSpacingCombo.SelectedIndex = 2;
                else _lineSpacingCombo.SelectedIndex = 3;
                _lineSpacingValueBox.Value = (decimal)val;
            }

            _loading = false;
        }

        private ParagraphProperties BuildResult()
        {
            var p = new ParagraphProperties
            {
                Alignment = (StyleAlignment)Math.Max(0, _alignmentCombo.SelectedIndex),
                OutlineLevel = _initialOutline
            };

            p.LeftIndent = DisplayToPt(_leftIndentBox.Value);
            p.RightIndent = DisplayToPt(_rightIndentBox.Value);

            double flPt = DisplayToPt(_firstLineValueBox.Value);
            p.FirstLineIndent = _firstLineModeCombo.SelectedIndex switch
            {
                1 => flPt,
                2 => -flPt,
                _ => 0
            };

            p.SpaceBefore = DisplayToPt(_spaceBeforeBox.Value);
            p.SpaceAfter = DisplayToPt(_spaceAfterBox.Value);

            double lsVal = (double)(_lineSpacingValueBox.Value ?? 0m);
            switch (_lineSpacingCombo.SelectedIndex)
            {
                case 0: p.LineSpacingRule = LineSpacingRule.Auto; p.LineSpacingValue = 1.0; break;
                case 1: p.LineSpacingRule = LineSpacingRule.Auto; p.LineSpacingValue = 1.5; break;
                case 2: p.LineSpacingRule = LineSpacingRule.Auto; p.LineSpacingValue = 2.0; break;
                case 3: p.LineSpacingRule = LineSpacingRule.Auto; p.LineSpacingValue = lsVal > 0 ? lsVal : 1.0; break;
                case 4: p.LineSpacingRule = LineSpacingRule.Exact; p.LineSpacingValue = lsVal; break;
                case 5: p.LineSpacingRule = LineSpacingRule.AtLeast; p.LineSpacingValue = lsVal; break;
                default: p.LineSpacingRule = LineSpacingRule.Auto; p.LineSpacingValue = 1.0; break;
            }

            return p;
        }

        // ── Реакции на изменения ──────────────────────────────────────────

        private void OnAnyChanged(object? sender, SelectionChangedEventArgs e) => UpdatePreview();

        private void OnAnyValueChanged(object? sender, NumericUpDownValueChangedEventArgs e) => UpdatePreview();

        private void OnModeChanged(object? sender, SelectionChangedEventArgs e)
        {
            UpdateEnabledStates();
            UpdatePreview();
        }

        private void UpdateEnabledStates()
        {
            int flMode = _firstLineModeCombo.SelectedIndex;
            _firstLineValueBox.IsEnabled = flMode == 1 || flMode == 2;

            int lsMode = _lineSpacingCombo.SelectedIndex;
            _lineSpacingValueBox.IsEnabled = lsMode >= 3;
        }

        private void UpdatePreview()
        {
            if (_loading || _previewSample is null) return;

            _previewSample.TextAlignment = _alignmentCombo.SelectedIndex switch
            {
                1 => Avalonia.Media.TextAlignment.Center,
                2 => Avalonia.Media.TextAlignment.Right,
                3 => Avalonia.Media.TextAlignment.Justify,
                _ => Avalonia.Media.TextAlignment.Left
            };

            double leftPt = DisplayToPt(_leftIndentBox.Value);
            double rightPt = DisplayToPt(_rightIndentBox.Value);
            double beforePt = DisplayToPt(_spaceBeforeBox.Value);
            double afterPt = DisplayToPt(_spaceAfterBox.Value);

            double leftPx = Clamp(leftPt * 0.6, 0, 160);
            double rightPx = Clamp(rightPt * 0.6, 0, 160);
            double beforePx = Clamp(beforePt * 0.7, 0, 40);
            double afterPx = Clamp(afterPt * 0.7, 0, 40);

            _previewSample.Margin = new Thickness(leftPx, beforePx, rightPx, afterPx);

            const double baseFont = 13.0;
            double lsVal = (double)(_lineSpacingValueBox.Value ?? 0m);
            double lineHeight = _lineSpacingCombo.SelectedIndex switch
            {
                0 => baseFont * 1.0,
                1 => baseFont * 1.5,
                2 => baseFont * 2.0,
                3 => baseFont * (lsVal > 0 ? lsVal : 1.0),
                4 => lsVal > 0 ? lsVal : baseFont,
                5 => Math.Max(baseFont, lsVal),
                _ => baseFont
            };
            _previewSample.LineHeight = Math.Max(baseFont, lineHeight);
        }

        private static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);

        // ── Закрытие ──────────────────────────────────────────────────────

        private void OnOk(object? sender, RoutedEventArgs e) => Close(BuildResult());

        private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close(null);
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }
    }
}
