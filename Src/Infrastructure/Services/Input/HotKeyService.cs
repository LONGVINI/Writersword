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

namespace Writersword.Src.Infrastructure.Services.Input
{
    /// <summary>
    /// Реализация сервиса горячих клавиш.
    /// Поддерживает одиночные комбинации и последовательности (Ctrl+K -> Ctrl+C).
    /// Поддерживает несколько жестов на одно действие (мульти-бинд).
    /// Хранит определения клавиш отдельно от executor'ов:
    ///   _definitions    — все известные клавиши, регистрируются при старте из метаданных
    ///   _globalCommands — ICommand для глобальных клавиш
    ///   _executors      — IHotKeyProvider для модульных клавиш, только когда модуль живой
    /// </summary>
    public class HotKeyService : IHotKeyService
    {
        private readonly ILogger<HotKeyService> _logger;

        /// <summary>Все определения клавиш — id -> HotKey</summary>
        private readonly Dictionary<string, HotKey> _definitions = new();

        /// <summary>Команды глобальных клавиш — id -> ICommand</summary>
        private readonly Dictionary<string, ICommand> _globalCommands = new();

        /// <summary>Executor'ы модулей — moduleType -> IHotKeyProvider</summary>
        private readonly Dictionary<string, IHotKeyProvider> _executors = new();

        /// <summary>Накопленная последовательность нажатий</summary>
        private readonly List<KeyGesture> _pendingSequence = new();

        /// <summary>Таймер сброса последовательности</summary>
        private Timer? _sequenceTimer;

        /// <summary>Таймаут ожидания следующего шага последовательности (мс)</summary>
        private const int SequenceTimeoutMs = 1500;

        public event Action? HotKeysChanged;

        public HotKeyService()
        {
            _logger = App.Services.GetService<ILogger<HotKeyService>>()!;
        }

        /// <summary>
        /// Зарегистрировать глобальную команду с горячей клавишей
        /// </summary>
        public void Register(string id, HotKey hotKey, ICommand command)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("HotKey ID cannot be empty", nameof(id));

            hotKey.Id = id;
            hotKey.Scope = HotKeyScope.Global;
            _definitions[id] = hotKey;
            _globalCommands[id] = command;

            _logger.LogDebug("Registered global: {Id} -> {Gestures}", id,
                string.Join(", ", hotKey.ActiveGestures.Select(g => g.ToString())));
            HotKeysChanged?.Invoke();
        }

        /// <summary>
        /// Зарегистрировать определения горячих клавиш из дескриптора.
        /// Не привязывает executor — только сохраняет описание клавиш.
        /// Вызывается при старте приложения через ModuleFactory.
        /// </summary>
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
                    _logger.LogDebug("Registered from descriptor: {Id} (Scope: {Scope})",
                        hotKey.Id, hotKey.Scope);
                }
                else
                {
                    _logger.LogDebug("Definition already exists, skipping: {Id}", hotKey.Id);
                }
            }

            HotKeysChanged?.Invoke();
        }

        /// <summary>
        /// Зарегистрировать горячие клавиши модуля реализующего IHotKeyProvider.
        /// Если определения уже есть — только привязывает executor.
        /// Если определений нет — регистрирует и привязывает.
        /// </summary>
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
                    _logger.LogDebug("Registered module definition: {Id} (Scope: {Scope})",
                        hotKey.Id, hotKey.Scope);
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

        /// <summary>
        /// Привязать executor к уже зарегистрированным определениям модуля.
        /// Вызывается из BaseModule.Initialize().
        /// </summary>
        public void BindExecutor(string moduleType, IHotKeyProvider provider)
        {
            _executors[moduleType] = provider;
            _logger.LogDebug("Executor bound: {ModuleType}", moduleType);
        }

        /// <summary>
        /// Отвязать executor модуля не удаляя определения клавиш.
        /// Вызывается из BaseModule.Dispose().
        /// </summary>
        public void UnbindExecutor(string moduleType)
        {
            if (_executors.Remove(moduleType))
                _logger.LogDebug("Executor unbound: {ModuleType}", moduleType);
        }

        /// <summary>
        /// Отменить регистрацию горячих клавиш модуля полностью.
        /// Удаляет и определения и executor.
        /// </summary>
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
                _logger.LogDebug("Unregistered {Count} hotkeys for module: {ModuleType}",
                    toRemove.Count, moduleType);
                HotKeysChanged?.Invoke();
            }
        }

        /// <summary>
        /// Обработать нажатие клавиши.
        /// Накапливает последовательность и выполняет команду при совпадении.
        /// Проверяет все ActiveGestures каждой клавиши — поддержка мульти-биндов.
        /// </summary>
        public bool HandleKeyPress(KeyGesture gesture, string? focusedModuleType = null)
        {
            _pendingSequence.Add(gesture);
            ResetSequenceTimer();

            var allEntries = GetAllEntries().ToList();

            // Проверяем полное совпадение по любому из ActiveGestures
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

            // Проверяем является ли текущая последовательность префиксом любого жеста
            bool isPrefix = allEntries.Any(entry =>
                entry.hotKey.ActiveGestures.Any(g => g.IsPrefix(_pendingSequence)));

            if (isPrefix)
            {
                _logger.LogDebug("Sequence prefix matched, waiting for next key: {Steps}",
                    string.Join(" -> ", _pendingSequence.Select(g => g.ToString())));
                return true;
            }

            _logger.LogDebug("No match for sequence, clearing");
            ClearSequence();
            return false;
        }

        /// <summary>
        /// Получить все зарегистрированные горячие клавиши
        /// </summary>
        public IReadOnlyList<HotKey> GetAllHotKeys()
        {
            return _definitions.Values.ToList();
        }

        /// <summary>
        /// Получить горячую клавишу по ID
        /// </summary>
        public HotKey? GetHotKey(string id)
        {
            return _definitions.TryGetValue(id, out var hotKey) ? hotKey : null;
        }

        /// <summary>
        /// Получить команду по ID (только для глобальных клавиш)
        /// </summary>
        public ICommand? GetCommand(string id)
        {
            return _globalCommands.TryGetValue(id, out var command) ? command : null;
        }

        /// <summary>
        /// Установить одиночный пользовательский жест заменяя все существующие.
        /// Обратная совместимость — используется в базовом редакторе хоткеев.
        /// </summary>
        public bool SetCustomGesture(string id, KeyGesture? gesture)
        {
            var hotKey = GetHotKey(id);
            if (hotKey == null) return false;

            hotKey.CustomGestures.Clear();
            if (gesture != null)
                hotKey.CustomGestures.Add(new HotKeyGesture(gesture));

            _logger.LogDebug("Custom gesture set: {Id} -> {Gesture}", id, gesture);

            HotKeysChanged?.Invoke();
            SaveSettings();
            return true;
        }

        /// <summary>
        /// Установить одиночный пользовательский жест-последовательность заменяя все существующие.
        /// Обратная совместимость.
        /// </summary>
        public bool SetCustomGestureSequence(string id, HotKeyGesture? gesture)
        {
            var hotKey = GetHotKey(id);
            if (hotKey == null) return false;

            hotKey.CustomGestures.Clear();
            if (gesture != null)
                hotKey.CustomGestures.Add(gesture);

            _logger.LogDebug("Custom gesture sequence set: {Id} -> {Gesture}", id, gesture);

            HotKeysChanged?.Invoke();
            SaveSettings();
            return true;
        }

        /// <summary>
        /// Добавить дополнительный пользовательский жест не заменяя существующие.
        /// Используется при добавлении второго/третьего варианта нажатия.
        /// </summary>
        public bool AddCustomGesture(string id, HotKeyGesture gesture)
        {
            var hotKey = GetHotKey(id);
            if (hotKey == null) return false;

            // Если пользовательских нет — сначала копируем дефолтные
            if (hotKey.CustomGestures.Count == 0 && hotKey.DefaultGestures.Count > 0)
            {
                foreach (var dg in hotKey.DefaultGestures)
                    hotKey.CustomGestures.Add(dg);
            }

            hotKey.AddCustomGesture(gesture);
            _logger.LogDebug("Custom gesture added: {Id} -> {Gesture}", id, gesture);

            HotKeysChanged?.Invoke();
            SaveSettings();
            return true;
        }

        /// <summary>
        /// Удалить пользовательский жест по индексу.
        /// </summary>
        public bool RemoveCustomGesture(string id, int index)
        {
            var hotKey = GetHotKey(id);
            if (hotKey == null) return false;

            if (index < 0 || index >= hotKey.CustomGestures.Count) return false;

            hotKey.RemoveCustomGesture(index);
            _logger.LogDebug("Custom gesture removed: {Id}[{Index}]", id, index);

            HotKeysChanged?.Invoke();
            SaveSettings();
            return true;
        }

        /// <summary>
        /// Заменить пользовательский жест по индексу.
        /// </summary>
        public bool ReplaceCustomGesture(string id, int index, HotKeyGesture gesture)
        {
            var hotKey = GetHotKey(id);
            if (hotKey == null) return false;

            // Если пользовательских нет — сначала копируем дефолтные
            if (hotKey.CustomGestures.Count == 0 && hotKey.DefaultGestures.Count > 0)
            {
                foreach (var dg in hotKey.DefaultGestures)
                    hotKey.CustomGestures.Add(dg);
            }

            hotKey.ReplaceCustomGesture(index, gesture);
            _logger.LogDebug("Custom gesture replaced: {Id}[{Index}] -> {Gesture}", id, index, gesture);

            HotKeysChanged?.Invoke();
            SaveSettings();
            return true;
        }

        /// <summary>
        /// Сбросить горячую клавишу к значению по умолчанию.
        /// Очищает все пользовательские жесты.
        /// </summary>
        public void ResetToDefault(string id)
        {
            var hotKey = GetHotKey(id);
            if (hotKey == null) return;

            hotKey.ClearCustomGestures();
            _logger.LogDebug("Reset to default: {Id}", id);

            HotKeysChanged?.Invoke();
            SaveSettings();
        }

        /// <summary>
        /// Сбросить все горячие клавиши к значениям по умолчанию
        /// </summary>
        public void ResetAllToDefaults()
        {
            foreach (var hotKey in _definitions.Values)
                hotKey.ClearCustomGestures();

            _logger.LogDebug("All hotkeys reset to defaults");
            HotKeysChanged?.Invoke();
            SaveSettings();
        }

        /// <summary>
        /// Проверить наличие конфликта для одиночного жеста
        /// </summary>
        public bool HasConflict(KeyGesture gesture, string? excludeId = null)
        {
            return GetConflicts(gesture, excludeId).Count > 0;
        }

        /// <summary>
        /// Получить список ID конфликтующих клавиш для одиночного жеста.
        /// Проверяет все ActiveGestures каждой клавиши.
        /// </summary>
        public IReadOnlyList<string> GetConflicts(KeyGesture gesture, string? excludeId = null)
        {
            var conflicts = new List<string>();

            foreach (var hotKey in GetAllHotKeys())
            {
                if (hotKey.Id == excludeId) continue;

                foreach (var activeGesture in hotKey.ActiveGestures)
                {
                    if (activeGesture.Matches(new[] { gesture }) ||
                        activeGesture.HasPrefix(gesture))
                    {
                        conflicts.Add(hotKey.Id);
                        break;
                    }
                }
            }

            return conflicts;
        }

        /// <summary>
        /// Определить тип конфликта между двумя клавишами.
        /// Проверяет все комбинации ActiveGestures обеих клавиш.
        ///
        /// Правила:
        /// Global   vs *            -> Critical
        /// Background vs Background -> Critical
        /// Background vs Focused    -> Critical
        /// Focused  vs Focused      -> Warning
        /// Prefix   vs Single       -> Critical
        /// </summary>
        public HotKeyConflictType GetConflictType(string idA, string idB)
        {
            var hotKeyA = GetHotKey(idA);
            var hotKeyB = GetHotKey(idB);

            if (hotKeyA == null || hotKeyB == null)
                return HotKeyConflictType.None;

            if (hotKeyA.ActiveGestures.Count == 0 || hotKeyB.ActiveGestures.Count == 0)
                return HotKeyConflictType.None;

            bool gesturesConflict = false;

            foreach (var gestureA in hotKeyA.ActiveGestures)
            {
                foreach (var gestureB in hotKeyB.ActiveGestures)
                {
                    if (gestureA.Matches(gestureB.Steps) ||
                        gestureA.IsPrefix(gestureB.Steps) ||
                        gestureB.IsPrefix(gestureA.Steps))
                    {
                        gesturesConflict = true;
                        break;
                    }
                }

                if (gesturesConflict) break;
            }

            if (!gesturesConflict)
                return HotKeyConflictType.None;

            var scopeA = hotKeyA.Scope;
            var scopeB = hotKeyB.Scope;

            if (scopeA == HotKeyScope.Focused && scopeB == HotKeyScope.Focused)
                return HotKeyConflictType.Warning;

            return HotKeyConflictType.Critical;
        }

        /// <summary>
        /// Получить все зарезервированные префиксы.
        /// Клавиши которые являются первым шагом любой последовательности
        /// из любого ActiveGesture любой клавиши.
        /// </summary>
        public IReadOnlyList<KeyGesture> GetReservedPrefixes()
        {
            var prefixes = new List<KeyGesture>();

            foreach (var hotKey in GetAllHotKeys())
            {
                foreach (var gesture in hotKey.ActiveGestures)
                {
                    if (gesture.IsSequence)
                        prefixes.Add(gesture.FirstStep);
                }
            }

            return prefixes.Distinct(new KeyGestureComparer()).ToList();
        }

        /// <summary>
        /// Проверить активен ли executor для указанного moduleType
        /// </summary>
        public bool IsExecutorBound(string moduleType)
        {
            return _executors.ContainsKey(moduleType);
        }

        /// <summary>
        /// Загрузить пользовательские настройки.
        /// Формат: id -> "Ctrl+S" или "Ctrl+K -> Ctrl+C" или "Ctrl+S|Ctrl+K -> Ctrl+C" для мульти-биндов.
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
                    var hotKey = GetHotKey(kvp.Key);
                    if (hotKey == null) continue;

                    hotKey.CustomGestures.Clear();

                    if (string.IsNullOrEmpty(kvp.Value))
                        continue;

                    // Разбиваем по "|" — разделитель между вариантами мульти-бинда
                    var variants = kvp.Value.Split('|');

                    foreach (var variant in variants)
                    {
                        var trimmed = variant.Trim();
                        if (string.IsNullOrEmpty(trimmed)) continue;

                        try
                        {
                            // Разбиваем по " -> " — разделитель шагов последовательности
                            var parts = trimmed.Split(new[] { " -> " }, StringSplitOptions.RemoveEmptyEntries);
                            var steps = parts.Select(p => KeyGesture.Parse(p.Trim())).ToList();
                            hotKey.CustomGestures.Add(new HotKeyGesture(steps));
                        }
                        catch
                        {
                            _logger.LogWarning("Could not parse saved gesture for {Id}: {Value}",
                                kvp.Key, trimmed);
                        }
                    }
                }

                _logger.LogDebug("Hotkey settings loaded: {Count} entries", saved.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading hotkey settings");
            }
        }

        /// <summary>
        /// Сохранить пользовательские настройки.
        /// Формат: id -> "Ctrl+S" или "Ctrl+S|Ctrl+K -> Ctrl+C" для мульти-биндов.
        /// </summary>
        public void SaveSettings()
        {
            try
            {
                var settingsService = App.Services.GetRequiredService<ISettingsService>();
                var toSave = new Dictionary<string, string>();

                foreach (var hotKey in GetAllHotKeys())
                {
                    if (hotKey.CustomGestures.Count == 0) continue;

                    var variants = hotKey.CustomGestures.Select(g => g.ToString());
                    toSave[hotKey.Id] = string.Join("|", variants);
                }

                settingsService.SaveModuleSettings("hotkeys", toSave);
                _logger.LogDebug("Hotkey settings saved: {Count} entries", toSave.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving hotkey settings");
            }
        }

        /// <summary>
        /// Запустить или перезапустить таймер сброса последовательности
        /// </summary>
        private void ResetSequenceTimer()
        {
            _sequenceTimer?.Dispose();
            _sequenceTimer = new Timer(_ =>
            {
                _logger.LogDebug("Sequence timeout, clearing");
                ClearSequence();
            }, null, SequenceTimeoutMs, Timeout.Infinite);
        }

        /// <summary>
        /// Сбросить накопленную последовательность
        /// </summary>
        private void ClearSequence()
        {
            _pendingSequence.Clear();
            _sequenceTimer?.Dispose();
            _sequenceTimer = null;
        }

        /// <summary>
        /// Выполнить команду горячей клавиши
        /// </summary>
        private void ExecuteEntry((HotKey hotKey, ICommand? command, IHotKeyProvider? executor) entry)
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
                _logger.LogDebug("No executor bound for: {Id} (module: {ModuleType})",
                    entry.hotKey.Id, entry.hotKey.ModuleType);
            }
        }

        /// <summary>
        /// Получить все определения с привязанными executor'ами и командами
        /// </summary>
        private IEnumerable<(HotKey hotKey, ICommand? command, IHotKeyProvider? executor)> GetAllEntries()
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

        /// <summary>
        /// Компаратор для KeyGesture — используется в Distinct() для GetReservedPrefixes
        /// </summary>
        private sealed class KeyGestureComparer : IEqualityComparer<KeyGesture>
        {
            public bool Equals(KeyGesture? x, KeyGesture? y)
            {
                if (x == null && y == null) return true;
                if (x == null || y == null) return false;
                return x.Key == y.Key && x.KeyModifiers == y.KeyModifiers;
            }

            public int GetHashCode(KeyGesture obj)
            {
                return HashCode.Combine(obj.Key, obj.KeyModifiers);
            }
        }
    }
}