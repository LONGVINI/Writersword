using System.Collections.Generic;
using Writersword.Modules.Characters.Models;

namespace Writersword.Modules.Characters.ViewModels.Anketas
{
    /// <summary>
    /// Вариант выбора в особом виде анкеты и поля: значение для анкеты
    /// (CharacterLookAlign или null) и подпись для списка.
    /// </summary>
    public sealed class AnketaLookChoice
    {
        public AnketaLookChoice(string? value, string label)
        {
            Value = value;
            Label = label;
        }

        /// <summary>Значение; null — «как у анкеты» у поля и «сверху» у анкеты.</summary>
        public string? Value { get; }

        public string Label { get; }

        public override string ToString() => Label;

        /// <summary>Выравнивание поля в строке: своё или как у анкеты.</summary>
        public static IReadOnlyList<AnketaLookChoice> FieldAlignChoices { get; } = new[]
        {
            new AnketaLookChoice(null, "Как у анкеты"),
            new AnketaLookChoice(CharacterLookAlign.Top, "Сверху"),
            new AnketaLookChoice(CharacterLookAlign.Center, "По центру"),
            new AnketaLookChoice(CharacterLookAlign.Bottom, "Снизу")
        };

        /// <summary>Выравнивание полей в строках анкеты; без выбора — сверху.</summary>
        public static IReadOnlyList<AnketaLookChoice> AnketaAlignChoices { get; } = new[]
        {
            new AnketaLookChoice(null, "Сверху"),
            new AnketaLookChoice(CharacterLookAlign.Center, "По центру"),
            new AnketaLookChoice(CharacterLookAlign.Bottom, "Снизу")
        };
    }
}
