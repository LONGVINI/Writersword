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
    /// Результат попытки назначить жест хоткею или зарегистрировать префикс.
    /// Используется вместо bool чтобы VM мог показать конкретную причину блокировки.
    /// </summary>
    public enum GestureAssignResult
    {
        /// <summary>Операция выполнена успешно</summary>
        Ok,

        /// <summary>
        /// Жест уже используется другим хоткеем как одиночная команда.
        /// Нельзя зарегистрировать его как префикс.
        /// </summary>
        BlockedByHotKey,

        /// <summary>
        /// Жест зарезервирован как префикс последовательности.
        /// Нельзя назначить его одиночным хоткеем.
        /// </summary>
        BlockedByPrefix,

        /// <summary>
        /// Первый шаг последовательности не зарегистрирован как префикс.
        /// Нужно сначала добавить префикс в список префиксов.
        /// </summary>
        PrefixNotRegistered,

        /// <summary>Префикс с таким жестом уже зарегистрирован</summary>
        PrefixAlreadyExists,

        /// <summary>
        /// Префикс используется одним или несколькими хоткеями как первый шаг.
        /// Нельзя удалить пока хоткеи его используют — сначала нужно сменить им жест.
        /// </summary>
        PrefixInUse,

        /// <summary>Хоткей или префикс с указанным идентификатором не найден</summary>
        HotKeyNotFound,
    }

    /// <summary>
    /// Сервис управления горячими клавишами.
    /// Поддерживает одиночные комбинации и последовательности (Ctrl+K -> Ctrl+C).
    /// Хранит определения клавиш (IHotKeyDescriptor) отдельно от executor'ов (IHotKeyProvider).
    /// Определения регистрируются при старте через метаданные модулей,
    /// executor'ы привязываются только когда модуль живой.
    ///
    /// Префиксы — отдельный список жестов зарезервированных как первый шаг последовательности.
    /// Жест зарегистрированный как префикс нельзя назначить одиночным хоткеем.
    /// Хоткей с последовательностью можно назначить только если его первый шаг
    /// зарегистрирован как префикс. Каждый префикс может иметь пользовательский комментарий.
    /// </summary>
    public interface IHotKeyService
    {
        /// <summary>
        /// Зарегистрировать глобальную команду с горячей клавишей.
        /// Область действия автоматически устанавливается в Global.
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
        /// focusedModuleType — moduleType модуля в фокусе (null если нет фокуса).
        /// Возвращает true если нажатие обработано (совпадение или ожидание продолжения).
        /// </summary>
        bool HandleKeyPress(KeyGesture gesture, string? focusedModuleType = null);

        /// <summary>
        /// Получить все зарегистрированные горячие клавиши
        /// </summary>
        IReadOnlyList<HotKey> GetAllHotKeys();

        /// <summary>
        /// Получить горячую клавишу по ID.
        /// Возвращает null если ID не найден.
        /// </summary>
        HotKey? GetHotKey(string id);

        /// <summary>
        /// Получить команду по ID (только для глобальных клавиш).
        /// Возвращает null если ID не найден или клавиша модульная.
        /// </summary>
        ICommand? GetCommand(string id);

        /// <summary>
        /// Установить одиночный пользовательский жест заменяя существующий.
        /// Блокирует если жест зарезервирован как префикс — возвращает BlockedByPrefix.
        /// Передача null очищает пользовательский жест (сброс к дефолту без SaveSettings).
        /// </summary>
        GestureAssignResult SetCustomGesture(string id, KeyGesture? gesture);

        /// <summary>
        /// Установить пользовательский жест — одиночный или последовательность.
        /// Для последовательности блокирует если первый шаг не зарегистрирован как префикс.
        /// Для одиночного блокирует если жест зарезервирован как префикс.
        /// Передача null очищает пользовательский жест.
        /// </summary>
        GestureAssignResult SetCustomGestureSequence(string id, HotKeyGesture? gesture);

        /// <summary>
        /// Сбросить горячую клавишу к значению по умолчанию.
        /// Очищает CustomGestures и сохраняет настройки.
        /// </summary>
        void ResetToDefault(string id);

        /// <summary>
        /// Сбросить все горячие клавиши к значениям по умолчанию.
        /// Не затрагивает список префиксов.
        /// </summary>
        void ResetAllToDefaults();

        /// <summary>
        /// Проверить есть ли конфликт одиночного жеста с другими хоткеями.
        /// excludeId — ID хоткея который нужно исключить из проверки (сам себя).
        /// </summary>
        bool HasConflict(KeyGesture gesture, string? excludeId = null);

        /// <summary>
        /// Получить список ID хоткеев конфликтующих с указанным жестом.
        /// excludeId — ID хоткея который нужно исключить из проверки.
        /// </summary>
        IReadOnlyList<string> GetConflicts(KeyGesture gesture, string? excludeId = null);

        /// <summary>
        /// Определить тип конфликта между двумя зарегистрированными клавишами.
        /// Учитывает область действия (Scope) обеих клавиш.
        /// Возвращает None если клавиши не конфликтуют.
        /// </summary>
        HotKeyConflictType GetConflictType(string idA, string idB);

        /// <summary>
        /// Проверить активен ли executor для указанного moduleType.
        /// Используется в UI чтобы показать состояние модуля (запущен / не запущен).
        /// </summary>
        bool IsExecutorBound(string moduleType);

        /// <summary>
        /// Загрузить пользовательские настройки из SettingsService.
        /// Загружает как жесты хоткеев так и список префиксов с комментариями.
        /// </summary>
        void LoadSettings();

        /// <summary>
        /// Сохранить пользовательские настройки в SettingsService.
        /// Сохраняет как жесты хоткеев так и список префиксов с комментариями.
        /// </summary>
        void SaveSettings();

        /// <summary>
        /// Событие изменения горячих клавиш или префиксов.
        /// Вызывается после любой операции меняющей состояние сервиса.
        /// </summary>
        event Action? HotKeysChanged;

        // -----------------------------------------------------------------------
        // Управление префиксами
        // -----------------------------------------------------------------------

        /// <summary>
        /// Зарегистрировать жест как префикс последовательности с опциональным комментарием.
        /// Блокирует если жест уже используется одиночным хоткеем — возвращает BlockedByHotKey.
        /// Блокирует если префикс уже зарегистрирован — возвращает PrefixAlreadyExists.
        /// После регистрации жест нельзя назначить одиночным хоткеем.
        /// </summary>
        GestureAssignResult RegisterPrefix(KeyGesture gesture, string comment = "");

        /// <summary>
        /// Удалить зарегистрированный пользовательский префикс.
        /// Блокирует если хотя бы один хоткей использует его как первый шаг — возвращает PrefixInUse.
        /// Сначала нужно сменить жест у всех хоткеев использующих этот префикс.
        /// </summary>
        GestureAssignResult UnregisterPrefix(KeyGesture gesture);

        /// <summary>
        /// Обновить комментарий существующего префикса.
        /// Возвращает false если префикс с таким жестом не найден.
        /// Автоматически сохраняет настройки.
        /// </summary>
        bool UpdatePrefixComment(KeyGesture gesture, string comment);

        /// <summary>
        /// Получить все пользовательские префиксы с комментариями.
        /// Не включает автоматически выведенные префиксы дефолтных последовательностей.
        /// </summary>
        IReadOnlyList<HotKeyPrefix> GetUserPrefixes();

        /// <summary>
        /// Получить все зарезервированные жесты-префиксы.
        /// Объединяет пользовательские префиксы и автоматически выведенные
        /// из первых шагов дефолтных последовательностей хоткеев.
        /// Используется для блокировки при назначении одиночных хоткеев.
        /// </summary>
        IReadOnlyList<KeyGesture> GetReservedPrefixes();

        /// <summary>
        /// Проверить зарезервирован ли жест как префикс.
        /// Проверяет как пользовательские так и автоматически выведенные префиксы.
        /// </summary>
        bool IsReservedPrefix(KeyGesture gesture);

        /// <summary>
        /// Получить список ID хоткеев использующих указанный префикс
        /// как первый шаг последовательности в ActiveGesture.
        /// Используется в UI чтобы показать почему префикс нельзя удалить.
        /// </summary>
        IReadOnlyList<string> GetHotKeysUsingPrefix(KeyGesture prefix);

        /// <summary>
        /// Добавить новый пользовательский жест к хоткею не заменяя существующие.
        /// Блокирует если жест зарезервирован как префикс.
        /// Возвращает false если хоткей не найден или жест заблокирован.
        /// </summary>
        bool AddCustomGesture(string id, HotKeyGesture gesture);

        /// <summary>
        /// Удалить пользовательский жест по индексу из списка CustomGestures.
        /// Не делает ничего если индекс вне диапазона или хоткей не найден.
        /// Сохраняет настройки после удаления.
        /// </summary>
        void RemoveCustomGesture(string id, int index);

        /// <summary>
        /// Заменить пользовательский жест по индексу новым.
        /// Блокирует если новый жест зарезервирован как префикс — возвращает BlockedByPrefix.
        /// Блокирует если для последовательности первый шаг не зарегистрирован как префикс.
        /// Возвращает HotKeyNotFound если хоткей не найден или индекс вне диапазона.
        /// </summary>
        GestureAssignResult ReplaceCustomGesture(string id, int index, HotKeyGesture gesture);
    }
}