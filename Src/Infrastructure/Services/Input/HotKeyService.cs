using Avalonia.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Writersword.Core.Models.Settings;
using Writersword.Src.Core.Interfaces.Services.Input;

namespace Writersword.Src.Infrastructure.Services.Input
{
    /// <summary>
    /// Простая реализация сервиса горячих клавиш (временная)
    /// </summary>
    public class HotKeyService : IHotKeyService
    {
        private readonly ILogger<HotKeyService> _logger;
        private readonly Dictionary<string, (HotKey hotKey, ICommand command)> _registrations = new();

        public event Action? HotKeysChanged;

        public HotKeyService()
        {
            _logger = App.Services.GetService<ILogger<HotKeyService>>()!;
        }

        /// <summary>
        /// Зарегистрировать горячую клавишу
        /// </summary>
        public void Register(string id, HotKey hotKey, ICommand command)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("HotKey ID cannot be empty", nameof(id));

            hotKey.Id = id;
            _registrations[id] = (hotKey, command);

            _logger.LogDebug("Registered: {Id} -> {Gesture}", id, hotKey.ActiveGesture);
            HotKeysChanged?.Invoke();
        }

        /// <summary>
        /// Обработать нажатие клавиши
        /// </summary>
        public bool HandleKeyPress(KeyGesture gesture)
        {
            foreach (var kvp in _registrations)
            {
                var (hotKey, command) = kvp.Value;

                if (GesturesEqual(hotKey.ActiveGesture, gesture))
                {
                    if (command.CanExecute(null))
                    {
                        _logger.LogDebug("Executing: {Id}", kvp.Key);
                        command.Execute(null);
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Получить все зарегистрированные горячие клавиши
        /// </summary>
        public IReadOnlyList<HotKey> GetAllHotKeys()
        {
            return _registrations.Values.Select(x => x.hotKey).ToList();
        }

        /// <summary>
        /// Получить горячую клавишу по ID
        /// </summary>
        public HotKey? GetHotKey(string id)
        {
            return _registrations.TryGetValue(id, out var registration)
                ? registration.hotKey
                : null;
        }

        /// <summary>
        /// Получить команду по ID горячей клавиши
        /// </summary>
        public ICommand? GetCommand(string id)
        {
            return _registrations.TryGetValue(id, out var registration)
                ? registration.command
                : null;
        }

        /// <summary>
        /// Установить пользовательский жест для горячей клавиши
        /// </summary>
        public bool SetCustomGesture(string id, KeyGesture gesture)
        {
            if (!_registrations.TryGetValue(id, out var registration))
                return false;

            registration.hotKey.CustomGesture = gesture;
            _logger.LogDebug("Custom gesture set: {Id} -> {Gesture}", id, gesture);

            HotKeysChanged?.Invoke();
            return true;
        }

        /// <summary>
        /// Сбросить горячую клавишу к значению по умолчанию
        /// </summary>
        public void ResetToDefault(string id)
        {
            if (_registrations.TryGetValue(id, out var registration))
            {
                registration.hotKey.CustomGesture = null;
                _logger.LogDebug("Reset to default: {Id}", id);
                HotKeysChanged?.Invoke();
            }
        }

        /// <summary>
        /// Сбросить все горячие клавиши к значениям по умолчанию
        /// </summary>
        public void ResetAllToDefaults()
        {
            foreach (var registration in _registrations.Values)
            {
                registration.hotKey.CustomGesture = null;
            }

            _logger.LogDebug("All hotkeys reset to defaults");
            HotKeysChanged?.Invoke();
        }

        /// <summary>
        /// Проверить наличие конфликта с другими горячими клавишами
        /// </summary>
        public bool HasConflict(KeyGesture gesture, string? excludeId = null)
        {
            return GetConflicts(gesture, excludeId).Count > 0;
        }

        /// <summary>
        /// Получить список ID конфликтующих горячих клавиш
        /// </summary>
        public IReadOnlyList<string> GetConflicts(KeyGesture gesture, string? excludeId = null)
        {
            var conflicts = new List<string>();

            foreach (var kvp in _registrations)
            {
                if (kvp.Key == excludeId)
                    continue;

                if (GesturesEqual(kvp.Value.hotKey.ActiveGesture, gesture))
                {
                    conflicts.Add(kvp.Key);
                }
            }

            return conflicts;
        }

        /// <summary>
        /// Загрузить настройки горячих клавиш
        /// </summary>
        public void LoadSettings()
        {
            _logger.LogDebug("LoadSettings - not implemented yet");
        }

        /// <summary>
        /// Сохранить настройки горячих клавиш
        /// </summary>
        public void SaveSettings()
        {
            _logger.LogDebug("SaveSettings - not implemented yet");
        }

        /// <summary>
        /// Сравнить два жеста на равенство
        /// </summary>
        private bool GesturesEqual(KeyGesture a, KeyGesture b)
        {
            if (a == null || b == null)
                return false;

            return a.Key == b.Key && a.KeyModifiers == b.KeyModifiers;
        }
    }
}