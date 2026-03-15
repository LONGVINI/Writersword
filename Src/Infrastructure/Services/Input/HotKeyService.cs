using Avalonia.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Input;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Models.Settings;
using Writersword.Src.Core.Interfaces.Services.Input;
using Writersword.Src.Core.Interfaces.Services.Storage;
using Writersword.Src.Core.Interfaces.Services.UI;

namespace Writersword.Src.Infrastructure.Services.Input
{
    public class HotKeyService : IHotKeyService
    {
        private readonly ILogger<HotKeyService> _logger;

        private readonly Dictionary<string, HotKey> _definitions = new();
        private readonly Dictionary<string, ICommand> _globalCommands = new();
        private readonly Dictionary<string, IHotKeyProvider> _executors = new();

        /// <summary>
        /// Пользовательские префиксы с комментариями.
        /// Ключ — строковое представление жеста для быстрого поиска.
        /// </summary>
        private readonly List<HotKeyPrefix> _userPrefixes = new();

        private readonly List<KeyGesture> _pendingSequence = new();
        private Timer? _sequenceTimer;
        private const int SequenceTimeoutMs = 1500;

        public event Action? HotKeysChanged;

        public HotKeyService()
        {
            _logger = App.Services.GetService<ILogger<HotKeyService>>()!;
        }

        public void Register(string id, HotKey hotKey, ICommand command)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("HotKey ID cannot be empty", nameof(id));

            hotKey.Id = id;
            hotKey.Scope = HotKeyScope.Global;
            _definitions[id] = hotKey;
            _globalCommands[id] = command;

            _logger.LogDebug("Registered global: {Id}", id);
            HotKeysChanged?.Invoke();
        }

        public void RegisterFromDescriptor(IHotKeyDescriptor descriptor)
        {
            var hotKeys = descriptor.GetHotKeys();
            if (hotKeys == null || hotKeys.Count == 0) return;

            foreach (var hotKey in hotKeys)
            {
                if (string.IsNullOrWhiteSpace(hotKey.Id))
                {
                    _logger.LogWarning("HotKey descriptor has empty ID, skipping");
                    continue;
                }

                if (!_definitions.ContainsKey(hotKey.Id))
                {
                    _definitions[hotKey.Id] = hotKey;
                    _logger.LogDebug("Registered from descriptor: {Id}", hotKey.Id);
                }
            }

            HotKeysChanged?.Invoke();
        }

        public void RegisterModule(IHotKeyProvider provider)
        {
            var hotKeys = provider.GetHotKeys();
            if (hotKeys == null || hotKeys.Count == 0) return;

            string? moduleType = null;

            foreach (var hotKey in hotKeys)
            {
                if (string.IsNullOrWhiteSpace(hotKey.Id))
                {
                    _logger.LogWarning("Module hotkey has empty ID, skipping");
                    continue;
                }

                if (!_definitions.ContainsKey(hotKey.Id))
                {
                    _definitions[hotKey.Id] = hotKey;
                    _logger.LogDebug("Registered module definition: {Id}", hotKey.Id);
                }

                moduleType ??= hotKey.ModuleType;
            }

            if (moduleType != null)
            {
                _executors[moduleType] = provider;
                _logger.LogDebug("Executor bound via RegisterModule: {ModuleType}", moduleType);
            }

            HotKeysChanged?.Invoke();
        }

        public void BindExecutor(string moduleType, IHotKeyProvider provider)
        {
            _executors[moduleType] = provider;
            _logger.LogDebug("Executor bound: {ModuleType}", moduleType);
        }

        public void UnbindExecutor(string moduleType)
        {
            if (_executors.Remove(moduleType))
                _logger.LogDebug("Executor unbound: {ModuleType}", moduleType);
        }

        public void UnregisterModule(string moduleType)
        {
            _executors.Remove(moduleType);

            var toRemove = _definitions
                .Where(kvp => kvp.Value.ModuleType == moduleType)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var id in toRemove)
                _definitions.Remove(id);

            if (toRemove.Count > 0)
            {
                _logger.LogDebug("Unregistered {Count} hotkeys for: {ModuleType}",
                    toRemove.Count, moduleType);
                HotKeysChanged?.Invoke();
            }
        }

        public bool HandleKeyPress(KeyGesture gesture, string? focusedModuleType = null)
        {
            _logger.LogDebug("HandleKeyPress received: {Key} + {Modifiers}", gesture.Key, gesture.KeyModifiers);

            _pendingSequence.Add(gesture);
            ResetSequenceTimer();

            var allEntries = GetAllEntries().ToList();

            var matched = allEntries.FirstOrDefault(entry =>
                    entry.hotKey.ActiveGestures.Any(g => g.Matches(_pendingSequence)));

            if (matched.hotKey != null)
            {
                bool shouldExecute = matched.hotKey.Scope switch
                {
                    HotKeyScope.Global => true,
                    HotKeyScope.Background => true,
                    HotKeyScope.Focused => matched.hotKey.ModuleType == focusedModuleType,
                    _ => false
                };

                if (shouldExecute)
                {
                    _logger.LogDebug("Executing: {Id}", matched.hotKey.Id);
                    ClearSequence();
                    ExecuteEntry(matched);
                    return true;
                }
            }

            bool isPrefix = allEntries.Any(entry =>
                entry.hotKey.ActiveGestures.Any(g => g.IsPrefix(_pendingSequence)));

            if (isPrefix)
            {
                _logger.LogDebug("Prefix matched, waiting for next key");

                var notificationService = App.Services.GetService<INotificationService>();
                var pendingStr = string.Join(" ", _pendingSequence.Select(g => g.ToString()));
                notificationService?.Show(
                    $"{pendingStr}...",
                    NotificationType.Info,
                    TimeSpan.FromMilliseconds(SequenceTimeoutMs));

                return true;
            }

            _logger.LogDebug("No match, clearing sequence");
            ClearSequence();
            return false;
        }

        public IReadOnlyList<HotKey> GetAllHotKeys() => _definitions.Values.ToList();

        public HotKey? GetHotKey(string id) =>
            _definitions.TryGetValue(id, out var hk) ? hk : null;

        public ICommand? GetCommand(string id) =>
            _globalCommands.TryGetValue(id, out var cmd) ? cmd : null;

        public GestureAssignResult SetCustomGesture(string id, KeyGesture? gesture)
        {
            var hotKey = GetHotKey(id);
            if (hotKey == null) return GestureAssignResult.HotKeyNotFound;

            if (gesture != null && IsReservedPrefix(gesture))
            {
                _logger.LogDebug("SetCustomGesture blocked — reserved prefix: {Gesture}", gesture);
                return GestureAssignResult.BlockedByPrefix;
            }

            hotKey.CustomGestures.Clear();
            if (gesture != null)
                hotKey.CustomGestures.Add(new HotKeyGesture(gesture));

            _logger.LogDebug("Custom gesture set: {Id} -> {Gesture}", id, gesture);
            HotKeysChanged?.Invoke();
            SaveSettings();
            return GestureAssignResult.Ok;
        }

        public GestureAssignResult SetCustomGestureSequence(string id, HotKeyGesture? gesture)
        {
            var hotKey = GetHotKey(id);
            if (hotKey == null) return GestureAssignResult.HotKeyNotFound;

            if (gesture != null)
            {
                if (gesture.IsSequence && !IsReservedPrefix(gesture.FirstStep))
                {
                    _logger.LogDebug("SetCustomGestureSequence blocked — first step not a prefix: {Gesture}",
                        gesture.FirstStep);
                    return GestureAssignResult.PrefixNotRegistered;
                }

                if (gesture.IsSingle && IsReservedPrefix(gesture.FirstStep))
                {
                    _logger.LogDebug("SetCustomGestureSequence blocked — reserved prefix: {Gesture}",
                        gesture.FirstStep);
                    return GestureAssignResult.BlockedByPrefix;
                }
            }

            hotKey.CustomGestures.Clear();
            if (gesture != null)
                hotKey.CustomGestures.Add(gesture);

            _logger.LogDebug("Custom gesture sequence set: {Id} -> {Gesture}", id, gesture);
            HotKeysChanged?.Invoke();
            SaveSettings();
            return GestureAssignResult.Ok;
        }

        /// <summary>
        /// Добавить новый пользовательский жест к хоткею не заменяя существующие.
        /// Блокирует если одиночный жест зарезервирован как префикс.
        /// Блокирует если последовательность — первый шаг не зарегистрирован как префикс.
        /// Возвращает false если хоткей не найден или жест заблокирован.
        /// </summary>
        public bool AddCustomGesture(string id, HotKeyGesture gesture)
        {
            var hotKey = GetHotKey(id);
            if (hotKey == null) return false;

            if (gesture.IsSingle && IsReservedPrefix(gesture.FirstStep))
            {
                _logger.LogDebug("AddCustomGesture blocked — reserved prefix: {Gesture}", gesture.FirstStep);
                return false;
            }

            if (gesture.IsSequence && !IsReservedPrefix(gesture.FirstStep))
            {
                _logger.LogDebug("AddCustomGesture blocked — first step not a prefix: {Gesture}", gesture.FirstStep);
                return false;
            }

            hotKey.CustomGestures.Add(gesture);
            _logger.LogDebug("Custom gesture added: {Id} -> {Gesture}", id, gesture);
            HotKeysChanged?.Invoke();
            SaveSettings();
            return true;
        }

        /// <summary>
        /// Удалить пользовательский жест по индексу из списка CustomGestures.
        /// Не делает ничего если хоткей не найден или индекс вне диапазона.
        /// Сохраняет настройки после удаления.
        /// </summary>
        public void RemoveCustomGesture(string id, int index)
        {
            var hotKey = GetHotKey(id);
            if (hotKey == null) return;

            if (index < 0 || index >= hotKey.CustomGestures.Count) return;

            hotKey.CustomGestures.RemoveAt(index);
            _logger.LogDebug("Custom gesture removed: {Id} index {Index}", id, index);
            HotKeysChanged?.Invoke();
            SaveSettings();
        }

        /// <summary>
        /// Заменить пользовательский жест по индексу новым.
        /// Блокирует если новый жест зарезервирован как префикс — возвращает BlockedByPrefix.
        /// Блокирует если последовательность — первый шаг не зарегистрирован как префикс — возвращает PrefixNotRegistered.
        /// Возвращает HotKeyNotFound если хоткей не найден или индекс вне диапазона.
        /// </summary>
        public GestureAssignResult ReplaceCustomGesture(string id, int index, HotKeyGesture gesture)
        {
            var hotKey = GetHotKey(id);
            if (hotKey == null) return GestureAssignResult.HotKeyNotFound;

            if (index < 0 || index >= hotKey.CustomGestures.Count)
                return GestureAssignResult.HotKeyNotFound;

            if (gesture.IsSingle && IsReservedPrefix(gesture.FirstStep))
            {
                _logger.LogDebug("ReplaceCustomGesture blocked — reserved prefix: {Gesture}", gesture.FirstStep);
                return GestureAssignResult.BlockedByPrefix;
            }

            if (gesture.IsSequence && !IsReservedPrefix(gesture.FirstStep))
            {
                _logger.LogDebug("ReplaceCustomGesture blocked — first step not a prefix: {Gesture}", gesture.FirstStep);
                return GestureAssignResult.PrefixNotRegistered;
            }

            hotKey.CustomGestures[index] = gesture;
            _logger.LogDebug("Custom gesture replaced: {Id} index {Index} -> {Gesture}", id, index, gesture);
            HotKeysChanged?.Invoke();
            SaveSettings();
            return GestureAssignResult.Ok;
        }

        public void ResetToDefault(string id)
        {
            var hotKey = GetHotKey(id);
            if (hotKey == null) return;

            hotKey.ClearCustomGestures();
            _logger.LogDebug("Reset to default: {Id}", id);
            HotKeysChanged?.Invoke();
            SaveSettings();
        }

        public void ResetAllToDefaults()
        {
            foreach (var hotKey in _definitions.Values)
                hotKey.ClearCustomGestures();

            _logger.LogDebug("All hotkeys reset to defaults");
            HotKeysChanged?.Invoke();
            SaveSettings();
        }

        public bool HasConflict(KeyGesture gesture, string? excludeId = null) =>
            GetConflicts(gesture, excludeId).Count > 0;

        public IReadOnlyList<string> GetConflicts(KeyGesture gesture, string? excludeId = null)
        {
            var conflicts = new List<string>();

            foreach (var hotKey in GetAllHotKeys())
            {
                if (hotKey.Id == excludeId) continue;
                if (hotKey.ActiveGesture == null) continue;

                if (hotKey.ActiveGesture.Matches(new[] { gesture }) ||
                    hotKey.ActiveGesture.HasPrefix(gesture))
                {
                    conflicts.Add(hotKey.Id);
                }
            }

            return conflicts;
        }

        public HotKeyConflictType GetConflictType(string idA, string idB)
        {
            var hotKeyA = GetHotKey(idA);
            var hotKeyB = GetHotKey(idB);

            if (hotKeyA?.ActiveGesture == null || hotKeyB?.ActiveGesture == null)
                return HotKeyConflictType.None;

            var gestureA = hotKeyA.ActiveGesture;
            var gestureB = hotKeyB.ActiveGesture;

            bool conflict =
                gestureA.Matches(gestureB.Steps) ||
                gestureA.IsPrefix(gestureB.Steps) ||
                gestureB.IsPrefix(gestureA.Steps);

            if (!conflict) return HotKeyConflictType.None;

            if (hotKeyA.Scope == HotKeyScope.Focused && hotKeyB.Scope == HotKeyScope.Focused)
                return HotKeyConflictType.Warning;

            return HotKeyConflictType.Critical;
        }

        public bool IsExecutorBound(string moduleType) => _executors.ContainsKey(moduleType);

        // -----------------------------------------------------------------------
        // Префиксы
        // -----------------------------------------------------------------------

        public GestureAssignResult RegisterPrefix(KeyGesture gesture, string comment = "")
        {
            if (_userPrefixes.Any(p => GesturesEqual(p.Gesture, gesture)))
            {
                _logger.LogDebug("RegisterPrefix: already exists: {Gesture}", gesture);
                return GestureAssignResult.PrefixAlreadyExists;
            }

            bool usedAsHotKey = _definitions.Values.Any(hk =>
            {
                var allGestures = hk.CustomGestures.Count > 0
                    ? hk.CustomGestures
                    : hk.DefaultGestures;

                return allGestures.Any(g =>
                    g.IsSingle && GesturesEqual(g.FirstStep, gesture));
            });

            if (usedAsHotKey)
            {
                _logger.LogDebug("RegisterPrefix blocked — used as hotkey: {Gesture}", gesture);
                return GestureAssignResult.BlockedByHotKey;
            }

            _userPrefixes.Add(new HotKeyPrefix(gesture, comment));
            _logger.LogDebug("Prefix registered: {Gesture}", gesture);
            HotKeysChanged?.Invoke();
            SaveSettings();
            return GestureAssignResult.Ok;
        }

        public GestureAssignResult UnregisterPrefix(KeyGesture gesture)
        {
            var existing = _userPrefixes.FirstOrDefault(p => GesturesEqual(p.Gesture, gesture));
            if (existing == null)
            {
                _logger.LogDebug("UnregisterPrefix: not found: {Gesture}", gesture);
                return GestureAssignResult.HotKeyNotFound;
            }

            var inUse = GetHotKeysUsingPrefix(gesture);
            if (inUse.Count > 0)
            {
                _logger.LogDebug("UnregisterPrefix blocked — used by {Count} hotkeys", inUse.Count);
                return GestureAssignResult.PrefixInUse;
            }

            _userPrefixes.Remove(existing);
            _logger.LogDebug("Prefix unregistered: {Gesture}", gesture);
            HotKeysChanged?.Invoke();
            SaveSettings();
            return GestureAssignResult.Ok;
        }

        public bool UpdatePrefixComment(KeyGesture gesture, string comment)
        {
            var existing = _userPrefixes.FirstOrDefault(p => GesturesEqual(p.Gesture, gesture));
            if (existing == null) return false;

            existing.Comment = comment;
            _logger.LogDebug("Prefix comment updated: {Gesture}", gesture);
            SaveSettings();
            return true;
        }

        public IReadOnlyList<HotKeyPrefix> GetUserPrefixes() => _userPrefixes.AsReadOnly();

        public IReadOnlyList<KeyGesture> GetReservedPrefixes()
        {
            var result = _userPrefixes.Select(p => p.Gesture).ToList();

            foreach (var hotKey in _definitions.Values)
            {
                var allGestures = hotKey.CustomGestures.Count > 0
                    ? hotKey.CustomGestures
                    : hotKey.DefaultGestures;

                foreach (var gesture in allGestures)
                {
                    if (gesture.IsSequence &&
                        !result.Any(p => GesturesEqual(p, gesture.FirstStep)))
                    {
                        result.Add(gesture.FirstStep);
                    }
                }
            }

            return result;
        }

        public bool IsReservedPrefix(KeyGesture gesture) =>
            GetReservedPrefixes().Any(p => GesturesEqual(p, gesture));

        public IReadOnlyList<string> GetHotKeysUsingPrefix(KeyGesture prefix)
        {
            return _definitions.Values
                .Where(hk =>
                    hk.ActiveGesture != null &&
                    hk.ActiveGesture.IsSequence &&
                    GesturesEqual(hk.ActiveGesture.FirstStep, prefix))
                .Select(hk => hk.Id)
                .ToList();
        }

        // -----------------------------------------------------------------------
        // Сохранение / загрузка
        // -----------------------------------------------------------------------

        /// <summary>
        /// Формат хоткеев: id -> "Ctrl+K -> Ctrl+C" или "Ctrl+S".
        /// Префиксы под ключом "__prefixes__" -> JSON-подобный список:
        /// "Ctrl+K:Команды редактора|Ctrl+E:Навигация"
        /// Разделитель жеста и комментария — первое вхождение ':'.
        /// Разделитель между префиксами — '|'.
        /// </summary>
        public void LoadSettings()
        {
            try
            {
                var settingsService = App.Services.GetRequiredService<ISettingsService>();
                var saved = settingsService.GetModuleSettings<Dictionary<string, string>>("hotkeys");
                if (saved == null) return;

                foreach (var kvp in saved)
                {
                    if (kvp.Key == "__prefixes__")
                    {
                        LoadPrefixesFromString(kvp.Value);
                        continue;
                    }

                    var hotKey = GetHotKey(kvp.Key);
                    if (hotKey == null) continue;

                    hotKey.CustomGestures.Clear();
                    if (string.IsNullOrEmpty(kvp.Value)) continue;

                    try
                    {
                        // Несколько жестов разделены |||
                        var gestureStrings = kvp.Value.Split(
                            new[] { " ||| " }, StringSplitOptions.RemoveEmptyEntries);

                        foreach (var gestureStr in gestureStrings)
                        {
                            var parts = gestureStr.Trim().Split(
                                new[] { " -> ", " \u2192 ", "\u2192" },
                                StringSplitOptions.RemoveEmptyEntries);
                            var steps = parts.Select(p => KeyGesture.Parse(p.Trim())).ToList();
                            hotKey.CustomGestures.Add(new HotKeyGesture(steps));
                        }
                    }
                    catch
                    {
                        _logger.LogWarning("Could not parse saved gesture for {Id}: {Value}",
                            kvp.Key, kvp.Value);
                    }
                }

                _logger.LogDebug("Hotkey settings loaded: {Count} entries", saved.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading hotkey settings");
            }
        }

        public void SaveSettings()
        {
            try
            {
                var settingsService = App.Services.GetRequiredService<ISettingsService>();
                var toSave = new Dictionary<string, string>();

                foreach (var hotKey in GetAllHotKeys())
                {
                    if (hotKey.CustomGestures.Count == 0) continue;
                    // Все жесты через разделитель ||| (не пересекается с форматом жестов)
                    toSave[hotKey.Id] = string.Join(" ||| ", hotKey.CustomGestures.Select(g => g.ToString()));
                }

                if (_userPrefixes.Count > 0)
                {
                    toSave["__prefixes__"] = string.Join("|",
                        _userPrefixes.Select(p =>
                        {
                            var gestureStr = p.Gesture.ToString();
                            var comment = p.Comment.Replace("|", "").Replace(":", "");
                            return string.IsNullOrEmpty(comment)
                                ? gestureStr
                                : $"{gestureStr}:{comment}";
                        }));
                }

                settingsService.SaveModuleSettings("hotkeys", toSave);
                _logger.LogDebug("Hotkey settings saved: {Count} entries", toSave.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving hotkey settings");
            }
        }

        private void LoadPrefixesFromString(string value)
        {
            if (string.IsNullOrEmpty(value)) return;

            _userPrefixes.Clear();

            foreach (var part in value.Split('|'))
            {
                var trimmed = part.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                try
                {
                    // Разделяем жест и комментарий по первому ':'
                    var colonIndex = trimmed.IndexOf(':');
                    string gestureStr;
                    string comment = string.Empty;

                    if (colonIndex > 0)
                    {
                        gestureStr = trimmed[..colonIndex].Trim();
                        comment = trimmed[(colonIndex + 1)..].Trim();
                    }
                    else
                    {
                        gestureStr = trimmed;
                    }

                    var gesture = KeyGesture.Parse(gestureStr);
                    _userPrefixes.Add(new HotKeyPrefix(gesture, comment));
                }
                catch
                {
                    _logger.LogWarning("Could not parse saved prefix: {Value}", trimmed);
                }
            }

            _logger.LogDebug("Loaded {Count} user prefixes", _userPrefixes.Count);
        }

        // -----------------------------------------------------------------------
        // Вспомогательные методы
        // -----------------------------------------------------------------------

        private void ResetSequenceTimer()
        {
            _sequenceTimer?.Dispose();
            _sequenceTimer = new Timer(_ =>
            {
                _logger.LogDebug("Sequence timeout, clearing");
                ClearSequence();
            }, null, SequenceTimeoutMs, Timeout.Infinite);
        }

        private void ClearSequence()
        {
            _pendingSequence.Clear();
            _sequenceTimer?.Dispose();
            _sequenceTimer = null;
        }

        private void ExecuteEntry(
            (HotKey hotKey, ICommand? command, IHotKeyProvider? executor) entry)
        {
            if (entry.command != null)
            {
                if (entry.command.CanExecute(null))
                    entry.command.Execute(null);
            }
            else if (entry.executor != null)
            {
                entry.executor.ExecuteHotKey(entry.hotKey.Id);
            }
            else
            {
                _logger.LogDebug("No executor bound for: {Id}", entry.hotKey.Id);
            }
        }

        private IEnumerable<(HotKey hotKey, ICommand? command, IHotKeyProvider? executor)>
            GetAllEntries()
        {
            foreach (var kvp in _definitions)
            {
                var hotKey = kvp.Value;

                if (_globalCommands.TryGetValue(hotKey.Id, out var command))
                {
                    yield return (hotKey, command, null);
                    continue;
                }

                if (hotKey.ModuleType != null &&
                    _executors.TryGetValue(hotKey.ModuleType, out var executor))
                {
                    yield return (hotKey, null, executor);
                    continue;
                }

                yield return (hotKey, null, null);
            }
        }

        private static bool GesturesEqual(KeyGesture a, KeyGesture b) =>
            a.Key == b.Key && a.KeyModifiers == b.KeyModifiers;
    }
}