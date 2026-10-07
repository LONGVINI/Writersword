using System;
using System.Text.Json.Serialization;

namespace Writersword.Modules.TextEditor.Models.Inline
{
    /// <summary>
    /// Как показывать исправления (правки рецензирования) на листе — четыре вида Word
    /// из «Рецензирование → Отображение для проверки».
    /// </summary>
    public enum RevisionView
    {
        /// <summary>
        /// Все исправления: вставленное подчёркнуто, удалённое зачёркнуто, всё цветом
        /// автора правки; у строк с правками на поле — черта.
        /// </summary>
        AllMarkup = 0,

        /// <summary>
        /// Простая разметка: текст как после принятия всех правок, а у строк с правками
        /// на поле — красная черта.
        /// </summary>
        SimpleMarkup = 1,

        /// <summary>Без исправлений: текст как после принятия всех правок, без пометок.</summary>
        NoMarkup = 2,

        /// <summary>Исходный документ: текст как до всех правок — как после их отклонения.</summary>
        Original = 3
    }

    /// <summary>
    /// Отметка одной правки рецензирования: кто и когда её сделал.
    /// Одна и та же запись служит вставке, удалению и смене оформления.
    /// </summary>
    public sealed class RevisionInfo
    {
        /// <summary>
        /// Номер правки из файла (w:id). Номера в .docx должны быть разными у всех
        /// правок документа; выгрузка выдаёт свои, и этот номер нужен только чтобы
        /// узнать «ту же самую» правку, разрезанную по ранам.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int Id { get; set; }

        /// <summary>Автор правки (w:author).</summary>
        public string Author { get; set; } = string.Empty;

        /// <summary>Время правки (w:date). Null — время не записано.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTime? Date { get; set; }

        /// <summary>
        /// Правка — часть перемещения (w:moveFrom, w:moveTo), а не простая вставка или
        /// удаление.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsMove { get; set; }

        /// <summary>
        /// Имя перемещения (w:name у w:moveFromRangeStart / w:moveToRangeStart): по нему
        /// источник находит свой приёмник.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? MoveName { get; set; }

        /// <summary>Копия отметки.</summary>
        public RevisionInfo Clone() => (RevisionInfo)MemberwiseClone();

        /// <summary>
        /// Та же правка: тот же автор, то же время и тот же вид. Соседние знаки с такими
        /// отметками сливаются в один ран и уходят в файл одной правкой.
        /// </summary>
        public bool SameAs(RevisionInfo? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;

            return string.Equals(Author, other.Author, StringComparison.Ordinal)
                && Date == other.Date
                && IsMove == other.IsMove
                && string.Equals(MoveName, other.MoveName, StringComparison.Ordinal)
                && Id == other.Id;
        }

        /// <summary>Отметки равны, в том числе обе отсутствуют.</summary>
        public static bool Same(RevisionInfo? a, RevisionInfo? b)
            => a is null ? b is null : a.SameAs(b);
    }

    /// <summary>
    /// Смена оформления фрагмента под рецензированием (w:rPrChange): какое оформление
    /// было до правки. Отклонить правку — вернуть его, принять — забыть.
    /// </summary>
    public sealed class RunFormatChange
    {
        /// <summary>Кто и когда сменил оформление.</summary>
        public RevisionInfo Info { get; set; } = new();

        /// <summary>
        /// Оформление до правки. Null — до правки у фрагмента не было своего
        /// оформления.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public RunProperties? Previous { get; set; }

        /// <summary>Глубокая копия.</summary>
        public RunFormatChange Clone() => new()
        {
            Info = Info.Clone(),
            Previous = Previous?.WithoutRevisions()
        };

        /// <summary>Та же смена оформления.</summary>
        public static bool Same(RunFormatChange? a, RunFormatChange? b)
        {
            if (a is null || b is null) return a is null && b is null;
            if (ReferenceEquals(a, b)) return true;
            if (!a.Info.SameAs(b.Info)) return false;

            if (a.Previous is null || b.Previous is null)
                return a.Previous is null && b.Previous is null;

            return RunProperties.SameFormatting(a.Previous, b.Previous);
        }
    }
}
