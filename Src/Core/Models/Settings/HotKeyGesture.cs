using Avalonia.Input;
using System.Collections.Generic;
using System.Linq;

namespace Writersword.Core.Models.Settings
{
    /// <summary>
    /// Жест горячей клавиши — одиночная комбинация или последовательность
    /// Примеры:
    ///   Одиночная:          Ctrl+S
    ///   Последовательность: Ctrl+K -> Ctrl+C
    ///
    /// Если жест используется как первый шаг последовательности —
    /// он резервируется как префикс и не может быть одиночной командой
    /// </summary>
    public class HotKeyGesture
    {
        /// <summary>
        /// Шаги последовательности
        /// Для одиночной комбинации содержит один элемент
        /// </summary>
        public IReadOnlyList<KeyGesture> Steps { get; }

        /// <summary>Является ли жест одиночной комбинацией</summary>
        public bool IsSingle => Steps.Count == 1;

        /// <summary>Является ли жест последовательностью из нескольких шагов</summary>
        public bool IsSequence => Steps.Count > 1;

        /// <summary>Первый шаг — используется для определения префиксов</summary>
        public KeyGesture FirstStep => Steps[0];

        public HotKeyGesture(KeyGesture single)
        {
            Steps = new[] { single };
        }

        public HotKeyGesture(IEnumerable<KeyGesture> sequence)
        {
            Steps = sequence.ToList();
        }

        /// <summary>
        /// Проверить совпадает ли жест с накопленной последовательностью нажатий
        /// </summary>
        public bool Matches(IReadOnlyList<KeyGesture> pressed)
        {
            if (pressed.Count != Steps.Count) return false;

            for (int i = 0; i < Steps.Count; i++)
            {
                if (!GesturesEqual(Steps[i], pressed[i]))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Проверить является ли накопленная последовательность началом этого жеста
        /// Возвращает true если pressed — это неполный префикс Steps
        /// </summary>
        public bool IsPrefix(IReadOnlyList<KeyGesture> pressed)
        {
            if (pressed.Count == 0 || pressed.Count >= Steps.Count) return false;

            for (int i = 0; i < pressed.Count; i++)
            {
                if (!GesturesEqual(Steps[i], pressed[i]))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Проверить совпадает ли первый шаг этого жеста с указанным
        /// Используется для определения зарезервированных префиксов
        /// </summary>
        public bool HasPrefix(KeyGesture gesture)
        {
            return GesturesEqual(FirstStep, gesture);
        }

        public override string ToString()
        {
            return string.Join(" → ", Steps.Select(s => s.ToString()));
        }

        private static bool GesturesEqual(KeyGesture a, KeyGesture b)
        {
            return a.Key == b.Key && a.KeyModifiers == b.KeyModifiers;
        }
    }
}