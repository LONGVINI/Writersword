using System;
using System.Collections.Generic;
using System.IO;

namespace Writersword.Core.Services
{
    /// <summary>
    /// Файлы, которые нужно влить в только что созданный проект при первом его
    /// открытии. Нужен экрану приветствия: он создаёт проект из документа Word, но
    /// разобрать документ не умеет — это дело модуля правки, а модуль поднимается
    /// позже, когда вкладка проекта уже открыта и рабочая область построена.
    ///
    /// Экран кладёт сюда пару «файл проекта — исходный документ», модуль при
    /// появлении своего вида забирает её и делает импорт. Забирается пара один раз:
    /// повторное открытие того же проекта исходный документ уже не трогает.
    ///
    /// Хранится только в памяти. Если программу закрыли раньше, чем модуль успел
    /// забрать документ, проект остаётся пустым, а исходный файл — нетронутым.
    /// </summary>
    public static class PendingFileImports
    {
        private static readonly object Sync = new();
        private static readonly Dictionary<string, string> Pending = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Запомнить документ, который нужно влить в проект.</summary>
        public static void Register(string projectPath, string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || string.IsNullOrWhiteSpace(sourcePath)) return;

            lock (Sync)
                Pending[Normalize(projectPath)] = sourcePath;
        }

        /// <summary>Ждёт ли проект импорта.</summary>
        public static bool IsPending(string? projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath)) return false;

            lock (Sync)
                return Pending.ContainsKey(Normalize(projectPath!));
        }

        /// <summary>
        /// Забрать документ для проекта. После вызова пары больше нет: второй
        /// забравший получит false, и импорт не выполнится дважды.
        /// </summary>
        public static bool TryTake(string? projectPath, out string sourcePath)
        {
            sourcePath = string.Empty;
            if (string.IsNullOrWhiteSpace(projectPath)) return false;

            string key = Normalize(projectPath!);

            lock (Sync)
            {
                if (!Pending.TryGetValue(key, out var found)) return false;

                Pending.Remove(key);
                sourcePath = found;
                return true;
            }
        }

        /// <summary>Отказаться от импорта: проект так и не открылся.</summary>
        public static void Cancel(string? projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath)) return;

            lock (Sync)
                Pending.Remove(Normalize(projectPath!));
        }

        /// <summary>
        /// Один и тот же файл может прийти разными написаниями пути: с другим
        /// регистром, с «..» или с косой в другую сторону.
        /// </summary>
        private static string Normalize(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }
    }
}
