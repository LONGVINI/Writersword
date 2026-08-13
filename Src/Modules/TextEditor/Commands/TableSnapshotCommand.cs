using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Снимок одной таблицы — до и после операции.
    /// <para>
    /// Структурные правки таблиц раньше шли через DocumentSnapshotCommand, то есть
    /// каждая из них сериализовала документ целиком, дважды, и держала обе копии в
    /// стеке. На документе в сотню тысяч слов это заметная пауза на каждое действие
    /// и мегабайты памяти на десяток шагов. Здесь сериализуется одна таблица, а всё
    /// остальное содержимое документа не трогается вовсе.
    /// </para>
    /// <para>
    /// Таблица ищется по идентификатору блока, а не по ссылке на объект: отмена
    /// снимка всего документа пересобирает модель из JSON и заменяет экземпляры,
    /// после чего сохранённая ссылка указывала бы на объект, которого в документе
    /// уже нет. Идентификатор при этом сохраняется.
    /// </para>
    /// <para>
    /// Восстановление копирует поля в существующий экземпляр, а не подменяет блок в
    /// разделе: на найденную таблицу ссылаются раскладка, активная ячейка и
    /// вью-модели, и подмена объекта оставила бы их висеть на старом.
    /// </para>
    /// </summary>
    public sealed class TableSnapshotCommand : ITextCommand
    {
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly Guid _tableId;
        private readonly string _before;
        private string? _after;

        public string Description { get; }

        public TableSnapshotCommand(TableBlock table, string description)
        {
            _tableId = table.Id;
            Description = description;
            _before = JsonSerializer.Serialize(table, _jsonOptions);
        }

        /// <summary>Зафиксировать состояние «после». Вызывается по завершении операции.</summary>
        public void Commit(TableBlock table)
        {
            _after = JsonSerializer.Serialize(table, _jsonOptions);
        }

        /// <summary>
        /// Изменилось ли хоть что-то. Операция могла закончиться ничем — например,
        /// ручку столбца щёлкнули без перетаскивания. Пустой шаг в историю не нужен.
        /// </summary>
        public bool HasChanges =>
            _after is not null && !string.Equals(_after, _before, StringComparison.Ordinal);

        public void Apply(DocumentModel doc)
        {
            if (_after is not null) Restore(doc, _after);
        }

        public void Revert(DocumentModel doc) => Restore(doc, _before);

        /// <summary>
        /// Слияние выключено: соседние правки таблицы — самостоятельные шаги, и
        /// пользователь ожидает откатывать их по одному.
        /// </summary>
        public bool TryMerge(ITextCommand next) => false;

        private void Restore(DocumentModel doc, string json)
        {
            var target = FindTable(doc);
            if (target is null) return;

            var source = JsonSerializer.Deserialize<TableBlock>(json, _jsonOptions);
            if (source is null) return;

            // Идентификатор намеренно не переносится: он и так совпадает, а перезапись
            // сделала бы команду неработоспособной после повторного применения.
            target.RowCount = source.RowCount;
            target.ColumnCount = source.ColumnCount;
            target.Columns = source.Columns;
            target.Cells = source.Cells;
            target.StyleName = source.StyleName;
            target.WidthPercent = source.WidthPercent;
            target.LeftIndentPt = source.LeftIndentPt;
            target.RepeatHeader = source.RepeatHeader;
            target.SplitMode = source.SplitMode;
            target.BreakLabel = source.BreakLabel;
            target.ContinuationLabel = source.ContinuationLabel;
        }

        private TableBlock? FindTable(DocumentModel doc)
        {
            foreach (var section in doc.Sections)
                foreach (var block in section.Blocks)
                    if (block is TableBlock table && table.Id == _tableId)
                        return table;
            return null;
        }
    }
}
