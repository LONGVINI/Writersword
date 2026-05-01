using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Writersword.Modules.TextEditor.Models.Styles;

namespace Writersword.Modules.TextEditor.Models.Document
{
    /// <summary>
    /// Тип маркера списка.
    /// </summary>
    public enum ListMarkerType
    {
        None = 0,
        Bullet = 1,
        Dash = 2,
        Arrow = 3,
        Custom = 4,
        Decimal = 10,
        LowerAlpha = 11,
        UpperAlpha = 12,
        LowerRoman = 13,
        UpperRoman = 14
    }

    /// <summary>
    /// Свойства списка для параграфа.
    /// </summary>
    public sealed class ListProperties
    {
        /// <summary>Id списка (несколько параграфов с одним ListId образуют один список).</summary>
        public Guid ListId { get; set; }

        /// <summary>Уровень вложенности (0–8).</summary>
        public int Level { get; set; }

        /// <summary>Тип маркера.</summary>
        public ListMarkerType MarkerType { get; set; }

        /// <summary>Пользовательский символ маркера при MarkerType.Custom.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CustomMarker { get; set; }

        /// <summary>
        /// Продолжить нумерацию от предыдущего списка с тем же ListId.
        /// Если false — нумерация начинается заново.
        /// </summary>
        public bool ContinueNumbering { get; set; } = true;

        public ListProperties Clone() => (ListProperties)MemberwiseClone();
    }

    /// <summary>
    /// Параграф документа — основной текстовый блок.
    /// Текст хранится в чанках (<see cref="Chunks"/>) для эффективного дельта-кеша.
    /// Свойства форматирования хранятся в <see cref="Properties"/> и ссылке на стиль.
    /// </summary>
    public sealed class ParagraphBlock : BlockModel
    {
        public override BlockType BlockType => BlockType.Paragraph;

        /// <summary>
        /// Чанки параграфа в порядке следования.
        /// Минимум один чанк (может быть пустым для пустого параграфа).
        /// </summary>
        public List<TextChunk> Chunks { get; set; } = new() { new TextChunk() };

        /// <summary>
        /// Свойства форматирования абзаца.
        /// Свойства заданные здесь переопределяют значения из стиля.
        /// </summary>
        public ParagraphProperties Properties { get; set; } = new();

        /// <summary>Свойства списка. Null если параграф не является элементом списка.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ListProperties? ListProperties { get; set; }

        /// <summary>
        /// Суммарная длина текста параграфа в символах.
        /// Вычисляется по сумме длин чанков.
        /// </summary>
        [JsonIgnore]
        public int TotalLength
        {
            get
            {
                int total = 0;
                foreach (var chunk in Chunks)
                    total += chunk.Length;
                return total;
            }
        }

        /// <summary>
        /// Возвращает plain text параграфа без форматирования.
        /// </summary>
        public string GetPlainText()
        {
            if (Chunks.Count == 0) return string.Empty;
            if (Chunks.Count == 1) return Chunks[0].GetPlainText();

            var sb = new System.Text.StringBuilder();
            foreach (var chunk in Chunks)
                sb.Append(chunk.GetPlainText());
            return sb.ToString();
        }

        /// <summary>
        /// Заменяет всё содержимое параграфа одним plain text Run.
        /// Используется при редактировании через TextBox до реализации inline-рендеринга.
        /// </summary>
        public void SetPlainText(string text)
        {
            Chunks.Clear();
            Chunks.Add(new TextChunk
            {
                Runs = new System.Collections.Generic.List<Models.Inline.RunModel>
        {
            new Models.Inline.RunModel { Text = text ?? string.Empty }
        }
            });
            InvalidateAllChunks();
        }

        /// <summary>
        /// Вставляет/удаляет текст в диапазоне [from, to) с сохранением форматирования.
        /// Используется для всех операций редактирования (ввод, Delete, Backspace).
        /// В отличие от SetPlainText, не уничтожает RunProperties.
        /// </summary>
        public void SpliceText(int from, int to, string insert)
        {
            // Строим плоский список символов с их форматированием.
            var chars = new List<(char ch, Models.Inline.RunProperties? props)>();
            foreach (var chunk in Chunks)
                foreach (var run in chunk.Runs)
                    foreach (var ch in run.Text)
                        chars.Add((ch, run.Properties));

            int len = chars.Count;
            from = Math.Max(0, Math.Min(from, len));
            to = Math.Max(from, Math.Min(to, len));

            // Удаляем диапазон.
            if (to > from)
                chars.RemoveRange(from, to - from);

            // Определяем свойства для вставляемого текста:
            // берём форматирование символа в позиции вставки (или предыдущего).
            Models.Inline.RunProperties? insertProps = null;
            if (from < chars.Count)
                insertProps = chars[from].props;
            else if (from > 0)
                insertProps = chars[from - 1].props;

            // Вставляем новые символы.
            for (int i = 0; i < insert.Length; i++)
                chars.Insert(from + i, (insert[i], insertProps));

            // Реконструируем чанки/раны, объединяя соседние символы с одинаковым форматированием.
            Chunks.Clear();
            var newChunk = new TextChunk();
            Chunks.Add(newChunk);

            if (chars.Count == 0)
            {
                newChunk.Runs.Add(new Models.Inline.RunModel { Text = string.Empty });
                InvalidateAllChunks();
                return;
            }

            var sb = new System.Text.StringBuilder();
            var currentProps = chars[0].props;

            foreach (var (ch, props) in chars)
            {
                bool sameProps = ReferenceEquals(props, currentProps)
                    || RunPropertiesEqual(props, currentProps);

                if (!sameProps)
                {
                    newChunk.Runs.Add(new Models.Inline.RunModel
                    {
                        Text = sb.ToString(),
                        Properties = currentProps
                    });
                    sb.Clear();
                    currentProps = props;
                }
                sb.Append(ch);
            }

            newChunk.Runs.Add(new Models.Inline.RunModel
            {
                Text = sb.ToString(),
                Properties = currentProps
            });

            InvalidateAllChunks();
        }

        /// <summary>
        /// Сравнивает два RunProperties по значению всех полей.
        /// Null == Null и Null == default (все поля false/null).
        /// </summary>
        private static bool RunPropertiesEqual(
            Models.Inline.RunProperties? a,
            Models.Inline.RunProperties? b)
        {
            if (ReferenceEquals(a, b)) return true;

            bool aDefault = a is null || a.IsDefault();
            bool bDefault = b is null || b.IsDefault();
            if (aDefault && bDefault) return true;
            if (aDefault || bDefault) return false;

            return a!.FontFamily == b!.FontFamily
                && a.FontSize == b.FontSize
                && a.IsBold == b.IsBold
                && a.IsItalic == b.IsItalic
                && a.IsUnderline == b.IsUnderline
                && a.IsStrikethrough == b.IsStrikethrough
                && a.IsSuperscript == b.IsSuperscript
                && a.IsSubscript == b.IsSubscript
                && a.IsAllCaps == b.IsAllCaps
                && a.IsSmallCaps == b.IsSmallCaps
                && a.TextColor == b.TextColor
                && a.HighlightColor == b.HighlightColor
                && a.Language == b.Language;
        }

        /// <summary>
        /// Сбрасывает кешированные длины всех чанков.
        /// Вызывать после bulk-операций с текстом.
        /// </summary>
        public void InvalidateAllChunks()
        {
            foreach (var chunk in Chunks)
                chunk.InvalidateLength();
        }
    }
}