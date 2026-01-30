using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Writersword.Core.Enums;
using Writersword.Src.Core.Interfaces.Services.UI;
using Writersword.ViewModels.Components;
using Writersword.Views.Components;

namespace Writersword.Src.Infrastructure.Services.UI
{
    /// <summary>
    /// Сервис для показа всплывающих уведомлений
    /// Создаёт NotificationView и добавляет его в главное окно
    /// </summary>
    public class NotificationService : INotificationService
    {
        private NotificationViewModel? _currentNotification;
        private NotificationView? _currentView;
        private Panel? _notificationContainer;

        /// <summary>Показать успешное уведомление (зелёная галочка)</summary>
        public void ShowSuccess(string message)
        {
            Show(message, NotificationType.Success);
        }

        /// <summary>Показать информационное уведомление (синяя иконка)</summary>
        public void ShowInfo(string message)
        {
            Show(message, NotificationType.Info);
        }

        /// <summary>Показать предупреждение (жёлтая иконка)</summary>
        public void ShowWarning(string message)
        {
            Show(message, NotificationType.Warning);
        }

        /// <summary>Показать ошибку (красный крестик)</summary>
        public void ShowError(string message)
        {
            Show(message, NotificationType.Error);
        }

        /// <summary>
        /// Показать уведомление с настраиваемым типом и длительностью
        /// </summary>
        public void Show(string message, NotificationType type, TimeSpan? duration = null)
        {
            try
            {
                // Работаем в UI потоке!
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    // Получаем контейнер для уведомлений
                    EnsureNotificationContainer();

                    if (_notificationContainer == null)
                    {
                        Console.WriteLine("[NotificationService] ERROR: No notification container!");
                        return;
                    }

                    // Скрываем предыдущее уведомление если есть
                    if (_currentNotification != null)
                    {
                        _currentNotification.Hide();
                    }

                    // Создаём новое уведомление
                    _currentNotification = new NotificationViewModel();
                    _currentView = new NotificationView
                    {
                        DataContext = _currentNotification
                    };

                    // Добавляем в контейнер
                    _notificationContainer.Children.Clear();
                    _notificationContainer.Children.Add(_currentView);

                    // Показываем
                    _currentNotification.Show(message, type, duration);

                    Console.WriteLine($"[NotificationService] Shown: {message} (Type: {type})");
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NotificationService] ERROR: {ex.Message}");
            }
        }

        /// <summary>
        /// Получить или создать контейнер для уведомлений
        /// Контейнер находится в MainWindow
        /// </summary>
        private void EnsureNotificationContainer()
        {
            if (_notificationContainer != null)
                return;

            // Получаем главное окно
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow != null)
            {
                var mainWindow = desktop.MainWindow;

                // Ищем контейнер по имени "NotificationContainer"
                _notificationContainer = mainWindow.FindControl<Panel>("NotificationContainer");

                if (_notificationContainer != null)
                {
                    Console.WriteLine("[NotificationService] Notification container found");
                }
                else
                {
                    // Если контейнер не найден - создаём временный
                    Console.WriteLine("[NotificationService] WARNING: NotificationContainer not found in MainWindow!");
                    Console.WriteLine("[NotificationService] Add <Panel Name=\"NotificationContainer\"/> to MainWindow.axaml");

                    // Создаём временный контейнер
                    _notificationContainer = new Panel
                    {
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
                    };

                    // Добавляем в главное окно
                    if (mainWindow.Content is Panel rootPanel)
                    {
                        rootPanel.Children.Add(_notificationContainer);
                        Console.WriteLine("[NotificationService] Created temporary notification container");
                    }
                }
            }
            else
            {
                Console.WriteLine("[NotificationService] ERROR: MainWindow not found!");
            }
        }
    }
}