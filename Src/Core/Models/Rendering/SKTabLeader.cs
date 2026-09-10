namespace Writersword.Core.Models.Rendering
{
    /// <summary>
    /// Чем вёрстка заполняет пустое место прыжка табуляции.
    ///
    /// Живёт в слое вёрстки, а не в модели документа, потому что рисует его рендер и
    /// знать о свойствах абзаца ему не нужно: к моменту отрисовки строка уже разложена,
    /// и от заполнителя остаётся только вид линии.
    /// </summary>
    public enum SKTabLeader
    {
        /// <summary>Ничем — пустое место.</summary>
        None = 0,
        /// <summary>Точками.</summary>
        Dots = 1,
        /// <summary>Чёрточками.</summary>
        Dashes = 2,
        /// <summary>Сплошной линией.</summary>
        Line = 3
    }
}
