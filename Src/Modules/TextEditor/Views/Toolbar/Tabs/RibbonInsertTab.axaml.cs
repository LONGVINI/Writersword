using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Writersword.Modules.TextEditor.Document;
using Writersword.Modules.TextEditor.ViewModels.Toolbar;
using Writersword.Styles.UserControls;

namespace Writersword.Modules.TextEditor.Views.Toolbar.Tabs
{
    public partial class RibbonInsertTab : UserControl
    {
        private RibbonScrollContainer? _scrollContainer;

        public RibbonInsertTab()
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
            SetupTablePicker();
        }

        private void SetupTablePicker()
        {
            var popupHost = this.FindControl<Canvas>("TablePopupHost");

            var mainBtn = this.FindControl<Button>("BtnInsertTable");
            if (mainBtn is not null)
                AttachTablePopup(mainBtn, popupHost);

            var flyoutBtn = this.FindControl<Button>("BtnInsertTableFlyout");
            if (flyoutBtn is not null)
                AttachTablePopup(flyoutBtn, popupHost);
        }

        private void AttachTablePopup(Button anchorBtn, Canvas? popupHost)
        {
            var picker = new TableGridPickerControl();

            Popup? popup = null;
            picker.TableSelected += (rows, cols) =>
            {
                if (popup is not null)
                    popup.IsOpen = false;

                if (DataContext is RibbonInsertTabViewModel vm)
                    vm.InsertTableWithSize(rows, cols);
            };

            // Берём кисти из ресурсов приложения с безопасным fallback.
            IBrush bgBrush = Brushes.White;
            IBrush bordBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));

            if (Avalonia.Application.Current?.Resources
                    .TryGetValue("BgSurfaceBrush", out var bgObj) == true
                && bgObj is IBrush bgBrushFound)
                bgBrush = bgBrushFound;

            if (Avalonia.Application.Current?.Resources
                    .TryGetValue("BorderDefaultBrush", out var bordObj) == true
                && bordObj is IBrush bordBrushFound)
                bordBrush = bordBrushFound;

            var popupContent = new Border
            {
                Background = bgBrush,
                BorderBrush = bordBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                BoxShadow = new BoxShadows(new BoxShadow
                {
                    Blur = 10,
                    OffsetX = 0,
                    OffsetY = 3,
                    Color = Color.FromArgb(50, 0, 0, 0)
                }),
                Padding = new Thickness(4),
                Child = picker
            };

            popup = new Popup
            {
                PlacementTarget = anchorBtn,
                Placement = PlacementMode.Bottom,
                IsLightDismissEnabled = true,
                Child = popupContent
            };

            popupHost?.Children.Add(popup);

            anchorBtn.Click += (_, _) =>
            {
                popup.IsOpen = !popup.IsOpen;
            };
        }

        private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (DataContext is RibbonInsertTabViewModel vm)
            {
                vm.UpdateLayout(e.NewSize.Width);
                if (_scrollContainer is not null)
                {
                    _scrollContainer.ArrowsVisible = true;
                    _scrollContainer.NotifySizeChanged();
                }
            }
        }
    }
}