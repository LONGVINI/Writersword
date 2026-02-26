using Avalonia.Input;
using System.Collections.Generic;
using System.Linq;
using Writersword.Core.Enums;

namespace Writersword.Core.Models.Settings
{
    /// <summary>
    /// Модель горячей клавиши.
    /// Поддерживает одиночные комбинации и последовательности (Ctrl+K -> Ctrl+C).
    /// Поддерживает несколько вариантов жестов на одно действие (мульти-бинд).
    /// Например: действие "Сохранить" может срабатывать на Ctrl+S и Ctrl+K -> Ctrl+S одновременно.
    /// </summary>
    public class HotKey
    {
        /// <summary>Уникальный идентификатор команды</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Категория команды — для группировки в UI</summary>
        public HotKeyCategory Category { get; set; }

        /// <summary>Область действия — определяет когда клавиша активна</summary>
        public HotKeyScope Scope { get; set; } = HotKeyScope.Global;

        /// <summary>
        /// Тип модуля-владельца (null для глобальных клавиш).
        /// Используется для группировки в UI и определения конфликтов.
        /// </summary>
        public string? ModuleType { get; set; }

        /// <summary>Отображаемое имя команды (уже переведённая строка)</summary>
        public string DisplayNameKey { get; set; } = string.Empty;

        /// <summary>Описание команды (опционально)</summary>
        public string? DescriptionKey { get; set; }

        /// <summary>
        /// Жесты по умолчанию — список вариантов.
        /// Пустой список означает что жестов по умолчанию нет,
        /// пользователь назначает сам.
        /// </summary>
        public List<HotKeyGesture> DefaultGestures { get; set; } = new();

        /// <summary>
        /// Пользовательские жесты — список вариантов.
        /// Пустой список означает что пользователь не менял жесты.
        /// null-элементы не допускаются.
        /// </summary>
        public List<HotKeyGesture> CustomGestures { get; set; } = new();

        /// <summary>
        /// Активные жесты — пользовательские если заданы, иначе дефолтные.
        /// Всегда возвращает непустой список или пустой если ни одного жеста не задано.
        /// </summary>
        public IReadOnlyList<HotKeyGesture> ActiveGestures =>
            CustomGestures.Count > 0 ? CustomGestures : DefaultGestures;

        /// <summary>
        /// Первый активный жест.
        /// Обратная совместимость — используется в местах где нужен один жест для отображения.
        /// </summary>
        public HotKeyGesture? ActiveGesture => ActiveGestures.FirstOrDefault();

        /// <summary>
        /// Первый жест по умолчанию.
        /// Обратная совместимость — используется в UI для колонки "По умолчанию".
        /// </summary>
        public HotKeyGesture? DefaultGesture
        {
            get => DefaultGestures.FirstOrDefault();
            set
            {
                DefaultGestures.Clear();
                if (value != null)
                    DefaultGestures.Add(value);
            }
        }

        /// <summary>
        /// Первый пользовательский жест.
        /// Обратная совместимость — используется в SetCustomGesture.
        /// </summary>
        public HotKeyGesture? CustomGesture
        {
            get => CustomGestures.FirstOrDefault();
            set
            {
                CustomGestures.Clear();
                if (value != null)
                    CustomGestures.Add(value);
            }
        }

        /// <summary>Можно ли изменять эту горячую клавишу пользователем</summary>
        public bool IsCustomizable { get; set; } = true;

        /// <summary>
        /// Добавить дополнительный жест по умолчанию.
        /// Используется при регистрации модуля если нужно несколько дефолтных вариантов.
        /// </summary>
        public void AddDefaultGesture(HotKeyGesture gesture)
        {
            if (!DefaultGestures.Contains(gesture))
                DefaultGestures.Add(gesture);
        }

        /// <summary>
        /// Добавить пользовательский жест не заменяя существующие.
        /// Используется когда пользователь добавляет дополнительный вариант нажатия.
        /// </summary>
        public void AddCustomGesture(HotKeyGesture gesture)
        {
            if (!CustomGestures.Contains(gesture))
                CustomGestures.Add(gesture);
        }

        /// <summary>
        /// Удалить конкретный пользовательский жест по индексу.
        /// </summary>
        public void RemoveCustomGesture(int index)
        {
            if (index >= 0 && index < CustomGestures.Count)
                CustomGestures.RemoveAt(index);
        }

        /// <summary>
        /// Заменить пользовательский жест по индексу.
        /// Используется при редактировании конкретного варианта в UI.
        /// </summary>
        public void ReplaceCustomGesture(int index, HotKeyGesture gesture)
        {
            if (index >= 0 && index < CustomGestures.Count)
                CustomGestures[index] = gesture;
            else
                CustomGestures.Add(gesture);
        }

        /// <summary>
        /// Сбросить все пользовательские жесты — вернуться к дефолту.
        /// </summary>
        public void ClearCustomGestures()
        {
            CustomGestures.Clear();
        }

        /// <summary>
        /// Есть ли хотя бы один пользовательский жест.
        /// Используется в UI для отображения кнопки сброса.
        /// </summary>
        public bool HasCustomGestures => CustomGestures.Count > 0;
    }
}