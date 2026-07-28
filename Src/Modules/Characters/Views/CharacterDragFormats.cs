using Avalonia.Input;

namespace Writersword.Modules.Characters.Views
{
    /// <summary>
    /// Форматы перетаскивания внутри модуля персонажей. Формат один и общий:
    /// источник (боковой список редактора) и приёмник (полотно связей) живут
    /// в разных вью, и дублировать объявление в каждой из них нельзя — они
    /// разъедутся при первой же правке.
    /// </summary>
    public static class CharacterDragFormats
    {
        /// <summary>
        /// Идентификатор перетаскиваемого персонажа. В идентификаторе формата
        /// допустимы только буквы, цифры, точка и дефис.
        /// </summary>
        public static readonly DataFormat<string> CharacterId =
            DataFormat.CreateStringApplicationFormat("writersword.character-id");

        /// <summary>
        /// Идентификатор перетаскиваемой метки — порядок меток задаётся
        /// перетаскиванием чипов в карточке.
        /// </summary>
        public static readonly DataFormat<string> LabelId =
            DataFormat.CreateStringApplicationFormat("writersword.character-label-id");

        /// <summary>
        /// Ссылка на картинку галереи — порядок картинок задаётся
        /// перетаскиванием плиток, как порядок карточек в списке.
        /// </summary>
        public static readonly DataFormat<string> GalleryImage =
            DataFormat.CreateStringApplicationFormat("writersword.character-gallery-image");
    }
}
