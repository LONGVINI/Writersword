using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Writersword.Modules.TextEditor.ViewModels.Blocks;

namespace Writersword.Modules.TextEditor.Views.Document
{
    public partial class EditorParagraphView : UserControl
    {
        private ParagraphViewModel? _vm;
        private TextBox? _box;
        private Border? _border;

        public EditorParagraphView()
        {
            InitializeComponent();
            _box = this.FindControl<TextBox>("ParagraphBox");
            _border = this.FindControl<Border>("SelectionBorder");
            WireBoxEvents();
            DataContextChanged += OnDataContextChanged;
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        // ── DataContext ────────────────────────────────────────────────

        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnVmPropertyChanged;
                _vm.FocusRequested -= OnFocusRequested;
                _vm.RequestFocusAtPosition = null;
                _vm.OnActivated = null;
                _vm.OnSelectionChanged = null;
            }

            _vm = DataContext as ParagraphViewModel;

            if (_vm is null) return;

            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.FocusRequested += OnFocusRequested;

            _vm.RequestFocusAtPosition = pos =>
                Dispatcher.UIThread.Post(() =>
                {
                    if (_box is null) return;
                    _box.Focus();
                    _box.SelectionStart = pos;
                    _box.SelectionEnd = pos;
                }, DispatcherPriority.Input);

            _vm.OnActivated = pvm => { /* уже обрабатывается в GotFocus */ };
            _vm.OnSelectionChanged = _ => { };

            // Применяем начальное состояние IsSelected.
            ApplySelectedClass();
        }

        // ── Подписка на события TextBox ────────────────────────────────

        private void WireBoxEvents()
        {
            if (_box is null) return;

            _box.GotFocus += (_, _) =>
            {
                _vm?.OnActivated?.Invoke(_vm);
                // Снимаем document-level выделение при входе в TextBox.
                _vm?.RequestClearSelection?.Invoke();
            };

            _box.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.SelectionStartProperty
                 || e.Property == TextBox.SelectionEndProperty)
                {
                    if (_vm is null || _box is null) return;
                    _vm.SelectionStart = _box.SelectionStart;
                    _vm.SelectionEnd = _box.SelectionEnd;
                    _vm.OnSelectionChanged?.Invoke(_vm);
                }
            };

            _box.AddHandler(KeyDownEvent, OnBoxKeyDown,
                Avalonia.Interactivity.RoutingStrategies.Tunnel);
        }

        private void OnBoxKeyDown(object? sender, KeyEventArgs e)
        {
            if (_vm is null || _box is null) return;

            // ── Enter → новый параграф ─────────────────────────────────
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                int caret = _box.SelectionStart;
                string before = (_box.Text ?? "")[..caret];
                string after = (_box.Text ?? "")[caret..];

                _vm.PlainText = before;
                var newVm = _vm.RequestAddAfter?.Invoke(_vm);
                if (newVm is not null)
                {
                    newVm.PlainText = after;
                    // Фокус на новом параграфе запрашивает сам RequestAddAfter.
                }
                return;
            }

            // ── Backspace ──────────────────────────────────────────────
            if (e.Key == Key.Back && _box.SelectionStart == 0 && _box.SelectionEnd == 0)
            {
                e.Handled = true;
                if (string.IsNullOrEmpty(_box.Text))
                    _vm.RequestDelete?.Invoke(_vm);
                else
                    _vm.RequestMergeWithPrevious?.Invoke(_vm, _vm.PlainText);
                return;
            }

            // ── Ctrl+A: первое нажатие — выделить всё в абзаце,
            //            второе — выделить весь документ ───────────────
            if (e.Key == Key.A && e.KeyModifiers == KeyModifiers.Control)
            {
                bool docSelected = _vm.RequestGetDocumentSelectedText?.Invoke() is not null;
                if (!docSelected)
                {
                    // Если весь текст TextBox уже выделен — выделяем весь документ.
                    bool allInBox = _box.SelectionStart == 0
                                 && _box.SelectionEnd == (_box.Text?.Length ?? 0);
                    if (allInBox)
                        _vm.RequestSelectAll?.Invoke();
                    else
                        _box.SelectAll();
                }
                else
                {
                    _vm.RequestClearSelection?.Invoke();
                    _box.SelectAll();
                }
                e.Handled = true;
            }
        }

        // ── ViewModel → View ───────────────────────────────────────────

        private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ParagraphViewModel.IsSelected))
                ApplySelectedClass();
        }

        /// <summary>
        /// Добавляет/убирает CSS-класс "selected" на Border.
        /// Именно этот класс включает синий фон абзаца при мультивыделении.
        /// </summary>
        private void ApplySelectedClass()
        {
            if (_border is null) return;

            if (_vm?.IsSelected == true)
            {
                if (!_border.Classes.Contains("selected"))
                    _border.Classes.Add("selected");
            }
            else
            {
                _border.Classes.Remove("selected");
            }
        }

        private void OnFocusRequested()
        {
            Dispatcher.UIThread.Post(() => _box?.Focus(), DispatcherPriority.Input);
        }
    }
}