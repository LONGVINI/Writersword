using Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;

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
            Id = System.Guid.NewGuid().ToString();

            System.Console.WriteLine($"[FloatingWindow] Constructor called, ID: {Id}");

            // Логирование активации окна
            Activated += (s, e) =>
            {
                System.Console.WriteLine($"[FloatingWindow] ACTIVATED: {Title}");
            };

            // Логирование деактивации окна
            Deactivated += (s, e) =>
            {
                System.Console.WriteLine($"[FloatingWindow] DEACTIVATED: {Title}");
            };

            // Отслеживание изменений свойств окна
            PropertyChanged += (s, e) =>
            {
                if (e.Property.Name == nameof(IsVisible))
                {
                    System.Console.WriteLine($"[FloatingWindow] IsVisible changed: {IsVisible} for {Title}");
                }
                if (e.Property.Name == nameof(WindowState))
                {
                    System.Console.WriteLine($"[FloatingWindow] WindowState changed: {WindowState} for {Title}");
                }
            };

            // Обработка закрытия окна
            Closing += (s, e) =>
            {
                System.Console.WriteLine($"[FloatingWindow] CLOSING: {Title}");

                // Проверяем можно ли закрыть окно
                bool canClose = OnClose();

                if (!canClose)
                {
                    System.Console.WriteLine($"[FloatingWindow] Close BLOCKED - contains uncloseable content");

                    // ВАЖНО: Не блокируем закрытие окна!
                    // Вместо этого возвращаем модуль обратно в главное окно
                    e.Cancel = false;  // Разрешаем закрытие окна

                    // Модуль автоматически вернётся в Dock когда окно закроется
                }
            };

            Closed += (s, e) =>
            {
                System.Console.WriteLine($"[FloatingWindow] CLOSED: {Title}");
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
            // Разрешаем закрытие окна
            return true;
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