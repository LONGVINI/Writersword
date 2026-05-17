namespace Writersword.Modules.Characters.Models
{
    public enum CharacterAvatarSource { Project, Library, BuiltIn }

    public class CharacterAvatarItem
    {
        public string AvatarRef { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public CharacterAvatarSource Source { get; set; }
        public string? PackId { get; set; }
    }
}