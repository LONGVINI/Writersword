using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Writersword.Modules.TextEditor.Models.Styles;
using StyleAlignment = Writersword.Modules.TextEditor.Models.Styles.TextAlignment;

namespace Writersword.Modules.TextEditor.Views.Dialogs
{
    /// <summary>
    /// Внутри-модульный оверлей настроек абзаца. Живёт в составе модуля (TextEditorView),
    /// затемняет и блокирует только его область, не создаёт окно ОС. Возвращает изменённые
    /// свойства абзаца через ShowAsync (null при отмене). Уровень структуры здесь не правится
    /// (он есть в риббоне) и сохраняется неизменным. Единицы отступов и интервалов независимы:
    /// у отступов по умолчанию сантиметры, у интервалов — пункты; в модель пишется в пунктах.
    /// </summary>
    public partial class ParagraphSettingsOverlay : UserControl
    {
        private const double PtPerCm = 28.3464567;

        private TaskCompletionSource<ParagraphProperties?>? _tcs;

        private int _indentUnit;     // 0 — см, 1 — пт
        private int _intervalUnit;   // 0 — см, 1 — пт
        private int _align;          // 0..3
        private int _initialOutline;
        private bool _loading;

        private Border _scrim = null!;
        private ScrollViewer _panelScroll = null!;
        private ToggleButton _alignLeftBtn = null!;
        private ToggleButton _alignCenterBtn = null!;
        private ToggleButton _alignRightBtn = null!;
        private ToggleButton _alignJustifyBtn = null!;
        private ToggleButton _indentCmBtn = null!;
        private ToggleButton _indentPtBtn = null!;
        private ToggleButton _intervalCmBtn = null!;
        private ToggleButton _intervalPtBtn = null!;
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

        public ParagraphSettingsOverlay()
        {
            InitializeComponent();
            IsVisible = false;

            _scrim = this.FindControl<Border>("Scrim")!;
            _panelScroll = this.FindControl<ScrollViewer>("PanelScroll")!;
            _alignLeftBtn = this.FindControl<ToggleButton>("AlignLeftBtn")!;
            _alignCenterBtn = this.FindControl<ToggleButton>("AlignCenterBtn")!;
            _alignRightBtn = this.FindControl<ToggleButton>("AlignRightBtn")!;
            _alignJustifyBtn = this.FindControl<ToggleButton>("AlignJustifyBtn")!;
            _indentCmBtn = this.FindControl<ToggleButton>("IndentCmBtn")!;
            _indentPtBtn = this.FindControl<ToggleButton>("IndentPtBtn")!;
            _intervalCmBtn = this.FindControl<ToggleButton>("IntervalCmBtn")!;
            _intervalPtBtn = this.FindControl<ToggleButton>("IntervalPtBtn")!;
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
            var closeBtn = this.FindControl<Button>("CloseBtn")!;
            okBtn.Click += OnOk;
            cancelBtn.Click += OnCancel;
            closeBtn.Click += OnCancel;
            _scrim.PointerPressed += OnScrimPressed;

            _alignLeftBtn.Click += OnAlignClick;
            _alignCenterBtn.Click += OnAlignClick;
            _alignRightBtn.Click += OnAlignClick;
            _alignJustifyBtn.Click += OnAlignClick;

            _indentCmBtn.Click += OnIndentUnitClick;
            _indentPtBtn.Click += OnIndentUnitClick;
            _intervalCmBtn.Click += OnIntervalUnitClick;
            _intervalPtBtn.Click += OnIntervalUnitClick;

            _firstLineModeCombo.SelectionChanged += OnModeChanged;
            _lineSpacingCombo.SelectionChanged += OnModeChanged;

            _leftIndentBox.ValueChanged += OnAnyValueChanged;
            _rightIndentBox.ValueChanged += OnAnyValueChanged;
            _firstLineValueBox.ValueChanged += OnAnyValueChanged;
            _spaceBeforeBox.ValueChanged += OnAnyValueChanged;
            _spaceAfterBox.ValueChanged += OnAnyValueChanged;
            _lineSpacingValueBox.ValueChanged += OnAnyValueChanged;

            this.GetObservable(BoundsProperty).Subscribe(b =>
            {
                if (_panelScroll is not null)
                    _panelScroll.MaxHeight = Math.Max(200, b.Height - 80);
            });
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
            if (e.Key == Key.Escape)
            {
                CompleteCancel();
                e.Handled = true;
            }
        }

        /// <summary>
        /// Показывает оверлей поверх модуля. Возвращает изменённые свойства абзаца или null при отмене.
        /// </summary>
        public Task<ParagraphProperties?> ShowAsync(ParagraphProperties current)
        {
            _tcs?.TrySetResult(null);
            _tcs = new TaskCompletionSource<ParagraphProperties?>();

            LoadFrom(current);
            ApplyUnitFormat();
            UpdateEnabledStates();
            UpdatePreview();

            IsVisible = true;
            Focus();
            return _tcs.Task;
        }

        private void Complete(ParagraphProperties? result)
        {
            IsVisible = false;
            var tcs = _tcs;
            _tcs = null;
            tcs?.TrySetResult(result);
        }

        private void CompleteCancel() => Complete(null);

        private void OnOk(object? sender, RoutedEventArgs e) => Complete(BuildResult());
        private void OnCancel(object? sender, RoutedEventArgs e) => CompleteCancel();
        private void OnScrimPressed(object? sender, PointerPressedEventArgs e) => CompleteCancel();

        // ── Выравнивание ──────────────────────────────────────────────────

        private void OnAlignClick(object? sender, RoutedEventArgs e)
        {
            if (ReferenceEquals(sender, _alignCenterBtn)) _align = 1;
            else if (ReferenceEquals(sender, _alignRightBtn)) _align = 2;
            else if (ReferenceEquals(sender, _alignJustifyBtn)) _align = 3;
            else _align = 0;
            SyncAlignChecked();
            UpdatePreview();
        }

        private void SyncAlignChecked()
        {
            _alignLeftBtn.IsChecked = _align == 0;
            _alignCenterBtn.IsChecked = _align == 1;
            _alignRightBtn.IsChecked = _align == 2;
            _alignJustifyBtn.IsChecked = _align == 3;
        }

        // ── Единицы ───────────────────────────────────────────────────────

        private static string Suffix(int unit) => unit == 0 ? "см" : "пт";

        private static double ToPt(decimal? v, int unit)
        {
            double d = (double)(v ?? 0m);
            return unit == 0 ? d * PtPerCm : d;
        }

        private static double FromPt(double pt, int unit) => unit == 0 ? pt / PtPerCm : pt;

        private static void FormatBox(NumericUpDown box, int unit)
        {
            box.Increment = unit == 0 ? 0.25m : 1m;
            box.FormatString = unit == 0 ? "0.##" : "0.#";
        }

        private void ApplyUnitFormat()
        {
            FormatBox(_leftIndentBox, _indentUnit);
            FormatBox(_rightIndentBox, _indentUnit);
            FormatBox(_firstLineValueBox, _indentUnit);
            FormatBox(_spaceBeforeBox, _intervalUnit);
            FormatBox(_spaceAfterBox, _intervalUnit);

            _leftLabel.Text = $"Слева ({Suffix(_indentUnit)})";
            _rightLabel.Text = $"Справа ({Suffix(_indentUnit)})";
            _firstLineLabel.Text = $"на ({Suffix(_indentUnit)})";
            _beforeLabel.Text = $"Перед ({Suffix(_intervalUnit)})";
            _afterLabel.Text = $"После ({Suffix(_intervalUnit)})";

            _indentCmBtn.IsChecked = _indentUnit == 0;
            _indentPtBtn.IsChecked = _indentUnit == 1;
            _intervalCmBtn.IsChecked = _intervalUnit == 0;
            _intervalPtBtn.IsChecked = _intervalUnit == 1;
        }

        private static void ConvertBox(NumericUpDown box, int oldUnit, int newUnit)
        {
            double pt = oldUnit == 0 ? (double)(box.Value ?? 0m) * PtPerCm : (double)(box.Value ?? 0m);
            double disp = newUnit == 0 ? pt / PtPerCm : pt;
            box.Value = (decimal)Math.Round(disp, 2);
        }

        private void OnIndentUnitClick(object? sender, RoutedEventArgs e)
        {
            int newUnit = ReferenceEquals(sender, _indentPtBtn) ? 1 : 0;
            if (newUnit == _indentUnit) { ApplyUnitFormat(); return; }

            _loading = true;
            ConvertBox(_leftIndentBox, _indentUnit, newUnit);
            ConvertBox(_rightIndentBox, _indentUnit, newUnit);
            ConvertBox(_firstLineValueBox, _indentUnit, newUnit);
            _indentUnit = newUnit;
            _loading = false;

            ApplyUnitFormat();
            UpdatePreview();
        }

        private void OnIntervalUnitClick(object? sender, RoutedEventArgs e)
        {
            int newUnit = ReferenceEquals(sender, _intervalPtBtn) ? 1 : 0;
            if (newUnit == _intervalUnit) { ApplyUnitFormat(); return; }

            _loading = true;
            ConvertBox(_spaceBeforeBox, _intervalUnit, newUnit);
            ConvertBox(_spaceAfterBox, _intervalUnit, newUnit);
            _intervalUnit = newUnit;
            _loading = false;

            ApplyUnitFormat();
            UpdatePreview();
        }

        // ── Загрузка / сбор ───────────────────────────────────────────────

        private void LoadFrom(ParagraphProperties p)
        {
            _loading = true;

            _initialOutline = p.OutlineLevel;
            _indentUnit = 0;     // отступы по умолчанию в сантиметрах
            _intervalUnit = 1;   // интервалы по умолчанию в пунктах

            _align = (int)(p.Alignment ?? StyleAlignment.Left);
            SyncAlignChecked();

            _leftIndentBox.Value = (decimal)FromPt(p.LeftIndent ?? 0, _indentUnit);
            _rightIndentBox.Value = (decimal)FromPt(p.RightIndent ?? 0, _indentUnit);

            double fl = p.FirstLineIndent ?? 0;
            if (fl > 0)
            {
                _firstLineModeCombo.SelectedIndex = 1;
                _firstLineValueBox.Value = (decimal)FromPt(fl, _indentUnit);
            }
            else if (fl < 0)
            {
                _firstLineModeCombo.SelectedIndex = 2;
                _firstLineValueBox.Value = (decimal)FromPt(-fl, _indentUnit);
            }
            else
            {
                _firstLineModeCombo.SelectedIndex = 0;
                _firstLineValueBox.Value = 0m;
            }

            _spaceBeforeBox.Value = (decimal)FromPt(p.SpaceBefore ?? 0, _intervalUnit);
            _spaceAfterBox.Value = (decimal)FromPt(p.SpaceAfter ?? 0, _intervalUnit);

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
                Alignment = (StyleAlignment)_align,
                OutlineLevel = _initialOutline
            };

            p.LeftIndent = ToPt(_leftIndentBox.Value, _indentUnit);
            p.RightIndent = ToPt(_rightIndentBox.Value, _indentUnit);

            double flPt = ToPt(_firstLineValueBox.Value, _indentUnit);
            p.FirstLineIndent = _firstLineModeCombo.SelectedIndex switch
            {
                1 => flPt,
                2 => -flPt,
                _ => 0
            };

            p.SpaceBefore = ToPt(_spaceBeforeBox.Value, _intervalUnit);
            p.SpaceAfter = ToPt(_spaceAfterBox.Value, _intervalUnit);

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

        // ── Реакции ───────────────────────────────────────────────────────

        private void OnAnyValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
        {
            if (_loading) return;
            UpdatePreview();
        }

        private void OnModeChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
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
            if (_previewSample is null) return;

            _previewSample.TextAlignment = _align switch
            {
                1 => Avalonia.Media.TextAlignment.Center,
                2 => Avalonia.Media.TextAlignment.Right,
                3 => Avalonia.Media.TextAlignment.Justify,
                _ => Avalonia.Media.TextAlignment.Left
            };

            double leftPt = ToPt(_leftIndentBox.Value, _indentUnit);
            double rightPt = ToPt(_rightIndentBox.Value, _indentUnit);
            double beforePt = ToPt(_spaceBeforeBox.Value, _intervalUnit);
            double afterPt = ToPt(_spaceAfterBox.Value, _intervalUnit);

            double leftPx = Clamp(leftPt * 0.6, 0, 160);
            double rightPx = Clamp(rightPt * 0.6, 0, 160);
            double beforePx = Clamp(beforePt * 0.7, 0, 40);
            double afterPx = Clamp(afterPt * 0.7, 0, 40);

            _previewSample.Margin = new Thickness(leftPx, beforePx, rightPx, afterPx);

            const double baseFont = 13.0;
            const double lineUnit = baseFont * 1.4;   // натуральная высота строки с запасом под нижние выносные
            double lsVal = (double)(_lineSpacingValueBox.Value ?? 0m);
            double lineHeight = _lineSpacingCombo.SelectedIndex switch
            {
                0 => lineUnit,
                1 => lineUnit * 1.5,
                2 => lineUnit * 2.0,
                3 => lineUnit * (lsVal > 0 ? lsVal : 1.0),
                4 => lsVal > 0 ? lsVal : lineUnit,
                5 => lsVal > 0 ? lsVal : lineUnit,
                _ => lineUnit
            };
            _previewSample.LineHeight = Math.Max(lineUnit, lineHeight);
        }

        private static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);
    }
}
