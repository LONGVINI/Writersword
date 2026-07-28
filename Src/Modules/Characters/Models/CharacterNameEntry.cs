using System;
using System.Collections.Generic;
using System.Linq;

namespace Writersword.Modules.Characters.Models
{
    /// <summary>
    /// Одно имя персонажа. Карточка держит список таких записей: имя может
    /// меняться по ходу истории (Вадим — Диана — Камень), а псевдонимы
    /// («Ваше величество», «Балбес») живут в том же списке. Первая запись —
    /// отображаемая: под ней персонаж виден в списках и графе. Порядок задаёт
    /// автор, программа его не меняет и ни о чём не спрашивает.
    /// </summary>
    public class CharacterNameEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string Value { get; set; } = string.Empty;

        /// <summary>
        /// Необязательная пометка происхождения: «после перехода»,
        /// «реинкарнация», «так зовут в детстве». Ни на что не влияет
        /// механически — она для автора.
        /// </summary>
        public string Note { get; set; } = string.Empty;
    }

    /// <summary>
    /// Приведение списка имён к рабочему виду. Вызывается при загрузке проекта:
    /// старые сохранения списка не знают, у них есть только Name и Aliases.
    /// </summary>
    public static class CharacterNames
    {
        public static void Normalize(Character character)
        {
            character.Names ??= new List<CharacterNameEntry>();

            // Пустые записи в список не пускаем: они дали бы безымянный чип,
            // по которому нельзя ни найти, ни кликнуть.
            character.Names.RemoveAll(n => n == null || string.IsNullOrWhiteSpace(n.Value));

            if (character.Names.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(character.Name))
                    character.Names.Add(new CharacterNameEntry { Value = character.Name });

                foreach (var alias in character.Aliases ?? new List<string>())
                {
                    if (string.IsNullOrWhiteSpace(alias)) continue;
                    if (character.Names.Any(n => n.Value == alias)) continue;
                    character.Names.Add(new CharacterNameEntry { Value = alias });
                }
            }

            // Отображаемое имя — всегда первое в списке. Весь остальной код
            // (списки, граф, связи, превью) продолжает читать Name и о списке
            // не знает.
            if (character.Names.Count > 0)
                character.Name = character.Names[0].Value;
        }

        /// <summary>Все имена персонажа, включая отображаемое.</summary>
        public static IEnumerable<string> AllValues(Character character)
        {
            if (character.Names != null && character.Names.Count > 0)
                return character.Names.Select(n => n.Value);

            var single = new List<string>();
            if (!string.IsNullOrWhiteSpace(character.Name)) single.Add(character.Name);
            single.AddRange(character.Aliases ?? new List<string>());
            return single;
        }
    }
}
