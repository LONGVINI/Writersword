using System.Text.Json.Serialization;
using Writersword.Modules.TextEditor.Models.Inline;

namespace Writersword.Modules.TextEditor.Models.Document
{
    /// <summary>
    /// Объект, у которого правкой рецензирования меняется вид: картинка и фигура.
    /// Сдвиг, размер, поворот, обрезка, обтекание, рамка — всё, что меняет, как
    /// объект стоит на листе, — запоминается сменой вида с прежним видом и
    /// принимается или отклоняется, как смена оформления текста.
    /// </summary>
    public interface IAppearanceTracked : IRevisionMarked
    {
        /// <summary>Смена вида объекта правкой; null — вид не менялся.</summary>
        ObjectChange? Change { get; set; }
    }

    /// <summary>Смена вида объекта: кто и когда, и каким объект был до неё.</summary>
    public sealed class ObjectChange
    {
        /// <summary>Кто и когда сменил вид.</summary>
        [JsonPropertyName("info")]
        public RevisionInfo Info { get; set; } = new();

        /// <summary>
        /// Вид объекта до правки: свойства, которые меняет правка вида, записью
        /// «имя — значение» (Services.ObjectAppearance).
        /// </summary>
        [JsonPropertyName("previous")]
        public string Previous { get; set; } = "{}";

        public ObjectChange Clone() => new() { Info = Info.Clone(), Previous = Previous };
    }
}
