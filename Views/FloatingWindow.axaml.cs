using Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Writersword.Views
{
    /// <summary>
    /// Плавающее окно для отображения модулей в Float режиме.
    /// Независимое окно, не привязанное к главному окну приложения.
    /// </summary>
    public partial class FloatingWindow : Window, IDockWindow
    {
        public FloatingWindow()
        {
            InitializeComponent();
            Id = Guid.NewGuid().ToString();

            Console.WriteLine($"[FloatingWindow] Constructor called, ID: {Id}");

            // Логирование активации окна
            Activated += (s, e) =>
            {
                Console.WriteLine($"[FloatingWindow] ACTIVATED: {Title}");
            };

            // Логирование деактивации окна
            Deactivated += (s, e) =>
            {
                Console.WriteLine($"[FloatingWindow] DEACTIVATED: {Title}");
            };

            // Отслеживание изменений свойств окна
            PropertyChanged += (s, e) =>
            {
                if (e.Property.Name == nameof(IsVisible))
                {
                    Console.WriteLine($"[FloatingWindow] IsVisible changed: {IsVisible} for {Title}");
                }
                if (e.Property.Name == nameof(WindowState))
                {
                    Console.WriteLine($"[FloatingWindow] WindowState changed: {WindowState} for {Title}");
                }
            };

            // Обработка закрытия окна
            Closing += (s, e) =>
            {
                Console.WriteLine($"[FloatingWindow] CLOSING: {Title}");

                // Проверяем можно ли закрыть окно
                bool canClose = OnClose();

                if (!canClose)
                {
                    Console.WriteLine($"[FloatingWindow] Close BLOCKED - contains uncloseable content");

                    // Не блокируем закрытие окна!
                    // Вместо этого возвращаем модуль обратно в главное окно
                    e.Cancel = false;  // Разрешаем закрытие окна

                    // Модуль автоматически вернётся в Dock когда окно закроется
                }
            };

            Closed += (s, e) =>
            {
                Console.WriteLine($"[FloatingWindow] CLOSED: {Title}");
            };
        }

        // IDockWindow свойства
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


        public new IDockable? Owner { get; set; }
        public IFactory? Factory { get; set; }

        public IRootDock? Layout
        {
            get => DockControlHost.Layout as IRootDock;
            set => DockControlHost.Layout = value;
        }

        public IHostWindow? Host { get; set; }

        // IDockWindow методы
        public bool OnClose()
        {
            // Проверяем можно ли закрыть содержимое окна
            bool canCloseContent = CanCloseFloatingContent();

            if (!canCloseContent)
            {
                Console.WriteLine($"[FloatingWindow] Window contains uncloseable modules - will return to dock");
            }

            // Всегда разрешаем закрытие ОКНА
            // Модуль вернется в Dock автоматически через DockFactory
            return true;
        }

        /// <summary>
        /// Проверить можно ли закрыть содержимое Float окна
        /// Возвращает false если в окне есть модули с CanClose = false
        /// </summary>
        private bool CanCloseFloatingContent()
        {
            if (Layout == null) return true;

            // Ищем все Document в Layout
            var documents = FindAllDockables(Layout);

            foreach (var dockable in documents)
            {
                // Проверяем CanClose через рефлексию (т.к. Document может быть недоступен)
                var canCloseProperty = dockable.GetType().GetProperty("CanClose");
                if (canCloseProperty != null)
                {
                    var canCloseValue = canCloseProperty.GetValue(dockable);
                    if (canCloseValue is bool canClose && !canClose)
                    {
                        Console.WriteLine($"[FloatingWindow] Found uncloseable dockable: {dockable.Id}");
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Рекурсивно найти все IDockable в Layout
        /// </summary>
        private List<IDockable> FindAllDockables(IDockable dockable)
        {
            var result = new List<IDockable>();

            // Добавляем текущий элемент
            result.Add(dockable);

            // Рекурсивно обходим детей
            if (dockable is IDock dock && dock.VisibleDockables != null)
            {
                foreach (var child in dock.VisibleDockables)
                {
                    result.AddRange(FindAllDockables(child));
                }
            }

            return result;
        }

        public bool OnMoveDragBegin()
        {
            return true;
        }

        public void OnMoveDrag()
        {
        }

        public void OnMoveDragEnd()
        {
        }

        public void Save()
        {
        }

        public void Present(bool isDialog)
        {
            // Показываем окно (НЕ используется, управление через HostWindow)
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