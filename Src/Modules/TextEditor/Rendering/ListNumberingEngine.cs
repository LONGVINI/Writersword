using System;
using System.Collections.Generic;
using System.Text;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Rendering
{
    /// <summary>
    /// Вычисленный маркер одного элемента списка.
    /// Text — готовая строка маркера («1.», «a)», «•», пользовательский символ).
    /// Геометрия (позиция маркера/текста) берётся из ListProperties в момент отрисовки.
    /// </summary>
    public readonly struct ListMarkerInfo
    {
        public string Text { get; }
        public bool IsNumbered { get; }

        public ListMarkerInfo(string text, bool isNumbered)
        {
            Text = text;
            IsNumbered = isNumbered;
        }
    }

    /// <summary>
    /// Движок нумерации списков.
    /// За один проход по блокам документа в порядке следования вычисляет строку
    /// маркера для каждого параграфа-элемента списка.
    /// Счётчики ведутся отдельно по каждому ListId и по каждому уровню вложенности:
    /// элементы с одним ListId образуют единую нумерацию даже если между ними
    /// стоят обычные параграфы. Появление элемента более мелкого уровня сбрасывает
    /// счётчики всех более глубоких уровней этого списка.
    /// Чистая функция без состояния — вызывается на каждый пересчёт раскладки.
    /// </summary>
    public static class ListNumberingEngine
    {
        private const int MaxLevel = 8;

        /// <summary>
        /// Строит карту «параграф → маркер» для всех элементов списков в наборе блоков.
        /// Параграфы вне списков (ListProperties == null или MarkerType == None) в карту не попадают.
        /// </summary>
        public static Dictionary<ParagraphBlock, ListMarkerInfo> Compute(IReadOnlyList<BlockModel> blocks)
        {
            var result = new Dictionary<ParagraphBlock, ListMarkerInfo>();
            if (blocks is null || blocks.Count == 0) return result;

            // Счётчики нумерованных элементов по каждому списку и уровню.
            var counters = new Dictionary<Guid, int[]>();

            foreach (var block in blocks)
            {
                if (block is not ParagraphBlock para) continue;

                var lp = para.ListProperties;
                if (lp is null || lp.MarkerType == ListMarkerType.None) continue;

                // Тип маркера текущего уровня (для многоуровневого списка каждый уровень свой).
                var effType = lp.EffectiveMarkerTypeForLevel();
                bool numbered = (int)effType >= 10;

                if (!numbered)
                {
                    result[para] = new ListMarkerInfo(BuildBulletMarker(lp, effType), isNumbered: false);
                    continue;
                }

                if (!counters.TryGetValue(lp.ListId, out var levels))
                {
                    levels = new int[MaxLevel + 1];
                    counters[lp.ListId] = levels;
                }

                int level = Math.Clamp(lp.Level, 0, MaxLevel);

                // Появление элемента уровня level сбрасывает более глубокие уровни.
                for (int d = level + 1; d <= MaxLevel; d++)
                    levels[d] = 0;

                if (!lp.ContinueNumbering)
                    levels[level] = lp.StartAt;              // Явный перезапуск нумерации.
                else if (levels[level] == 0)
                    levels[level] = lp.StartAt;              // Первый элемент этого уровня.
                else
                    levels[level] += 1;

                result[para] = new ListMarkerInfo(
                    BuildNumberMarker(lp, effType, levels[level]), isNumbered: true);
            }

            return result;
        }

        // ── Маркированные ─────────────────────────────────────────────────

        private static string BuildBulletMarker(ListProperties lp, ListMarkerType type)
        {
            return type switch
            {
                ListMarkerType.Bullet => "•",   // •
                ListMarkerType.Dash => "–",     // –
                ListMarkerType.Arrow => "➤",    // ➤
                ListMarkerType.Square => "▪",   // ▪
                ListMarkerType.Circle => "◦",   // ◦
                ListMarkerType.Custom => string.IsNullOrEmpty(lp.CustomMarker)
                    ? "•" : lp.CustomMarker!,
                _ => "•"
            };
        }

        // ── Нумерованные ──────────────────────────────────────────────────

        private static string BuildNumberMarker(ListProperties lp, ListMarkerType type, int number)
        {
            // Пользовательская последовательность символов: элемент N берёт символ по индексу.
            if (type == ListMarkerType.CustomSequence)
                return BuildSequenceMarker(lp, number);

            string prefix = lp.NumberPrefix ?? string.Empty;
            string suffix = lp.NumberSuffix ?? ".";
            return prefix + FormatNumber(number, type) + suffix;
        }

        private static string BuildSequenceMarker(ListProperties lp, int number)
        {
            var seq = lp.CustomSequence;
            if (seq is null || seq.Count == 0) return "•";

            int idx = number - 1;            // number начинается с StartAt (обычно 1) → индекс 0
            if (idx < 0) idx = 0;

            string sym;
            if (idx < seq.Count)
                sym = seq[idx];
            else if (lp.SequenceWrap)
                sym = seq[idx % seq.Count];  // повтор сначала
            else
                sym = seq[seq.Count - 1];    // остановка на последнем символе

            string prefix = lp.NumberPrefix ?? string.Empty;
            string suffix = lp.NumberSuffix ?? string.Empty; // для последовательности по умолчанию без разделителя
            return prefix + sym + suffix;
        }

        private static string FormatNumber(int number, ListMarkerType type)
        {
            if (number < 1) number = 1;
            return type switch
            {
                ListMarkerType.Decimal => number.ToString(),
                ListMarkerType.DecimalLeadingZero => number < 10
                    ? "0" + number.ToString() : number.ToString(),
                ListMarkerType.LowerAlpha => ToAlpha(number, upper: false),
                ListMarkerType.UpperAlpha => ToAlpha(number, upper: true),
                ListMarkerType.LowerRoman => ToRoman(number, upper: false),
                ListMarkerType.UpperRoman => ToRoman(number, upper: true),
                _ => number.ToString()
            };
        }

        // 1→a, 26→z, 27→aa, 28→ab …
        private static string ToAlpha(int number, bool upper)
        {
            var sb = new StringBuilder();
            int n = number;
            while (n > 0)
            {
                n--;
                char c = (char)('a' + n % 26);
                sb.Insert(0, c);
                n /= 26;
            }
            string s = sb.ToString();
            return upper ? s.ToUpperInvariant() : s;
        }

        private static readonly (int Value, string Symbol)[] RomanTable =
        {
            (1000, "m"), (900, "cm"), (500, "d"), (400, "cd"),
            (100, "c"), (90, "xc"), (50, "l"), (40, "xl"),
            (10, "x"), (9, "ix"), (5, "v"), (4, "iv"), (1, "i")
        };

        private static string ToRoman(int number, bool upper)
        {
            if (number <= 0 || number >= 4000) return number.ToString();
            var sb = new StringBuilder();
            int n = number;
            foreach (var (value, symbol) in RomanTable)
            {
                while (n >= value)
                {
                    sb.Append(symbol);
                    n -= value;
                }
            }
            string s = sb.ToString();
            return upper ? s.ToUpperInvariant() : s;
        }
    }
}
