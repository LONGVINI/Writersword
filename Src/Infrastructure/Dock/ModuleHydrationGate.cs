using System;
using System.Collections.Generic;

namespace Writersword.Infrastructure.Dock
{
    /// <summary>
    /// Ворота гидрации модулей. Пока ворота удержаны (Hold), запуск загрузки
    /// вью модулей откладывается: плейсхолдеры показываются сразу, а реальная
    /// гидрация (чтение данных, создание вьюмоделей и вью) стартует только
    /// после освобождения (Release).
    /// Используется перетаскиванием вкладок: вкладка переключается мгновенно —
    /// строится layout с окнами модулей, — но тяжёлая загрузка не запускается,
    /// пока кнопка мыши не отпущена, поэтому drag остаётся идеально плавным.
    /// Все вызовы — только с UI-потока.
    /// </summary>
    public static class ModuleHydrationGate
    {
        private static int _holdCount;
        private static readonly List<Action> _pending = new();

        /// <summary>Ворота удержаны — гидрация откладывается.</summary>
        public static bool IsHeld => _holdCount > 0;

        /// <summary>Удержать ворота (вложенные удержания складываются).</summary>
        public static void Hold() => _holdCount++;

        /// <summary>
        /// Освободить ворота. Когда счётчик доходит до нуля — выполняются
        /// все отложенные запуски гидрации в порядке постановки.
        /// </summary>
        public static void Release()
        {
            if (_holdCount == 0) return;

            _holdCount--;
            if (_holdCount > 0) return;

            if (_pending.Count == 0) return;

            var toRun = new List<Action>(_pending);
            _pending.Clear();

            foreach (var action in toRun)
                action();
        }

        /// <summary>
        /// Выполнить запуск гидрации немедленно, либо отложить до освобождения
        /// ворот, если они удержаны.
        /// </summary>
        public static void EnqueueOrRun(Action action)
        {
            if (IsHeld)
                _pending.Add(action);
            else
                action();
        }
    }
}
