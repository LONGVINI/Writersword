namespace Writersword.Core.Enums
{
    /// <summary>
    /// Типы уведомлений для NotificationService
    /// Определяет цвет и иконку уведомления
    /// </summary>
    public enum NotificationType
    {
        /// <summary>
        /// Успешное действие (зелёный, галочка ✓)
        /// Пример: "Проект сохранён"
        /// </summary>
        Success,

        /// <summary>
        /// Информация (синий, ℹ)
        /// Пример: "Модуль открыт"
        /// </summary>
        Info,

        /// <summary>
        /// Предупреждение (жёлтый, ⚠)
        /// Пример: "Автосохранение..."
        /// </summary>
        Warning,

        /// <summary>
        /// Ошибка (красный, ❌)
        /// Пример: "Не удалось сохранить проект"
        /// </summary>
        Error
    }
}