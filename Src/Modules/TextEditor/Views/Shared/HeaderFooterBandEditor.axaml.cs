using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Writersword.Modules.TextEditor.Views.Shared
{
    /// <summary>
    /// Поле правки полосы колонтитула на листе. Само ничего не применяет: отдаёт текст
    /// трёх мест виду, а тот передаёт его вью-модели документа.
    ///
    /// Правка применяется, когда фокус уходит из поля целиком (на лист, на ленту), —
    /// переход между местами полосы правкой не считается.
    /// </summary>
    public partial class HeaderFooterBandEditor : UserControl
    {
        private readonly TextBox _left;
        private readonly TextBox _center;
        private readonly TextBox _right;
        private readonly Panel _band;
        private readonly Panel _centerHost;
        private readonly Panel _rightHost;
        private readonly Border _mismatchPanel;
        private readonly TextBlock _mismatchText;

        // Место, где каретка стояла последней: туда вставляются поля.
        private TextBox? _lastSlot;

        private bool _header = true;

        // Текст мест, с которым поле открылось или который был принят последним:
        // уход фокуса без правки шага отмены не заводит.
        private string[] _baseline = { string.Empty, string.Empty, string.Empty };

        // Проверка ухода фокуса уже поставлена в очередь.
        private bool _focusCheckPending;

        // Шрифт мест, поставленный последним: поле ставится заново на каждом проходе
        // раскладки полотна, и шрифт меняется, только если поменялся.
        private (string? Family, double Size, bool Bold, bool Italic)? _slotFont;

        /// <summary>Правку приняли: текст левого, среднего и правого места.</summary>
        public event Action<string[]>? Committed;

        /// <summary>Esc: выйти из колонтитулов.</summary>
        public event Action? ExitRequested;

        /// <summary>
        /// Ctrl+Z в поле: отмена общая для документа, как везде в редакторе. Начатая
        /// правка полосы перед этим принимается — она и откатится первой.
        /// </summary>
        public event Action? UndoRequested;

        /// <summary>Ctrl+Y (Ctrl+Shift+Z) в поле: повтор, общий для документа.</summary>
        public event Action? RedoRequested;

        /// <summary>На вопрос о счёте ответили «продолжить счёт отсюда».</summary>
        public event Action? ContinueCountingChosen;

        /// <summary>На вопрос о счёте ответили «только эта страница».</summary>
        public event Action? KeepSingleChosen;

        public HeaderFooterBandEditor()
        {
            InitializeComponent();

            _left = this.FindControl<TextBox>("LeftSlot")!;
            _center = this.FindControl<TextBox>("CenterSlot")!;
            _right = this.FindControl<TextBox>("RightSlot")!;
            _band = this.FindControl<Panel>("BandPanel")!;
            _centerHost = this.FindControl<Panel>("CenterHost")!;
            _rightHost = this.FindControl<Panel>("RightHost")!;
            _mismatchPanel = this.FindControl<Border>("MismatchPanel")!;
            _mismatchText = this.FindControl<TextBlock>("MismatchText")!;

            foreach (var slot in new[] { _left, _center, _right })
            {
                var box = slot;
                box.AddHandler(KeyDownEvent, OnSlotKeyDown, RoutingStrategies.Tunnel);
                box.GotFocus += (_, _) => _lastSlot = box;
                box.LostFocus += (_, _) => ScheduleFocusCheck();
            }

            _band.PointerPressed += OnBandPointerPressed;

            this.FindControl<Button>("ContinueCountingButton")!.Click += (_, _) => ContinueCountingChosen?.Invoke();
            this.FindControl<Button>("KeepSingleButton")!.Click += (_, _) => KeepSingleChosen?.Invoke();
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        /// <summary>Поле открыто.</summary>
        public bool IsEditing => IsVisible;

        /// <summary>Показан вопрос о счёте.</summary>
        public bool IsAsking => IsVisible && _mismatchPanel.IsVisible;

        /// <summary>Каретка стоит в одном из мест полосы.</summary>
        public bool HasSlotFocus => _left.IsFocused || _center.IsFocused || _right.IsFocused;

        /// <summary>
        /// Сдвиг полосы от верха поля: у нижнего колонтитула над полосой может стоять
        /// вопрос о счёте. Вид вычитает его, ставя поле так, чтобы полоса легла на лист.
        /// </summary>
        public double BandOffsetY
        {
            get
            {
                if (_header || !_mismatchPanel.IsVisible) return 0;
                double bar = _mismatchPanel.Bounds.Height;
                if (bar <= 0) bar = 30;
                return bar + _mismatchPanel.Margin.Top + _mismatchPanel.Margin.Bottom;
            }
        }

        /// <summary>
        /// Открывает правку полосы.
        /// </summary>
        /// <param name="texts">Текст трёх мест со значениями полей этого листа.</param>
        /// <param name="header">Верхний колонтитул (иначе нижний).</param>
        /// <param name="focusSlot">Место, куда встаёт каретка: 0 — лево, 1 — центр, 2 — право.</param>
        public void Open(string[] texts, bool header, int focusSlot)
        {
            _header = header;

            // У верхнего колонтитула вопрос о счёте стоит под полосой, у нижнего — над ней.
            Grid.SetRow(_band, header ? 0 : 1);
            Grid.SetRow(_mismatchPanel, header ? 1 : 0);

            _mismatchPanel.IsVisible = false;
            Load(texts);
            IsVisible = true;

            FocusSlot(focusSlot, true);
        }

        /// <summary>
        /// Показывает принятый текст мест, не трогая каретку: правку применили, и
        /// значения полей листа могли измениться.
        /// </summary>
        public void Load(string[] texts)
        {
            _left.Text = texts.Length > 0 ? texts[0] : string.Empty;
            _center.Text = texts.Length > 1 ? texts[1] : string.Empty;
            _right.Text = texts.Length > 2 ? texts[2] : string.Empty;
            _baseline = GetTexts();
        }

        /// <summary>Ставит каретку в место полосы: в конец текста или туда, где она стояла.</summary>
        public void FocusSlot(int slot, bool toEnd)
        {
            var target = slot switch
            {
                0 => _left,
                2 => _right,
                _ => _center
            };

            _lastSlot = target;
            Dispatcher.UIThread.Post(() =>
            {
                target.Focus();
                if (toEnd) target.CaretIndex = target.Text?.Length ?? 0;
            }, DispatcherPriority.Input);
        }

        /// <summary>Размер полосы в точках экрана — по полосе листа.</summary>
        public void SetBandSize(double width, double height)
        {
            _band.Width = Math.Max(width, 40);
            _band.MinHeight = Math.Max(height, 12);

            // Место не шире полосы: длинный текст уходит под соседние места так же,
            // как на листе, но не вылезает за поле листа.
            foreach (var box in new[] { _left, _center, _right })
                box.MaxWidth = Math.Max(width, 40);
        }

        /// <summary>
        /// Где стоят среднее и правое места, точки экрана от левого края полосы: середина
        /// среднего и правый край правого — по позициям колонтитула.
        /// </summary>
        public void SetSlotAnchors(double centerPx, double rightPx)
        {
            double center = Math.Max(centerPx * 2.0, 1.0);
            double right = Math.Max(rightPx, 1.0);

            if (Math.Abs(_centerHost.Width - center) > 0.5 || double.IsNaN(_centerHost.Width)) _centerHost.Width = center;
            if (Math.Abs(_rightHost.Width - right) > 0.5 || double.IsNaN(_rightHost.Width)) _rightHost.Width = right;
        }

        /// <summary>Шрифт и цвет мест — как у колонтитулов листа, в масштабе полотна.</summary>
        public void SetSlotFont(string? family, double sizePx, bool bold, bool italic)
        {
            var font = (family, sizePx, bold, italic);
            if (_slotFont == font) return;
            _slotFont = font;

            foreach (var box in new[] { _left, _center, _right })
            {
                box.FontSize = Math.Max(sizePx, 4);
                box.FontWeight = bold ? FontWeight.Bold : FontWeight.Normal;
                box.FontStyle = italic ? FontStyle.Italic : FontStyle.Normal;

                if (!string.IsNullOrWhiteSpace(family))
                    box.FontFamily = new FontFamily(family);
                else
                    box.ClearValue(TextBox.FontFamilyProperty);
            }
        }

        /// <summary>Цвет текста мест — цвет колонтитулов на этой бумаге.</summary>
        public void SetInk(Color ink)
        {
            foreach (var box in new[] { _left, _center, _right })
            {
                if (box.Foreground is ISolidColorBrush current && current.Color == ink) continue;
                var brush = new SolidColorBrush(ink);
                box.Foreground = brush;
                box.CaretBrush = brush;
            }
        }

        /// <summary>Текст трёх мест.</summary>
        public string[] GetTexts() => new[]
        {
            Normalize(_left.Text),
            Normalize(_center.Text),
            Normalize(_right.Text)
        };

        /// <summary>Вопрос о счёте рядом с полосой: набранное число разошлось со счётом.</summary>
        public void Ask(string message)
        {
            _mismatchText.Text = message;
            _mismatchPanel.IsVisible = true;
        }

        /// <summary>Убирает вопрос о счёте.</summary>
        public void HideAsk() => _mismatchPanel.IsVisible = false;

        /// <summary>Прячет поле целиком.</summary>
        public void Hide()
        {
            _mismatchPanel.IsVisible = false;
            IsVisible = false;
        }

        /// <summary>Принимает правку, если текст мест изменился.</summary>
        public void Commit()
        {
            if (!IsVisible) return;

            var texts = GetTexts();
            if (SameTexts(texts, _baseline)) return;

            _baseline = texts;
            Committed?.Invoke(texts);
        }

        /// <summary>Вставляет поле (номер или число страниц) туда, где стоит каретка.</summary>
        public void InsertToken(string token)
        {
            var box = _lastSlot ?? _center;
            InsertText(box, token);
            box.Focus();
        }

        private void ScheduleFocusCheck()
        {
            if (_focusCheckPending) return;
            _focusCheckPending = true;

            // Переход из места в место — это потеря фокуса одним полем и получение
            // другим. Решение принимается, когда фокус уже осел.
            Dispatcher.UIThread.Post(() =>
            {
                _focusCheckPending = false;
                if (IsVisible && !HasSlotFocus) Commit();
            }, DispatcherPriority.Background);
        }

        private void OnBandPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            // Щелчок по самому месту TextBox разбирает сам.
            if (!ReferenceEquals(e.Source, _band)) return;

            double third = Math.Max(_band.Bounds.Width / 3.0, 1.0);
            int slot = Math.Clamp((int)(e.GetPosition(_band).X / third), 0, 2);
            FocusSlot(slot, true);
            e.Handled = true;
        }

        private void OnSlotKeyDown(object? sender, KeyEventArgs e)
        {
            if (sender is not TextBox) return;

            if (e.Key == Key.Escape)
            {
                Commit();
                ExitRequested?.Invoke();
                e.Handled = true;
                return;
            }

            bool ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
            bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;

            if (ctrl && (e.Key == Key.Y || (e.Key == Key.Z && shift)))
            {
                Commit();
                RedoRequested?.Invoke();
                e.Handled = true;
                return;
            }

            if (ctrl && e.Key == Key.Z)
            {
                Commit();
                UndoRequested?.Invoke();
                e.Handled = true;
                return;
            }

            // Правку продолжили — вопрос о счёте больше не к месту.
            if (_mismatchPanel.IsVisible) HideAsk();
        }

        /// <summary>Точка внутри вопроса о счёте: щелчок по нему вопрос не закрывает.</summary>
        public bool IsInsideAsk(Visual? visual)
        {
            while (visual is not null)
            {
                if (ReferenceEquals(visual, _mismatchPanel)) return true;
                visual = visual.GetVisualParent();
            }
            return false;
        }

        private static void InsertText(TextBox box, string text)
        {
            string current = box.Text ?? string.Empty;

            int start = Math.Min(box.SelectionStart, box.SelectionEnd);
            int end = Math.Max(box.SelectionStart, box.SelectionEnd);
            start = Math.Clamp(start, 0, current.Length);
            end = Math.Clamp(end, start, current.Length);

            if (start == end)
            {
                start = Math.Clamp(box.CaretIndex, 0, current.Length);
                end = start;
            }

            box.Text = current.Substring(0, start) + text + current.Substring(end);
            box.SelectionStart = box.SelectionEnd = box.CaretIndex = start + text.Length;
        }

        private static bool SameTexts(string[] a, string[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }

        // Переводы строк TextBox бывают "\r\n" — в шаблоне колонтитула строки делит "\n".
        private static string Normalize(string? text)
            => (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
    }
}
