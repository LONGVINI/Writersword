using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Writersword.Modules.TextEditor.Models.Styles;

namespace Writersword.Modules.TextEditor.Models.Document
{
    /// <summary>
    /// Тип маркера списка.
    /// Значения 1–9 — маркированные (bullet), 10+ — нумерованные (счётные системы).
    /// </summary>
    public enum ListMarkerType
    {
        None = 0,
        Bullet = 1,
        Dash = 2,
        Arrow = 3,
        Custom = 4,
        Square = 5,
        Circle = 6,
        Decimal = 10,
        DecimalLeadingZero = 11,
        LowerAlpha = 12,
        UpperAlpha = 13,
        LowerRoman = 14,
        UpperRoman = 15,
        CustomSequence = 16
    }

    /// <summary>
    /// Свойства списка для параграфа.
    /// </summary>
    public sealed class ListProperties
    {
        /// <summary>Шаг отступа одного уровня по умолчанию (pt). ~0.63 см — как в Word.</summary>
        public const double DefaultLevelStepPt = 18.0;

        /// <summary>Выступ маркера по умолчанию (pt): расстояние от края текста до маркера.</summary>
        public const double DefaultHangingPt = 18.0;

        /// <summary>Минимальный зазор маркер→текст по умолчанию (pt).</summary>
        public const double DefaultMarkerTextGapPt = 6.0;

        /// <summary>Id списка (несколько параграфов с одним ListId образуют один список).</summary>
        public Guid ListId { get; set; }

        /// <summary>Уровень вложенности (0–8).</summary>
        public int Level { get; set; }

        /// <summary>Тип маркера.</summary>
        public ListMarkerType MarkerType { get; set; }

        /// <summary>Пользовательский символ маркера при MarkerType.Custom.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CustomMarker { get; set; }

        /// <summary>
        /// Символ(ы) после номера для счётных типов: ".", ")", "" и т.п.
        /// null — тип задаёт разделитель по умолчанию (точка).
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? NumberSuffix { get; set; }

        /// <summary>Символ(ы) перед номером для счётных типов (например "(").</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? NumberPrefix { get; set; }

        /// <summary>Начальный номер нумерации (по умолчанию 1).</summary>
        public int StartAt { get; set; } = 1;

        /// <summary>
        /// Минимальный зазор между маркером и текстом (pt). При автоматическом сдвиге маркера
        /// он не может подойти к тексту ближе этого значения.
        /// </summary>
        public double MarkerTextMinGapPt { get; set; } = DefaultMarkerTextGapPt;

        /// <summary>
        /// Пользовательская последовательность символов-«номеров» для MarkerType.CustomSequence.
        /// Элемент N берёт символ с индексом (N-1). Пустой список — как обычный bullet.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? CustomSequence { get; set; }

        /// <summary>
        /// Для CustomSequence: после последнего символа начинать сначала (true) или
        /// остановиться на последнем (false).
        /// </summary>
        public bool SequenceWrap { get; set; } = true;

        /// <summary>
        /// Тип маркера для каждого уровня многоуровневого списка (индекс = уровень).
        /// null — список одноуровневый, используется <see cref="MarkerType"/> для всех уровней.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ListMarkerType>? LevelMarkers { get; set; }

        /// <summary>
        /// Готовый текст маркера («1.», «•»), вычисленный движком нумерации перед раскладкой.
        /// Транзиентное поле (не сериализуется): раскладка меряет ширину, чтобы отодвинуть текст
        /// первой строки на зазор после цифры. Заполняется в проходе раскладки.
        /// </summary>
        [JsonIgnore]
        public string? ComputedMarkerText { get; set; }

        /// <summary>Измеренная ширина маркера в pt (заполняет раскладка). Транзиентное поле.</summary>
        [JsonIgnore]
        public double ComputedMarkerWidthPt { get; set; }

        /// <summary>
        /// Смещение текста ПЕРВОЙ строки относительно левого отступа (pt), вычисленное раскладкой
        /// как (позиция маркера + ширина + зазор − левый отступ). Транзиентное: линейка ставит по
        /// нему «абзацную стрелку». Не сериализуется.
        /// </summary>
        [JsonIgnore]
        public double ComputedFirstLineOffsetPt { get; set; }

        /// <summary>
        /// Явная позиция маркера от левого поля (pt). null — вычисляется по уровню.
        /// Двигается дополнительной стрелкой «край списка» на линейке (hanging).
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? MarkerIndentPt { get; set; }

        /// <summary>
        /// Явная позиция текста элемента списка от левого поля (pt). null — по уровню.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? TextIndentPt { get; set; }

        /// <summary>
        /// Продолжить нумерацию от предыдущего списка с тем же ListId.
        /// Если false — нумерация начинается заново.
        /// </summary>
        public bool ContinueNumbering { get; set; } = true;

        /// <summary>true — счётный (нумерованный) тип маркера; false — маркированный.</summary>
        [JsonIgnore]
        public bool IsNumbered => (int)MarkerType >= 10;

        /// <summary>Позиция текста элемента списка от левого поля (pt) с учётом уровня.</summary>
        public double EffectiveTextIndentPt()
            => TextIndentPt ?? (Level + 1) * DefaultLevelStepPt;

        /// <summary>Позиция маркера от левого поля (pt) с учётом уровня.</summary>
        public double EffectiveMarkerIndentPt()
            => MarkerIndentPt ?? Math.Max(0.0, EffectiveTextIndentPt() - DefaultHangingPt);

        /// <summary>Тип маркера текущего уровня: из <see cref="LevelMarkers"/>, иначе <see cref="MarkerType"/>.</summary>
        public ListMarkerType EffectiveMarkerTypeForLevel()
        {
            if (LevelMarkers is not null && Level >= 0 && Level < LevelMarkers.Count)
                return LevelMarkers[Level];
            return MarkerType;
        }

        public ListProperties Clone()
        {
            var c = (ListProperties)MemberwiseClone();
            // MemberwiseClone копирует ссылки на списки — делаем отдельные копии,
            // иначе клоны делили бы один и тот же список символов/уровней.
            if (CustomSequence is not null)
                c.CustomSequence = new List<string>(CustomSequence);
            if (LevelMarkers is not null)
                c.LevelMarkers = new List<ListMarkerType>(LevelMarkers);
            return c;
        }
    }

    /// <summary>
    /// Параграф документа — основной текстовый блок.
    /// Текст хранится в чанках (<see cref="Chunks"/>) для эффективного дельта-кеша.
    /// Свойства форматирования хранятся в <see cref="Properties"/> и ссылке на стиль.
    /// </summary>
    public sealed class ParagraphBlock : BlockModel
    {
        public override BlockType BlockType => BlockType.Paragraph;

        /// <summary>
        /// Чанки параграфа в порядке следования.
        /// Минимум один чанк (может быть пустым для пустого параграфа).
        /// </summary>
        public List<TextChunk> Chunks { get; set; } = new() { new TextChunk() };

        /// <summary>
        /// Свойства форматирования абзаца.
        /// Свойства заданные здесь переопределяют значения из стиля.
        /// </summary>
        public ParagraphProperties Properties { get; set; } = new();

        /// <summary>Свойства списка. Null если параграф не является элементом списка.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ListProperties? ListProperties { get; set; }

        /// <summary>
        /// Суммарная длина текста параграфа в символах.
        /// Вычисляется по сумме длин чанков.
        /// </summary>
        [JsonIgnore]
        public int TotalLength
        {
            get
            {
                int total = 0;
                foreach (var chunk in Chunks)
                    total += chunk.Length;
                return total;
            }
        }

        /// <summary>
        /// Возвращает plain text параграфа без форматирования.
        /// </summary>
        public string GetPlainText()
        {
            if (Chunks.Count == 0) return string.Empty;
            if (Chunks.Count == 1) return Chunks[0].GetPlainText();

            var sb = new System.Text.StringBuilder();
            foreach (var chunk in Chunks)
                sb.Append(chunk.GetPlainText());
            return sb.ToString();
        }

        /// <summary>
        /// Заменяет всё содержимое параграфа одним plain text Run.
        /// Используется при редактировании через TextBox до реализации inline-рендеринга.
        /// </summary>
        public void SetPlainText(string text)
        {
            Chunks.Clear();
            Chunks.Add(new TextChunk
            {
                Runs = new List<Inline.RunModel>
        {
            new Models.Inline.RunModel { Text = text ?? string.Empty }
        }
            });
            InvalidateAllChunks();
        }

        /// <summary>
        /// Символ параграфа вместе с его форматированием. Объект в строке (картинка)
        /// представлен одной ячейкой с символом-заполнителем
        /// <see cref="Inline.RunModel.ObjectPlaceholder"/> и ссылкой на объект: вся
        /// посимвольная арифметика редактора считает его обычным символом.
        /// </summary>
        public readonly struct CharCell
        {
            public CharCell(char ch, Inline.RunProperties? props, Guid? inlineImageId = null)
            {
                Ch = ch;
                Props = props;
                InlineImageId = inlineImageId;
            }

            public char Ch { get; }
            public Inline.RunProperties? Props { get; }
            public Guid? InlineImageId { get; }

            public bool IsInlineObject => InlineImageId.HasValue;
        }

        /// <summary>
        /// Разворачивает содержимое параграфа в плоский список символов с форматированием.
        /// Единственный способ разбирать параграф посимвольно: он не теряет ссылку на
        /// встроенный объект, тогда как обход по run.Text превратил бы картинку
        /// в голый символ-заполнитель.
        /// </summary>
        public List<CharCell> ToCharCells()
        {
            var cells = new List<CharCell>();
            foreach (var chunk in Chunks)
            {
                foreach (var run in chunk.Runs)
                {
                    if (run.InlineImageId is Guid objectId)
                    {
                        // Объектный run занимает ровно одну позицию независимо от того,
                        // что лежит в его Text.
                        cells.Add(new CharCell(
                            Inline.RunModel.ObjectPlaceholder, run.Properties, objectId));
                        continue;
                    }

                    foreach (var ch in run.Text)
                        cells.Add(new CharCell(ch, run.Properties));
                }
            }
            return cells;
        }

        /// <summary>
        /// Пересобирает чанки и раны из плоского списка символов. Соседние символы
        /// с одинаковым форматированием сливаются в один run, объект в строке всегда
        /// остаётся отдельным раном — иначе ссылка на картинку потерялась бы при слиянии.
        /// </summary>
        public void RebuildFromCharCells(IReadOnlyList<CharCell> cells)
        {
            Chunks.Clear();
            var newChunk = new TextChunk();
            Chunks.Add(newChunk);

            if (cells.Count == 0)
            {
                newChunk.Runs.Add(new Inline.RunModel { Text = string.Empty });
                InvalidateAllChunks();
                return;
            }

            var sb = new System.Text.StringBuilder();
            Inline.RunProperties? currentProps = null;
            bool hasPendingText = false;

            void FlushText()
            {
                if (!hasPendingText) return;
                newChunk.Runs.Add(new Inline.RunModel
                {
                    Text = sb.ToString(),
                    Properties = currentProps
                });
                sb.Clear();
                hasPendingText = false;
            }

            foreach (var cell in cells)
            {
                if (cell.IsInlineObject)
                {
                    FlushText();
                    newChunk.Runs.Add(new Inline.RunModel
                    {
                        Text = Inline.RunModel.ObjectPlaceholder.ToString(),
                        Properties = cell.Props,
                        InlineImageId = cell.InlineImageId
                    });
                    continue;
                }

                if (!hasPendingText)
                {
                    currentProps = cell.Props;
                    hasPendingText = true;
                }
                else if (!ReferenceEquals(cell.Props, currentProps)
                    && !RunPropertiesEqual(cell.Props, currentProps))
                {
                    FlushText();
                    currentProps = cell.Props;
                    hasPendingText = true;
                }

                sb.Append(cell.Ch);
            }

            FlushText();

            if (newChunk.Runs.Count == 0)
                newChunk.Runs.Add(new Inline.RunModel { Text = string.Empty });

            InvalidateAllChunks();
        }

        /// <summary>
        /// Вставляет/удаляет текст в диапазоне [from, to) с сохранением форматирования.
        /// Используется для всех операций редактирования (ввод, Delete, Backspace).
        /// В отличие от SetPlainText, не уничтожает RunProperties.
        /// </summary>
        public void SpliceText(int from, int to, string insert)
        {
            var cells = ToCharCells();

            int len = cells.Count;
            from = Math.Max(0, Math.Min(from, len));
            to = Math.Max(from, Math.Min(to, len));

            // Удаляем диапазон.
            if (to > from)
                cells.RemoveRange(from, to - from);

            // Определяем свойства для вставляемого текста:
            // берём форматирование символа в позиции вставки (или предыдущего).
            Inline.RunProperties? insertProps = null;
            if (from > 0)
                insertProps = cells[from - 1].Props;
            else if (from < cells.Count)
                insertProps = cells[from].Props;

            // Пустой абзац (символов нет): у пустого рана может быть форматирование — его
            // проставляют при Enter, чтобы ввод продолжал шрифт/начертание. Без этого ввод
            // в пустой абзац сбрасывался бы на дефолтный шрифт.
            if (insertProps is null && cells.Count == 0)
            {
                foreach (var chunk in Chunks)
                {
                    foreach (var run in chunk.Runs)
                        if (run.Properties is not null) { insertProps = run.Properties; break; }
                    if (insertProps is not null) break;
                }
            }

            // Вставляем новые символы.
            for (int i = 0; i < insert.Length; i++)
                cells.Insert(from + i, new CharCell(insert[i], insertProps));

            RebuildFromCharCells(cells);
        }

        /// <summary>
        /// Вставляет объект в строку (картинку) в позицию at как один символ.
        /// Форматирование наследуется от соседнего символа — объект встаёт в поток
        /// текста и дальше живёт по правилам обычного символа.
        /// </summary>
        public void InsertInlineObject(int at, Guid inlineImageId, Inline.RunProperties? props = null)
        {
            var cells = ToCharCells();
            at = Math.Max(0, Math.Min(at, cells.Count));

            var inherited = props;
            if (inherited is null && at > 0) inherited = cells[at - 1].Props;
            if (inherited is null && at < cells.Count) inherited = cells[at].Props;

            cells.Insert(at, new CharCell(
                Inline.RunModel.ObjectPlaceholder, inherited, inlineImageId));

            RebuildFromCharCells(cells);
        }

        /// <summary>
        /// Id встроенного объекта в позиции charIndex или null, если там обычный символ.
        /// </summary>
        public Guid? GetInlineImageIdAt(int charIndex)
        {
            if (charIndex < 0) return null;

            int pos = 0;
            foreach (var chunk in Chunks)
            {
                foreach (var run in chunk.Runs)
                {
                    int runLen = run.InlineImageId.HasValue ? 1 : (run.Text?.Length ?? 0);
                    if (charIndex < pos + runLen)
                        return run.InlineImageId;
                    pos += runLen;
                }
            }
            return null;
        }

        /// <summary>Позиция объекта с заданным Id в тексте параграфа или -1.</summary>
        public int IndexOfInlineObject(Guid inlineImageId)
        {
            int pos = 0;
            foreach (var chunk in Chunks)
            {
                foreach (var run in chunk.Runs)
                {
                    if (run.InlineImageId == inlineImageId) return pos;
                    pos += run.InlineImageId.HasValue ? 1 : (run.Text?.Length ?? 0);
                }
            }
            return -1;
        }

        /// <summary>Id всех объектов в строке, встречающихся в параграфе.</summary>
        public IEnumerable<Guid> EnumerateInlineImageIds()
        {
            foreach (var chunk in Chunks)
                foreach (var run in chunk.Runs)
                    if (run.InlineImageId is Guid id)
                        yield return id;
        }

        /// <summary>
        /// Сравнивает два RunProperties по значению всех полей.
        /// Null == Null и Null == default (все поля false/null).
        /// </summary>
        private static bool RunPropertiesEqual(
            Models.Inline.RunProperties? a,
            Models.Inline.RunProperties? b)
        {
            if (ReferenceEquals(a, b)) return true;

            bool aDefault = a is null || a.IsDefault();
            bool bDefault = b is null || b.IsDefault();
            if (aDefault && bDefault) return true;
            if (aDefault || bDefault) return false;

            return a!.FontFamily == b!.FontFamily
                && a.FontSize == b.FontSize
                && a.IsBold == b.IsBold
                && a.IsItalic == b.IsItalic
                && a.IsUnderline == b.IsUnderline
                && a.IsStrikethrough == b.IsStrikethrough
                && a.IsSuperscript == b.IsSuperscript
                && a.IsSubscript == b.IsSubscript
                && a.IsAllCaps == b.IsAllCaps
                && a.IsSmallCaps == b.IsSmallCaps
                && a.TextColor == b.TextColor
                && a.HighlightColor == b.HighlightColor
                && a.Language == b.Language;
        }

        /// <summary>
        /// Сбрасывает кешированные длины всех чанков.
        /// Вызывать после bulk-операций с текстом.
        /// </summary>
        public void InvalidateAllChunks()
        {
            foreach (var chunk in Chunks)
                chunk.InvalidateLength();
        }
    }
}