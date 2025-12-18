// src/Core/Models/Settings/HotKey.cs

using Avalonia.Input;
using Writersword.Core.Enums;

namespace Writersword.Core.Models.Settings
{
    /// <summary>
    /// Модель горячей клавиши
    /// </summary>
    public class HotKey
    {
        /// <summary>Уникальный идентификатор команды</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Категория команды</summary>
        public HotKeyCategory Category { get; set; }

        /// <summary>Отображаемое имя команды (локализуется)</summary>
        public string DisplayNameKey { get; set; } = string.Empty;

        /// <summary>Описание команды (локализуется)</summary>
        public string? DescriptionKey { get; set; }

        /// <summary>Горячая клавиша по умолчанию</summary>
        public KeyGesture DefaultGesture { get; set; } = null!;

        /// <summary>Текущая пользовательская горячая клавиша (null = используется дефолтная)</summary>
        public KeyGesture? CustomGesture { get; set; }

        /// <summary>Активная горячая клавиша (CustomGesture ?? DefaultGesture)</summary>
        public KeyGesture ActiveGesture => CustomGesture ?? DefaultGesture;

        /// <summary>Можно ли изменять эту горячую клавишу</summary>
        public bool IsCustomizable { get; set; } = true;
    }
}