using System;

namespace Writersword.Modules.Characters.Models.Enums
{
    /// <summary>
    /// Способы, которыми автор анкеты разрешает задавать свободное число.
    /// Возраст не всегда известен точно: «около тридцати», «от двадцати пяти до
    /// тридцати пяти», «взрослый» — тоже ответы, и честнее принять их как есть,
    /// чем вынуждать выдумывать точную цифру.
    /// </summary>
    [Flags]
    public enum CharacterNumberModes
    {
        None = 0,

        /// <summary>Точное число.</summary>
        Exact = 1,

        /// <summary>Примерно: «около 30».</summary>
        Approx = 2,

        /// <summary>Диапазон: «от 25 до 35».</summary>
        Range = 4,

        /// <summary>Этап из списка анкеты: «взрослый».</summary>
        Stage = 8,

        /// <summary>Год рождения вместо возраста.</summary>
        BirthYear = 16
    }

    /// <summary>
    /// Способ, которым задано число у конкретного персонажа. Один из
    /// разрешённых анкетой.
    ///
    /// Новые члены дописываются только в конец.
    /// </summary>
    public enum CharacterNumberMode
    {
        Exact,
        Approx,
        Range,
        Stage,
        BirthYear
    }
}
