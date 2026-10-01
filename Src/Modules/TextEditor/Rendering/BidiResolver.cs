using System;
using System.Globalization;

namespace Writersword.Modules.TextEditor.Rendering
{
    /// <summary>
    /// Направление текста по правилам Unicode (UAX #9) для одной строки абзаца: у каждого
    /// знака — уровень вложенности, чётный — слева направо, нечётный — справа налево, и
    /// по уровням — порядок кусков строки на листе.
    ///
    /// Реализована часть алгоритма без явных вставок направления (LRE, RLE, изоляторов):
    /// классы знаков, правила W1–W7 для слабых знаков, N1–N2 для нейтральных, I1–I2 для
    /// уровней, L1 для хвостовых пробелов и L2 для порядка. Этого хватает для обычного
    /// текста: иврит и арабский внутри русского абзаца и русские слова, числа и знаки
    /// препинания внутри абзаца, набранного справа налево.
    ///
    /// Класс знака берётся по блокам Unicode, а не по полной таблице: иврит, арабский,
    /// сирийский, тана, нко и их формы представления — справа налево; цифры, знаки
    /// валют и разделители чисел — свои слабые классы; буквы остальных письменностей —
    /// слева направо.
    /// </summary>
    public static class BidiResolver
    {
        /// <summary>Класс знака по UAX #9.</summary>
        public enum BidiClass : byte
        {
            L, R, AL, EN, ES, ET, AN, CS, NSM, BN, B, S, WS, ON
        }

        /// <summary>Класс знака.</summary>
        public static BidiClass Classify(char c)
        {
            switch (c)
            {
                case '\t': return BidiClass.S;
                case '\n':
                case '\r':
                case '\u2029':
                case '\u001C':
                case '\u001D':
                case '\u001E':
                case '\u0085':
                    return BidiClass.B;
                case ' ':
                case '\u000C':
                case '\u1680':
                case '\u2028':
                case '\u205F':
                case '\u3000':
                    return BidiClass.WS;
                case '\u200E': return BidiClass.L;
                case '\u200F': return BidiClass.R;
                case '\u061C': return BidiClass.AL;
                case '+':
                case '-':
                case '\u207A':
                case '\u207B':
                case '\u208A':
                case '\u208B':
                case '\u2212':
                case '\uFB29':
                case '\uFE62':
                case '\uFE63':
                case '\uFF0B':
                case '\uFF0D':
                    return BidiClass.ES;
                case '#':
                case '$':
                case '%':
                case '\u00B0':
                case '\u00B1':
                case '\u0609':
                case '\u060A':
                case '\u066A':
                case '\u2030':
                case '\u2031':
                case '\u2032':
                case '\u2033':
                case '\u2034':
                case '\u212E':
                    return BidiClass.ET;
                case ',':
                case '.':
                case '/':
                case ':':
                case '\u00A0':
                case '\u060C':
                case '\u202F':
                case '\u2044':
                case '\uFE50':
                case '\uFE52':
                case '\uFE55':
                case '\uFF0C':
                case '\uFF0E':
                case '\uFF0F':
                case '\uFF1A':
                    return BidiClass.CS;
                case '\u00AD':
                case '\u200B':
                case '\u200C':
                case '\u200D':
                case '\u2060':
                case '\uFEFF':
                    return BidiClass.BN;
            }

            if (c >= '0' && c <= '9') return BidiClass.EN;
            if (c == '\u00B2' || c == '\u00B3' || c == '\u00B9') return BidiClass.EN;
            if (c >= '\u06F0' && c <= '\u06F9') return BidiClass.EN;
            if (c >= '\u2070' && c <= '\u2079' && c != '\u2071' && c != '\u2072' && c != '\u2073') return BidiClass.EN;
            if (c >= '\u2080' && c <= '\u2089') return BidiClass.EN;
            if (c >= '\uFF10' && c <= '\uFF19') return BidiClass.EN;

            if (c >= '\u0660' && c <= '\u0669') return BidiClass.AN;
            if (c == '\u066B' || c == '\u066C') return BidiClass.AN;
            if (c >= '\u0600' && c <= '\u0605') return BidiClass.AN;

            if (c >= '\u00A2' && c <= '\u00A5') return BidiClass.ET;
            if (c >= '\u20A0' && c <= '\u20CF') return BidiClass.ET;

            var category = CharUnicodeInfo.GetUnicodeCategory(c);

            if (category == UnicodeCategory.NonSpacingMark || category == UnicodeCategory.EnclosingMark)
                return BidiClass.NSM;

            if (c < ' ' || (c >= '\u007F' && c <= '\u009F'))
                return BidiClass.BN;

            if (category == UnicodeCategory.Format)
                return BidiClass.BN;

            if (category == UnicodeCategory.SpaceSeparator)
                return BidiClass.WS;

            // Иврит и справа налево пишущиеся письменности без арабского начертания.
            if (c >= '\u0590' && c <= '\u05FF') return BidiClass.R;
            if (c >= '\u07C0' && c <= '\u085F') return BidiClass.R;
            if (c >= '\uFB1D' && c <= '\uFB4F') return BidiClass.R;

            // Арабский, сирийский, тана и формы представления арабского.
            if (c >= '\u0600' && c <= '\u07BF') return BidiClass.AL;
            if (c >= '\u0860' && c <= '\u08FF') return BidiClass.AL;
            if (c >= '\uFB50' && c <= '\uFDFF') return BidiClass.AL;
            if (c >= '\uFE70' && c <= '\uFEFF') return BidiClass.AL;

            if (char.IsLetter(c)) return BidiClass.L;

            if (category == UnicodeCategory.SpacingCombiningMark
                || category == UnicodeCategory.LetterNumber
                || category == UnicodeCategory.OtherNumber
                || category == UnicodeCategory.PrivateUse
                || category == UnicodeCategory.Surrogate)
                return BidiClass.L;

            return BidiClass.ON;
        }

        /// <summary>Знак сам по себе пишется справа налево (иврит, арабский).</summary>
        public static bool IsRightToLeftStrong(char c)
        {
            var cls = Classify(c);
            return cls == BidiClass.R || cls == BidiClass.AL;
        }

        /// <summary>В тексте есть знаки, пишущиеся справа налево.</summary>
        public static bool HasRightToLeft(string? text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (char c in text)
            {
                // Быстрый отсев: всё, что ниже иврита, справа налево не пишется.
                if (c < '\u0590') continue;
                if (IsRightToLeftStrong(c)) return true;
            }
            return false;
        }

        /// <summary>
        /// Уровни знаков строки. baseLevel — уровень абзаца: 0 — слева направо,
        /// 1 — справа налево.
        /// </summary>
        public static byte[] ResolveLevels(string text, byte baseLevel)
        {
            int n = text.Length;
            var levels = new byte[n];
            if (n == 0) return levels;

            var initial = new BidiClass[n];
            var types = new BidiClass[n];
            for (int i = 0; i < n; i++)
            {
                initial[i] = Classify(text[i]);
                types[i] = initial[i];
            }

            var sos = (baseLevel & 1) == 0 ? BidiClass.L : BidiClass.R;
            var eos = sos;

            // Знаки без направления (BN) правилами не рассматриваются: они берут класс
            // соседа слева, как метки, и остаются на его уровне.
            for (int i = 0; i < n; i++)
            {
                if (types[i] == BidiClass.BN)
                    types[i] = i == 0 ? sos : types[i - 1];
            }

            // W1: метка берёт класс предыдущего знака.
            for (int i = 0; i < n; i++)
            {
                if (types[i] == BidiClass.NSM)
                    types[i] = i == 0 ? sos : types[i - 1];
            }

            // W2: европейская цифра после арабской буквы — арабская цифра.
            {
                var lastStrong = sos;
                for (int i = 0; i < n; i++)
                {
                    var t = types[i];
                    if (t == BidiClass.L || t == BidiClass.R || t == BidiClass.AL)
                        lastStrong = t;
                    else if (t == BidiClass.EN && lastStrong == BidiClass.AL)
                        types[i] = BidiClass.AN;
                }
            }

            // W3: арабская буква — просто справа налево.
            for (int i = 0; i < n; i++)
            {
                if (types[i] == BidiClass.AL) types[i] = BidiClass.R;
            }

            // W4: одиночный разделитель между цифрами одного рода становится цифрой.
            for (int i = 1; i < n - 1; i++)
            {
                var t = types[i];
                var prev = types[i - 1];
                var next = types[i + 1];

                if (t == BidiClass.ES && prev == BidiClass.EN && next == BidiClass.EN)
                    types[i] = BidiClass.EN;
                else if (t == BidiClass.CS && prev == BidiClass.EN && next == BidiClass.EN)
                    types[i] = BidiClass.EN;
                else if (t == BidiClass.CS && prev == BidiClass.AN && next == BidiClass.AN)
                    types[i] = BidiClass.AN;
            }

            // W5: знаки валют и процента рядом с европейскими цифрами — цифры.
            for (int i = 0; i < n; i++)
            {
                if (types[i] != BidiClass.ET) continue;

                int runStart = i;
                int runEnd = i;
                while (runEnd + 1 < n && types[runEnd + 1] == BidiClass.ET) runEnd++;

                bool touchesEn = (runStart > 0 && types[runStart - 1] == BidiClass.EN)
                    || (runEnd + 1 < n && types[runEnd + 1] == BidiClass.EN);

                if (touchesEn)
                    for (int k = runStart; k <= runEnd; k++) types[k] = BidiClass.EN;

                i = runEnd;
            }

            // W6: оставшиеся разделители и знаки валют — нейтральные.
            for (int i = 0; i < n; i++)
            {
                var t = types[i];
                if (t == BidiClass.ES || t == BidiClass.ET || t == BidiClass.CS)
                    types[i] = BidiClass.ON;
            }

            // W7: европейская цифра после латиницы или кириллицы — слева направо.
            {
                var lastStrong = sos;
                for (int i = 0; i < n; i++)
                {
                    var t = types[i];
                    if (t == BidiClass.L || t == BidiClass.R)
                        lastStrong = t;
                    else if (t == BidiClass.EN && lastStrong == BidiClass.L)
                        types[i] = BidiClass.L;
                }
            }

            // N1, N2: нейтральные между знаками одного направления берут его, иначе —
            // направление абзаца. Цифры для этого считаются знаками справа налево.
            for (int i = 0; i < n; i++)
            {
                if (!IsNeutral(types[i])) continue;

                int runStart = i;
                int runEnd = i;
                while (runEnd + 1 < n && IsNeutral(types[runEnd + 1])) runEnd++;

                var before = runStart == 0 ? sos : StrongForNeutrals(types[runStart - 1]);
                var after = runEnd + 1 >= n ? eos : StrongForNeutrals(types[runEnd + 1]);

                var resolved = before == after
                    ? before
                    : ((baseLevel & 1) == 0 ? BidiClass.L : BidiClass.R);

                for (int k = runStart; k <= runEnd; k++) types[k] = resolved;

                i = runEnd;
            }

            // I1, I2: уровни.
            for (int i = 0; i < n; i++)
            {
                var t = types[i];
                if ((baseLevel & 1) == 0)
                {
                    levels[i] = t switch
                    {
                        BidiClass.R => (byte)(baseLevel + 1),
                        BidiClass.AN or BidiClass.EN => (byte)(baseLevel + 2),
                        _ => baseLevel
                    };
                }
                else
                {
                    levels[i] = t switch
                    {
                        BidiClass.L or BidiClass.EN or BidiClass.AN => (byte)(baseLevel + 1),
                        _ => baseLevel
                    };
                }
            }

            // L1: разделители сегментов и абзаца, пробелы перед ними и хвостовые пробелы
            // строки — на уровне абзаца.
            bool trailing = true;
            for (int i = n - 1; i >= 0; i--)
            {
                var original = initial[i];

                if (original == BidiClass.S || original == BidiClass.B)
                {
                    levels[i] = baseLevel;
                    trailing = true;
                    continue;
                }

                if (trailing && (original == BidiClass.WS || original == BidiClass.BN))
                {
                    levels[i] = baseLevel;
                    continue;
                }

                trailing = false;
            }

            return levels;
        }

        /// <summary>
        /// Порядок кусков строки на листе по их уровням (правило L2): начиная с самого
        /// высокого уровня и до самого низкого нечётного, каждая непрерывная цепочка кусков
        /// этого уровня и выше разворачивается. Возвращает номера кусков слева направо.
        /// </summary>
        public static int[] VisualOrder(byte[] levels)
        {
            int n = levels.Length;
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            if (n == 0) return order;

            byte highest = 0;
            byte lowestOdd = byte.MaxValue;
            foreach (byte level in levels)
            {
                if (level > highest) highest = level;
                if ((level & 1) == 1 && level < lowestOdd) lowestOdd = level;
            }

            if (lowestOdd == byte.MaxValue) return order;

            var current = new byte[n];
            Array.Copy(levels, current, n);

            for (int level = highest; level >= lowestOdd; level--)
            {
                int i = 0;
                while (i < n)
                {
                    if (current[i] < level)
                    {
                        i++;
                        continue;
                    }

                    int start = i;
                    while (i < n && current[i] >= level) i++;

                    Array.Reverse(order, start, i - start);
                    Array.Reverse(current, start, i - start);
                }
            }

            return order;
        }

        private static bool IsNeutral(BidiClass t)
            => t == BidiClass.WS || t == BidiClass.ON || t == BidiClass.B || t == BidiClass.S || t == BidiClass.BN;

        private static BidiClass StrongForNeutrals(BidiClass t)
            => t == BidiClass.L ? BidiClass.L : BidiClass.R;
    }
}
