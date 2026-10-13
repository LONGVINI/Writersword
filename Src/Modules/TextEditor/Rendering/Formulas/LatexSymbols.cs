using System;
using System.Collections.Generic;

namespace Writersword.Modules.TextEditor.Rendering.Formulas
{
    /// <summary>
    /// Словари записи формул командами LaTeX: буквы и знаки, знаки сумм и интегралов,
    /// функции, надстрочные знаки, пробелы, скобки и цвета. Одни и те же словари
    /// служат разбору записи и обратной записи формулы Word командами.
    /// </summary>
    internal static class LatexSymbols
    {
        /// <summary>Команда → знак.</summary>
        private static readonly Dictionary<string, string> Symbols = new(StringComparer.Ordinal)
        {
            // Греческие строчные.
            ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ",
            ["epsilon"] = "ϵ", ["varepsilon"] = "ε", ["zeta"] = "ζ", ["eta"] = "η",
            ["theta"] = "θ", ["vartheta"] = "ϑ", ["iota"] = "ι", ["kappa"] = "κ",
            ["varkappa"] = "ϰ", ["lambda"] = "λ", ["mu"] = "μ", ["nu"] = "ν",
            ["xi"] = "ξ", ["omicron"] = "ο", ["pi"] = "π", ["varpi"] = "ϖ",
            ["rho"] = "ρ", ["varrho"] = "ϱ", ["sigma"] = "σ", ["varsigma"] = "ς",
            ["tau"] = "τ", ["upsilon"] = "υ", ["phi"] = "ϕ", ["varphi"] = "φ",
            ["chi"] = "χ", ["psi"] = "ψ", ["omega"] = "ω", ["digamma"] = "ϝ",

            // Греческие прописные.
            ["Gamma"] = "Γ", ["Delta"] = "Δ", ["Theta"] = "Θ", ["Lambda"] = "Λ",
            ["Xi"] = "Ξ", ["Pi"] = "Π", ["Sigma"] = "Σ", ["Upsilon"] = "Υ",
            ["Phi"] = "Φ", ["Psi"] = "Ψ", ["Omega"] = "Ω",

            // Буквоподобные знаки.
            ["hbar"] = "ℏ", ["hslash"] = "ℏ", ["ell"] = "ℓ", ["wp"] = "℘",
            ["Re"] = "ℜ", ["Im"] = "ℑ", ["aleph"] = "ℵ", ["beth"] = "ℶ",
            ["gimel"] = "ℷ", ["imath"] = "ı", ["jmath"] = "ȷ",
            ["infty"] = "∞", ["partial"] = "∂", ["nabla"] = "∇",

            // Действия.
            ["pm"] = "±", ["mp"] = "∓", ["times"] = "×", ["div"] = "÷",
            ["cdot"] = "⋅", ["ast"] = "∗", ["star"] = "⋆", ["circ"] = "∘",
            ["bullet"] = "∙", ["oplus"] = "⊕", ["ominus"] = "⊖", ["otimes"] = "⊗",
            ["oslash"] = "⊘", ["odot"] = "⊙", ["cup"] = "∪", ["cap"] = "∩",
            ["setminus"] = "∖", ["wedge"] = "∧", ["land"] = "∧", ["vee"] = "∨",
            ["lor"] = "∨", ["sqcup"] = "⊔", ["sqcap"] = "⊓", ["uplus"] = "⊎",
            ["dagger"] = "†", ["ddagger"] = "‡", ["amalg"] = "⨿",

            // Отношения.
            ["le"] = "≤", ["leq"] = "≤", ["ge"] = "≥", ["geq"] = "≥",
            ["leqslant"] = "⩽", ["geqslant"] = "⩾", ["ne"] = "≠", ["neq"] = "≠",
            ["approx"] = "≈", ["equiv"] = "≡", ["sim"] = "∼", ["simeq"] = "≃",
            ["cong"] = "≅", ["propto"] = "∝", ["ll"] = "≪", ["gg"] = "≫",
            ["prec"] = "≺", ["succ"] = "≻", ["preceq"] = "⪯", ["succeq"] = "⪰",
            ["in"] = "∈", ["notin"] = "∉", ["ni"] = "∋", ["subset"] = "⊂",
            ["supset"] = "⊃", ["subseteq"] = "⊆", ["supseteq"] = "⊇",
            ["subsetneq"] = "⊊", ["supsetneq"] = "⊋", ["perp"] = "⊥",
            ["parallel"] = "∥", ["mid"] = "∣", ["nmid"] = "∤", ["models"] = "⊨",
            ["vdash"] = "⊢", ["dashv"] = "⊣", ["asymp"] = "≍", ["doteq"] = "≐",
            ["coloneqq"] = "≔", ["triangleq"] = "≜",

            // Стрелки.
            ["to"] = "→", ["rightarrow"] = "→", ["leftarrow"] = "←", ["gets"] = "←",
            ["leftrightarrow"] = "↔", ["Rightarrow"] = "⇒", ["Leftarrow"] = "⇐",
            ["Leftrightarrow"] = "⇔", ["iff"] = "⇔", ["implies"] = "⇒",
            ["impliedby"] = "⇐", ["mapsto"] = "↦", ["longrightarrow"] = "⟶",
            ["longleftarrow"] = "⟵", ["Longrightarrow"] = "⟹", ["Longleftarrow"] = "⟸",
            ["longleftrightarrow"] = "⟷", ["Longleftrightarrow"] = "⟺",
            ["longmapsto"] = "⟼", ["uparrow"] = "↑", ["downarrow"] = "↓",
            ["updownarrow"] = "↕", ["Uparrow"] = "⇑", ["Downarrow"] = "⇓",
            ["nearrow"] = "↗", ["searrow"] = "↘", ["swarrow"] = "↙", ["nwarrow"] = "↖",
            ["hookrightarrow"] = "↪", ["hookleftarrow"] = "↩",
            ["rightleftharpoons"] = "⇌",

            // Логика и множества.
            ["forall"] = "∀", ["exists"] = "∃", ["nexists"] = "∄", ["neg"] = "¬",
            ["lnot"] = "¬", ["emptyset"] = "∅", ["varnothing"] = "∅",
            ["therefore"] = "∴", ["because"] = "∵", ["top"] = "⊤", ["bot"] = "⊥",

            // Прочее.
            ["angle"] = "∠", ["measuredangle"] = "∡", ["triangle"] = "△",
            ["square"] = "□", ["Box"] = "□", ["diamond"] = "⋄", ["degree"] = "°",
            ["prime"] = "′", ["ldots"] = "…", ["dots"] = "…", ["cdots"] = "⋯",
            ["vdots"] = "⋮", ["ddots"] = "⋱", ["dotsc"] = "…", ["dotsb"] = "⋯",
            ["checkmark"] = "✓", ["clubsuit"] = "♣", ["diamondsuit"] = "♢",
            ["heartsuit"] = "♡", ["spadesuit"] = "♠", ["flat"] = "♭",
            ["natural"] = "♮", ["sharp"] = "♯", ["S"] = "§", ["P"] = "¶",
            ["copyright"] = "©", ["textdegree"] = "°", ["lbrace"] = "{", ["rbrace"] = "}",
            ["langle"] = "⟨", ["rangle"] = "⟩", ["lfloor"] = "⌊", ["rfloor"] = "⌋",
            ["lceil"] = "⌈", ["rceil"] = "⌉", ["vert"] = "|", ["Vert"] = "‖",
            ["backslash"] = "\\", ["colon"] = ":", ["lvert"] = "|", ["rvert"] = "|",
            ["lVert"] = "‖", ["rVert"] = "‖"
        };

        /// <summary>Знак → запись командой с обратной косой чертой: для обратной записи.</summary>
        private static readonly Dictionary<string, string> CommandsBySymbol = BuildReverse();

        /// <summary>Знаки сумм, произведений и интегралов.</summary>
        public static readonly Dictionary<string, int> NaryOperators = new(StringComparer.Ordinal)
        {
            ["sum"] = 0x2211, ["prod"] = 0x220F, ["coprod"] = 0x2210,
            ["int"] = 0x222B, ["iint"] = 0x222C, ["iiint"] = 0x222D,
            ["oint"] = 0x222E, ["oiint"] = 0x222F, ["oiiint"] = 0x2230,
            ["bigcup"] = 0x22C3, ["bigcap"] = 0x22C2, ["bigvee"] = 0x22C1,
            ["bigwedge"] = 0x22C0, ["bigoplus"] = 0x2A01, ["bigotimes"] = 0x2A02,
            ["bigodot"] = 0x2A00, ["biguplus"] = 0x2A04, ["bigsqcup"] = 0x2A06
        };

        /// <summary>Функции: имя прямым начертанием, за ним аргумент.</summary>
        public static readonly HashSet<string> Functions = new(StringComparer.Ordinal)
        {
            "sin", "cos", "tan", "tg", "cot", "ctg", "sec", "csc", "cosec",
            "arcsin", "arccos", "arctan", "arctg", "arccot", "arcctg",
            "sinh", "cosh", "tanh", "coth", "sh", "ch", "th", "cth",
            "log", "ln", "lg", "exp", "det", "dim", "ker", "deg", "arg", "hom",
            "gcd", "lcm", "Pr", "lim", "limsup", "liminf", "max", "min", "sup", "inf",
            "sgn", "mod", "Tr", "tr", "rank", "Res", "grad", "div", "rot"
        };

        /// <summary>Функции, у которых предел стоит под именем: lim, max, min.</summary>
        public static readonly HashSet<string> LimitFunctions = new(StringComparer.Ordinal)
        {
            "lim", "limsup", "liminf", "max", "min", "sup", "inf", "det", "gcd", "Pr"
        };

        /// <summary>Надстрочные знаки: команда → комбинируемый знак Юникода.</summary>
        public static readonly Dictionary<string, int> Accents = new(StringComparer.Ordinal)
        {
            ["hat"] = 0x0302, ["widehat"] = 0x0302, ["check"] = 0x030C,
            ["tilde"] = 0x0303, ["widetilde"] = 0x0303, ["acute"] = 0x0301,
            ["grave"] = 0x0300, ["dot"] = 0x0307, ["ddot"] = 0x0308,
            ["dddot"] = 0x20DB, ["breve"] = 0x0306, ["bar"] = 0x0305,
            ["vec"] = 0x20D7, ["overrightarrow"] = 0x20D7, ["overleftarrow"] = 0x20D6,
            ["overleftrightarrow"] = 0x20E1, ["mathring"] = 0x030A
        };

        /// <summary>Пробелы: команда → знак пробела нужной ширины.</summary>
        public static readonly Dictionary<string, string> Spaces = new(StringComparer.Ordinal)
        {
            ["quad"] = " ",
            ["qquad"] = "  ",
            ["enspace"] = " ",
            ["thinspace"] = " ",
            ["medspace"] = " ",
            ["thickspace"] = " ",
            ["negthinspace"] = string.Empty,
            ["space"] = " "
        };

        /// <summary>Скобки после \left, \right, \middle: команда → знак.</summary>
        public static readonly Dictionary<string, int> Delimiters = new(StringComparer.Ordinal)
        {
            ["langle"] = 0x27E8, ["rangle"] = 0x27E9, ["lfloor"] = 0x230A,
            ["rfloor"] = 0x230B, ["lceil"] = 0x2308, ["rceil"] = 0x2309,
            ["lbrace"] = '{', ["rbrace"] = '}', ["vert"] = '|', ["Vert"] = 0x2016,
            ["lvert"] = '|', ["rvert"] = '|', ["lVert"] = 0x2016, ["rVert"] = 0x2016,
            ["lbrack"] = '[', ["rbrack"] = ']', ["uparrow"] = 0x2191,
            ["downarrow"] = 0x2193, ["updownarrow"] = 0x2195, ["backslash"] = '\\',
            ["lgroup"] = 0x27EE, ["rgroup"] = 0x27EF, ["llbracket"] = 0x27E6,
            ["rrbracket"] = 0x27E7
        };

        /// <summary>Команда знака или буквы.</summary>
        public static bool TryGet(string name, out string symbol)
            => Symbols.TryGetValue(name, out symbol!);

        /// <summary>Запись знака командой (\le); null — знак пишется как есть.</summary>
        public static string? CommandFor(string symbol)
            => CommandsBySymbol.TryGetValue(symbol, out var command) ? command : null;

        /// <summary>Команда знака суммы или интеграла.</summary>
        public static string NaryCommand(int codepoint)
        {
            foreach (var pair in NaryOperators)
                if (pair.Value == codepoint) return pair.Key;
            return string.Empty;
        }

        /// <summary>Команда надстрочного знака; null — у знака своей команды нет.</summary>
        public static string? AccentCommand(int codepoint)
        {
            foreach (var pair in Accents)
                if (pair.Value == codepoint) return pair.Key;
            return null;
        }

        /// <summary>Запись скобки для \left и \right.</summary>
        public static string DelimiterText(int codepoint)
        {
            if (codepoint is '(' or ')' or '[' or ']' or '|' or '/' or '<' or '>')
                return ((char)codepoint).ToString();

            switch (codepoint)
            {
                case 0: return ".";
                case '{': return "\\{";
                case '}': return "\\}";
                case 0x2016: return "\\|";
            }

            foreach (var pair in Delimiters)
                if (pair.Value == codepoint && !pair.Key.StartsWith("l", StringComparison.Ordinal)
                    && !pair.Key.StartsWith("r", StringComparison.Ordinal))
                    return "\\" + pair.Key;

            foreach (var pair in Delimiters)
                if (pair.Value == codepoint) return "\\" + pair.Key;

            return char.ConvertFromUtf32(codepoint);
        }

        /// <summary>Цвет по имени LaTeX или в виде #RRGGBB → RRGGBB; null — цвет неизвестен.</summary>
        public static string? ColorHex(string name)
        {
            string value = name.Trim();
            if (value.StartsWith("#", StringComparison.Ordinal)) value = value.Substring(1);
            if (value.Length == 6 && IsHex(value)) return value.ToUpperInvariant();

            return value.ToLowerInvariant() switch
            {
                "black" => "000000",
                "white" => "FFFFFF",
                "red" => "FF0000",
                "green" => "00A000",
                "blue" => "0000FF",
                "cyan" => "00FFFF",
                "magenta" => "FF00FF",
                "yellow" => "FFFF00",
                "orange" => "FF8000",
                "purple" => "800080",
                "violet" => "8000FF",
                "brown" => "804000",
                "gray" or "grey" => "808080",
                "darkgray" => "404040",
                "lightgray" => "C0C0C0",
                "pink" => "FFC0C0",
                "olive" => "808000",
                "teal" => "008080",
                _ => null
            };
        }

        private static bool IsHex(string value)
        {
            foreach (char c in value)
                if (!Uri.IsHexDigit(c)) return false;
            return true;
        }

        private static Dictionary<string, string> BuildReverse()
        {
            var reverse = new Dictionary<string, string>(StringComparer.Ordinal);

            // Предпочтительные записи знаков, у которых команд несколько.
            string[] preferred =
            {
                "le", "ge", "ne", "to", "Rightarrow", "Leftarrow", "Leftrightarrow",
                "land", "lor", "neg", "emptyset", "ldots", "cdots", "vert", "Vert",
                "hbar", "bot", "rightarrow", "lbrace", "rbrace", "langle", "rangle"
            };

            foreach (var name in preferred)
                if (Symbols.TryGetValue(name, out var symbol) && !reverse.ContainsKey(symbol))
                    reverse[symbol] = "\\" + name;

            foreach (var pair in Symbols)
                if (!reverse.ContainsKey(pair.Value)) reverse[pair.Value] = "\\" + pair.Key;

            // Греческие буквы и привычные знаки читаются и без команд: в записи
            // формулы они остаются знаками, как их ввёл человек.
            foreach (var keep in new[]
                     {
                         "α", "β", "γ", "δ", "ε", "ϵ", "ζ", "η", "θ", "ϑ", "ι", "κ", "λ", "μ", "ν", "ξ", "ο",
                         "π", "ρ", "σ", "ς", "τ", "υ", "φ", "ϕ", "χ", "ψ", "ω", "Γ", "Δ", "Θ", "Λ", "Ξ",
                         "Π", "Σ", "Υ", "Φ", "Ψ", "Ω", "±", "×", "÷", "≤", "≥", "≠", "≈", "≡", "∞", "→",
                         "←", "⇒", "⇔", "∈", "∉", "⊂", "⊆", "∪", "∩", "∀", "∃", "∅", "∂", "∇", "°", "′",
                         "…", "⋅", "∼", "∝", "{", "}", "|", "⟨", "⟩", "\\"
                     })
            {
                reverse.Remove(keep);
            }

            reverse["{"] = "\\{";
            reverse["}"] = "\\}";
            reverse["\\"] = "\\backslash";

            return reverse;
        }
    }
}
