using Avalonia;
using Dock.Model.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using Writersword.ViewModels;
using Writersword.Views;

namespace Writersword.Src.Infrastructure.Dock
{
    /// <summary>
    /// Реализация IHostWindow для Float окон
    /// Управляет жизненным циклом плавающих окон модулей
    /// </summary>
    public class HostWindow : IHostWindow
    {
        private FloatingWindow? _window;
        private IDock? _pendingLayout;
        private PixelPoint? _pendingPosition;

        public IHostWindowState? HostWindowState { get; set; }
        public bool IsTracked { get; set; }

        public IDockWindow? Window
        {
            get => _window;
            set => _window = value as FloatingWindow;
        }

        /// <summary>
        /// Показать Float окно
        /// </summary>
        public void Present(bool isDialog)
        {
            Console.WriteLine($"[HostWindow] Present() START");

            _window = new FloatingWindow();

            if (_pendingPosition.HasValue)
            {
                _window.Position = _pendingPosition.Value;
            }

            if (_pendingLayout != null)
            {
                var rootDock = FindRootDock(_pendingLayout);
                if (rootDock != null)
                {
                    _window.Layout = rootDock;
                    _window.Factory = _pendingLayout.Factory;
                }
            }

            _window.Closed += OnWindowClosed;
            _window.Show();
            _pendingPosition = null;

            if (_pendingLayout != null && _pendingLayout.Factory != null)
            {
                var rootDock = FindRootDock(_pendingLayout);
                if (rootDock != null && rootDock.Windows != null)
                {
                    var dockWindow = _pendingLayout.Factory.CreateDockWindow();
                    dockWindow.Host = this;

                    var rootDockForWindow = FindRootDock(_pendingLayout);
                    if (rootDockForWindow != null)
                    {
                        dockWindow.Layout = rootDockForWindow;
                    }

                    dockWindow.Id = "Float_" + (_pendingLayout as IDockable)?.Id;

                    rootDock.Windows.Add(dockWindow);
                    Console.WriteLine($"[HostWindow] Added window to RootDock.Windows: {dockWindow.Id}");
                }
            }
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
            _pendingPosition = new Avalonia.PixelPoint((int)x, (int)y);
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
        /// Проверяет можно ли закрыть модуль или нужно вернуть в Dock
        /// </summary>
        private void OnWindowClosed(object? sender, EventArgs e)
        {
            Console.WriteLine($"[HostWindow] Float window closed");

            if (_window != null)
            {
                _window.Closed -= OnWindowClosed;
            }

            if (_pendingLayout != null)
            {
                var floatDock = FindDocumentDockInLayout(_pendingLayout);
                if (floatDock != null && floatDock.VisibleDockables != null)
                {
                    foreach (var dockable in floatDock.VisibleDockables)
                    {
                        if (dockable is Document document)
                        {
                            string moduleId = document.Id?.Replace("Module_", "") ?? "";

                            if (!document.CanClose)
                            {
                                Console.WriteLine($"[HostWindow] Module {moduleId} is required - returning to dock");

                                ReturnRequiredModuleToDock(moduleId);
                            }
                            else
                            {
                                Console.WriteLine($"[HostWindow] Notifying close for module: {moduleId}");

                                var mainVM = App.Services.GetRequiredService<MainWindowViewModel>();
                                mainVM.HandleModuleClosedInDock(moduleId);
                            }
                        }
                    }
                }
            }

            _window = null;
        }

        /// <summary>
        /// Вернуть обязательный модуль из Float окна обратно в Dock
        /// Делегирует в WorkspaceController активной вкладки
        /// </summary>
        private void ReturnRequiredModuleToDock(string moduleId)
        {
            Console.WriteLine($"[HostWindow] Returning required module to dock: {moduleId}");

            var tabCollection = App.Services.GetRequiredService<Writersword.Src.Core.Interfaces.WorkFlows.ITabCollection>();
            if (tabCollection.ActiveTab == null)
            {
                Console.WriteLine($"[HostWindow] No active tab");
                return;
            }

            if (tabCollection.ActiveTab.Workspace == null)
            {
                Console.WriteLine($"[HostWindow] No Workspace in active tab");
                return;
            }

            tabCollection.ActiveTab.Workspace.ReturnRequiredModuleToDock(moduleId);

            Console.WriteLine($"[HostWindow] Module {moduleId} returned successfully");
        }

        /// <summary>
        /// Найти DocumentDock внутри Layout
        /// </summary>
        private DocumentDock? FindDocumentDockInLayout(IDock? layout)
        {
            if (layout == null) return null;

            if (layout is DocumentDock dd)
                return dd;

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
        /// Получить окно
        /// </summary>
        public FloatingWindow? GetWindow()
        {
            return _window;
        }
    }
}