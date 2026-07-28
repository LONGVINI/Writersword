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
        ///
        /// Оставлено для совместимости: источником истины стали формы ниже,
        /// а этот список всегда повторяет их значения.
        /// </summary>
        public List<string> SourceCallsTargetAs { get; set; } = new();

        /// <summary>
        /// Обращения с поводом: «Алинусик» нежно, «Хрюшка» в шутку, «Алина»
        /// когда злится. Один источник зовёт цель по-разному, и это не конфликт
        /// данных, а регистры отношений — в них живёт изрядная часть сюжета.
        ///
        /// Аддитивно: у старых сохранений собирается из SourceCallsTargetAs
        /// при загрузке (CharacterAddress.Normalize).
        /// </summary>
        public List<CharacterAddressForm> SourceCallsTargetForms { get; set; } = new();
    }
}
