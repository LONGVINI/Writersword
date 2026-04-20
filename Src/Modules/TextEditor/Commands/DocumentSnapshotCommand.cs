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

        public string Description { get; }

        public DocumentSnapshotCommand(DocumentViewModel docVm, string description)
        {
            _docVm = docVm;
            Description = description;
            _before = Serialize(docVm.Document);
        }

        /// <summary>
        /// Фиксирует состояние ПОСЛЕ операции.
        /// Вызывать сразу по завершении изменений, до следующего rebuild.
        /// </summary>
        public void Commit()
        {
            _after = Serialize(_docVm.Document);
        }

        public void Execute()
        {
            if (_after is not null)
                Restore(_after);
        }

        public void Undo() => Restore(_before);

        private void Restore(string json)
        {
            var restored = JsonSerializer.Deserialize<DocumentModel>(json, _jsonOptions);
            if (restored is null) return;

            var doc = _docVm.Document;

            // Восстанавливаем содержимое — заменяем разделы и настройки на месте,
            // не создавая новый объект DocumentModel (ссылки во ViewModels остаются живыми).
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

            // Перестраиваем VM-список параграфов под новую структуру блоков.
            _docVm.RebuildParagraphViewModelsPublic();
            _docVm.FireParagraphFormatChanged();
        }

        private static string Serialize(DocumentModel doc)
            => JsonSerializer.Serialize(doc, _jsonOptions);
    }
}