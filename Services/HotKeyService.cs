// src/Services/HotKeyService.cs

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Avalonia.Input;
using Writersword.Core.Models.Settings;
using Writersword.Services.Interfaces;

namespace Writersword.Services
{
    /// <summary>
    /// Простая реализация сервиса горячих клавиш (временная)
    /// </summary>
    public class HotKeyService : IHotKeyService
    {
        private readonly Dictionary<string, (HotKey hotKey, ICommand command)> _registrations = new();

        public event Action? HotKeysChanged;

        public void Register(string id, HotKey hotKey, ICommand command)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("HotKey ID cannot be empty", nameof(id));

            hotKey.Id = id;
            _registrations[id] = (hotKey, command);

            Console.WriteLine($"[HotKeyService] Registered: {id} -> {hotKey.ActiveGesture}");
            HotKeysChanged?.Invoke();
        }

        public bool HandleKeyPress(KeyGesture gesture)
        {
            foreach (var kvp in _registrations)
            {
                var (hotKey, command) = kvp.Value;

                if (GesturesEqual(hotKey.ActiveGesture, gesture))
                {
                    if (command.CanExecute(null))
                    {
                        Console.WriteLine($"[HotKeyService] Executing: {kvp.Key}");
                        command.Execute(null);
                        return true;
                    }
                }
            }

            return false;
        }

        public IReadOnlyList<HotKey> GetAllHotKeys()
        {
            return _registrations.Values.Select(x => x.hotKey).ToList();
        }

        public HotKey? GetHotKey(string id)
        {
            return _registrations.TryGetValue(id, out var registration)
                ? registration.hotKey
                : null;
        }

        public ICommand? GetCommand(string id)
        {
            return _registrations.TryGetValue(id, out var registration)
                ? registration.command
                : null;
        }

        public bool SetCustomGesture(string id, KeyGesture gesture)
        {
            if (!_registrations.TryGetValue(id, out var registration))
                return false;

            registration.hotKey.CustomGesture = gesture;
            Console.WriteLine($"[HotKeyService] Custom gesture set: {id} -> {gesture}");

            HotKeysChanged?.Invoke();
            return true;
        }

        public void ResetToDefault(string id)
        {
            if (_registrations.TryGetValue(id, out var registration))
            {
                registration.hotKey.CustomGesture = null;
                Console.WriteLine($"[HotKeyService] Reset to default: {id}");
                HotKeysChanged?.Invoke();
            }
        }

        public void ResetAllToDefaults()
        {
            foreach (var registration in _registrations.Values)
            {
                registration.hotKey.CustomGesture = null;
            }

            Console.WriteLine("[HotKeyService] All hotkeys reset to defaults");
            HotKeysChanged?.Invoke();
        }

        public bool HasConflict(KeyGesture gesture, string? excludeId = null)
        {
            return GetConflicts(gesture, excludeId).Count > 0;
        }

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

        public void LoadSettings()
        {
            // TODO: Загрузка из SettingsService
            Console.WriteLine("[HotKeyService] LoadSettings - not implemented yet");
        }

        public void SaveSettings()
        {
            // TODO: Сохранение в SettingsService
            Console.WriteLine("[HotKeyService] SaveSettings - not implemented yet");
        }

        private bool GesturesEqual(KeyGesture a, KeyGesture b)
        {
            if (a == null || b == null)
                return false;

            return a.Key == b.Key && a.KeyModifiers == b.KeyModifiers;
        }
    }
}