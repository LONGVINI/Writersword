using System;
using System.Collections.Generic;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Результат проверки одного слова.
    /// </summary>
    public sealed class SpellCheckResult
    {
        /// <summary>Слово корректно.</summary>
        public bool IsCorrect { get; set; }

        /// <summary>Список предложений замены (до 10).</summary>
        public IReadOnlyList<string> Suggestions { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// Обёртка над NHunspell для проверки орфографии.
    /// Словари подбираются по коду языка (<c>ru</c>, <c>uk</c>, <c>en</c> и т.д.).
    /// Словарные файлы (.aff, .dic) ожидаются в директории <c>Assets/Dictionaries/{lang}/</c>.
    /// </summary>
    public sealed class SpellCheckService : IDisposable
    {
        /// <summary>
        /// Кеш загруженных экземпляров Hunspell: lang → объект.
        /// NHunspell.Hunspell потокобезопасен для чтения, но не для одновременного Dispose.
        /// </summary>
        private readonly Dictionary<string, object> _instances = new();
        private bool _disposed;

        /// <summary>
        /// Проверяет одно слово на заданном языке.
        /// При первом вызове для нового языка загружает словарь.
        /// </summary>
        /// <param name="word">Слово без знаков пунктуации.</param>
        /// <param name="language">Код языка (ru, uk, en и т.д.).</param>
        public SpellCheckResult Check(string word, string language)
        {
            // NHunspell пока не подключён как зависимость — заглушка возвращает корректный результат.
            // Подключить: Install-Package NHunspell, загрузить словари,
            // раскомментировать реализацию ниже.
            return new SpellCheckResult { IsCorrect = true };

            /*
            var hunspell = GetOrLoad(language);
            if (hunspell is null)
                return new SpellCheckResult { IsCorrect = true };

            bool correct = hunspell.Spell(word);
            var suggestions = correct
                ? Array.Empty<string>()
                : hunspell.Suggest(word).Take(10).ToArray();

            return new SpellCheckResult
            {
                IsCorrect = correct,
                Suggestions = suggestions
            };
            */
        }

        /// <summary>
        /// Проверяет весь текст и возвращает список диапазонов с ошибками.
        /// Диапазон: (startIndex, length).
        /// </summary>
        public IReadOnlyList<(int Start, int Length)> FindErrors(string text, string language)
        {
            var errors = new List<(int, int)>();

            int i = 0;
            while (i < text.Length)
            {
                // Пропускаем не-буквенные символы.
                if (!char.IsLetter(text[i])) { i++; continue; }

                int start = i;
                while (i < text.Length && char.IsLetter(text[i])) i++;
                string word = text.Substring(start, i - start);

                if (!Check(word, language).IsCorrect)
                    errors.Add((start, word.Length));
            }

            return errors;
        }

        /// <summary>
        /// Возвращает true если словарь для языка доступен.
        /// </summary>
        public bool IsDictionaryAvailable(string language)
        {
            string affPath = $"Assets/Dictionaries/{language}/index.aff";
            return System.IO.File.Exists(affPath);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var instance in _instances.Values)
            {
                if (instance is IDisposable d) d.Dispose();
            }

            _instances.Clear();
        }
    }
}
