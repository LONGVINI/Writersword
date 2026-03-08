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
