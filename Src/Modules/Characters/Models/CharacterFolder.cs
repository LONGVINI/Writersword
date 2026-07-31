using System;
using System.Collections.Generic;
using Writersword.Modules.Characters.Models.Enums;

namespace Writersword.Modules.Characters.Models
{
    /// <summary>
    /// Папка (группа) для организации персонажей.
    /// Персонаж может быть в одной папке или без папки.
    /// </summary>
    public class CharacterFolder
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "Новая папка";

        /// <summary>
        /// Серая подпись справа от названия папки.
        /// </summary>
        public string Comment { get; set; } = string.Empty;

        public string Color { get; set; } = "#607D8B";
        public int Order { get; set; }

        /// <summary>Id персонажей в этой папке (упорядочены)</summary>
        public List<string> CharacterIds { get; set; } = new();

        /// <summary>
        /// Ступень важности, которую папка выдаёт попавшим в неё персонажам —
        /// при создании и при переносе. Папка «Главные герои» тем самым сама
        /// проставляет первую ступень, и её не приходится выбирать в каждой
        /// карточке заново.
        ///
        /// В интерфейсе ступень есть всегда. Пусто здесь значит только одно:
        /// папка сохранена версией, которая о ступенях не знала, — при загрузке
        /// такой папке ступень проставляется по её роли. Новые папки заводятся
        /// третьей ступенью, самой безобидной: повышают осознанно, а понижать
        /// десяток случайно созданных карточек пришлось бы руками.
        /// </summary>
        public CharacterImportanceLevel? ImportanceLevel { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}