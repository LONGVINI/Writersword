namespace Writersword.Modules.Characters.Models.Enums
{
    /// <summary>
    /// Правило шага свободного числа: какие значения в нём допустимы.
    /// Новые члены дописываются только в конец: перечисление хранится числом.
    /// </summary>
    public enum CharacterStepRule
    {
        /// <summary>Ровный шаг: от начала через одинаковое расстояние — 0, 5, 10, 15.</summary>
        Linear,

        /// <summary>Умножение: каждое следующее значение во столько-то раз больше — 1, 2, 4, 8.</summary>
        Multiply,

        /// <summary>Свой список допустимых значений — 1, 3, 7, 12.</summary>
        List
    }
}
