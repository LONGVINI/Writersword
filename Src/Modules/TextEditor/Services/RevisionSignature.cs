using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Models.Styles;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Отпечаток документа, каким он станет, если отклонить все правки.
    ///
    /// На нём держится защита записи исправлений. Правка, записанная исправлением,
    /// этот вид не меняет: вставленное при отклонении уходит, удалённое возвращается,
    /// сменённое оформление становится прежним. Значит, если действие изменило
    /// отпечаток, оно изменило документ мимо записи, и под защитой такое действие
    /// откатывается. Так защита не зависит от того, умеет ли каждое отдельное
    /// действие редактора записываться правкой: что не записалось — не прошло.
    ///
    /// В отпечаток входит всё, что автор должен получить обратно, отклонив правки:
    /// текст и его оформление, оформление абзацев и принадлежность к спискам,
    /// таблицы с их составом и оформлением, картинки и фигуры с их местом и видом,
    /// разрывы, сноски, колонтитулы. Не входят примечания (под защитой их оставлять
    /// можно, как в Word) и абзацы оглавления — их пересобирает сама программа.
    /// </summary>
    public static class RevisionSignature
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

        /// <summary>Отпечаток вида «все правки отклонены».</summary>
        public static string Compute(DocumentModel document)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Describe(document))));

        /// <summary>
        /// Сам вид «все правки отклонены» строкой — то, из чего считается отпечаток.
        /// Для журнала и проверки: по нему видно, чем два состояния различаются.
        /// </summary>
        internal static string Describe(DocumentModel document)
        {
            var writer = new Writer(document);
            writer.WriteDocument();
            return writer.ToString();
        }

        private sealed class Writer
        {
            private readonly DocumentModel _document;
            private readonly StringBuilder _sb = new(1 << 16);
            private readonly Dictionary<Guid, BlockModel> _inlineObjects = new();
            private readonly Dictionary<RunProperties, string> _runKeys = new(ReferenceEqualityComparer.Instance);

            public Writer(DocumentModel document)
            {
                _document = document;
                foreach (var section in document.Sections)
                    foreach (var block in section.InlineObjects)
                        _inlineObjects[block.Id] = block;
            }

            public override string ToString() => _sb.ToString();

            public void WriteDocument()
            {
                foreach (var section in _document.Sections)
                {
                    _sb.Append("\u0010S");

                    foreach (var block in section.Blocks)
                        WriteBlock(block);

                    // Плавающие объекты — по составу и виду: вставленный правкой при
                    // отклонении уходит, у остальных вид — прежний, если сменён правкой.
                    _sb.Append("\u0010F");
                    foreach (var block in section.FloatingObjects)
                        WriteFloating(block);

                    _sb.Append("\u0010H");
                    WriteParagraphs(section.Header.Paragraphs);
                    _sb.Append("\u0010f");
                    WriteParagraphs(section.Footer.Paragraphs);
                }

                if (_document.HeaderFooter is { } headerFooter)
                    _sb.Append("\u0010C").Append(JsonSerializer.Serialize(headerFooter, JsonOptions));

                // Параметры последнего раздела: лист, поля, колонки — прежние, если
                // сменены правкой.
                _sb.Append("\u0010P").Append(SectionFrame.RejectViewFinal(_document));
            }

            private void WriteBlock(BlockModel block)
            {
                switch (block)
                {
                    case ParagraphBlock paragraph:
                        WriteParagraph(paragraph);
                        break;

                    case TableBlock table:
                        WriteTable(table);
                        break;

                    case ImageBlock image:
                        if (image.Inserted is null)
                            _sb.Append("\u0010I").Append(image.Id.ToString("N")).Append(ObjectAppearance.RejectView(image));
                        break;

                    case BreakBlock breakBlock:
                        if (!IsInserted(breakBlock))
                        {
                            _sb.Append("\u0010B").Append((int)breakBlock.BreakType).Append(breakBlock.InParagraph ? 'p' : 'b');

                            // Параметры раздела — прежние, если сменены правкой.
                            if (breakBlock.IsSectionBreak)
                                _sb.Append(SectionFrame.RejectView(breakBlock, _document));
                        }
                        break;

                    default:
                        if (!IsInserted(block))
                            _sb.Append("\u0010O").Append((int)block.BlockType).Append(block.Id.ToString("N"))
                               .Append(ObjectAppearance.RejectView(block));
                        break;
                }
            }

            private void WriteFloating(BlockModel block)
            {
                if (IsInserted(block)) return;
                _sb.Append("\u0011").Append((int)block.BlockType).Append(block.Id.ToString("N"))
                   .Append(ObjectAppearance.RejectView(block));

                // Надпись — текст на листе: правка в ней тоже должна быть правкой.
                if (block is FloatingTextBlock textBox)
                    WriteParagraphs(textBox.Paragraphs);
            }

            private void WriteTable(TableBlock table)
            {
                // Таблица с правками строк, столбцов или оформления — как после их
                // отклонения: вставленные строки уходят, удалённые остаются, оформление
                // прежнее. Отклоняется копия: сам документ не трогается.
                if (TableRevisions.HasMarks(table))
                {
                    var rejected = (TableBlock)DocumentCloner.CloneBlock(table);
                    TableRevisions.RejectStructure(rejected);
                    table = rejected;
                }

                var cells = new List<TableCell>(table.Cells);
                cells.Sort((a, b) => a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.Column.CompareTo(b.Column));

                // Состав и оформление: сетка, объединения, границы, заливка, ширины и
                // высоты — изменённые мимо записи, они тоже правка.
                _sb.Append("\u0010T").Append(table.RowCount).Append('x').Append(table.ColumnCount)
                   .Append(TableFrame.Capture(table));
                foreach (var cell in cells)
                {
                    _sb.Append("\u0012").Append(cell.Row).Append(',').Append(cell.Column)
                       .Append(',').Append(cell.RowSpan).Append(',').Append(cell.ColSpan).Append(';');
                    foreach (var paragraph in cell.Paragraphs)
                        WriteParagraph(paragraph);

                    if (cell.NestedTables is { Count: > 0 } nested)
                        foreach (var inner in nested)
                            WriteTable(inner.Table);

                    // Картинки и фигуры, плавающие в ячейке: убранная мимо записи —
                    // тоже правка.
                    if (cell.Floats is { Count: > 0 } floats)
                        foreach (var cellFloat in floats)
                            if (!IsInserted(cellFloat.Object))
                                _sb.Append("\u0013").Append((int)cellFloat.Object.BlockType).Append(cellFloat.Object.Id.ToString("N"))
                                   .Append(ObjectAppearance.RejectView(cellFloat.Object));
                }
                _sb.Append("\u0010t");
            }

            private void WriteParagraphs(List<ParagraphBlock> paragraphs)
            {
                foreach (var paragraph in paragraphs)
                    WriteParagraph(paragraph);
            }

            /// <summary>
            /// Абзац как после отклонения правок: вставленные знаки уходят, удалённые
            /// остаются, оформление — прежнее, если его сменили правкой. Вставленный
            /// знак абзаца при отклонении сливает абзац со следующим — граница не
            /// пишется.
            /// </summary>
            private void WriteParagraph(ParagraphBlock paragraph)
            {
                // Оглавление пересобирает программа — оно не правка человека.
                if (paragraph.Properties.TocOwnerId is not null) return;

                // Оформление пишется только там, где оно меняется: на сколько ранов
                // разбит одинаково оформленный текст, от правки к правке разное, и
                // документу это безразлично.
                string? lastKey = null;

                foreach (var chunk in paragraph.Chunks)
                {
                    foreach (var run in chunk.Runs)
                    {
                        var props = run.Properties;
                        if (props?.Inserted is not null) continue;

                        string key = RunKey(props);
                        if (!string.Equals(key, lastKey, StringComparison.Ordinal))
                        {
                            _sb.Append('\u0013').Append(key).Append('\u0013');
                            lastKey = key;
                        }

                        if (run.InlineImageId is Guid objectId)
                        {
                            WriteInlineObject(objectId);
                            continue;
                        }

                        _sb.Append(run.Text);
                    }
                }

                if (paragraph.Properties.MarkInserted is not null) return;

                var properties = paragraph.Properties.FormatChange?.Previous ?? paragraph.Properties;
                _sb.Append('\u0014').Append(ParagraphKey(properties));

                var list = paragraph.Properties.FormatChange is { ListChanged: true } listChange
                    ? listChange.PreviousList
                    : paragraph.ListProperties;

                if (list is not null)
                    _sb.Append('\u0015').Append(list.ListId.ToString("N")).Append(':').Append(list.Level)
                       .Append(':').Append((int)list.MarkerType);

                _sb.Append('\n');
            }

            private void WriteInlineObject(Guid objectId)
            {
                _sb.Append('\uFFFC').Append(objectId.ToString("N"));

                // Картинка и фигура в строке — с видом: размер и поворот видны на листе.
                if (_inlineObjects.TryGetValue(objectId, out var shaped) && shaped is IAppearanceTracked)
                    _sb.Append(ObjectAppearance.RejectView(shaped));

                // Текст сноски — часть рукописи: правка в нём тоже должна быть правкой.
                if (_inlineObjects.TryGetValue(objectId, out var block) && block is NoteBlock note)
                {
                    _sb.Append('\u0016');
                    WriteParagraphs(note.Paragraphs);
                    _sb.Append('\u0017');
                }
            }

            private string RunKey(RunProperties? props)
            {
                if (props is null) return string.Empty;
                if (_runKeys.TryGetValue(props, out var key)) return key;

                // Оформление, которое вернёт отклонение: прежнее при смене оформления.
                var look = props.FormatChange is { } change
                    ? change.Previous
                    : props;

                key = look is null || look.IsDefaultFormatting()
                    ? string.Empty
                    : FormattingKey(look);

                _runKeys[props] = key;
                return key;
            }

            private static string FormattingKey(RunProperties p)
            {
                var sb = new StringBuilder(96);
                sb.Append(p.StyleName).Append('|')
                  .Append(p.FontFamily).Append('|')
                  .Append(p.FontSize?.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                  .Append(p.IsBold switch { true => '1', false => '0', _ => '-' })
                  .Append(p.IsItalic switch { true => '1', false => '0', _ => '-' })
                  .Append((int)p.UnderlineStyle).Append(p.UnderlineColor).Append('|')
                  .Append(p.IsStrikethrough ? '1' : '0').Append(p.IsDoubleStrikethrough ? '1' : '0')
                  .Append(p.IsSuperscript ? '1' : '0').Append(p.IsSubscript ? '1' : '0')
                  .Append(p.IsAllCaps ? '1' : '0').Append(p.IsSmallCaps ? '1' : '0')
                  .Append(p.IsHidden ? '1' : '0').Append(p.IsOutline ? '1' : '0').Append(p.IsShadow ? '1' : '0')
                  .Append(p.IsEmboss ? '1' : '0').Append(p.IsImprint ? '1' : '0').Append((int)p.EmphasisMark).Append('|')
                  .Append(p.CharacterSpacing?.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                  .Append(p.CharacterScale).Append('|')
                  .Append(p.BaselineOffset?.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                  .Append(p.CharBorderColor).Append(p.CharBorderWidthPt).Append((int)p.CharBorderStyle).Append('|')
                  .Append(p.TextColor).Append('|').Append(p.HighlightColor).Append('|').Append(p.Language).Append('|');

                if (p.Effects is not null)
                    sb.Append(JsonSerializer.Serialize(p.Effects, JsonOptions));

                return sb.ToString();
            }

            /// <summary>
            /// Оформление абзаца строкой. Поля перечислены вручную, а не сериализацией:
            /// отпечаток считается на каждое действие под защитой, и разбор каждого
            /// абзаца сериализатором стоил бы заметной паузы на большой рукописи.
            /// Редкие сложные поля — табуляции и рамка — сериализуются, только когда есть.
            /// </summary>
            private static string ParagraphKey(ParagraphProperties p)
            {
                var sb = new StringBuilder(96);
                sb.Append(p.Alignment is { } alignment ? (int)alignment : -1).Append('|')
                  .Append(Num(p.FirstLineIndent)).Append('|').Append(Num(p.LeftIndent)).Append('|').Append(Num(p.RightIndent)).Append('|')
                  .Append(Num(p.SpaceBefore)).Append('|').Append(Num(p.SpaceAfter)).Append('|')
                  .Append(p.ContextualSpacing switch { true => '1', false => '0', _ => '-' })
                  .Append(p.LineSpacingRule is { } rule ? (int)rule : -1).Append('|').Append(Num(p.LineSpacingValue)).Append('|')
                  .Append((int)p.LineTextAlignment)
                  .Append(p.RightToLeft ? '1' : '0').Append(p.KeepTogether ? '1' : '0')
                  .Append(p.KeepWithNext ? '1' : '0').Append(p.PageBreakBefore ? '1' : '0').Append('|')
                  .Append(p.StyleName).Append('|').Append(p.OutlineLevel).Append('|')
                  .Append(p.IncludeInToc ? '1' : '0').Append('|')
                  .Append(p.ShadingColor).Append('|').Append(p.ShadingPattern).Append('|').Append(p.ShadingPatternColor).Append('|');

                if (p.TabStops is { Count: > 0 } tabs)
                    sb.Append(JsonSerializer.Serialize(tabs, JsonOptions));
                sb.Append('|');
                if (p.Borders is not null)
                    sb.Append(JsonSerializer.Serialize(p.Borders, JsonOptions));

                return sb.ToString();
            }

            private static string Num(double? value)
                => value?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "-";

            /// <summary>Блок вставлен правкой: при отклонении он уходит.</summary>
            private static bool IsInserted(BlockModel block) => block switch
            {
                ImageBlock image => image.Inserted is not null,
                _ => RevisionMarks.InsertedOf(block) is not null
            };
        }
    }
}
