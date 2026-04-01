using System;
using System.Collections.Generic;

namespace Writersword.Modules.Characters.Models
{
    public class CharacterStatus
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string Color { get; set; } = "#607D8B";
        public string Description { get; set; } = string.Empty;
    }

    public class CharacterContext
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string AccentColor { get; set; } = "#607D8B";
        public string Notes { get; set; } = string.Empty;
    }

    public class CharacterNote
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Title { get; set; } = "Заметка";
        public string Content { get; set; } = string.Empty;
        public string AccentColor { get; set; } = "#607D8B";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class CharacterPersonalEvent
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Title { get; set; } = string.Empty;
        public string Timestamp { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool IsKeyEvent { get; set; } = false;
        public string AccentColor { get; set; } = "#607D8B";
    }

    public class CharacterItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    public class CharacterAnketa
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool IsBuiltIn { get; set; } = false;
        public List<string> ProjectTypeTags { get; set; } = new();
        public List<CharacterAnketaField> Fields { get; set; } = new();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
