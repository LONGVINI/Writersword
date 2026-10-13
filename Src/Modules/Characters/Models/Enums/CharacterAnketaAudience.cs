namespace Writersword.Modules.Characters.Models.Enums
{
    /// <summary>
    /// Для кого анкета. Выбор анкет в карточке показывает только подходящие:
    /// человеку — анкеты персонажа, группе — анкеты группы, а общие — всем.
    /// </summary>
    public enum CharacterAnketaAudience
    {
        /// <summary>Подходит и персонажу, и группе: «Фракция», «Цели».</summary>
        Any,

        /// <summary>Только для отдельного персонажа: «Внешность», «Характер».</summary>
        Individual,

        /// <summary>Только для группы, народа, организации: «Народ / Группа».</summary>
        Collective
    }
}
