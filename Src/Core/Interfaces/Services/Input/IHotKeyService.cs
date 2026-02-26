using Avalonia.Input;
using System;
using System.Collections.Generic;
using System.Windows.Input;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Models.Settings;

namespace Writersword.Src.Core.Interfaces.Services.Input
{
    /// <summary>
    /// Тип конфликта между горячими клавишами
    /// </summary>
    public enum HotKeyConflictType
    {
        /// <summary>Конфликта нет</summary>
        None,

        /// <summary>
        /// Конфликт с глобальной клавишей, между фоновыми модулями,
        /// или префикс резервирует одиночную комбинацию.
        /// Одна из клавиш всегда будет перехвачена первой.
        /// </summary>
        Critical,

        /// <summary>
        /// Конфликт между фокусными модулями.
        /// Сработает только если оба модуля активны одновременно.
        /// </summary>
        Warning
    }

    /// <summary>
    /// Сервис управления горячими клавишами.
    /// Поддерживает одиночные комбинации и последовательности (Ctrl+K -> Ctrl+C).
    /// Хранит определения клавиш (IHotKeyDescriptor) отдельно от executor'ов (IHotKeyProvider).
    /// Определения регистрируются при старте через метаданные модулей,
    /// executor'ы привязываются только когда модуль живой.
    /// </summary>
    public interface IHotKeyService
    {
        /// <summary>
        /// Зарегистрировать глобальную команду с горячей клавишей
        /// </summary>
        void Register(string id, HotKey hotKey, ICommand command);

        /// <summary>
        /// Зарегистрировать определения горячих клавиш из дескриптора.
        /// Вызывается при старте приложения через ModuleFactory для каждого модуля
        /// реализующего IHotKeyDescriptor в метаданных.
        /// Не привязывает executor — только сохраняет описание клавиш.
        /// </summary>
        void RegisterFromDescriptor(IHotKeyDescriptor descriptor);

        /// <summary>
        /// Зарегистрировать горячие клавиши модуля реализующего IHotKeyProvider.
        /// Используется для обратной совместимости когда модуль регистрирует
        /// сам себя через Initialize().
        /// Если определения уже зарегистрированы через RegisterFromDescriptor —
        /// только привязывает executor, не дублирует определения.
        /// </summary>
        void RegisterModule(IHotKeyProvider provider);

        /// <summary>
        /// Привязать executor к уже зарегистрированным определениям модуля.
        /// Вызывается из BaseModule.Initialize() когда модуль становится живым.
        /// </summary>
        void BindExecutor(string moduleType, IHotKeyProvider provider);

        /// <summary>
        /// Отвязать executor модуля не удаляя определения клавиш.
        /// Вызывается из BaseModule.Dispose() когда модуль закрывается.
        /// Клавиши остаются видимы в таблице настроек.
        /// </summary>
        void UnbindExecutor(string moduleType);

        /// <summary>
        /// Отменить регистрацию горячих клавиш модуля полностью.
        /// Удаляет и определения и executor.
        /// Используется только при полном удалении модуля из системы.
        /// </summary>
        void UnregisterModule(string moduleType);

        /// <summary>
        /// Обработать нажатие горячей клавиши.
        /// Накапливает последовательность и выполняет команду при совпадении.
        /// focusedModuleType — moduleType модуля в фокусе (null если нет).
        /// </summary>
        bool HandleKeyPress(KeyGesture gesture, string? focusedModuleType = null);

        /// <summary>
        /// Получить все зарегистрированные горячие клавиши
        /// </summary>
        IReadOnlyList<HotKey> GetAllHotKeys();

        /// <summary>
        /// Получить горячую клавишу по ID
        /// </summary>
        HotKey? GetHotKey(string id);

        /// <summary>
        /// Получить команду по ID (только для глобальных клавиш)
        /// </summary>
        ICommand? GetCommand(string id);

        /// <summary>
        /// Установить пользовательский жест (одиночная комбинация)
        /// </summary>
        bool SetCustomGesture(string id, KeyGesture? gesture);

        /// <summary>
        /// Установить пользовательский жест (одиночная или последовательность)
        /// </summary>
        bool SetCustomGestureSequence(string id, HotKeyGesture? gesture);

        /// <summary>
        /// Сбросить горячую клавишу к значению по умолчанию
        /// </summary>
        void ResetToDefault(string id);

        /// <summary>
        /// Сбросить все горячие клавиши к значениям по умолчанию
        /// </summary>
        void ResetAllToDefaults();

        /// <summary>
        /// Проверить есть ли конфликт с другими горячими клавишами
        /// </summary>
        bool HasConflict(KeyGesture gesture, string? excludeId = null);

        /// <summary>
        /// Получить список ID конфликтующих горячих клавиш
        /// </summary>
        IReadOnlyList<string> GetConflicts(KeyGesture gesture, string? excludeId = null);

        /// <summary>
        /// Определить тип конфликта между двумя зарегистрированными клавишами
        /// </summary>
        HotKeyConflictType GetConflictType(string idA, string idB);

        /// <summary>
        /// Получить все зарезервированные префиксы.
        /// Одиночные комбинации которые нельзя использовать — они являются
        /// первым шагом зарегистрированных последовательностей.
        /// </summary>
        IReadOnlyList<KeyGesture> GetReservedPrefixes();

        /// <summary>
        /// Проверить активен ли executor для указанного moduleType.
        /// Используется в UI чтобы показать состояние модуля (запущен / не запущен).
        /// </summary>
        bool IsExecutorBound(string moduleType);

        /// <summary>
        /// Загрузить пользовательские настройки из SettingsService
        /// </summary>
        void LoadSettings();

        /// <summary>
        /// Сохранить пользовательские настройки в SettingsService
        /// </summary>
        void SaveSettings();

        /// <summary>
        /// Событие изменения горячих клавиш
        /// </summary>
        event Action? HotKeysChanged;

        /// <summary>
        /// Добавить дополнительный пользовательский жест не заменяя существующие
        /// </summary>
        bool AddCustomGesture(string id, HotKeyGesture gesture);

        /// <summary>
        /// Удалить пользовательский жест по индексу
        /// </summary>
        bool RemoveCustomGesture(string id, int index);

        /// <summary>
        /// Заменить пользовательский жест по индексу
        /// </summary>
        bool ReplaceCustomGesture(string id, int index, HotKeyGesture gesture);
    }
}