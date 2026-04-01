using System;
using System.Collections.Generic;

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

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}