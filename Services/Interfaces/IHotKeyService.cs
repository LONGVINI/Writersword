// src/Services/Interfaces/IHotKeyService.cs

using System;
using System.Collections.Generic;
using System.Windows.Input;
using Avalonia.Input;
using Writersword.Core.Models.Settings;

namespace Writersword.Services.Interfaces
{
    /// <summary>
    /// Сервис управления горячими клавишами
    /// </summary>
    public interface IHotKeyService
    {
        /// <summary>
        /// Зарегистрировать команду с горячей клавишей
        /// </summary>
        void Register(string id, HotKey hotKey, ICommand command);

        /// <summary>
        /// Получить все зарегистрированные горячие клавиши
        /// </summary>
        IReadOnlyList<HotKey> GetAllHotKeys();

        /// <summary>
        /// Получить горячую клавишу по ID
        /// </summary>
        HotKey? GetHotKey(string id);

        /// <summary>
        /// Получить команду по ID
        /// </summary>
        ICommand? GetCommand(string id);

        /// <summary>
        /// Установить пользовательскую горячую клавишу
        /// </summary>
        bool SetCustomGesture(string id, KeyGesture gesture);

        /// <summary>
        /// Сбросить горячую клавишу к значению по умолчанию
        /// </summary>
        void ResetToDefault(string id);

        /// <summary>
        /// Сбросить все горячие клавиши к значениям по умолчанию
        /// </summary>
        void ResetAllToDefaults();

        /// <summary>
        /// Проверить, есть ли конфликт с другими горячими клавишами
        /// </summary>
        bool HasConflict(KeyGesture gesture, string? excludeId = null);

        /// <summary>
        /// Получить список конфликтующих команд
        /// </summary>
        IReadOnlyList<string> GetConflicts(KeyGesture gesture, string? excludeId = null);

        /// <summary>
        /// Загрузить настройки из SettingsService
        /// </summary>
        void LoadSettings();

        /// <summary>
        /// Сохранить настройки в SettingsService
        /// </summary>
        void SaveSettings();

        /// <summary>
        /// Событие изменения горячих клавиш
        /// </summary>
        event Action? HotKeysChanged;
    }
}