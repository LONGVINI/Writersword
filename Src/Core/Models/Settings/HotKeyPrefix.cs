using Avalonia.Input;

namespace Writersword.Core.Models.Settings
{
    /// <summary>
    /// Зарегистрированный префикс последовательности горячих клавиш.
    /// Жест зарегистрированный как префикс нельзя назначить одиночным хоткеем.
    /// Хранит пользовательский комментарий для удобства навигации.
    /// </summary>
    public class HotKeyPrefix
    {
        /// <summary>
        /// Жест-префикс — первый шаг последовательности.
        /// Например Ctrl+K в последовательности Ctrl+K -> Ctrl+C.
        /// </summary>
        public KeyGesture Gesture { get; set; }

        /// <summary>
        /// Пользовательский комментарий — для чего используется этот префикс.
        /// Например "Команды редактора" или "Навигация".
        /// Опциональный — может быть пустым.
        /// </summary>
        public string Comment { get; set; } = string.Empty;

        public HotKeyPrefix(KeyGesture gesture, string comment = "")
        {
            Gesture = gesture;
            Comment = comment;
        }
    }
}