// src/Core/Enums/HotKeyCategory.cs

namespace Writersword.Core.Enums
{
    /// <summary>
    /// Категории горячих клавиш для группировки в UI
    /// </summary>
    public enum HotKeyCategory
    {
        File,        // Файл (New, Open, Save, Close)
        Edit,        // Редактирование (Undo, Redo, Copy, Paste)
        View,        // Вид (Zoom, Fullscreen)
        Navigation,  // Навигация (Next Tab, Previous Tab)
        Formatting,  // Форматирование (Bold, Italic)
        Tools,       // Инструменты
        Help         // Помощь
    }
}