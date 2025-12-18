// src/Core/Models/Settings/HotKeySettings.cs

using System.Collections.Generic;

namespace Writersword.Core.Models.Settings
{
    /// <summary>
    /// Настройки горячих клавиш для сохранения в settings.json
    /// </summary>
    public class HotKeySettings
    {
        /// <summary>
        /// Словарь пользовательских горячих клавиш
        /// Key = Id команды, Value = строковое представление KeyGesture (например "Ctrl+S")
        /// </summary>
        public Dictionary<string, string> CustomHotKeys { get; set; } = new();
    }
}