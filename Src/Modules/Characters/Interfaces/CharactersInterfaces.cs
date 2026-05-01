using System.Collections.Generic;
using Writersword.Modules.Characters.Models;

namespace Writersword.Modules.Characters.Interfaces
{
    public interface ICharacterService
    {
        IReadOnlyList<Character> GetAll();
        IReadOnlyList<Character> Search(string query);
        IReadOnlyList<Character> GetByTags(IEnumerable<string> tags);
        Character? GetById(string id);
        IReadOnlyList<string> GetAllTags();

        Character Create(string name);
        Character CreateFromAnketas(string name, IEnumerable<CharacterAnketa> anketas, bool randomize = false);
        Character CreateCollective(string name, IEnumerable<CharacterAnketa>? anketas = null);
        void Update(Character character);
        void Delete(string id);
        Character Duplicate(string id);

        void ApplyAnketa(string characterId, CharacterAnketa anketa, bool randomize = false);
        void RandomizeParameters(string characterId);

        string? GetAvatarPath(string characterId);
        void SetAvatar(string characterId, byte[] data, string extension);
        void DeleteAvatar(string characterId);

        CharactersModuleData GetModuleData();
        void LoadModuleData(CharactersModuleData data);
        void SetContext(object? context);
        Character CreateWithId(Character character);
    }

    public interface IRelationshipService
    {
        IReadOnlyList<CharacterRelationship> GetAll();
        IReadOnlyList<CharacterRelationship> GetAllForCharacter(string characterId);
        IReadOnlyList<CharacterRelationship> GetOutgoing(string characterId);
        IReadOnlyList<CharacterRelationship> GetIncoming(string characterId);
        CharacterRelationship? GetBetween(string sourceId, string targetId);

        CharacterRelationship Create(string sourceId, string targetId);
        void Update(CharacterRelationship relationship);
        void Delete(string id);

        void LoadRelationships(List<CharacterRelationship> relationships);
    }

    public interface ICharacterAnketaService
    {
        IReadOnlyList<CharacterAnketa> GetAll();
        IReadOnlyList<CharacterAnketa> GetBuiltIn();
        IReadOnlyList<CharacterAnketa> GetCustom();
        IReadOnlyList<CharacterAnketa> GetRecommended(IEnumerable<string> tags);
        CharacterAnketa? GetById(string id);

        CharacterAnketa Create(string name);
        void Update(CharacterAnketa anketa);
        void Delete(string id);
        CharacterAnketa Duplicate(string id);

        List<CharacterParameter> BuildParameters(CharacterAnketa anketa);
        List<CharacterParameter> BuildParametersRandomized(CharacterAnketa anketa);
        List<CharacterParameter> MergeParameters(IEnumerable<CharacterAnketa> anketas);
        List<CharacterParameter> MergeParametersRandomized(IEnumerable<CharacterAnketa> anketas);
        void RandomizeParameters(List<CharacterParameter> parameters);

        void LoadCustomAnketas(List<CharacterAnketa> anketas);
    }
}
