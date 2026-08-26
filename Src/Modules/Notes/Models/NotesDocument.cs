using System;
using System.Collections.Generic;

namespace Writersword.Modules.Notes.Models
{
    public enum NoteBlockType { Paragraph, Heading1, Heading2, Heading3, Bullet, Checklist, Quote, Divider }

    public sealed class NoteBlock
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public NoteBlockType Type { get; set; }
        public string Text { get; set; } = string.Empty;
        public bool IsChecked { get; set; }
        public bool IsHighlighted { get; set; }
        public bool IsStruckThrough { get; set; }
    }

    public sealed class NotePage
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Title { get; set; } = "Новая страница";
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
        public List<NoteBlock> Blocks { get; set; } = new();
    }

    public sealed class NotesData
    {
        public int FormatVersion { get; set; } = 1;
        public List<NotePage> Pages { get; set; } = new();
    }

    public sealed class NotesSessionData
    {
        public Guid? SelectedPageId { get; set; }
        public bool IsPagePanelOpen { get; set; } = true;
    }
}
