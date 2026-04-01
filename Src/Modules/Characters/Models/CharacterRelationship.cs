using System;
using System.Collections.Generic;
using Writersword.Modules.Characters.Models.Enums;

namespace Writersword.Modules.Characters.Models
{
    public class CharacterRelationship
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string SourceCharacterId { get; set; } = string.Empty;
        public string TargetCharacterId { get; set; } = string.Empty;
        public string RelationshipType { get; set; } = string.Empty;
        public CharacterRelationshipContext Context { get; set; } = CharacterRelationshipContext.Public;
        public CharacterRelationshipEmotion Emotion { get; set; } = CharacterRelationshipEmotion.Neutral;

        /// <summary>Сила связи от 0 до 1</summary>
        public double Strength { get; set; } = 0.5;
        public bool IsBidirectional { get; set; } = true;
        public string Note { get; set; } = string.Empty;

        /// <summary>
        /// Как источник называет цель в этой связи.
        /// Например: Жак называет Короля "Балбес", дочь называет отца "Папуля".
        /// </summary>
        public List<string> SourceCallsTargetAs { get; set; } = new();
    }
}
