using Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Writersword.Views
{
    public partial class FloatingWindowView : Window, IDockWindow
    {
        private readonly ILogger<FloatingWindowView> _logger;

        public FloatingWindowView()
        {
            _logger = App.Services.GetService<ILogger<FloatingWindowView>>()!;

            InitializeComponent();
            Id = Guid.NewGuid().ToString();

            _logger.LogDebug("FloatingWindowView created, ID: {Id}", Id);

            Activated += (s, e) =>
            {
                _logger.LogDebug("FloatingWindowView activated: {Title}", Title);
            };

            Deactivated += (s, e) =>
            {
                _logger.LogDebug("FloatingWindowView deactivated: {Title}", Title);
            };

            PropertyChanged += (s, e) =>
            {
                if (e.Property.Name == nameof(IsVisible))
                    _logger.LogDebug("IsVisible changed: {IsVisible} for {Title}", IsVisible, Title);
                if (e.Property.Name == nameof(WindowState))
                    _logger.LogDebug("WindowState changed: {WindowState} for {Title}", WindowState, Title);
            };

            Closing += (s, e) =>
            {
                _logger.LogDebug("FloatingWindowView closing: {Title}", Title);
                bool canClose = OnClose();
                if (!canClose)
                {
                    _logger.LogDebug("Close blocked - contains uncloseable content");
                    e.Cancel = false;
                }
            };

            Closed += (s, e) =>
            {
                _logger.LogDebug("FloatingWindowView closed: {Title}", Title);
            };
        }

        // ── IDockWindow ───────────────────────────────────────────────────
        public string Id { get; set; }

        public double X
        {
            get => Position.X;
            set => Position = new Avalonia.PixelPoint((int)value, Position.Y);
        }

        public double Y
        {
            get => Position.Y;
            set => Position = new Avalonia.PixelPoint(Position.X, (int)value);
        }

        public new string Title
        {
            get => base.Title ?? string.Empty;
            set => base.Title = value;
        }

        // Новые члены Dock 12
        public new DockWindowState WindowState
        {
            get => base.WindowState switch
            {
                Avalonia.Controls.WindowState.Minimized => DockWindowState.Minimized,
                Avalonia.Controls.WindowState.Maximized => DockWindowState.Maximized,
                Avalonia.Controls.WindowState.FullScreen => DockWindowState.FullScreen,
                _ => DockWindowState.Normal
            };
            set => base.WindowState = value switch
            {
                DockWindowState.Minimized => Avalonia.Controls.WindowState.Minimized,
                DockWindowState.Maximized => Avalonia.Controls.WindowState.Maximized,
                DockWindowState.FullScreen => Avalonia.Controls.WindowState.FullScreen,
                _ => Avalonia.Controls.WindowState.Normal
            };
        }

        public bool IsModal { get; set; }
        public DockWindowOwnerMode OwnerMode { get; set; }
        public IDockWindow? ParentWindow { get; set; }

        public new bool? ShowInTaskbar
        {
            get => base.ShowInTaskbar;
            set => base.ShowInTaskbar = value ?? true;
        }

        public new IDockable? Owner { get; set; }
        public IFactory? Factory { get; set; }

        public IRootDock? Layout
        {
            get => DockControlHost.Layout as IRootDock;
            set => DockControlHost.Layout = value;
        }

        public IHostWindow? Host { get; set; }

        // ── IDockWindow методы ────────────────────────────────────────────
        public bool OnClose()
        {
            bool canCloseContent = CanCloseFloatingContent();
            if (!canCloseContent)
                _logger.LogDebug("Window contains uncloseable modules - will return to dock");
            return true;
        }

        private bool CanCloseFloatingContent()
        {
            if (Layout == null) return true;

            var documents = FindAllDockables(Layout);
            foreach (var dockable in documents)
            {
                var canCloseProperty = dockable.GetType().GetProperty("CanClose");
                if (canCloseProperty != null)
                {
                    var canCloseValue = canCloseProperty.GetValue(dockable);
                    if (canCloseValue is bool canClose && !canClose)
                    {
                        _logger.LogDebug("Found uncloseable dockable: {Id}", dockable.Id);
                        return false;
                    }
                }
            }
            return true;
        }

        private List<IDockable> FindAllDockables(IDockable dockable)
        {
            var result = new List<IDockable>();
            result.Add(dockable);
            if (dockable is IDock dock && dock.VisibleDockables != null)
                foreach (var child in dock.VisibleDockables)
                    result.AddRange(FindAllDockables(child));
            return result;
        }

        public bool OnMoveDragBegin() => true;
        public void OnMoveDrag() { }
        public void OnMoveDragEnd() { }
        public void Save() { }

        public void Present(bool isDialog)
        {
            Show();
        }

        public void Exit()
        {
            Close();
        }

        public void SetActive()
        {
            Activate();
        }
    }
}