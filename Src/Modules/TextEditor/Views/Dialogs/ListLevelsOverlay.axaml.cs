using System;
using System.Collections.Generic;
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
    /// Оверлей «Настроить уровни» многоуровневого списка. Для каждого из первых уровней
    /// задаётся тип маркера. Возвращает схему (тип маркера по уровню, длиной 9) через ShowAsync.
    /// </summary>
    public partial class ListLevelsOverlay : UserControl
    {
        private const int ConfigurableLevels = 5;
        private const int TotalLevels = 9;

        private TaskCompletionSource<List<ListMarkerType>?>? _tcs;

        private Border _scrim = null!;
        private readonly ComboBox[] _levelCombos = new ComboBox[ConfigurableLevels];

        // Соответствие индекса пункта ComboBox → тип маркера.
        private static readonly ListMarkerType[] TypeByIndex =
        {
            ListMarkerType.Decimal,            // 1.
            ListMarkerType.DecimalLeadingZero, // 01.
            ListMarkerType.LowerAlpha,         // a.
            ListMarkerType.UpperAlpha,         // A.
            ListMarkerType.LowerRoman,         // i.
            ListMarkerType.UpperRoman,         // I.
            ListMarkerType.Bullet,             // •
            ListMarkerType.Circle,             // ◦
            ListMarkerType.Square,             // ▪
            ListMarkerType.Dash,               // –
            ListMarkerType.Arrow               // ➤
        };

        public ListLevelsOverlay()
        {
            InitializeComponent();
            IsVisible = false;

            _scrim = this.FindControl<Border>("Scrim")!;
            for (int i = 0; i < ConfigurableLevels; i++)
                _levelCombos[i] = this.FindControl<ComboBox>("Level" + (i + 1) + "Combo")!;

            var okBtn = this.FindControl<Button>("OkBtn")!;
            var cancelBtn = this.FindControl<Button>("CancelBtn")!;
            var closeBtn = this.FindControl<Button>("CloseBtn")!;
            okBtn.Click += OnOk;
            cancelBtn.Click += OnCancel;
            closeBtn.Click += OnCancel;
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
            if (e.Key == Key.Escape) { CompleteCancel(); e.Handled = true; }
        }

        /// <summary>
        /// Показывает оверлей. current — текущая схема (может быть null). Возвращает новую схему
        /// длиной 9 или null при отмене.
        /// </summary>
        public Task<List<ListMarkerType>?> ShowAsync(List<ListMarkerType>? current)
        {
            _tcs?.TrySetResult(null);
            _tcs = new TaskCompletionSource<List<ListMarkerType>?>();

            LoadFrom(current);
            IsVisible = true;
            Focus();
            return _tcs.Task;
        }

        private void Complete(List<ListMarkerType>? result)
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

        private static int TypeToIndex(ListMarkerType type)
        {
            for (int i = 0; i < TypeByIndex.Length; i++)
                if (TypeByIndex[i] == type) return i;
            return 0;
        }

        private void LoadFrom(List<ListMarkerType>? current)
        {
            // Значения по умолчанию, если схема не задана.
            var defaults = new[]
            {
                ListMarkerType.Decimal, ListMarkerType.LowerAlpha, ListMarkerType.LowerRoman,
                ListMarkerType.Decimal, ListMarkerType.LowerAlpha
            };

            for (int i = 0; i < ConfigurableLevels; i++)
            {
                ListMarkerType t = (current is not null && i < current.Count) ? current[i] : defaults[i];
                _levelCombos[i].SelectedIndex = TypeToIndex(t);
            }
        }

        private List<ListMarkerType> BuildResult()
        {
            var configured = new List<ListMarkerType>(ConfigurableLevels);
            for (int i = 0; i < ConfigurableLevels; i++)
            {
                int idx = Math.Clamp(_levelCombos[i].SelectedIndex, 0, TypeByIndex.Length - 1);
                configured.Add(TypeByIndex[idx]);
            }

            // Дополняем до 9 уровней, повторяя настроенные по кругу.
            var scheme = new List<ListMarkerType>(TotalLevels);
            for (int i = 0; i < TotalLevels; i++)
                scheme.Add(configured[i % ConfigurableLevels]);
            return scheme;
        }
    }
}
