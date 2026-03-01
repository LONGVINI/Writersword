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
    /// <summary>
    /// Плавающее окно для отображения модулей в Float режиме.
    /// Независимое окно, не привязанное к главному окну приложения.
    /// </summary>
    public partial class FloatingWindowView : Window, IDockWindow
    {
        private readonly ILogger<FloatingWindowView> _logger;

        public FloatingWindowView()
        {
            _logger = App.Services.GetService<ILogger<FloatingWindowView>>()!;

            InitializeComponent();
            Id = Guid.NewGuid().ToString();

            _logger.LogDebug("FloatingWindowView created, ID: {Id}", Id);

            // Логирование активации окна
            Activated += (s, e) =>
            {
                _logger.LogDebug("FloatingWindowView activated: {Title}", Title);
            };

            // Логирование деактивации окна
            Deactivated += (s, e) =>
            {
                _logger.LogDebug("FloatingWindowView deactivated: {Title}", Title);
            };

            // Отслеживание изменений свойств окна
            PropertyChanged += (s, e) =>
            {
                if (e.Property.Name == nameof(IsVisible))
                {
                    _logger.LogDebug("IsVisible changed: {IsVisible} for {Title}", IsVisible, Title);
                }
                if (e.Property.Name == nameof(WindowState))
                {
                    _logger.LogDebug("WindowState changed: {WindowState} for {Title}", WindowState, Title);
                }
            };

            // Обработка закрытия окна
            Closing += (s, e) =>
            {
                _logger.LogDebug("FloatingWindowView closing: {Title}", Title);

                // Проверяем можно ли закрыть окно
                bool canClose = OnClose();

                if (!canClose)
                {
                    _logger.LogDebug("Close blocked - contains uncloseable content");

                    // Не блокируем закрытие окна!
                    // Вместо этого возвращаем модуль обратно в главное окно
                    e.Cancel = false;  // Разрешаем закрытие окна

                    // Модуль автоматически вернётся в Dock когда окно закроется
                }
            };

            Closed += (s, e) =>
            {
                _logger.LogDebug("FloatingWindowView closed: {Title}", Title);
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
        /// <summary>
        /// Обработчик закрытия окна
        /// </summary>
        public bool OnClose()
        {
            // Проверяем можно ли закрыть содержимое окна
            bool canCloseContent = CanCloseFloatingContent();

            if (!canCloseContent)
            {
                _logger.LogDebug("Window contains uncloseable modules - will return to dock");
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
                        _logger.LogDebug("Found uncloseable dockable: {Id}", dockable.Id);
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

        /// <summary>
        /// Начало перетаскивания окна
        /// </summary>
        public bool OnMoveDragBegin()
        {
            return true;
        }

        /// <summary>
        /// Процесс перетаскивания окна
        /// </summary>
        public void OnMoveDrag()
        {
        }

        /// <summary>
        /// Завершение перетаскивания окна
        /// </summary>
        public void OnMoveDragEnd()
        {
        }

        /// <summary>
        /// Сохранить состояние окна
        /// </summary>
        public void Save()
        {
        }

        /// <summary>
        /// Показать окно
        /// </summary>
        public void Present(bool isDialog)
        {
            // Показываем окно (НЕ используется, управление через HostWindow)
            Show();
        }

        /// <summary>
        /// Закрыть окно
        /// </summary>
        public void Exit()
        {
            Close();
        }

        /// <summary>
        /// Активировать окно (передать фокус)
        /// </summary>
        public void SetActive()
        {
            Activate();
        }
    }
}