using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Dock.Model.Controls;
using Dock.Model.Core;
using System.Collections.Generic;
using System.Linq;
using Writersword.Views;

namespace Writersword.Services
{
    /// <summary>
    /// Управление плавающими окнами (Float) для Dock системы.
    /// Окна остаются открытыми при переключении между модулями.
    /// </summary>
    public class HostWindow : IHostWindow
    {
        // Хранилище всех открытых окон: ключ = ID документа, значение = окно
        private static readonly Dictionary<string, FloatingWindow> _activeWindows = new();

        private FloatingWindow? _window;
        private IDock? _pendingLayout;
        private string? _documentId;
        private bool _isWindowShown = false; // Флаг что окно уже показано
        private Avalonia.PixelPoint? _pendingPosition;

        #region IHostWindow Properties

        public IHostWindowState? HostWindowState { get; set; }
        public bool IsTracked { get; set; }

        public IDockWindow? Window
        {
            get => _window;
            set
            {
                System.Console.WriteLine($"[HostWindow] Window setter called");
                _window = value as FloatingWindow;
            }
        }

        #endregion

        #region IHostWindow Methods

        /// <summary>
        /// Показать плавающее окно
        /// </summary>
        public void Present(bool isDialog)
        {
            System.Console.WriteLine($"[HostWindow] Present() START");
            System.Console.WriteLine($"  - _documentId: {_documentId ?? "null"}");

            if (!string.IsNullOrEmpty(_documentId) && _activeWindows.ContainsKey(_documentId))
            {
                System.Console.WriteLine($"[HostWindow] REUSING existing window");
                _window = _activeWindows[_documentId];
                _isWindowShown = true;

                if (_pendingLayout != null)
                {
                    _window.Layout = _pendingLayout as IRootDock;
                    _window.Factory = _pendingLayout.Factory;
                }

                _window.Activate();
                return;
            }

            System.Console.WriteLine($"[HostWindow] Creating NEW FloatingWindow");
            _window = new FloatingWindow();

            // ИСПОЛЬЗУЕМ позицию из SetPosition если она есть
            if (_pendingPosition.HasValue)
            {
                _window.Position = _pendingPosition.Value;
                System.Console.WriteLine($"[HostWindow] Using position from SetPosition: ({_pendingPosition.Value.X}, {_pendingPosition.Value.Y})");
            }
            else
            {
                // Fallback: центр главного окна
                var mainWindow = GetMainWindow();
                if (mainWindow != null)
                {
                    var mainPos = mainWindow.Position;
                    var mainSize = mainWindow.ClientSize;

                    _window.Position = new Avalonia.PixelPoint(
                        mainPos.X + (int)(mainSize.Width / 2) - 400,
                        mainPos.Y + (int)(mainSize.Height / 2) - 300
                    );
                }
                System.Console.WriteLine($"[HostWindow] Using fallback center position");
            }

            if (_pendingLayout != null)
            {
                _window.Layout = _pendingLayout as IRootDock;
                _window.Factory = _pendingLayout.Factory;

                if (!string.IsNullOrEmpty(_documentId))
                {
                    _activeWindows[_documentId] = _window;

                    string docId = _documentId;
                    _window.Closed += (s, e) =>
                    {
                        System.Console.WriteLine($"[HostWindow] User closed window: {docId}");
                        _activeWindows.Remove(docId);
                    };
                }
            }

            _window.Show();
            _isWindowShown = true;
            _pendingPosition = null; // Сбрасываем после использования

            System.Console.WriteLine($"[HostWindow] Present() END");
        }

        /// <summary>
        /// Dock система вызывает Exit() при переключении модулей.
        /// Игнорируем это и оставляем окно открытым.
        /// </summary>
        public void Exit()
        {
            System.Console.WriteLine($"[HostWindow] Exit() called - IGNORING (window stays open)");
        }

        /// <summary>
        /// Сохранить позицию для окна.
        /// Вызывается Dock системой ДО Present().
        /// </summary>
        public void SetPosition(double x, double y)
        {
            System.Console.WriteLine($"[HostWindow] SetPosition({x}, {y})");

            // Сохраняем позицию для использования в Present()
            _pendingPosition = new Avalonia.PixelPoint((int)x, (int)y);

            // Если окно уже создано - применяем сразу
            if (_window != null && !_isWindowShown)
            {
                _window.Position = _pendingPosition.Value;
                System.Console.WriteLine($"[HostWindow] Applied position to existing window");
            }
        }

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

        public void SetSize(double width, double height)
        {
            // Также игнорируем SetSize для показанных окон
            if (_isWindowShown)
            {
                System.Console.WriteLine($"[HostWindow] SetSize({width}, {height}) - IGNORED (window already shown)");
                return;
            }

            System.Console.WriteLine($"[HostWindow] SetSize({width}, {height})");
            if (_window != null)
            {
                _window.Width = width;
                _window.Height = height;
            }
        }

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

        public void SetTitle(string? title)
        {
            if (_window != null && !string.IsNullOrEmpty(title))
            {
                _window.Title = title;
            }
        }

        public void SetLayout(IDock layout)
        {
            System.Console.WriteLine($"[HostWindow] SetLayout() called");

            // Извлекаем ID документа
            _documentId = ExtractDocumentId(layout);
            System.Console.WriteLine($"  - Document ID: '{_documentId ?? "null"}'");

            // Сохраняем layout
            _pendingLayout = layout;

            // Если окно уже создано - обновляем layout
            if (_window != null)
            {
                _window.Layout = layout as IRootDock;
                _window.Factory = layout.Factory;
            }
        }

        public void SetActive()
        {
            _window?.Activate();
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Закрыть все открытые Float окна.
        /// Вызывается при выходе из приложения.
        /// </summary>
        public static void CloseAllWindows()
        {
            System.Console.WriteLine($"[HostWindow] Closing all {_activeWindows.Count} Float windows");

            var windows = _activeWindows.Values.ToList();

            foreach (var window in windows)
            {
                try
                {
                    System.Console.WriteLine($"[HostWindow] Closing window: {window.Title}");
                    window.Close();
                }
                catch (System.Exception ex)
                {
                    System.Console.WriteLine($"[HostWindow] Error closing window: {ex.Message}");
                }
            }

            _activeWindows.Clear();
            System.Console.WriteLine($"[HostWindow] All Float windows closed");
        }

        /// <summary>
        /// Извлечь ID документа из layout структуры
        /// </summary>
        private string? ExtractDocumentId(IDock? layout)
        {
            if (layout == null) return null;
            return FindDocumentId(layout);
        }

        /// <summary>
        /// Рекурсивно ищет Document в дереве
        /// </summary>
        private string? FindDocumentId(IDockable? dockable)
        {
            if (dockable == null) return null;

            var typeName = dockable.GetType().Name;

            if (typeName == "Document" && !string.IsNullOrEmpty(dockable.Id))
            {
                return dockable.Id;
            }

            if (dockable is IDock dock && dock.VisibleDockables != null)
            {
                foreach (var child in dock.VisibleDockables)
                {
                    var foundId = FindDocumentId(child);
                    if (foundId != null) return foundId;
                }
            }

            return null;
        }

        #endregion

        /// <summary>
        /// Получить главное окно приложения
        /// </summary>
        private Window? GetMainWindow()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                return desktop.MainWindow;
            }
            return null;
        }
    }
}