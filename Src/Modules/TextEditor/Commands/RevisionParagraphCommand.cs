using System;
using System.Collections.Generic;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Styles;

namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Шаг отмены правки под рецензированием, не меняющей состава абзацев: набор
    /// текста, удаление знака, отметка знака абзаца удалённым.
    ///
    /// Удаление под рецензированием не убирает текст, а отмечает его, поэтому
    /// обычные шаги «вставка» и «удаление» его не описывают. Шаг хранит абзацы
    /// целиком — посимвольно, со всем оформлением и отметками правок, — и их
    /// свойства до и после правки. Снимок всей рукописи здесь не нужен: меняется
    /// один-два абзаца.
    ///
    /// Подряд набранные знаки сливаются в один шаг, как при обычном наборе: до
    /// пробела, чтобы Ctrl+Z откатывал по слову.
    /// </summary>
    public sealed class RevisionParagraphCommand : ITextCommand
    {
        /// <summary>Вид правки — для слияния подряд идущих шагов.</summary>
        public enum EditKind
        {
            Typing,
            Delete,
            Mark
        }

        /// <summary>Один абзац: чем он был и чем стал.</summary>
        public sealed class Entry
        {
            public Guid ParaId { get; init; }

            public List<ParagraphBlock.CharCell> Before { get; init; } = new();

            public List<ParagraphBlock.CharCell> After { get; set; } = new();

            public ParagraphProperties PropertiesBefore { get; init; } = new();

            public ParagraphProperties PropertiesAfter { get; set; } = new();
        }

        private readonly List<Entry> _entries;

        public RevisionParagraphCommand(List<Entry> entries, EditKind kind, string description)
        {
            _entries = entries;
            Kind = kind;
            Description = description;
        }

        public string Description { get; }

        public EditKind Kind { get; }

        /// <summary>Абзац и место каретки после правки.</summary>
        public Guid CaretParaAfter { get; set; }

        public int CaretCharAfter { get; set; }

        /// <summary>Абзац и место каретки до правки.</summary>
        public Guid CaretParaBefore { get; set; }

        public int CaretCharBefore { get; set; }

        /// <summary>Набранный этим шагом текст — по нему слияние решает, где кончилось слово.</summary>
        public string TypedText { get; set; } = string.Empty;

        /// <summary>
        /// Обновить вид после отката или повтора: абзацы с этими Id изменились,
        /// каретка — в указанный абзац и позицию. Назначает полотно.
        /// </summary>
        public Action<IReadOnlyList<Guid>, Guid, int>? RefreshCallback { get; set; }

        public void Apply(DocumentModel doc)
        {
            var ids = new List<Guid>(_entries.Count);
            foreach (var entry in _entries)
            {
                var paragraph = DocumentModelHelper.FindParagraph(doc, entry.ParaId);
                if (paragraph is null) continue;

                paragraph.RebuildFromCharCells(entry.After);
                paragraph.Properties.CopyFrom(entry.PropertiesAfter.Clone());
                ids.Add(entry.ParaId);
            }

            RefreshCallback?.Invoke(ids, CaretParaAfter, CaretCharAfter);
        }

        public void Revert(DocumentModel doc)
        {
            var ids = new List<Guid>(_entries.Count);
            foreach (var entry in _entries)
            {
                var paragraph = DocumentModelHelper.FindParagraph(doc, entry.ParaId);
                if (paragraph is null) continue;

                paragraph.RebuildFromCharCells(entry.Before);
                paragraph.Properties.CopyFrom(entry.PropertiesBefore.Clone());
                ids.Add(entry.ParaId);
            }

            RefreshCallback?.Invoke(ids, CaretParaBefore, CaretCharBefore);
        }

        /// <summary>
        /// Слияние подряд набранного текста одного абзаца: следующий шаг начинается
        /// там, где кончился этот, и этот не кончается пробелом или переносом.
        /// </summary>
        public bool TryMerge(ITextCommand next)
        {
            if (next is not RevisionParagraphCommand other) return false;
            if (Kind != EditKind.Typing || other.Kind != EditKind.Typing) return false;
            if (_entries.Count != 1 || other._entries.Count != 1) return false;
            if (_entries[0].ParaId != other._entries[0].ParaId) return false;
            if (other.CaretParaBefore != CaretParaAfter || other.CaretCharBefore != CaretCharAfter) return false;

            if (TypedText.EndsWith(' ') || TypedText.EndsWith('\n') || TypedText.EndsWith('\t'))
                return false;

            _entries[0].After = other._entries[0].After;
            _entries[0].PropertiesAfter = other._entries[0].PropertiesAfter;
            CaretParaAfter = other.CaretParaAfter;
            CaretCharAfter = other.CaretCharAfter;
            TypedText += other.TypedText;
            return true;
        }
    }
}
