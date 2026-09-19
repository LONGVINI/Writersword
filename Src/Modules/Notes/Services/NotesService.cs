using System;
using System.Collections.Generic;
using System.Text;
using Writersword.Modules.Notes.Models;

namespace Writersword.Modules.Notes.Services
{
    /// <summary>
    /// Разбор разметки строк и перевод страницы в обычный текст.
    ///
    /// Собрано в одном месте намеренно. Раньше разбор сокращений жил и в
    /// модели представления, и в разметке представления, причём в двух копиях
    /// с разным порядком проверок: «---» в одной срабатывало до списков, в
    /// другой после, и одна и та же строка превращалась то в разделитель, то
    /// в абзац.
    ///
    /// Разметка двух родов. Начало строки задаёт её вид — заголовок, список,
    /// цитата, код, черта. Внутри строки работает оформление кусков текста —
    /// жирный, курсив, зачёркнутый, код, выделенный.
    /// </summary>
    public static class NotesService
    {
        /// <summary>Разделители строк, по которым разбирается вставленный текст.</summary>
        private static readonly string[] LineSeparators = { "\r\n", "\n", "\r" };

        /// <summary>Отступ, которым записывается строка кода.</summary>
        private const string CodeIndent = "    ";

        /// <summary>Предел вложенности оформления внутри строки.</summary>
        private const int MaxInlineDepth = 6;

        // ── Вид строки ────────────────────────────────────────────────────

        /// <summary>
        /// Опознать сокращение в начале строки.
        /// Возвращает false, если строка начинается не с сокращения: тогда
        /// <paramref name="type"/> равен <see cref="NoteBlockType.Paragraph"/>,
        /// а <paramref name="content"/> повторяет исходный текст без изменений.
        /// </summary>
        /// <param name="text">Текст строки.</param>
        /// <param name="type">Опознанный вид строки.</param>
        /// <param name="content">Текст без сокращения.</param>
        /// <param name="isChecked">Задача записана отмеченной — «- [x] ».</param>
        public static bool TryParseShortcut(
            string? text, out NoteBlockType type, out string content, out bool isChecked)
        {
            type = NoteBlockType.Paragraph;
            content = text ?? string.Empty;
            isChecked = false;

            if (string.IsNullOrEmpty(text))
                return false;

            // Отступ в четыре пробела — код. Проверяется до обрезки пробелов:
            // после неё отступ неотличим от обычного выравнивания.
            if (text.StartsWith(CodeIndent, StringComparison.Ordinal))
            {
                type = NoteBlockType.Code;
                content = text.Substring(CodeIndent.Length);
                return true;
            }

            var value = text.TrimStart();

            // Порядок проверок важен: «###» начинается с «#», «- [ ] » — с «- ».
            // Длинные сокращения проверяются раньше коротких.
            if (IsDividerLine(value))
            {
                type = NoteBlockType.Divider;
                content = string.Empty;
                return true;
            }

            if (value.StartsWith("```", StringComparison.Ordinal))
            {
                type = NoteBlockType.Code;
                content = value.Substring(3).TrimStart();
                return true;
            }

            if (TryParseChecklist(value, out content, out isChecked))
            {
                type = NoteBlockType.Checklist;
                return true;
            }

            for (var level = 6; level >= 1; level--)
            {
                var prefix = new string('#', level) + " ";
                if (!value.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                type = NoteBlockType.Heading1 + (level - 1);
                content = value.Substring(prefix.Length);
                return true;
            }

            if (TryParseNumbered(value, out content))
            {
                type = NoteBlockType.Numbered;
                return true;
            }

            if (StartsWith(value, "- ", out content) || StartsWith(value, "* ", out content) ||
                StartsWith(value, "+ ", out content) || StartsWith(value, "• ", out content))
            {
                type = NoteBlockType.Bullet;
                return true;
            }

            if (StartsWith(value, "> ", out content))
            {
                type = NoteBlockType.Quote;
                return true;
            }

            content = text;
            return false;
        }

        /// <summary>
        /// Опознать сокращение, набранное перед кареткой.
        /// Применяется в момент нажатия пробела: сокращение должно занимать
        /// всю строку до каретки, иначе «5 - 3» посреди фразы превратилось бы
        /// в список.
        /// </summary>
        /// <param name="text">Текст строки целиком.</param>
        /// <param name="caret">Положение каретки в тексте.</param>
        /// <param name="type">Опознанный вид строки.</param>
        /// <param name="isChecked">Задача записана отмеченной.</param>
        /// <param name="consumed">Сколько символов занимает сокращение.</param>
        public static bool TryParseShortcutBeforeCaret(
            string? text, int caret, out NoteBlockType type, out bool isChecked, out int consumed)
        {
            type = NoteBlockType.Paragraph;
            isChecked = false;
            consumed = 0;

            if (string.IsNullOrEmpty(text) || caret <= 0 || caret > text.Length)
                return false;

            var head = text.Substring(0, caret);

            // Черта и начало кода записываются без завершающего пробела,
            // поэтому общая проверка их здесь не узнаёт: «--- » это уже не «---».
            var trimmedHead = head.Trim();
            if (IsDividerLine(trimmedHead))
            {
                type = NoteBlockType.Divider;
                consumed = caret;
                return true;
            }

            if (trimmedHead == "```")
            {
                type = NoteBlockType.Code;
                consumed = caret;
                return true;
            }

            // Пробел ещё не вставлен: сокращение проверяется вместе с ним.
            if (!TryParseShortcut(head + " ", out var parsed, out var rest, out var parsedChecked))
                return false;

            // Сокращение должно занимать всю строку до каретки без остатка.
            if (rest.Length != 0)
                return false;

            type = parsed;
            isChecked = parsedChecked;
            consumed = caret;
            return true;
        }

        /// <summary>
        /// Разобрать вставленный или перенесённый текст в строки заметки.
        /// Сокращения применяются к каждой строке: вставленный markdown
        /// становится настоящими списками и заголовками.
        /// </summary>
        public static List<NoteBlock> ParseText(string? text)
        {
            var blocks = new List<NoteBlock>();
            if (string.IsNullOrEmpty(text))
                return blocks;

            foreach (var line in text.Split(LineSeparators, StringSplitOptions.None))
            {
                TryParseShortcut(line, out var type, out var content, out var isChecked);
                blocks.Add(new NoteBlock
                {
                    Type = type,
                    Text = type == NoteBlockType.Divider ? string.Empty : content,
                    IsChecked = type == NoteBlockType.Checklist && isChecked
                });
            }

            return blocks;
        }

        // ── Оформление внутри строки ──────────────────────────────────────

        /// <summary>
        /// Разобрать оформление внутри строки.
        /// Возвращает куски текста без знаков разметки: «очень **важно**» —
        /// это «очень » обычным и «важно» жирным.
        /// </summary>
        public static List<NoteSpan> ParseInline(string? text)
        {
            var spans = new List<NoteSpan>();
            AppendInline(spans, text ?? string.Empty, NoteSpanStyle.None, 0);
            if (spans.Count == 0)
                spans.Add(new NoteSpan(string.Empty, NoteSpanStyle.None));
            return spans;
        }

        /// <summary>
        /// В строке есть оформление, которое разметка нарисует иначе, чем
        /// записано. Пока его нет, показывать поверх поля отрисованную надпись
        /// незачем.
        /// </summary>
        public static bool HasInlineMarkup(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            var spans = ParseInline(text);
            if (spans.Count != 1)
                return true;
            return spans[0].Style != NoteSpanStyle.None || spans[0].Text.Length != text.Length;
        }

        // ── Обычный текст ─────────────────────────────────────────────────

        /// <summary>
        /// Перевести страницу в обычный текст с сокращениями.
        /// Тот же текст можно вставить обратно и получить те же строки.
        /// </summary>
        public static string ToPlainText(NotePage? page)
        {
            if (page == null)
                return string.Empty;

            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(page.Title))
            {
                builder.Append(page.Title);
                builder.Append(Environment.NewLine);
                builder.Append(Environment.NewLine);
            }

            var number = 0;
            for (var i = 0; i < page.Blocks.Count; i++)
            {
                var block = page.Blocks[i];
                number = block.Type == NoteBlockType.Numbered ? number + 1 : 0;
                if (i > 0)
                    builder.Append(Environment.NewLine);
                builder.Append(ToPlainText(block, number));
            }

            return builder.ToString();
        }

        /// <summary>Перевести одну строку в обычный текст с сокращением.</summary>
        /// <param name="block">Строка.</param>
        /// <param name="number">Номер строки нумерованного списка; для прочих видов не используется.</param>
        public static string ToPlainText(NoteBlock? block, int number = 1)
        {
            if (block == null)
                return string.Empty;

            return block.Type switch
            {
                NoteBlockType.Heading1 => "# " + block.Text,
                NoteBlockType.Heading2 => "## " + block.Text,
                NoteBlockType.Heading3 => "### " + block.Text,
                NoteBlockType.Heading4 => "#### " + block.Text,
                NoteBlockType.Heading5 => "##### " + block.Text,
                NoteBlockType.Heading6 => "###### " + block.Text,
                NoteBlockType.Bullet => "- " + block.Text,
                NoteBlockType.Numbered => Math.Max(number, 1) + ". " + block.Text,
                NoteBlockType.Checklist => (block.IsChecked ? "- [x] " : "- [ ] ") + block.Text,
                NoteBlockType.Quote => "> " + block.Text,
                NoteBlockType.Code => CodeIndent + block.Text,
                NoteBlockType.Divider => "---",
                _ => block.Text
            };
        }

        /// <summary>
        /// Строка пуста по смыслу: обычный текст без содержимого и без пометок.
        /// Черта пустой не считается — она и не должна ничего содержать.
        /// </summary>
        public static bool IsEmpty(NoteBlock? block) =>
            block != null &&
            block.Type == NoteBlockType.Paragraph &&
            block.Text.Length == 0 &&
            !block.IsChecked &&
            !block.IsHighlighted &&
            !block.IsStruckThrough;

        /// <summary>Создать страницу с одной пустой строкой.</summary>
        public static NotePage CreatePage(string title)
        {
            var now = DateTime.UtcNow;
            return new NotePage
            {
                Title = title,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Blocks = new List<NoteBlock> { new() }
            };
        }

        // ── Внутреннее ────────────────────────────────────────────────────

        /// <summary>Строка целиком состоит из знаков горизонтальной черты.</summary>
        private static bool IsDividerLine(string value)
        {
            if (value.Length < 3)
                return false;

            var mark = value[0];
            if (mark != '-' && mark != '*' && mark != '_')
                return false;

            foreach (var symbol in value)
            {
                if (symbol != mark)
                    return false;
            }

            return true;
        }

        /// <summary>Опознать «- [ ] » и «- [x] » с любым из трёх знаков списка.</summary>
        private static bool TryParseChecklist(string value, out string content, out bool isChecked)
        {
            content = value;
            isChecked = false;

            // «- [ ] » — шесть символов: знак списка, пробел, скобка, состояние,
            // скобка, пробел.
            if (value.Length < 6)
                return false;
            if (value[0] != '-' && value[0] != '*' && value[0] != '+')
                return false;
            if (value[1] != ' ' || value[2] != '[' || value[4] != ']' || value[5] != ' ')
                return false;

            var state = value[3];
            if (state == 'x' || state == 'X')
                isChecked = true;
            else if (state != ' ')
                return false;

            content = value.Substring(6);
            return true;
        }

        /// <summary>Опознать «12. » и «12) ».</summary>
        private static bool TryParseNumbered(string value, out string content)
        {
            content = value;

            var digits = 0;
            while (digits < value.Length && char.IsAsciiDigit(value[digits]))
                digits++;

            // Номер должен быть, а за ним — точка или скобка и пробел.
            if (digits == 0 || digits + 1 >= value.Length)
                return false;
            if (value[digits] != '.' && value[digits] != ')')
                return false;
            if (value[digits + 1] != ' ')
                return false;

            content = value.Substring(digits + 2);
            return true;
        }

        /// <summary>
        /// Разобрать кусок строки, добавляя найденные куски в список.
        /// Вложенность ограничена: «**«**«**…» в тексте не должно уводить
        /// разбор на произвольную глубину.
        /// </summary>
        private static void AppendInline(List<NoteSpan> spans, string text, NoteSpanStyle style, int depth)
        {
            if (text.Length == 0)
                return;

            if (depth >= MaxInlineDepth)
            {
                Append(spans, text, style);
                return;
            }

            var plain = new StringBuilder();
            var i = 0;
            while (i < text.Length)
            {
                if (TryReadMarked(text, i, style, out var marker, out var inner, out var next))
                {
                    if (plain.Length > 0)
                    {
                        Append(spans, plain.ToString(), style);
                        plain.Clear();
                    }

                    // Внутри кода разметка не разбирается: «`**`» — это две
                    // звёздочки, а не начало жирного.
                    if ((marker & NoteSpanStyle.Code) != 0)
                        Append(spans, inner, style | marker);
                    else
                        AppendInline(spans, inner, style | marker, depth + 1);

                    i = next;
                    continue;
                }

                plain.Append(text[i]);
                i++;
            }

            if (plain.Length > 0)
                Append(spans, plain.ToString(), style);
        }

        /// <summary>
        /// Прочитать от позиции знак разметки и парный к нему.
        /// Возвращает false, если на этом месте разметки нет или пара не
        /// закрыта — тогда знак остаётся обычным символом текста.
        /// </summary>
        private static bool TryReadMarked(
            string text, int start, NoteSpanStyle style, out NoteSpanStyle marker, out string inner, out int next)
        {
            marker = NoteSpanStyle.None;
            inner = string.Empty;
            next = start;

            foreach (var (token, kind) in Markers)
            {
                // Один и тот же знак внутри себя не повторяется: «**a**» —
                // это жирное «a», а не жирное жирное.
                if ((style & kind) != 0)
                    continue;
                if (!Matches(text, start, token))
                    continue;

                // Подчёркивание работает только на границе слова, иначе
                // имя_вроде_этого расползалось бы на курсив.
                if (token[0] == '_' && start > 0 && char.IsLetterOrDigit(text[start - 1]))
                    continue;

                var from = start + token.Length;
                var close = IndexOfToken(text, from, token);
                if (close < 0 || close == from)
                    continue;
                if (token[0] == '_' && close + token.Length < text.Length &&
                    char.IsLetterOrDigit(text[close + token.Length]))
                    continue;

                marker = kind;
                inner = text.Substring(from, close - from);
                next = close + token.Length;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Знаки оформления по убыванию длины: «**» обязан проверяться раньше
        /// «*», иначе жирный текст разберётся как курсив со звёздочками.
        /// </summary>
        private static readonly (string Token, NoteSpanStyle Style)[] Markers =
        {
            ("**", NoteSpanStyle.Bold),
            ("__", NoteSpanStyle.Bold),
            ("~~", NoteSpanStyle.Strikethrough),
            ("==", NoteSpanStyle.Mark),
            ("`", NoteSpanStyle.Code),
            ("*", NoteSpanStyle.Italic),
            ("_", NoteSpanStyle.Italic)
        };

        private static bool Matches(string text, int start, string token)
        {
            if (start + token.Length > text.Length)
                return false;
            for (var i = 0; i < token.Length; i++)
            {
                if (text[start + i] != token[i])
                    return false;
            }

            return true;
        }

        /// <summary>Найти парный знак, не считая за него более длинный знак из того же символа.</summary>
        private static int IndexOfToken(string text, int from, string token)
        {
            for (var i = from; i + token.Length <= text.Length; i++)
            {
                if (!Matches(text, i, token))
                    continue;

                // «*» не должен закрываться первой звёздочкой пары «**».
                if (token.Length == 1 && i + 1 < text.Length && text[i + 1] == token[0])
                {
                    i++;
                    continue;
                }

                return i;
            }

            return -1;
        }

        /// <summary>Добавить кусок, склеивая его с предыдущим такого же оформления.</summary>
        private static void Append(List<NoteSpan> spans, string text, NoteSpanStyle style)
        {
            if (text.Length == 0)
                return;

            if (spans.Count > 0 && spans[^1].Style == style)
            {
                spans[^1] = new NoteSpan(spans[^1].Text + text, style);
                return;
            }

            spans.Add(new NoteSpan(text, style));
        }

        /// <summary>
        /// Отрезать сокращение от начала строки.
        /// Возвращает false и оставляет <paramref name="rest"/> прежним, если
        /// строка начинается не с этого сокращения.
        /// </summary>
        private static bool StartsWith(string value, string prefix, out string rest)
        {
            if (value.StartsWith(prefix, StringComparison.Ordinal))
            {
                rest = value.Substring(prefix.Length);
                return true;
            }

            rest = value;
            return false;
        }
    }
}
