using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Views.Dialogs
{
    /// <summary>
    /// Внутри-модульный оверлей «Определить новый список». Живёт в составе модуля
    /// (TextEditorView), затемняет и блокирует только его область, окно ОС не создаёт.
    /// Возвращает настроенные свойства списка через ShowAsync (null при отмене).
    /// Три типа: маркированный (свой символ), нумерованный (счётная система) и свой набор
    /// символов (произвольная последовательность). Отступы задаются в пунктах.
    /// </summary>
    public partial class ListSettingsOverlay : UserControl
    {
        private TaskCompletionSource<ListProperties?>? _tcs;
        private bool _loading;
        private Guid _listId;
        private int _level;

        private Border _scrim = null!;
        private ScrollViewer _panelScroll = null!;
        private ComboBox _typeCombo = null!;
        private StackPanel _bulletPanel = null!;
        private StackPanel _numberPanel = null!;
        private StackPanel _sequencePanel = null!;
        private StackPanel _rulesPanel = null!;
        private TextBox _bulletSymbolBox = null!;
        private ComboBox _numberSystemCombo = null!;
        private TextBox _prefixBox = null!;
        private TextBox _suffixBox = null!;
        private NumericUpDown _startAtBox = null!;
        private ComboBox _numberingModeCombo = null!;
        private TextBox _sequenceBox = null!;
        private ComboBox _sequenceWrapCombo = null!;
        private NumericUpDown _markerIndentBox = null!;
        private NumericUpDown _textIndentBox = null!;
        private NumericUpDown _minGapBox = null!;
        private TextBlock _previewSample = null!;

        public ListSettingsOverlay()
        {
            InitializeComponent();
            IsVisible = false;

            _scrim = this.FindControl<Border>("Scrim")!;
            _panelScroll = this.FindControl<ScrollViewer>("PanelScroll")!;
            _typeCombo = this.FindControl<ComboBox>("TypeCombo")!;
            _bulletPanel = this.FindControl<StackPanel>("BulletPanel")!;
            _numberPanel = this.FindControl<StackPanel>("NumberPanel")!;
            _sequencePanel = this.FindControl<StackPanel>("SequencePanel")!;
            _rulesPanel = this.FindControl<StackPanel>("RulesPanel")!;
            _bulletSymbolBox = this.FindControl<TextBox>("BulletSymbolBox")!;
            _numberSystemCombo = this.FindControl<ComboBox>("NumberSystemCombo")!;
            _prefixBox = this.FindControl<TextBox>("PrefixBox")!;
            _suffixBox = this.FindControl<TextBox>("SuffixBox")!;
            _startAtBox = this.FindControl<NumericUpDown>("StartAtBox")!;
            _numberingModeCombo = this.FindControl<ComboBox>("NumberingModeCombo")!;
            _sequenceBox = this.FindControl<TextBox>("SequenceBox")!;
            _sequenceWrapCombo = this.FindControl<ComboBox>("SequenceWrapCombo")!;
            _markerIndentBox = this.FindControl<NumericUpDown>("MarkerIndentBox")!;
            _textIndentBox = this.FindControl<NumericUpDown>("TextIndentBox")!;
            _minGapBox = this.FindControl<NumericUpDown>("MinGapBox")!;
            _previewSample = this.FindControl<TextBlock>("PreviewSample")!;

            var okBtn = this.FindControl<Button>("OkBtn")!;
            var cancelBtn = this.FindControl<Button>("CancelBtn")!;
            var closeBtn = this.FindControl<Button>("CloseBtn")!;
            okBtn.Click += OnOk;
            cancelBtn.Click += OnCancel;
            closeBtn.Click += OnCancel;
            _scrim.PointerPressed += OnScrimPressed;

            _typeCombo.SelectionChanged += OnAnyComboChanged;
            _numberSystemCombo.SelectionChanged += OnAnyComboChanged;
            _sequenceWrapCombo.SelectionChanged += OnAnyComboChanged;
            _numberingModeCombo.SelectionChanged += OnAnyComboChanged;
            _bulletSymbolBox.TextChanged += OnAnyTextChanged;
            _prefixBox.TextChanged += OnAnyTextChanged;
            _suffixBox.TextChanged += OnAnyTextChanged;
            _sequenceBox.TextChanged += OnAnyTextChanged;
            _startAtBox.ValueChanged += OnAnyValueChanged;

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
        /// Показывает оверлей. current — свойства списка активного абзаца (null — не список).
        /// Возвращает настроенные свойства или null при отмене.
        /// </summary>
        public Task<ListProperties?> ShowAsync(ListProperties? current)
        {
            _tcs?.TrySetResult(null);
            _tcs = new TaskCompletionSource<ListProperties?>();

            LoadFrom(current);
            UpdateVisibleSection();
            UpdatePreview();

            IsVisible = true;
            Focus();
            return _tcs.Task;
        }

        private void Complete(ListProperties? result)
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

        private const int TypeBullet = 0;
        private const int TypeNumbered = 1;
        private const int TypeSequence = 2;

        private static ListMarkerType SystemIndexToType(int idx) => idx switch
        {
            0 => ListMarkerType.Decimal,
            1 => ListMarkerType.DecimalLeadingZero,
            2 => ListMarkerType.LowerAlpha,
            3 => ListMarkerType.UpperAlpha,
            4 => ListMarkerType.LowerRoman,
            5 => ListMarkerType.UpperRoman,
            _ => ListMarkerType.Decimal
        };

        private static int TypeToSystemIndex(ListMarkerType type) => type switch
        {
            ListMarkerType.Decimal => 0,
            ListMarkerType.DecimalLeadingZero => 1,
            ListMarkerType.LowerAlpha => 2,
            ListMarkerType.UpperAlpha => 3,
            ListMarkerType.LowerRoman => 4,
            ListMarkerType.UpperRoman => 5,
            _ => 0
        };

        private void LoadFrom(ListProperties? p)
        {
            _loading = true;

            _listId = p?.ListId ?? Guid.Empty;
            _level = p?.Level ?? 0;

            int type = TypeBullet;
            if (p is not null)
            {
                if (p.MarkerType == ListMarkerType.CustomSequence) type = TypeSequence;
                else if (p.IsNumbered) type = TypeNumbered;
            }
            _typeCombo.SelectedIndex = type;

            _bulletSymbolBox.Text = p is { MarkerType: ListMarkerType.Custom } && !string.IsNullOrEmpty(p.CustomMarker)
                ? p.CustomMarker : "•";

            _numberSystemCombo.SelectedIndex = (p is not null && p.IsNumbered && p.MarkerType != ListMarkerType.CustomSequence)
                ? TypeToSystemIndex(p.MarkerType) : 0;
            _prefixBox.Text = p?.NumberPrefix ?? string.Empty;
            _suffixBox.Text = p?.NumberSuffix ?? ".";
            _startAtBox.Value = p?.StartAt ?? 1;
            _numberingModeCombo.SelectedIndex = (p?.ContinueNumbering ?? true) ? 0 : 1;

            _sequenceBox.Text = (p?.CustomSequence is { Count: > 0 })
                ? string.Join(" ", p.CustomSequence) : "① ② ③ ④ ⑤";
            _sequenceWrapCombo.SelectedIndex = (p?.SequenceWrap ?? true) ? 0 : 1;

            double textLeftPt = p?.TextIndentPt ?? (_level + 1) * ListProperties.DefaultLevelStepPt;
            double markerAbsPt = p?.MarkerIndentPt
                ?? Math.Max(0.0, textLeftPt - ListProperties.DefaultHangingPt);

            _textIndentBox.Value = (decimal)Math.Round(textLeftPt, 1);
            _markerIndentBox.Value = (decimal)Math.Round(markerAbsPt, 1);
            _minGapBox.Value = (decimal)Math.Round(p?.MarkerTextMinGapPt ?? ListProperties.DefaultMarkerTextGapPt, 1);

            _loading = false;
        }

        private ListProperties BuildResult()
        {
            int type = _typeCombo.SelectedIndex;

            var lp = new ListProperties
            {
                ListId = _listId != Guid.Empty ? _listId : Guid.NewGuid(),
                Level = _level,
                MarkerIndentPt = (double)(_markerIndentBox.Value ?? 0m),
                TextIndentPt = (double)(_textIndentBox.Value ?? 0m),
                MarkerTextMinGapPt = (double)(_minGapBox.Value ?? 0m)
            };

            if (type == TypeNumbered)
            {
                lp.MarkerType = SystemIndexToType(_numberSystemCombo.SelectedIndex);
                lp.NumberPrefix = string.IsNullOrEmpty(_prefixBox.Text) ? null : _prefixBox.Text;
                lp.NumberSuffix = _suffixBox.Text ?? ".";
                lp.StartAt = (int)(_startAtBox.Value ?? 1m);
                lp.ContinueNumbering = _numberingModeCombo.SelectedIndex == 0;
            }
            else if (type == TypeSequence)
            {
                lp.MarkerType = ListMarkerType.CustomSequence;
                lp.CustomSequence = ParseSequence(_sequenceBox.Text);
                lp.SequenceWrap = _sequenceWrapCombo.SelectedIndex == 0;
                lp.NumberPrefix = string.IsNullOrEmpty(_prefixBox.Text) ? null : _prefixBox.Text;
                lp.NumberSuffix = string.IsNullOrEmpty(_suffixBox.Text) ? null : _suffixBox.Text;
                lp.StartAt = (int)(_startAtBox.Value ?? 1m);
                lp.ContinueNumbering = _numberingModeCombo.SelectedIndex == 0;
            }
            else
            {
                lp.MarkerType = ListMarkerType.Custom;
                lp.CustomMarker = string.IsNullOrEmpty(_bulletSymbolBox.Text) ? "•" : _bulletSymbolBox.Text;
            }

            return lp;
        }

        // Разбивает строку на отдельные символы-«номера» по пробелам и переводам строк.
        private static List<string> ParseSequence(string? text)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return result;
            var parts = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                result.Add(part);
                if (result.Count >= 500) break;
            }
            return result;
        }

        private void OnAnyComboChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            UpdateVisibleSection();
            UpdatePreview();
        }

        private void OnAnyTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            UpdatePreview();
        }

        private void OnAnyValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
        {
            if (_loading) return;
            UpdatePreview();
        }

        private void UpdateVisibleSection()
        {
            int type = _typeCombo.SelectedIndex;
            _bulletPanel.IsVisible = type == TypeBullet;
            _numberPanel.IsVisible = type == TypeNumbered;
            _sequencePanel.IsVisible = type == TypeSequence;
            // Префикс/суффикс, старт и режим нумерации нужны и нумерованному, и своему набору.
            _rulesPanel.IsVisible = type != TypeBullet;
        }

        private void UpdatePreview()
        {
            if (_previewSample is null) return;
            int type = _typeCombo.SelectedIndex;

            if (type == TypeNumbered)
            {
                var t = SystemIndexToType(_numberSystemCombo.SelectedIndex);
                int start = (int)(_startAtBox.Value ?? 1m);
                string prefix = _prefixBox.Text ?? string.Empty;
                string suffix = string.IsNullOrEmpty(_suffixBox.Text) ? "." : _suffixBox.Text!;
                _previewSample.Text =
                    SampleNumber(t, start, prefix, suffix) + " элемент      " +
                    SampleNumber(t, start + 1, prefix, suffix) + " элемент      " +
                    SampleNumber(t, start + 2, prefix, suffix) + " элемент";
            }
            else if (type == TypeSequence)
            {
                var seq = ParseSequence(_sequenceBox.Text);
                if (seq.Count == 0) { _previewSample.Text = "(введите символы)"; return; }
                string prefix = _prefixBox.Text ?? string.Empty;
                string suffix = _suffixBox.Text ?? string.Empty;
                var sb = new StringBuilder();
                for (int i = 0; i < 3; i++)
                {
                    int idx = i;
                    string sym = idx < seq.Count ? seq[idx]
                        : (_sequenceWrapCombo.SelectedIndex == 0 ? seq[idx % seq.Count] : seq[seq.Count - 1]);
                    sb.Append(prefix).Append(sym).Append(suffix).Append(" элемент      ");
                }
                _previewSample.Text = sb.ToString().TrimEnd();
            }
            else
            {
                string m = string.IsNullOrEmpty(_bulletSymbolBox.Text) ? "•" : _bulletSymbolBox.Text!;
                _previewSample.Text = $"{m}  элемент      {m}  элемент      {m}  элемент";
            }
        }

        private static string SampleNumber(ListMarkerType type, int n, string prefix, string suffix)
        {
            string num = type switch
            {
                ListMarkerType.DecimalLeadingZero => n < 10 ? "0" + n : n.ToString(),
                ListMarkerType.LowerAlpha => ToAlpha(n, false),
                ListMarkerType.UpperAlpha => ToAlpha(n, true),
                ListMarkerType.LowerRoman => ToRoman(n, false),
                ListMarkerType.UpperRoman => ToRoman(n, true),
                _ => n.ToString()
            };
            return prefix + num + suffix;
        }

        private static string ToAlpha(int number, bool upper)
        {
            var sb = new StringBuilder();
            int nn = number;
            while (nn > 0) { nn--; sb.Insert(0, (char)('a' + nn % 26)); nn /= 26; }
            return upper ? sb.ToString().ToUpperInvariant() : sb.ToString();
        }

        private static string ToRoman(int number, bool upper)
        {
            if (number <= 0 || number >= 4000) return number.ToString();
            (int v, string s)[] table =
            {
                (1000,"m"),(900,"cm"),(500,"d"),(400,"cd"),(100,"c"),(90,"xc"),
                (50,"l"),(40,"xl"),(10,"x"),(9,"ix"),(5,"v"),(4,"iv"),(1,"i")
            };
            var sb = new StringBuilder();
            int nn = number;
            foreach (var (v, s) in table) while (nn >= v) { sb.Append(s); nn -= v; }
            return upper ? sb.ToString().ToUpperInvariant() : sb.ToString();
        }
    }
}
