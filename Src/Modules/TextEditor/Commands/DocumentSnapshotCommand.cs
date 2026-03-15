using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Core.Interfaces.Modules;
using Writersword.Modules.TextEditor.ViewModels;

namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Снапшот текста всех параграфов — до и после операции.
    /// Используется для Undo/Redo любого изменения текста.
    /// </summary>
    public sealed class DocumentSnapshotCommand : IUndoableCommand
    {
        private readonly DocumentViewModel _docVm;
        private readonly List<string> _before;
        private List<string> _after = new();

        public string Description { get; }

        public DocumentSnapshotCommand(DocumentViewModel docVm, string description)
        {
            _docVm = docVm;
            Description = description;
            _before = docVm.Paragraphs.Select(p => p.PlainText ?? "").ToList();
        }

        public void Commit()
        {
            _after = _docVm.Paragraphs.Select(p => p.PlainText ?? "").ToList();
        }

        public void Execute() => Restore(_after);

        public void Undo() => Restore(_before);

        private void Restore(List<string> snapshot)
        {
            // Подгоняем количество параграфов
            while (_docVm.Paragraphs.Count > snapshot.Count && _docVm.Paragraphs.Count > 1)
                _docVm.DeleteParagraph(_docVm.Paragraphs[^1]);

            while (_docVm.Paragraphs.Count < snapshot.Count)
                _docVm.AddParagraphAfter(_docVm.Paragraphs[^1]);

            for (int i = 0; i < snapshot.Count; i++)
                _docVm.Paragraphs[i].PlainText = snapshot[i];
        }
    }
}