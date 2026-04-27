using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Avalonia;
using System;
using System.Reactive;
using System.Reactive.Linq;
using Writersword.Core.Enums;

namespace Writersword.ViewModels.Components
{
    /// <summary>
    /// ViewModel для всплывающего уведомления
    /// Управляет отображением, анимацией и автоматическим скрытием
    /// </summary>
    public class NotificationViewModel : ViewModelBase
    {
        private readonly ILogger<NotificationViewModel> _logger;
        private string _message = "";
        private NotificationType _type = NotificationType.Info;
        private bool _isVisible = false;
        private IDisposable? _hideTimer;

        /// <summary>Текст уведомления</summary>
        public string Message
        {
            get => _message;
            set => this.RaiseAndSetIfChanged(ref _message, value);
        }

        /// <summary>Тип уведомления (Success, Info, Warning, Error)</summary>
        public NotificationType Type
        {
            get => _type;
            set => this.RaiseAndSetIfChanged(ref _type, value);
        }

        /// <summary>Видимость уведомления</summary>
        public bool IsVisible
        {
            get => _isVisible;
            set => this.RaiseAndSetIfChanged(ref _isVisible, value);
        }

        /// <summary>Иконка для отображения (зависит от Type)</summary>
        public string Icon
        {
            get
            {
                return Type switch
                {
                    NotificationType.Success => "✓",
                    NotificationType.Info => "ℹ",
                    NotificationType.Warning => "⚠",
                    NotificationType.Error => "❌",
                    _ => "ℹ"
                };
            }
        }

        /// <summary>Цвет иконки (зависит от Type)</summary>
        public string IconColor
        {
            get
            {
                return Type switch
                {
                    NotificationType.Success => "#28A745",
                    NotificationType.Info => "#007ACC",
                    NotificationType.Warning => "#FFA500",
                    NotificationType.Error => "#DC3545",
                    _ => "#007ACC"
                };
            }
        }

        /// <summary>Цвет фона (зависит от Type)</summary>
        public string BackgroundColor
        {
            get
            {
                return Type switch
                {
                    NotificationType.Success => "#1E3A28",
                    NotificationType.Info => "#1E2A3A",
                    NotificationType.Warning => "#3A2E1E",
                    NotificationType.Error => "#3A1E1E",
                    _ => "#1E2A3A"
                };
            }
        }

        /// <summary>Команда закрытия уведомления</summary>
        public ReactiveCommand<Unit, Unit> CloseCommand { get; }

        public NotificationViewModel()
        {
            _logger = App.Services.GetService<ILogger<NotificationViewModel>>()!;

            CloseCommand = ReactiveCommand.Create(Hide);
        }

        /// <summary>
        /// Показать уведомление с автоматическим скрытием
        /// </summary>
        /// <param name="message">Текст сообщения</param>
        /// <param name="type">Тип уведомления</param>
        /// <param name="duration">Длительность показа (по умолчанию 3 секунды)</param>
        public void Show(string message, NotificationType type, TimeSpan? duration = null)
        {
            Message = message;
            Type = type;

            this.RaisePropertyChanged(nameof(Icon));
            this.RaisePropertyChanged(nameof(IconColor));
            this.RaisePropertyChanged(nameof(BackgroundColor));

            _hideTimer?.Dispose();

            IsVisible = true;

            _logger.LogDebug("Showing notification: {Message} (Type: {Type})", message, type);

            var hideDelay = duration ?? TimeSpan.FromSeconds(3);
            _hideTimer = Observable
                .Timer(hideDelay)
                .ObserveOn(AvaloniaScheduler.Instance)
                .Subscribe(_ => Hide());
        }

        /// <summary>Скрыть уведомление</summary>
        public void Hide()
        {
            IsVisible = false;
            _hideTimer?.Dispose();
            _hideTimer = null;

            _logger.LogDebug("Notification hidden");
        }

        /// <summary>Освободить ресурсы</summary>
        public void Dispose()
        {
            _hideTimer?.Dispose();
        }
    }
}