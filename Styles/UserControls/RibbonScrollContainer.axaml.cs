using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Writersword.Styles.UserControls
{
    public partial class RibbonScrollContainer : UserControl
    {
        private ScrollViewer? _scrollViewer;
        private Button? _btnLeft;
        private Button? _btnRight;
        private ContentPresenter? _contentHost;

        private const double ScrollStep = 120.0;

        public static readonly StyledProperty<object?> RibbonContentProperty =
            AvaloniaProperty.Register<RibbonScrollContainer, object?>(nameof(RibbonContent));

        public object? RibbonContent
        {
            get => GetValue(RibbonContentProperty);
            set => SetValue(RibbonContentProperty, value);
        }

        public static readonly StyledProperty<bool> ArrowsVisibleProperty =
            AvaloniaProperty.Register<RibbonScrollContainer, bool>(
                nameof(ArrowsVisible), defaultValue: true);

        public bool ArrowsVisible
        {
            get => GetValue(ArrowsVisibleProperty);
            set => SetValue(ArrowsVisibleProperty, value);
        }

        public RibbonScrollContainer()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == RibbonContentProperty && _contentHost is not null)
                _contentHost.Content = change.NewValue;

            if (change.Property == ArrowsVisibleProperty)
                UpdateArrowVisibility();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            AttachControls();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            DetachControls();
        }

        private void AttachControls()
        {
            _scrollViewer = this.FindControl<ScrollViewer>("RibbonScrollViewer");
            _btnLeft = this.FindControl<Button>("BtnScrollLeft");
            _btnRight = this.FindControl<Button>("BtnScrollRight");
            _contentHost = this.FindControl<ContentPresenter>("ContentHost");

            if (_contentHost is not null)
                _contentHost.Content = RibbonContent;

            if (_scrollViewer is not null)
            {
                _scrollViewer.ScrollChanged -= OnScrollChanged;
                _scrollViewer.ScrollChanged += OnScrollChanged;
            }

            if (_btnLeft is not null)
            {
                _btnLeft.Click -= OnScrollLeftClick;
                _btnLeft.Click += OnScrollLeftClick;
            }

            if (_btnRight is not null)
            {
                _btnRight.Click -= OnScrollRightClick;
                _btnRight.Click += OnScrollRightClick;
            }

            UpdateArrowVisibility();
        }

        private void DetachControls()
        {
            if (_scrollViewer is not null)
                _scrollViewer.ScrollChanged -= OnScrollChanged;
            if (_btnLeft is not null)
                _btnLeft.Click -= OnScrollLeftClick;
            if (_btnRight is not null)
                _btnRight.Click -= OnScrollRightClick;
        }

        public void NotifySizeChanged()
        {
            Dispatcher.UIThread.Post(UpdateArrowVisibility, DispatcherPriority.Render);
        }

        private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            UpdateArrowVisibility();
        }

        private void UpdateArrowVisibility()
        {
            if (_scrollViewer is null || _btnLeft is null || _btnRight is null) return;

            if (!ArrowsVisible)
            {
                _btnLeft.IsVisible = false;
                _btnRight.IsVisible = false;
                return;
            }

            double offset = _scrollViewer.Offset.X;
            double scrollable = _scrollViewer.ScrollBarMaximum.X;
            bool hasScroll = scrollable > 0.5;

            _btnLeft.IsVisible = hasScroll && offset > 0.5;
            _btnRight.IsVisible = hasScroll && offset < scrollable - 0.5;
        }

        private void OnScrollLeftClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_scrollViewer is null) return;
            double newOffset = System.Math.Max(0, _scrollViewer.Offset.X - ScrollStep);
            _scrollViewer.Offset = new Vector(newOffset, 0);
        }

        private void OnScrollRightClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_scrollViewer is null) return;
            double newOffset = System.Math.Min(
                _scrollViewer.ScrollBarMaximum.X,
                _scrollViewer.Offset.X + ScrollStep);
            _scrollViewer.Offset = new Vector(newOffset, 0);
        }
    }
}