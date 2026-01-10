using System;

namespace Writersword.Core.Interfaces.Services
{
    /// <summary>
    /// Сервис автоматического сохранения активной вкладки в файл
    /// Периодически сохраняет .writersword файл (как Ctrl+S)
    /// Работает с одной активной вкладкой, интервал настраивается
    /// </summary>
    public interface IAutoSaveService
    {
        /// <summary>Включить автосохранение</summary>
        void Enable();

        /// <summary>Выключить автосохранение</summary>
        void Disable();

        /// <summary>
        /// Установить интервал автосохранения
        /// Если interval = TimeSpan.Zero, автосохранение отключается
        /// </summary>
        /// <param name="interval">Интервал (по умолчанию 2 минуты, 0 = отключено)</param>
        void SetInterval(TimeSpan interval);

        /// <summary>Включено ли автосохранение (из настроек пользователя)</summary>
        bool IsEnabled { get; set; }

        /// <summary>Текущий интервал автосохранения</summary>
        TimeSpan Interval { get; }

        /// <summary>Событие завершения автосохранения</summary>
        event EventHandler? ProjectSaved;
    }
}