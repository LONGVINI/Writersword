using Avalonia;
using Dock.Model.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using Writersword.Core.Interfaces.WorkFlows;
using Writersword.Views;

namespace Writersword.Infrastructure.Dock
{
    /// <summary>
    /// Реализация IHostWindow для Float окон
    /// Управляет жизненным циклом плавающих окон модулей
    /// Dock.Avalonia сам управляет регистрацией DockWindow в rootDock.Windows —
    /// ручное добавление не требуется и приводит к дублированию окон
    /// </summary>
    public class HostWindow : IHostWindow
    {
        private readonly ILogger<HostWindow> _logger;
        private FloatingWindowView? _window;
        private IDock? _pendingLayout;
        private PixelPoint? _pendingPosition;

        public IHostWindowState? HostWindowState { get; set; }
        public bool IsTracked { get; set; }

        public IDockWindow? Window
        {
            get => _window;
            set => _window = value as FloatingWindowView;
        }

        public HostWindow()
        {
            _logger = App.Services.GetService<ILogger<HostWindow>>()!;
        }

        /// <summary>
        /// Показать Float окно
        /// Dock.Avalonia самостоятельно регистрирует DockWindow в rootDock.Windows
        /// </summary>
        public void Present(bool isDialog)
        {
            _logger.LogDebug("Present() called, hasLayout={HasLayout}", _pendingLayout != null);

            _window = new FloatingWindowView();

            if (_pendingPosition.HasValue)
                _window.Position = _pendingPosition.Value;

            if (_pendingLayout != null)
            {
                var rootDock = FindRootDock(_pendingLayout);
                if (rootDock != null)
                {
                    _window.Layout = rootDock;
                    _window.Factory = _pendingLayout.Factory;
                    _logger.LogDebug("Window layout and factory set");
                }
                else
                {
                    _logger.LogWarning("FindRootDock returned null");
                }
            }

            _window.Closed += OnWindowClosed;
            _window.Show();
            _pendingPosition = null;

            _logger.LogDebug("Present() complete");
        }

        /// <summary>
        /// Закрыть Float окно
        /// </summary>
        public void Exit()
        {
            _window?.Close();
            _window = null;
        }

        /// <summary>
        /// Установить позицию окна
        /// </summary>
        public void SetPosition(double x, double y)
        {
            _pendingPosition = new PixelPoint((int)x, (int)y);
        }

        /// <summary>
        /// Получить позицию окна
        /// </summary>
        public void GetPosition(out double x, out double y)
        {
            if (_window != null)
            {
                x = _window.Position.X;
                y = _window.Position.Y;
            }
            else
            {
                x = 0;
                y = 0;
            }
        }

        /// <summary>
        /// Установить размер окна
        /// </summary>
        public void SetSize(double width, double height)
        {
            if (_window != null)
            {
                _window.Width = width;
                _window.Height = height;
            }
        }

        /// <summary>
        /// Получить размер окна
        /// </summary>
        public void GetSize(out double width, out double height)
        {
            if (_window != null)
            {
                width = _window.Width;
                height = _window.Height;
            }
            else
            {
                width = 800;
                height = 600;
            }
        }

        /// <summary>
        /// Установить заголовок окна
        /// </summary>
        public void SetTitle(string? title)
        {
        }

        /// <summary>
        /// Установить Layout для окна
        /// </summary>
        public void SetLayout(IDock layout)
        {
            _pendingLayout = layout;

            if (_window != null)
            {
                var rootDock = FindRootDock(layout);
                if (rootDock != null)
                {
                    _window.Layout = rootDock;
                    _window.Factory = layout.Factory;
                }
            }
        }

        /// <summary>
        /// Активировать окно
        /// </summary>
        public void SetActive()
        {
            _window?.Activate();
        }

        /// <summary>
        /// Обработчик закрытия Float окна
        /// Уведомляет WorkspaceController о закрытии каждого модуля в окне
        /// Удаляет DockWindow из rootDock.Windows — это единственное место где мы чистим коллекцию
        /// </summary>
        private void OnWindowClosed(object? sender, EventArgs e)
        {
            _logger.LogDebug("Float window closed");

            if (_window != null)
                _window.Closed -= OnWindowClosed;

            if (_pendingLayout == null)
            {
                _logger.LogDebug("No pending layout, nothing to clean up");
                _window = null;
                return;
            }

            var floatDock = FindDocumentDockInLayout(_pendingLayout);
            if (floatDock?.VisibleDockables == null)
            {
                _logger.LogDebug("No DocumentDock in float window");
                _window = null;
                return;
            }

            var documentsToClose = floatDock.VisibleDockables
                .OfType<Document>()
                .ToList();

            _logger.LogDebug("Closing {Count} modules from float window", documentsToClose.Count);

            var tabCollection = App.Services.GetRequiredService<ITabCollection>();
            var activeTab = tabCollection.ActiveTab;

            foreach (var document in documentsToClose)
            {
                if (string.IsNullOrWhiteSpace(document.Id) || !document.Id.StartsWith("Module_"))
                {
                    _logger.LogWarning("Invalid document Id: '{DocumentId}', skipping", document.Id);
                    continue;
                }

                string moduleType = document.Id.Replace("Module_", "");

                if (string.IsNullOrWhiteSpace(moduleType))
                {
                    _logger.LogWarning("Empty moduleType after parsing '{DocumentId}', skipping", document.Id);
                    continue;
                }

                if (!document.CanClose)
                {
                    _logger.LogDebug("Module {moduleType} is required, returning to dock", moduleType);
                    ReturnRequiredModuleToDock(moduleType);
                }
                else
                {
                    _logger.LogDebug("Module {moduleType} closed with float window", moduleType);
                    activeTab?.Workspace?.HandleModuleClosedInDock(moduleType);
                }
            }

            if (activeTab?.Workspace != null)
            {
                var mainRootDock = activeTab.Workspace.GetCurrentLayout();

                if (mainRootDock?.Windows != null)
                {
                    var windowToRemove = mainRootDock.Windows.FirstOrDefault(w => w.Host == this);

                    if (windowToRemove != null)
                    {
                        mainRootDock.Windows.Remove(windowToRemove);
                        _logger.LogDebug("Removed DockWindow from rootDock.Windows: {WindowId}", windowToRemove.Id);
                    }
                    else
                    {
                        _logger.LogWarning("DockWindow not found in rootDock.Windows");
                    }
                }
            }

            _window = null;
            _logger.LogDebug("Float window cleanup complete");
        }

        /// <summary>
        /// Вернуть обязательный модуль из Float окна обратно в Dock
        /// </summary>
        private void ReturnRequiredModuleToDock(string moduleType)
        {
            _logger.LogDebug("Returning required module to dock: {moduleType}", moduleType);

            var tabCollection = App.Services.GetRequiredService<ITabCollection>();
            if (tabCollection.ActiveTab?.Workspace == null)
            {
                _logger.LogWarning("No active tab or Workspace");
                return;
            }

            tabCollection.ActiveTab.Workspace.ReturnRequiredModuleToDock(moduleType);

            _logger.LogDebug("Module {moduleType} returned to dock", moduleType);
        }

        /// <summary>
        /// Найти DocumentDock внутри Layout
        /// </summary>
        private DocumentDock? FindDocumentDockInLayout(IDock? layout)
        {
            if (layout == null) return null;

            if (layout is DocumentDock dd) return dd;

            if (layout is IRootDock rootDock && rootDock.VisibleDockables != null)
            {
                foreach (var child in rootDock.VisibleDockables)
                {
                    if (child is DocumentDock docDock)
                        return docDock;
                }
            }

            if (layout.VisibleDockables != null)
            {
                foreach (var child in layout.VisibleDockables)
                {
                    if (child is DocumentDock docDock)
                        return docDock;

                    if (child is IDock childDock)
                    {
                        var found = FindDocumentDockInLayout(childDock);
                        if (found != null) return found;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Найти RootDock в иерархии
        /// </summary>
        private IRootDock? FindRootDock(IDock? layout)
        {
            if (layout == null) return null;
            if (layout is IRootDock root) return root;

            IDockable? current = layout;
            while (current != null)
            {
                if (current is IRootDock rootDock) return rootDock;
                current = current.Owner;
            }

            return null;
        }

        /// <summary>
        /// Получить FloatingWindowView (для фокусировки из MainWindowViewModel)
        /// </summary>
        public FloatingWindowView? GetWindow()
        {
            return _window;
        }
    }
}