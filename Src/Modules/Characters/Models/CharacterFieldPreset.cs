using System;

namespace Writersword.Modules.Characters.Models
{
    /// <summary>
    /// Заготовка поля анкеты: поле со всеми настройками под частый вопрос —
    /// «Возраст» с единицами и этапами, «Пол» с вариантами, «Цвет глаз» с
    /// палитрой. Новое поле из заготовки встаёт на лист уже настроенным, его
    /// остаётся только поправить.
    ///
    /// Встроенные заготовки собираются из кода (CharacterFieldPresets), свои
    /// лежат в проекте: их сохраняют из полей анкет.
    /// </summary>
    public class CharacterFieldPreset
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>Название в списке заготовок; оно же — название нового поля.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Коротко, что в заготовке: «лет, этапы жизни», «4 варианта».</summary>
        public string Hint { get; set; } = string.Empty;

        /// <summary>Встроенная заготовка: её нельзя удалить, в проект она не пишется.</summary>
        public bool IsBuiltIn { get; set; }

        /// <summary>
        /// Поле, из которого строится новое. Ключ, идентификатор, группа и
        /// уровень у него пустые: их получает новое поле на месте.
        /// </summary>
        public CharacterAnketaField Field { get; set; } = new();
    }
}
