using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Modules.Characters.Interfaces;
using Writersword.Modules.Characters.Models;

namespace Writersword.Modules.Characters.Services
{
    public class CharacterService : ICharacterService
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterService>();

        private readonly IRelationshipService _relationshipService;
        private readonly ICharacterAnketaService _anketaService;
        private readonly List<Character> _characters = new();
        private object? _context;

        private const string AvatarFolder = "Characters/avatars";

        public CharacterService(IRelationshipService relationshipService, ICharacterAnketaService anketaService)
        {
            _relationshipService = relationshipService;
            _anketaService = anketaService;
        }

        public void SetContext(object? context) => _context = context;

        public IReadOnlyList<Character> GetAll() => _characters.AsReadOnly();

        public IReadOnlyList<Character> Search(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return GetAll();
            var q = query.ToLowerInvariant();
            return _characters
                .Where(c => c.Name.ToLowerInvariant().Contains(q) ||
                            c.ShortDescription.ToLowerInvariant().Contains(q) ||
                            c.Tags.Any(t => t.ToLowerInvariant().Contains(q)))
                .ToList().AsReadOnly();
        }

        public IReadOnlyList<Character> GetByTags(IEnumerable<string> tags)
        {
            var tagList = tags.ToList();
            if (!tagList.Any()) return GetAll();
            return _characters.Where(c => c.Tags.Any(t => tagList.Contains(t))).ToList().AsReadOnly();
        }

        public Character? GetById(string id) => _characters.FirstOrDefault(c => c.Id == id);

        public IReadOnlyList<string> GetAllTags() =>
            _characters.SelectMany(c => c.Tags).Distinct().OrderBy(t => t).ToList().AsReadOnly();

        public Character Create(string name)
        {
            var c = new Character { Id = Guid.NewGuid().ToString(), Name = name };
            _characters.Add(c);
            _logger.Debug("Character created: {Id} '{Name}'", c.Id, c.Name);
            return c;
        }

        public Character CreateFromAnketas(string name, IEnumerable<CharacterAnketa> anketas, bool randomize = false)
        {
            var c = new Character { Id = Guid.NewGuid().ToString(), Name = name };
            var list = anketas.ToList();
            c.Parameters = randomize
                ? _anketaService.MergeParametersRandomized(list)
                : _anketaService.MergeParameters(list);
            _characters.Add(c);
            _logger.Debug("Character created from anketas: {Id}, params={Count}, rand={R}", c.Id, c.Parameters.Count, randomize);
            return c;
        }

        public Character CreateCollective(string name, IEnumerable<CharacterAnketa>? anketas = null)
        {
            var c = new Character { Id = Guid.NewGuid().ToString(), Name = name, IsCollective = true };
            var list = anketas?.ToList() ?? new List<CharacterAnketa>();
            if (list.Any()) c.Parameters = _anketaService.MergeParameters(list);
            _characters.Add(c);
            _logger.Debug("Collective character created: {Id}", c.Id);
            return c;
        }

        public void Update(Character character)
        {
            var idx = _characters.FindIndex(c => c.Id == character.Id);
            if (idx >= 0)
            {
                character.UpdatedAt = DateTime.UtcNow;
                _characters[idx] = character;
            }
        }

        public void Delete(string id)
        {
            _characters.RemoveAll(c => c.Id == id);
            _logger.Debug("Character deleted: {Id}", id);
        }

        public Character Duplicate(string id)
        {
            var original = GetById(id);
            if (original == null) throw new InvalidOperationException($"Character not found: {id}");

            var copy = new Character
            {
                Id = Guid.NewGuid().ToString(),
                Name = $"{original.Name} (копия)",
                ShortDescription = original.ShortDescription,
                Color = original.Color,
                FallbackIcon = original.FallbackIcon,
                ImportanceLevel = original.ImportanceLevel,
                Tags = new List<string>(original.Tags),
                Parameters = original.Parameters.Select(p => new CharacterParameter
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = p.Name, Type = p.Type, GroupName = p.GroupName,
                    Description = p.Description, MinValue = p.MinValue, MaxValue = p.MaxValue,
                    NumericValue = p.NumericValue, Step = p.Step,
                    MinDescription = p.MinDescription, MaxDescription = p.MaxDescription,
                    ScalePoints = new System.Collections.Generic.Dictionary<double, string>(p.ScalePoints),
                    States = new List<string>(p.States), CurrentStateIndex = p.CurrentStateIndex,
                    TextValue = p.TextValue, BoolValue = p.BoolValue,
                    TrueLabel = p.TrueLabel, FalseLabel = p.FalseLabel,
                    Order = p.Order
                }).ToList(),
                IsCollective = original.IsCollective,
                PopulationNote = original.PopulationNote
            };

            _characters.Add(copy);
            _logger.Debug("Character duplicated: {OrigId} -> {NewId}", id, copy.Id);
            return copy;
        }

        public void ApplyAnketa(string characterId, CharacterAnketa anketa, bool randomize = false)
        {
            var character = GetById(characterId);
            if (character == null) return;

            var newParams = randomize
                ? _anketaService.BuildParametersRandomized(anketa)
                : _anketaService.BuildParameters(anketa);

            var existingNames = character.Parameters.Select(p => p.Name).ToHashSet();
            foreach (var p in newParams)
            {
                if (!existingNames.Contains(p.Name))
                    character.Parameters.Add(p);
            }
            character.UpdatedAt = DateTime.UtcNow;
        }

        public void RandomizeParameters(string characterId)
        {
            var character = GetById(characterId);
            if (character == null) return;
            _anketaService.RandomizeParameters(character.Parameters);
            character.UpdatedAt = DateTime.UtcNow;
        }

        public string? GetAvatarPath(string characterId) => GetById(characterId)?.AvatarPath;

        public void SetAvatar(string characterId, byte[] data, string extension)
        {
            var character = GetById(characterId);
            if (character == null) return;
            character.AvatarPath = $"{AvatarFolder}/{characterId}/avatar{extension}";
            character.UpdatedAt = DateTime.UtcNow;
        }

        public void DeleteAvatar(string characterId)
        {
            var character = GetById(characterId);
            if (character == null) return;
            character.AvatarPath = null;
            character.UpdatedAt = DateTime.UtcNow;
        }

        public CharactersModuleData GetModuleData() => new()
        {
            Characters = _characters.ToList(),
            Relationships = _relationshipService.GetAll().ToList()
        };

        public void LoadModuleData(CharactersModuleData data)
        {
            _characters.Clear();
            if (data.Characters != null) _characters.AddRange(data.Characters);
            _logger.Debug("Module data loaded: {Count} characters", _characters.Count);
        }
    }
}
