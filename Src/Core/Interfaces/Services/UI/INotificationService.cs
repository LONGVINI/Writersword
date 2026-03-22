using System;
using Writersword.Core.Enums;

namespace Writersword.Core.Interfaces.Services.UI
{
    /// <summary>
    /// Сервис для показа всплывающих уведомлений (toast notifications)
    /// Используется для информирования пользователя о событиях
    /// </summary>
    public interface INotificationService
    {
        /// <summary>Показать успешное уведомление (зелёная галочка)</summary>
        void ShowSuccess(string message);

        /// <summary>Показать информационное уведомление (синяя иконка)</summary>
        void ShowInfo(string message);

        /// <summary>Показать предупреждение (жёлтая иконка)</summary>
        void ShowWarning(string message);

        /// <summary>Показать ошибку (красный крестик)</summary>
        void ShowError(string message);

        /// <summary>
        /// Показать уведомление с настраиваемым типом и длительностью
        /// </summary>
        /// <param name="message">Текст сообщения</param>
        /// <param name="type">Тип уведомления</param>
        /// <param name="duration">Длительность показа (по умолчанию 3 секунды)</param>
        void Show(string message, NotificationType type, TimeSpan? duration = null);
    }
}