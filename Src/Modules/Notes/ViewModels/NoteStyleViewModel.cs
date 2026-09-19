using Avalonia.Media;
using Writersword.Modules.Notes.Models;

namespace Writersword.Modules.Notes.ViewModels
{
    /// <summary>
    /// Карточка вида строки в галерее стилей ленты.
    ///
    /// Галерея показывает не название команды, а образец начертания — так же,
    /// как «Стили» в ленте редактора. Название набрано тем самым начертанием,
    /// которое применится: заголовок крупным, цитата курсивом, код
    /// моноширинным.
    /// </summary>
    public sealed class NoteStyleViewModel
    {
        public NoteStyleViewModel(
            NoteBlockType type,
            string name,
            double previewSize,
            FontWeight previewWeight,
            FontStyle previewStyle,
            FontFamily? previewFont = null)
        {
            Type = type;
            Name = name;
            PreviewSize = previewSize;
            PreviewWeight = previewWeight;
            PreviewStyle = previewStyle;
            PreviewFont = previewFont ?? FontFamily.Default;
        }

        /// <summary>Вид строки, который ставит эта карточка.</summary>
        public NoteBlockType Type { get; }

        /// <summary>Название вида — оно же образец.</summary>
        public string Name { get; }

        /// <summary>Кегль образца.</summary>
        public double PreviewSize { get; }

        /// <summary>Насыщенность образца.</summary>
        public FontWeight PreviewWeight { get; }

        /// <summary>Наклон образца.</summary>
        public FontStyle PreviewStyle { get; }

        /// <summary>Шрифт образца — у кода он моноширинный.</summary>
        public FontFamily PreviewFont { get; }
    }
}
