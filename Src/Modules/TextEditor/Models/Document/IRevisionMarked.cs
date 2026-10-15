namespace Writersword.Modules.TextEditor.Models.Document
{
    /// <summary>
    /// Блок, который правка рецензирования вставляет или удаляет целиком: плавающая
    /// картинка, плавающая фигура, разрыв. У текста правка живёт на знаках
    /// (RunProperties), у знака абзаца — в свойствах абзаца, у таких блоков — здесь.
    /// </summary>
    public interface IRevisionMarked
    {
        /// <summary>Блок вставлен правкой: при отклонении уходит.</summary>
        Inline.RevisionInfo? Inserted { get; set; }

        /// <summary>Блок удалён правкой: при принятии уходит, при отклонении остаётся.</summary>
        Inline.RevisionInfo? Deleted { get; set; }
    }
}
