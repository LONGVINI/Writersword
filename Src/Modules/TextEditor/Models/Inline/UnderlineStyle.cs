namespace Writersword.Modules.TextEditor.Models.Inline
{
    /// <summary>
    /// Вид подчёркивания фрагмента — тот же набор, что у Word (w:u в OOXML).
    ///
    /// Числа закреплены: ими же вид передаётся отрисовке (SKRunSegment.UnderlineStyle)
    /// и хранится в снимках отмены, поэтому новые виды добавляются только в конец.
    /// </summary>
    public enum UnderlineStyle
    {
        /// <summary>Подчёркивания нет.</summary>
        None = 0,

        /// <summary>Одинарная сплошная линия.</summary>
        Single = 1,

        /// <summary>Только слова: пробелы между словами не подчёркиваются.</summary>
        Words = 2,

        /// <summary>Двойная линия.</summary>
        Double = 3,

        /// <summary>Жирная сплошная линия.</summary>
        Thick = 4,

        /// <summary>Точки.</summary>
        Dotted = 5,

        /// <summary>Жирные точки.</summary>
        DottedHeavy = 6,

        /// <summary>Штрих.</summary>
        Dash = 7,

        /// <summary>Жирный штрих.</summary>
        DashedHeavy = 8,

        /// <summary>Длинный штрих.</summary>
        DashLong = 9,

        /// <summary>Жирный длинный штрих.</summary>
        DashLongHeavy = 10,

        /// <summary>Штрих-пунктир: штрих, точка.</summary>
        DotDash = 11,

        /// <summary>Жирный штрих-пунктир.</summary>
        DashDotHeavy = 12,

        /// <summary>Штрих и две точки.</summary>
        DotDotDash = 13,

        /// <summary>Жирный штрих и две точки.</summary>
        DashDotDotHeavy = 14,

        /// <summary>Волна.</summary>
        Wave = 15,

        /// <summary>Жирная волна.</summary>
        WavyHeavy = 16,

        /// <summary>Двойная волна.</summary>
        WavyDouble = 17
    }
}
