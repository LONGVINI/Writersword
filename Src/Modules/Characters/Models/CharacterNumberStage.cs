namespace Writersword.Modules.Characters.Models
{
    /// <summary>
    /// Этап для свободного числа: «Подросток, 13–17». Этапы задаёт анкета,
    /// потому что у эльфа «молодой» — это сто лет, а у человека двадцать.
    /// Границы нужны для сравнения: этап сводится к середине своего диапазона.
    /// </summary>
    public class CharacterNumberStage
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>Нижняя граница этапа включительно.</summary>
        public double From { get; set; }

        /// <summary>Верхняя граница включительно; пусто у последнего, открытого этапа.</summary>
        public double? To { get; set; }

        public CharacterNumberStage Clone() => new() { Name = Name, From = From, To = To };

        /// <summary>
        /// Число, к которому этап сводится при сравнении: середина диапазона,
        /// а у открытого этапа — его начало.
        /// </summary>
        public double Midpoint => To.HasValue ? (From + To.Value) / 2.0 : From;
    }
}
