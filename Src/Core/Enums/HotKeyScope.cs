namespace Writersword.Core.Enums
{
    /// <summary>
    /// Область действия горячей клавиши
    /// Определяет когда клавиша активна и как определяются конфликты
    /// </summary>
    public enum HotKeyScope
    {
        /// <summary>
        /// Глобальная — всегда активна, перехватывается первой
        /// </summary>
        Global,

        /// <summary>
        /// Фоновый модуль — всегда активна независимо от фокуса (например Timer)
        /// </summary>
        Background,

        /// <summary>
        /// Фокусный модуль — активна только когда модуль в фокусе (например TextEditor, Notes)
        /// </summary>
        Focused
    }
}