using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Writersword.Modules.TextEditor.Models.Inline;

namespace Writersword.Modules.TextEditor.Models.Document
{
    /// <summary>
    /// Чанк параграфа — единица дельта-кеша.
    /// Один параграф содержит один или несколько чанков.
    /// <para>
    /// Правила разбиения:
    /// - Нормальный размер чанка: до <see cref="NormalChunkSize"/> символов суммарно по всем Run.
    /// - Принудительный сплит при превышении <see cref="SplitThreshold"/> (аномальная вставка).
    /// - Мерж двух соседних чанков если оба меньше <see cref="MergeThreshold"/>. 
    ///   Происходит при сохранении, не в реальном времени.
    /// - Границы сплита выбираются по пробелу/переводу строки, не в середине слова.
    /// </para>
    /// </summary>
    public sealed class TextChunk
    {
        /// <summary>Нормальный верхний предел символов в чанке.</summary>
        public const int NormalChunkSize = 12_000;

        /// <summary>Порог принудительного сплита (аномальная вставка).</summary>
        public const int SplitThreshold = 50_000;

        /// <summary>Порог мержа: оба соседних чанка должны быть меньше этого значения.</summary>
        public const int MergeThreshold = 3_000;

        /// <summary>
        /// Уникальный идентификатор чанка. Стабилен между сессиями.
        /// При сплите старый чанк исчезает, появляются два новых с новыми Id.
        /// При мерже оба исчезают, появляется один новый.
        /// </summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>
        /// SHA-256 хеш содержимого чанка.
        /// Пересчитывается при сохранении в кеш, не при каждом нажатии клавиши.
        /// Сравнивается с хешем в кеше для определения изменений.
        /// </summary>
        [JsonIgnore]
        public string Hash { get; set; } = string.Empty;

        /// <summary>
        /// Список Run внутри чанка.
        /// Run не выходят за границы чанка — при сплите параграфа по Run Run разрезаются.
        /// </summary>
        public List<RunModel> Runs { get; set; } = new();

        /// <summary>
        /// Суммарное количество символов во всех Run чанка.
        /// Вычисляется лениво, сбрасывается при изменении Runs.
        /// </summary>
        [JsonIgnore]
        private int _cachedLength = -1;

        /// <summary>Суммарная длина текста чанка в символах.</summary>
        [JsonIgnore]
        public int Length
        {
            get
            {
                if (_cachedLength < 0)
                    _cachedLength = CalculateLength();
                return _cachedLength;
            }
        }

        /// <summary>Сбрасывает кешированную длину. Вызывать при изменении Runs.</summary>
        public void InvalidateLength() => _cachedLength = -1;

        private int CalculateLength()
        {
            int total = 0;
            foreach (var run in Runs)
                total += run.Text.Length;
            return total;
        }

        /// <summary>Возвращает plain text чанка без форматирования.</summary>
        public string GetPlainText()
        {
            if (Runs.Count == 0) return string.Empty;
            if (Runs.Count == 1) return Runs[0].Text;

            var sb = new System.Text.StringBuilder();
            foreach (var run in Runs)
                sb.Append(run.Text);
            return sb.ToString();
        }


    }
}
