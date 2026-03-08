using System.Collections.Generic;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Применяет правила автозамены к введённому тексту.
    /// Вызывается после каждого символа введённого пользователем.
    /// </summary>
    public sealed class AutoReplaceService
    {
        /// <summary>
        /// Максимальная длина паттерна замены.
        /// Используется для ограничения глубины просмотра назад.
        /// </summary>
        private const int MaxPatternLength = 10;

        /// <summary>
        /// Проверяет текст заканчивающийся на только что введённый символ
        /// и возвращает замену если одно из правил сработало.
        /// </summary>
        /// <param name="textBefore">
        /// Текст перед курсором (включая только что введённый символ).
        /// Передаётся хвост не длиннее <see cref="MaxPatternLength"/> символов.
        /// </param>
        /// <param name="rules">Активные правила замены (глобальные + документа).</param>
        /// <param name="matchedFrom">Строка которую нужно заменить. Null если замена не найдена.</param>
        /// <param name="matchedTo">Строка замены. Null если замена не найдена.</param>
        /// <returns>True если найдено правило для замены.</returns>
        public bool TryMatch(
            string textBefore,
            IReadOnlyList<AutoReplaceRule> rules,
            out string? matchedFrom,
            out string? matchedTo)
        {
            matchedFrom = null;
            matchedTo = null;

            if (string.IsNullOrEmpty(textBefore)) return false;

            // Ограничиваем хвост для производительности.
            string tail = textBefore.Length > MaxPatternLength
                ? textBefore.Substring(textBefore.Length - MaxPatternLength)
                : textBefore;

            foreach (var rule in rules)
            {
                if (!rule.IsEnabled || string.IsNullOrEmpty(rule.From)) continue;

                if (tail.EndsWith(rule.From, System.StringComparison.Ordinal))
                {
                    matchedFrom = rule.From;
                    matchedTo = rule.To;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Применяет замену к строке текста: убирает <paramref name="from"/> с конца
        /// и добавляет <paramref name="to"/>.
        /// </summary>
        public string ApplyReplacement(string text, string from, string to)
        {
            if (!text.EndsWith(from, System.StringComparison.Ordinal)) return text;
            return text.Substring(0, text.Length - from.Length) + to;
        }
    }
}
