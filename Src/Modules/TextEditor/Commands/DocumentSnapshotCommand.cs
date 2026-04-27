using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Writersword.Core.Interfaces.Modules;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.ViewModels;

namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Снапшот полного состояния документа — до и после операции.
    /// Сериализует DocumentModel в JSON при создании (before) и при Commit (after).
    /// Покрывает текст, таблицы, форматирование и структурные изменения.
    /// </summary>
    public sealed class DocumentSnapshotCommand : IUndoableCommand
    {
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly DocumentViewModel _docVm;
        private readonly string _before;
        private string? _after;

        // Позиции каретки до и после операции.
        // Хранятся как индекс параграфа в Paragraphs + символьная позиция.
        private readonly int _caretParaBefore;
        private readonly int _caretCharBefore;
        private int _caretParaAfter;
        private int _caretCharAfter;

        // Callback для восстановления каретки после undo/redo.
        public Action<int, int>? RestoreCaretCallback { get; set; }

        public string Description { get; }

        public DocumentSnapshotCommand(DocumentViewModel docVm, string description,
            int caretPara, int caretChar)
        {
            _docVm = docVm;
            Description = description;
            _before = Serialize(docVm.Document);
            _caretParaBefore = caretPara;
            _caretCharBefore = caretChar;
        }

        public void Commit(int caretPara, int caretChar)
        {
            _after = Serialize(_docVm.Document);
            _caretParaAfter = caretPara;
            _caretCharAfter = caretChar;
        }

        public void Execute()
        {
            if (_after is not null)
            {
                Restore(_after);
                RestoreCaretCallback?.Invoke(_caretParaAfter, _caretCharAfter);
            }
        }

        public void Undo()
        {
            Restore(_before);
            RestoreCaretCallback?.Invoke(_caretParaBefore, _caretCharBefore);
        }

        private void Restore(string json)
        {
            var restored = JsonSerializer.Deserialize<DocumentModel>(json, _jsonOptions);
            if (restored is null) return;

            var doc = _docVm.Document;

            doc.Sections.Clear();
            foreach (var section in restored.Sections)
                doc.Sections.Add(section);

            doc.Styles.Clear();
            foreach (var style in restored.Styles)
                doc.Styles.Add(style);

            doc.PageSettings.MarginTopMm = restored.PageSettings.MarginTopMm;
            doc.PageSettings.MarginBottomMm = restored.PageSettings.MarginBottomMm;
            doc.PageSettings.MarginLeftMm = restored.PageSettings.MarginLeftMm;
            doc.PageSettings.MarginRightMm = restored.PageSettings.MarginRightMm;

            _docVm.RebuildParagraphViewModelsPublic();
            _docVm.FireParagraphFormatChanged();
        }

        private static string Serialize(DocumentModel doc)
            => JsonSerializer.Serialize(doc, _jsonOptions);
    }
}