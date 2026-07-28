using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Modules.Characters.Interfaces;
using Writersword.Modules.Characters.Models;

namespace Writersword.Modules.Characters.Services
{
    public class RelationshipService : IRelationshipService
    {
        private static readonly ILogger _logger = Log.ForContext<RelationshipService>();
        private readonly List<CharacterRelationship> _relationships = new();

        public IReadOnlyList<CharacterRelationship> GetAll() => _relationships.AsReadOnly();

        public IReadOnlyList<CharacterRelationship> GetAllForCharacter(string id) =>
            _relationships.Where(r => r.SourceCharacterId == id || r.TargetCharacterId == id).ToList().AsReadOnly();

        public IReadOnlyList<CharacterRelationship> GetOutgoing(string id) =>
            _relationships.Where(r => r.SourceCharacterId == id).ToList().AsReadOnly();

        public IReadOnlyList<CharacterRelationship> GetIncoming(string id) =>
            _relationships.Where(r => r.TargetCharacterId == id).ToList().AsReadOnly();

        public CharacterRelationship? GetBetween(string sourceId, string targetId) =>
            _relationships.FirstOrDefault(r =>
                (r.SourceCharacterId == sourceId && r.TargetCharacterId == targetId) ||
                (r.IsBidirectional && r.SourceCharacterId == targetId && r.TargetCharacterId == sourceId));

        public CharacterRelationship Create(string sourceId, string targetId)
        {
            var rel = new CharacterRelationship
            {
                Id = Guid.NewGuid().ToString(),
                SourceCharacterId = sourceId,
                TargetCharacterId = targetId
            };
            _relationships.Add(rel);
            _logger.Debug("Relationship created: {Id}", rel.Id);
            return rel;
        }

        public void Update(CharacterRelationship relationship)
        {
            var idx = _relationships.FindIndex(r => r.Id == relationship.Id);
            if (idx >= 0) _relationships[idx] = relationship;
        }

        public void Delete(string id)
        {
            _relationships.RemoveAll(r => r.Id == id);
            _logger.Debug("Relationship deleted: {Id}", id);
        }

        public void LoadRelationships(List<CharacterRelationship> relationships)
        {
            _relationships.Clear();
            if (relationships != null)
            {
                // Старые сохранения знают только список строк — формы обращения
                // с поводом собираются из него, чтобы связи любого возраста
                // работали одинаково.
                foreach (var relationship in relationships)
                    CharacterAddress.Normalize(relationship);

                _relationships.AddRange(relationships);
            }
            _logger.Debug("Relationships loaded: {Count}", _relationships.Count);
        }
    }
}
