using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Models.Styles;
using Writersword.Modules.TextEditor.Models.Toc;
using W = DocumentFormat.OpenXml.Wordprocessing;
using Wp = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using Dr = DocumentFormat.OpenXml.Drawing;
using Pic = DocumentFormat.OpenXml.Drawing.Pictures;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Результат импорта документа.
    /// </summary>
    public sealed class ImportResult
    {
        public bool Success { get; set; }
        public DocumentModel? Document { get; set; }
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// Предупреждения о потере форматирования при импорте.
        /// </summary>
        public string[] Warnings { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Байты изображений, извлечённых из импортируемого файла.
        /// Ключ — сгенерированное имя файла (совпадает с <see cref="Models.Document.ImageBlock.ImageFileName"/>
        /// у соответствующей картинки в возвращённом <see cref="Document"/>), значение — содержимое файла.
        /// Импортёр текста никогда не пишет напрямую в хранилище проекта (у него нет доступа
        /// к контексту открытой вкладки) — эти байты обязан сохранить вызывающий код
        /// (см. <see cref="ViewModels.DocumentViewModel.ImportFromFile"/>) через
        /// <c>DocumentContext.WriteFile("TextEditor/Images/{fileName}", bytes)</c> до того,
        /// как документ станет активным — иначе ссылки на картинки повиснут.
        /// </summary>
        public Dictionary<string, byte[]> ExtractedImages { get; set; } = new();

        public static ImportResult Ok(DocumentModel doc, string[] warnings = null!) =>
            new() { Success = true, Document = doc, Warnings = warnings ?? Array.Empty<string>() };

        public static ImportResult Fail(string error) =>
            new() { Success = false, ErrorMessage = error };
    }

    /// <summary>
    /// Импортирует документы из внешних форматов в <see cref="DocumentModel"/>.
    /// Writersword-специфичные метки (персонажи, таймлайн) не могут быть восстановлены
    /// из внешних форматов — только базовое форматирование.
    /// </summary>
    public sealed class ImportService
    {
        // Единицы измерения OOXML: twips = 1/20 пункта = 1/1440 дюйма.
        private const double TwipsPerPoint = 20.0;
        private const double TwipsPerMm = 1440.0 / 25.4;
        private const double EmuPerPoint = 12700.0;
        private const double HalfPointsPerPoint = 2.0;
        private const double EighthsPerPoint = 8.0;

        /// <summary>
        /// Импортирует .docx файл в DocumentModel через DocumentFormat.OpenXml.
        /// Стили Word (по цепочке BasedOn + docDefaults) разрешаются в конкретные
        /// свойства и записываются на параграфы/раны напрямую — так внешний вид
        /// не зависит от произвольных пользовательских стилей исходного файла,
        /// которых нет в наборе встроенных стилей Writersword.
        /// Заголовки (Heading1–6), цитаты и код дополнительно помечаются именем
        /// стиля Writersword — для соответствующей семантики (структура, регистр).
        /// Поддерживается: параграфы, форматирование текста, списки (маркированные
        /// и нумерованные), таблицы (включая объединение ячеек, границы, заливку),
        /// картинки в тексте, разрывы страниц, параметры страницы финального раздела.
        /// Не поддерживается (игнорируется с предупреждением в Warnings):
        /// многораздельные документы (кроме последнего раздела), колонтитулы,
        /// сноски/концевые сноски, комментарии, отслеживание изменений (принимаются
        /// как есть), вложенные таблицы, векторные картинки (WMF/EMF), обтекание
        /// текстом у плавающих объектов (импортируются как обычные картинки в тексте).
        /// </summary>
        public Task<ImportResult> ImportFromDocxAsync(string filePath)
        {
            return Task.Run(() =>
            {
                try
                {
                    return ImportFromDocxCore(filePath);
                }
                catch (Exception ex)
                {
                    return ImportResult.Fail(ex.Message);
                }
            });
        }

        private ImportResult ImportFromDocxCore(string filePath)
        {
            var warnings = new List<string>();
            var extractedImages = new Dictionary<string, byte[]>();

            using var wordDoc = WordprocessingDocument.Open(filePath, false);
            var mainPart = wordDoc.MainDocumentPart;
            var body = mainPart?.Document?.Body;
            if (mainPart is null || body is null)
                return ImportResult.Fail("Файл не содержит тела документа (возможно, повреждён или это не .docx).");

            string? title = null;
            try { title = wordDoc.PackageProperties.Title; } catch { /* нечитаемые метаданные не критичны */ }
            if (string.IsNullOrWhiteSpace(title))
                title = Path.GetFileNameWithoutExtension(filePath);

            var doc = DocumentModel.CreateNew(title!);
            doc.Styles = new List<DocumentStyle>(DocumentStyle.CreateBuiltInStyles());
            var section = doc.Sections[0];
            section.Blocks.Clear();

            var resolver = new DocxFormatResolver(mainPart);
            var numbering = new DocxNumberingMap(mainPart);
            var listIdMap = new Dictionary<int, Guid>();

            BeginTocTracking(doc);

            ApplyWordCompatibility(mainPart, doc);

            ApplyFinalSectionPageSettings(body, doc, resolver, warnings);

            // Разрывы разделов считаются отдельным проходом: так разбор элементов не
            // тащит за собой счётчик и может вызывать сам себя для содержимого
            // контейнеров, вложенных на любую глубину.
            int nonFinalSectionBreaks = body.Descendants<W.Paragraph>()
                .Count(HasOwnSectionProperties);

            ImportBlockElements(body.Elements(), section, resolver, numbering, listIdMap,
                mainPart, extractedImages, warnings);

            // Оглавление, чьё поле так и не закрылось до конца документа, всё равно
            // остаётся оглавлением: Word такой файл открывает, и строки в нём есть.
            CloseToc();
            ResolveTocLinks(doc);
            NormalizeBorderGroups(doc);

            if (section.Blocks.Count == 0)
                section.Blocks.Add(new ParagraphBlock());

            if (nonFinalSectionBreaks > 0)
                warnings.Add(
                    $"Документ содержит {nonFinalSectionBreaks} внутр. разрыв(ов) раздела — " +
                    "импортированы как разрывы страницы; параметры страницы применены только " +
                    "из последнего раздела документа.");

            var result = ImportResult.Ok(doc, warnings.Distinct().ToArray());
            result.ExtractedImages = extractedImages;
            return result;
        }

        // ── Оглавление Word ─────────────────────────────────────────────────

        /// <summary>
        /// Одна строка собираемого оглавления: абзац, его уровень в оглавлении Word
        /// (0 — не строка с уровнем) и закладка главы, на которую строка ссылается.
        /// </summary>
        private sealed class ImportedTocLine
        {
            public ImportedTocLine(ParagraphBlock block, int level, string? anchor)
            {
                Block = block;
                Level = level;
                Anchor = anchor;
            }

            public ParagraphBlock Block { get; }
            public int Level { get; }
            public string? Anchor { get; }

            /// <summary>Строка оглавления, а не название над ним и не пустая строка.</summary>
            public bool IsEntry => Level > 0 || Anchor is not null;
        }

        /// <summary>Оглавление Word, строки которого сейчас собираются.</summary>
        private sealed class ImportedToc
        {
            public ImportedToc(string? instruction) => Instruction = instruction;

            /// <summary>Код поля TOC: уровни, номера страниц. null — поле не найдено.</summary>
            public string? Instruction { get; }

            public List<ImportedTocLine> Lines { get; } = new();
        }

        /// <summary>
        /// Открытый кадр поля Word. Поле состоит из begin, кода, separate, результата и
        /// end, и они разбросаны по ранам, а у оглавления — ещё и по абзацам.
        /// </summary>
        private sealed class FieldFrame
        {
            public System.Text.StringBuilder Code { get; } = new();
            public bool CodeComplete { get; set; }
            public bool IsToc { get; set; }
        }

        private DocumentModel? _tocDocument;
        private ImportedToc? _activeToc;

        // Оглавление открыто полем TOC посреди тела документа, а не контейнером:
        // закрывается оно концом своего поля, а не концом контейнера.
        private bool _activeTocIsField;

        // Глубина вложенности поля TOC. Внутри оглавления строки несут свои поля
        // PAGEREF и HYPERLINK, и конец каждого из них концом оглавления не является.
        private int _tocFieldDepth;

        private readonly List<FieldFrame> _fieldStack = new();

        // Закладка → абзац, в котором она стоит. По закладкам строки оглавления Word
        // находят свои главы (_Toc123456 и подобные).
        private readonly Dictionary<string, Guid> _bookmarkTargets = new(StringComparer.Ordinal);

        // Закладки, встреченные между абзацами: достаются следующему абзацу.
        private readonly List<string> _pendingBookmarks = new();

        // Строки всех оглавлений документа со ссылками на главы. Ссылки разрешаются в
        // конце: главы идут после оглавления, и при разборе строки их ещё нет.
        private readonly List<(ParagraphBlock Entry, string? Anchor)> _tocLinks = new();

        private static readonly Regex TocInstructionRegex =
            new(@"^\s*TOC(\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex PageRefRegex =
            new(@"PAGEREF\s+(\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex OutlineSwitchRegex =
            new(@"\\o\s+""?\s*(\d)\s*-\s*(\d)\s*""?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex NoPageNumbersSwitchRegex =
            new(@"\\n(\s|$)", RegexOptions.CultureInvariant);

        /// <summary>Сбросить сведения об оглавлениях перед новым импортом.</summary>
        private void BeginTocTracking(DocumentModel doc)
        {
            _tocDocument = doc;
            _activeToc = null;
            _activeTocIsField = false;
            _tocFieldDepth = 0;
            _fieldStack.Clear();
            _bookmarkTargets.Clear();
            _pendingBookmarks.Clear();
            _tocLinks.Clear();
            _betweenBorders.Clear();
        }

        /// <summary>
        /// Контейнер оглавления Word. Word помечает его галереей «Table of Contents»;
        /// у файлов из других программ пометки бывает нет, и тогда оглавление узнаётся
        /// по полю TOC внутри.
        /// </summary>
        private static bool IsTocContainer(W.SdtBlock sdt)
        {
            if (sdt.SdtProperties is { } props)
            {
                foreach (var gallery in props.Descendants<W.DocPartGallery>())
                {
                    string? value = gallery.Val?.Value;
                    if (value is not null
                        && value.IndexOf("Table of Contents", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }

            return sdt.SdtContentBlock is { } content && FindTocInstruction(content) is not null;
        }

        /// <summary>
        /// Код поля TOC внутри элемента. Код бывает разрезан на несколько ранов —
        /// собирается целиком, до разделителя поля.
        /// </summary>
        private static string? FindTocInstruction(OpenXmlElement scope)
        {
            var stack = new List<System.Text.StringBuilder>();

            foreach (var element in scope.Descendants())
            {
                switch (element)
                {
                    case W.FieldChar fc when fc.FieldCharType?.Value == W.FieldCharValues.Begin:
                        stack.Add(new System.Text.StringBuilder());
                        break;

                    case W.FieldCode code when stack.Count > 0:
                        stack[stack.Count - 1].Append(code.Text);
                        break;

                    case W.FieldChar fc when stack.Count > 0
                        && (fc.FieldCharType?.Value == W.FieldCharValues.Separate
                            || fc.FieldCharType?.Value == W.FieldCharValues.End):
                        string instruction = stack[stack.Count - 1].ToString();
                        if (TocInstructionRegex.IsMatch(instruction)) return instruction.Trim();
                        if (fc.FieldCharType?.Value == W.FieldCharValues.End)
                            stack.RemoveAt(stack.Count - 1);
                        else
                            stack[stack.Count - 1].Clear();
                        break;

                    case W.SimpleField simple when simple.Instruction?.Value is { } simpleInstruction
                        && TocInstructionRegex.IsMatch(simpleInstruction):
                        return simpleInstruction.Trim();
                }
            }

            return null;
        }

        private void OpenToc(string? instruction, bool isField)
        {
            _activeToc = new ImportedToc(instruction);
            _activeTocIsField = isField;
        }

        /// <summary>
        /// Учитывает только что разобранный абзац: его закладки, поля и — если идёт
        /// оглавление — место абзаца в нём.
        /// </summary>
        /// <param name="p">Абзац Word.</param>
        /// <param name="section">Раздел, куда легли абзацы Writersword.</param>
        /// <param name="blocksBefore">Сколько блоков было в разделе до разбора абзаца.</param>
        /// <param name="resolver">Резолвер стилей — по стилю узнаётся уровень строки.</param>
        private void TrackParagraphForToc(
            W.Paragraph p, SectionModel section, int blocksBefore, DocxFormatResolver resolver)
        {
            var created = new List<ParagraphBlock>();
            for (int i = blocksBefore; i < section.Blocks.Count; i++)
                if (section.Blocks[i] is ParagraphBlock block)
                    created.Add(block);

            if (created.Count > 0)
            {
                foreach (var name in _pendingBookmarks)
                    _bookmarkTargets.TryAdd(name, created[0].Id);

                foreach (var bookmark in p.Descendants<W.BookmarkStart>())
                    if (bookmark.Name?.Value is { Length: > 0 } name)
                        _bookmarkTargets.TryAdd(name, created[0].Id);
            }

            _pendingBookmarks.Clear();

            bool closeAfter = ScanParagraphFields(p);

            if (_activeToc is not null && created.Count > 0)
            {
                int level = resolver.WordTocLevel(p);
                string? anchor = TocAnchorOf(p);

                // Абзац, разрезанный разрывом страницы, остаётся одной строкой: ссылка и
                // уровень достаются первой части, остальные идут как текст оглавления.
                for (int i = 0; i < created.Count; i++)
                {
                    _activeToc.Lines.Add(i == 0
                        ? new ImportedTocLine(created[i], level, anchor)
                        : new ImportedTocLine(created[i], 0, null));
                }
            }

            if (closeAfter)
                CloseToc();
        }

        /// <summary>
        /// Проходит по полям абзаца. Открывает оглавление, когда встречено поле TOC
        /// посреди тела документа, и сообщает, что поле оглавления в этом абзаце
        /// закрылось.
        /// </summary>
        /// <returns>true — оглавление, открытое полем, кончается на этом абзаце.</returns>
        private bool ScanParagraphFields(W.Paragraph p)
        {
            bool closeAfter = false;

            foreach (var element in p.Descendants())
            {
                switch (element)
                {
                    case W.FieldChar fc when fc.FieldCharType?.Value == W.FieldCharValues.Begin:
                        _fieldStack.Add(new FieldFrame());
                        break;

                    case W.FieldCode code when _fieldStack.Count > 0:
                        var open = _fieldStack[_fieldStack.Count - 1];
                        if (!open.CodeComplete) open.Code.Append(code.Text);
                        break;

                    case W.FieldChar fc when _fieldStack.Count > 0
                        && fc.FieldCharType?.Value == W.FieldCharValues.Separate:
                        CompleteFieldCode(_fieldStack[_fieldStack.Count - 1]);
                        break;

                    case W.FieldChar fc when _fieldStack.Count > 0
                        && fc.FieldCharType?.Value == W.FieldCharValues.End:
                        var closing = _fieldStack[_fieldStack.Count - 1];
                        CompleteFieldCode(closing);

                        if (_activeTocIsField && closing.IsToc && _fieldStack.Count == _tocFieldDepth)
                            closeAfter = true;

                        _fieldStack.RemoveAt(_fieldStack.Count - 1);
                        break;
                }
            }

            return closeAfter;
        }

        /// <summary>
        /// Код поля собран: если это TOC и оглавление ещё не открыто, оглавление
        /// начинается с этого абзаца.
        /// </summary>
        private void CompleteFieldCode(FieldFrame frame)
        {
            if (frame.CodeComplete) return;
            frame.CodeComplete = true;

            string instruction = frame.Code.ToString();
            if (!TocInstructionRegex.IsMatch(instruction)) return;

            frame.IsToc = true;

            if (_activeToc is null)
            {
                OpenToc(instruction.Trim(), isField: true);
                _tocFieldDepth = _fieldStack.Count;
            }
        }

        /// <summary>
        /// Закладка главы, на которую ссылается строка оглавления: у гиперссылки она в
        /// атрибуте anchor, у номера страницы — в коде поля PAGEREF.
        /// </summary>
        private static string? TocAnchorOf(W.Paragraph p)
        {
            foreach (var link in p.Descendants<W.Hyperlink>())
                if (link.Anchor?.Value is { Length: > 0 } anchor)
                    return anchor;

            var code = new System.Text.StringBuilder();
            foreach (var part in p.Descendants<W.FieldCode>())
                code.Append(part.Text).Append(' ');

            var match = PageRefRegex.Match(code.ToString());
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>
        /// Превращает собранные строки в оглавление Writersword: заводит ему настройки
        /// и помечает абзацы как его строки. С этого момента оно обновляет номера
        /// страниц само, пересобирается кнопкой и правится вкладкой «Оглавление» —
        /// а не лежит в рукописи набором текста, похожим на оглавление.
        ///
        /// Строки остаются такими, какими их сделал Word: с его шрифтом, отступами и
        /// правками, которые человек вносил в них руками. Пересборка по кнопке
        /// «Обновить» заменит их строками Writersword, как у любого другого оглавления.
        /// </summary>
        private void CloseToc()
        {
            var toc = _activeToc;
            _activeToc = null;
            _activeTocIsField = false;
            _tocFieldDepth = 0;

            if (toc is null || _tocDocument is null) return;

            var lines = toc.Lines;

            int first = lines.FindIndex(l => l.IsEntry);

            // Ни одной строки со ссылкой или уровнем: это не оглавление, а текст в
            // контейнере, — пусть остаётся текстом.
            if (first < 0) return;

            int last = lines.FindLastIndex(l => l.IsEntry);

            // Название — первая непустая строка над списком.
            int titleIndex = -1;
            for (int i = 0; i < first; i++)
            {
                if (NormalizeTocText(lines[i].Block.GetPlainText()).Length > 0)
                {
                    titleIndex = i;
                    break;
                }
            }

            var settings = BuildTocSettings(toc, lines, first, last, titleIndex);

            int rangeStart = titleIndex >= 0 ? titleIndex : first;

            for (int i = rangeStart; i <= last; i++)
            {
                var line = lines[i];
                var props = line.Block.Properties;

                props.TocOwnerId = settings.Id;

                // Строка оглавления заголовком рукописи не бывает: иначе навигатор
                // показал бы её рядом с главой, на которую она ссылается.
                props.OutlineLevel = 0;
                props.IncludeInToc = false;

                if (i == titleIndex)
                {
                    props.TocEntryLevel = 0;
                    props.StyleName = "TocTitle";
                    continue;
                }

                if (!line.IsEntry)
                {
                    // Пустая или служебная строка внутри списка: остаётся частью
                    // оглавления, чтобы оно шло одним куском, но номера не носит.
                    props.TocEntryLevel = 0;
                    continue;
                }

                int level = line.Level > 0 ? line.Level : settings.MinLevel;
                level = Math.Clamp(level, 1, 9);

                props.TocEntryLevel = level;
                props.StyleName = TocService.TocStyleName(level);

                _tocLinks.Add((line.Block, line.Anchor));
            }

            _tocDocument.TableOfContents ??= new List<TocSettings>();
            _tocDocument.TableOfContents.Add(settings);
        }

        /// <summary>
        /// Настройки оглавления по коду поля TOC и по самим строкам: уровни, номера
        /// страниц, заполнитель, шаг отступа. Всё, что строки говорят о себе, старше
        /// значений по умолчанию — пересборка должна дать то же, что было в Word.
        /// </summary>
        private static TocSettings BuildTocSettings(
            ImportedToc toc, List<ImportedTocLine> lines, int first, int last, int titleIndex)
        {
            var settings = new TocSettings();

            if (titleIndex >= 0)
            {
                settings.Title = NormalizeTocText(lines[titleIndex].Block.GetPlainText());
                settings.ShowTitle = true;
            }
            else
            {
                settings.ShowTitle = false;
            }

            int minLevel = int.MaxValue;
            int maxLevel = 0;
            bool anyPageNumber = false;
            ParagraphBlock? firstEntry = null;

            // Отступ первой встреченной строки каждого уровня — по ним считается шаг.
            var indentByLevel = new Dictionary<int, double>();

            for (int i = first; i <= last; i++)
            {
                var line = lines[i];
                if (!line.IsEntry) continue;

                firstEntry ??= line.Block;

                if (line.Level > 0)
                {
                    minLevel = Math.Min(minLevel, line.Level);
                    maxLevel = Math.Max(maxLevel, line.Level);

                    if (!indentByLevel.ContainsKey(line.Level))
                        indentByLevel[line.Level] = line.Block.Properties.LeftIndent ?? 0;
                }

                if (HasPageNumber(line.Block.GetPlainText()))
                    anyPageNumber = true;
            }

            string instruction = toc.Instruction ?? string.Empty;
            var outline = OutlineSwitchRegex.Match(instruction);

            if (outline.Success
                && int.TryParse(outline.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int from)
                && int.TryParse(outline.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int to)
                && from >= 1 && to >= from && to <= 9)
            {
                settings.MinLevel = from;
                settings.MaxLevel = to;
            }
            else if (maxLevel > 0)
            {
                settings.MinLevel = minLevel;
                settings.MaxLevel = maxLevel;
            }

            // Строки других уровней, чем объявлено в коде (правка руками, поле со
            // своим списком стилей), расширяют диапазон: пересборка не должна
            // выкидывать то, что в оглавлении было.
            if (maxLevel > 0)
            {
                settings.MinLevel = Math.Min(settings.MinLevel, minLevel);
                settings.MaxLevel = Math.Max(settings.MaxLevel, maxLevel);
            }

            // Номера страниц: есть в строках — показываются; поле просит их не
            // показывать (\n) и их нет — не показываются. Номер, которого в строке нет
            // лишь потому, что Word не обновил поле, допишет первый же пересчёт.
            settings.ShowPageNumbers = anyPageNumber || !NoPageNumbersSwitchRegex.IsMatch(instruction);

            // Заполнитель — тот, что стоит у правой позиции табуляции первой строки.
            if (firstEntry?.Properties.TabStops is { Count: > 0 } tabs)
            {
                var stop = tabs[tabs.Count - 1];
                settings.Leader = stop.Leader switch
                {
                    TabLeaderStyle.Dots => TocLeader.Dots,
                    TabLeaderStyle.Dashes => TocLeader.Dashes,
                    TabLeaderStyle.Line => TocLeader.Line,
                    _ => TocLeader.None
                };
            }

            // Шаг отступа — разница отступов соседних уровней. Одинаковые отступы у
            // разных уровней значат, что лесенки в оглавлении нет.
            var levels = new List<int>(indentByLevel.Keys);
            levels.Sort();

            if (levels.Count >= 2)
            {
                double step = (indentByLevel[levels[1]] - indentByLevel[levels[0]]) / (levels[1] - levels[0]);

                if (step > 0.5)
                {
                    settings.IndentByLevel = true;
                    settings.LevelIndentPt = Math.Round(step, 1);
                }
                else
                {
                    settings.IndentByLevel = false;
                }
            }

            return settings;
        }

        /// <summary>Есть ли у строки номер страницы: число после последней табуляции.</summary>
        private static bool HasPageNumber(string text)
        {
            int tabAt = text.LastIndexOf('\t');
            if (tabAt < 0) return false;

            string tail = text.Substring(tabAt + 1).Trim();
            if (tail.Length == 0) return false;

            foreach (char ch in tail)
                if (!char.IsLetterOrDigit(ch)) return false;

            return true;
        }

        /// <summary>
        /// Связывает строки оглавлений с главами. Сперва по закладке, на которую
        /// ссылается строка; не нашлась — по тексту: строка ищет главу с тем же
        /// названием, идя вперёд от последней найденной, так что одинаково названные
        /// главы («Пролог» в каждой части) разбираются по порядку.
        ///
        /// Строка, для которой главы не нашлось, остаётся без ссылки: она видна и
        /// правится, а пересборка заменит её строкой по настоящим заголовкам.
        /// </summary>
        private void ResolveTocLinks(DocumentModel doc)
        {
            if (_tocLinks.Count == 0) return;

            var headings = TocService.Collect(doc, includeManual: true);

            var headingIndexById = new Dictionary<Guid, int>();
            for (int i = 0; i < headings.Count; i++)
                headingIndexById[headings[i].BlockId] = i;

            int cursor = 0;

            foreach (var (entry, anchor) in _tocLinks)
            {
                Guid? target = null;

                if (anchor is not null && _bookmarkTargets.TryGetValue(anchor, out var byBookmark))
                {
                    target = byBookmark;
                    if (headingIndexById.TryGetValue(byBookmark, out int at))
                        cursor = at + 1;
                }
                else
                {
                    string title = EntryTitleText(entry);

                    if (title.Length > 0)
                    {
                        for (int i = cursor; i < headings.Count; i++)
                        {
                            if (!string.Equals(NormalizeTocText(headings[i].Text), title,
                                    StringComparison.OrdinalIgnoreCase))
                                continue;

                            target = headings[i].BlockId;
                            cursor = i + 1;
                            break;
                        }
                    }
                }

                entry.Properties.TocTargetBlockId = target;
            }

            _tocLinks.Clear();
        }

        /// <summary>Название строки оглавления без номера страницы, одной строкой.</summary>
        private static string EntryTitleText(ParagraphBlock entry)
        {
            string raw = entry.GetPlainText();
            int tabAt = raw.LastIndexOf('\t');
            if (tabAt >= 0 && HasPageNumber(raw))
                raw = raw.Substring(0, tabAt);

            return NormalizeTocText(raw);
        }

        /// <summary>
        /// Текст одной строкой: без символов картинок, переводов строк, табуляций и
        /// повторных пробелов — так же, как видит заголовки само оглавление.
        /// </summary>
        private static string NormalizeTocText(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;

            var sb = new System.Text.StringBuilder(raw.Length);
            bool lastWasSpace = false;

            foreach (char ch in raw)
            {
                if (ch == RunModel.ObjectPlaceholder) continue;

                bool isSpace = char.IsWhiteSpace(ch) || ch == '\u00A0' || ch == '\u202F' || ch == '\u2009';

                if (isSpace)
                {
                    if (!lastWasSpace && sb.Length > 0) sb.Append(' ');
                    lastWasSpace = true;
                    continue;
                }

                sb.Append(ch);
                lastWasSpace = false;
            }

            while (sb.Length > 0 && sb[sb.Length - 1] == ' ')
                sb.Length--;

            return sb.ToString();
        }

        // ── Рамки абзацев ───────────────────────────────────────────────────

        // Линия между абзацами одной рамки (w:between) — по абзацам, у которых она
        // есть. Нужна только до сведения групп: в модели её нет.
        private readonly Dictionary<ParagraphBlock, ParagraphBorderLine> _betweenBorders = new();

        /// <summary>
        /// Сводит подряд идущие абзацы с одинаковой рамкой в одну коробку — так их
        /// показывает Word. У абзацев группы, кроме первого, снимается верхняя линия
        /// (вместо неё встаёт линия между абзацами, если она задана), у всех, кроме
        /// последнего, — нижняя. Без этого врезка из трёх абзацев выходила бы тремя
        /// отдельными рамками одна под другой.
        ///
        /// Группа — это абзацы одного потока (раздела или ячейки), идущие вплотную, с
        /// одинаковыми линиями и одинаковыми отступами. Разрыв страницы, таблица или
        /// картинка между ними группу рвут.
        /// </summary>
        private void NormalizeBorderGroups(DocumentModel doc)
        {
            foreach (var section in doc.Sections)
            {
                NormalizeBorderGroups(section.Blocks);

                foreach (var block in section.Blocks)
                {
                    if (block is not TableBlock table) continue;

                    foreach (var cell in table.Cells)
                        NormalizeBorderGroups(cell.Paragraphs);
                }
            }

            _betweenBorders.Clear();
        }

        private void NormalizeBorderGroups<TBlock>(List<TBlock> blocks) where TBlock : BlockModel
        {
            int start = 0;

            while (start < blocks.Count)
            {
                if (blocks[start] is not ParagraphBlock first || first.Properties.Borders is null)
                {
                    start++;
                    continue;
                }

                int end = start;
                while (end + 1 < blocks.Count
                       && blocks[end + 1] is ParagraphBlock next
                       && SameBorderGroup(first, next))
                    end++;

                if (end > start)
                {
                    for (int i = start; i <= end; i++)
                    {
                        var para = (ParagraphBlock)(BlockModel)blocks[i];
                        var borders = para.Properties.Borders!;

                        if (i > start)
                            borders.Top = _betweenBorders.TryGetValue(para, out var between)
                                ? between.Clone()
                                : null;

                        if (i < end)
                            borders.Bottom = null;

                        if (borders.IsEmpty)
                            para.Properties.Borders = null;
                    }
                }

                start = end + 1;
            }
        }

        /// <summary>
        /// Два абзаца в одной рамке: линии одинаковы, линия между абзацами одна и та
        /// же, края текста совпадают.
        /// </summary>
        private bool SameBorderGroup(ParagraphBlock a, ParagraphBlock b)
        {
            if (!ParagraphBorders.Same(a.Properties.Borders, b.Properties.Borders)) return false;

            _betweenBorders.TryGetValue(a, out var betweenA);
            _betweenBorders.TryGetValue(b, out var betweenB);
            if (!ParagraphBorderLine.Same(betweenA, betweenB)) return false;

            return Math.Abs((a.Properties.LeftIndent ?? 0) - (b.Properties.LeftIndent ?? 0)) < 0.01
                && Math.Abs((a.Properties.RightIndent ?? 0) - (b.Properties.RightIndent ?? 0)) < 0.01;
        }

        // ── Параграфы и разрывы страниц ────────────────────────────────────

        /// <summary>
        /// Разбирает элементы уровня блока: абзацы, таблицы и контейнеры содержимого
        /// (w:sdt). В контейнер Word заворачивает оглавление, элементы управления и
        /// блоки шаблонов — без разбора его содержимого документ молча теряет целые
        /// куски текста, а у оглавления это разом все его строки.
        /// </summary>
        private void ImportBlockElements(
            IEnumerable<OpenXmlElement> elements,
            SectionModel section,
            DocxFormatResolver resolver,
            DocxNumberingMap numbering,
            Dictionary<int, Guid> listIdMap,
            MainDocumentPart mainPart,
            Dictionary<string, byte[]> extractedImages,
            List<string> warnings)
        {
            foreach (var element in elements)
            {
                switch (element)
                {
                    case W.Paragraph p:
                        int blocksBefore = section.Blocks.Count;
                        ImportParagraphWithBreaks(p, section, resolver, numbering, listIdMap,
                            mainPart, extractedImages, warnings);
                        TrackParagraphForToc(p, section, blocksBefore, resolver);
                        break;

                    case W.Table t:
                        var tableBlock = ImportTable(t, section, resolver, numbering, listIdMap,
                            mainPart, extractedImages, warnings, depth: 0);
                        if (tableBlock is not null)
                            section.Blocks.Add(tableBlock);
                        break;

                    case W.SdtBlock sdt:
                        if (sdt.SdtContentBlock is { } sdtContent)
                        {
                            // Контейнер оглавления Word: всё, что в нём лежит, — строки
                            // одного оглавления. Вложенное в уже открытое оглавление
                            // новым не считается.
                            bool tocContainer = _activeToc is null && IsTocContainer(sdt);
                            if (tocContainer)
                                OpenToc(FindTocInstruction(sdtContent), isField: false);

                            ImportBlockElements(sdtContent.ChildElements, section, resolver,
                                numbering, listIdMap, mainPart, extractedImages, warnings);

                            if (tocContainer)
                                CloseToc();
                        }
                        break;

                    case W.BookmarkStart bookmark:
                        // Закладка между абзацами относится к следующему абзацу: так
                        // её ставит Word, когда выделение начинается с начала абзаца.
                        if (bookmark.Name?.Value is { Length: > 0 } bookmarkName)
                            _pendingBookmarks.Add(bookmarkName);
                        break;

                    // W.SectionProperties как прямой потомок Body — параметры финального
                    // раздела, уже учтены в ApplyFinalSectionPageSettings.
                }
            }
        }

        /// <summary>
        /// Импортирует один W.Paragraph. Разрыв страницы (w:br type="page") внутри
        /// параграфа не имеет аналога «в середине абзаца» в модели Writersword —
        /// разрыв там отдельный блок потока. Поэтому параграф режется на несколько
        /// ParagraphBlock, между которыми вставляется BreakBlock(Page).
        /// </summary>
        private void ImportParagraphWithBreaks(
            W.Paragraph p,
            SectionModel section,
            DocxFormatResolver resolver,
            DocxNumberingMap numbering,
            Dictionary<int, Guid> listIdMap,
            MainDocumentPart mainPart,
            Dictionary<string, byte[]> extractedImages,
            List<string> warnings)
        {
            var effPara = resolver.ResolveEffectiveParagraph(p);
            string? styleName = resolver.MapStyleName(p, effPara);

            var segments = SplitRunsByPageBreak(p);

            for (int i = 0; i < segments.Count; i++)
            {
                var para = new ParagraphBlock();
                para.Properties = effPara.ToParagraphProperties(styleName);
                para.ListProperties = numbering.Resolve(effPara, para.Properties, listIdMap);

                var chunk = new TextChunk();
                para.Chunks.Clear();
                para.Chunks.Add(chunk);
                chunk.Runs.Clear();

                foreach (var runElement in segments[i])
                    AppendRunOrDrawing(runElement, chunk, section, resolver, effPara,
                        mainPart, extractedImages, warnings);

                if (chunk.Runs.Count == 0)
                    chunk.Runs.Add(BuildParagraphMarkRun(p, resolver, effPara));

                chunk.InvalidateLength();
                section.Blocks.Add(para);

                if (effPara.BetweenBorder is { } between)
                    _betweenBorders[para] = between;

                if (i < segments.Count - 1)
                    section.Blocks.Add(new BreakBlock { BreakType = BreakType.Page });
            }
        }

        /// <summary>
        /// Делит содержимое параграфа на сегменты по разрывам страниц (w:br type="page").
        /// Каждый сегмент — список дочерних элементов Run/Hyperlink/... между разрывами.
        /// </summary>
        private static List<List<OpenXmlElement>> SplitRunsByPageBreak(W.Paragraph p)
        {
            var segments = new List<List<OpenXmlElement>> { new() };

            foreach (var child in p.ChildElements)
            {
                if (child is W.ParagraphProperties) continue;

                if (child is W.Run run && run.Elements<W.Break>().Any(b => b.Type?.Value == W.BreakValues.Page))
                {
                    // Ран может содержать текст ДО разрыва и после — в большинстве
                    // документов разрыв страницы занимает ран целиком, но на всякий
                    // случай текст до/после разрыва распределяем по сегментам.
                    var before = new W.Run(run.RunProperties?.CloneNode(true) ?? new W.RunProperties());
                    var after = new W.Run(run.RunProperties?.CloneNode(true) ?? new W.RunProperties());
                    bool seenBreak = false;
                    foreach (var rc in run.ChildElements)
                    {
                        if (rc is W.Break brk && brk.Type?.Value == W.BreakValues.Page)
                        {
                            seenBreak = true;
                            continue;
                        }
                        if (rc is W.RunProperties) continue;
                        (seenBreak ? after : before).AppendChild(rc.CloneNode(true));
                    }

                    if (before.ChildElements.Count > 0) segments[^1].Add(before);
                    segments.Add(new List<OpenXmlElement>());
                    if (after.ChildElements.Count > 0) segments[^1].Add(after);
                    continue;
                }

                segments[^1].Add(child);
            }

            if (segments.Count == 0) segments.Add(new List<OpenXmlElement>());
            return segments;
        }

        /// <summary>
        /// Переносит из настроек документа Word то, от чего зависит вёрстка всех абзацев
        /// сразу: схлопывание интервалов между абзацами и сжатие пробелов при
        /// выравнивании по ширине.
        ///
        /// Интервалы Word схлопывает, пока в совместимости нет
        /// w:doNotUseHTMLParagraphAutoSpacing. Пробелы сжимает Word 2013 и новее —
        /// режим совместимости 15; документ без режима Word открывает как Word 2007.
        /// </summary>
        private static void ApplyWordCompatibility(MainDocumentPart mainPart, DocumentModel doc)
        {
            var compat = mainPart.DocumentSettingsPart?.Settings?.GetFirstChild<W.Compatibility>();

            bool sumSpacing = compat?.GetFirstChild<W.DoNotUseHTMLParagraphAutoSpacing>() is { } htmlFlag
                && (htmlFlag.Val is null || htmlFlag.Val.Value);
            doc.CollapseParagraphSpacing = !sumSpacing;

            int compatibilityMode = 12;
            if (compat is not null)
            {
                foreach (var setting in compat.Elements<W.CompatibilitySetting>())
                {
                    if (setting.Name?.Value != W.CompatSettingNameValues.CompatibilityMode) continue;
                    if (int.TryParse(setting.Val?.Value, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out int mode))
                        compatibilityMode = mode;
                }
            }
            doc.JustifyWithShrinking = compatibilityMode >= 15;
        }

        private static bool HasOwnSectionProperties(W.Paragraph p) =>
            p.ParagraphProperties?.SectionProperties is not null;

        /// <summary>
        /// Разворачивает один дочерний элемент параграфа (ран, гиперссылка, вставка/удаление
        /// при отслеживании правок) в раны нашей модели, дописывая их в чанк.
        /// </summary>
        private void AppendRunOrDrawing(
            OpenXmlElement element,
            TextChunk chunk,
            SectionModel section,
            DocxFormatResolver resolver,
            EffectiveParagraph effPara,
            MainDocumentPart mainPart,
            Dictionary<string, byte[]> extractedImages,
            List<string> warnings)
        {
            switch (element)
            {
                case W.Run run:
                    AppendRun(run, chunk, section, resolver, effPara, mainPart, extractedImages, warnings);
                    break;

                case W.Hyperlink hyperlink:
                    // Гиперссылка — просто контейнер ранов; сам URL не хранится в модели
                    // Writersword (гиперссылки как отдельная сущность не реализованы),
                    // текст ссылки импортируется как обычный форматированный текст.
                    foreach (var innerRun in hyperlink.Elements<W.Run>())
                        AppendRun(innerRun, chunk, section, resolver, effPara, mainPart, extractedImages, warnings);
                    break;

                case W.InsertedRun ins:
                    foreach (var innerRun in ins.Elements<W.Run>())
                        AppendRun(innerRun, chunk, section, resolver, effPara, mainPart, extractedImages, warnings);
                    break;

                case W.DeletedRun:
                    // Текст, удалённый с отслеживанием правок — не переносим в импорт
                    // (эквивалент «принять все правки» для удалений).
                    break;

                case W.SimpleField simpleField:
                    // В w:fldSimple Word хранит последнее вычисленное значение поля
                    // обычными ранами: код поля не нужен, а значение — это видимый
                    // текст документа, и терять его нельзя.
                    foreach (var innerRun in simpleField.Elements<W.Run>())
                        AppendRun(innerRun, chunk, section, resolver, effPara, mainPart, extractedImages, warnings);
                    break;

                case W.MoveToRun moveTo:
                    // Приёмник перемещения равнозначен вставке: принятая правка
                    // оставляет его текст в документе.
                    foreach (var innerRun in moveTo.Elements<W.Run>())
                        AppendRun(innerRun, chunk, section, resolver, effPara, mainPart, extractedImages, warnings);
                    break;

                case W.MoveFromRun:
                    // Источник перемещения равнозначен удалению. Разбирать его нельзя:
                    // фрагмент удвоился бы, оставшись и на старом месте, и на новом.
                    break;
            }
        }

        /// <summary>
        /// Ран пустого абзаца. Высоту такой строки задаёт форматирование знака конца
        /// абзаца (w:pPr/w:rPr): пустая строка кеглем 36 пт занимает 36 пт, а не
        /// размер по умолчанию. Без этого каждый пустой абзац крадёт по два десятка
        /// пунктов, и разбивка на страницы расходится с Word.
        /// </summary>
        private static RunModel BuildParagraphMarkRun(
            W.Paragraph p, DocxFormatResolver resolver, EffectiveParagraph effPara)
        {
            var markFormat = effPara.BaseRun.Clone();
            markFormat.MergeFrom(p.ParagraphProperties?.ParagraphMarkRunProperties,
                resolver.ThemeMajorFont, resolver.ThemeMinorFont);

            return new RunModel { Text = string.Empty, Properties = markFormat.ToRunProperties() };
        }

        private void AppendRun(
            W.Run run,
            TextChunk chunk,
            SectionModel section,
            DocxFormatResolver resolver,
            EffectiveParagraph effPara,
            MainDocumentPart mainPart,
            Dictionary<string, byte[]> extractedImages,
            List<string> warnings)
        {
            var runProps = resolver.ResolveEffectiveRun(run, effPara).ToRunProperties();

            foreach (var child in run.ChildElements)
            {
                switch (child)
                {
                    case W.Text t:
                        chunk.Runs.Add(new RunModel { Text = t.Text, Properties = runProps });
                        break;

                    case W.TabChar:
                        chunk.Runs.Add(new RunModel { Text = "\t", Properties = runProps });
                        break;

                    case W.CarriageReturn:
                    case W.Break brk when brk.Type?.Value != W.BreakValues.Page:
                        // Разрыв строки внутри абзаца (Shift+Enter) — переносим как перевод строки
                        // в тексте: раскладка абзаца интерпретирует \n как мягкий перенос строки.
                        chunk.Runs.Add(new RunModel { Text = "\n", Properties = runProps });
                        break;

                    case W.Drawing drawing:
                        ImportDrawing(drawing, chunk, section, runProps, mainPart, extractedImages, warnings);
                        break;

                    case W.FootnoteReference:
                    case W.EndnoteReference:
                        warnings.Add("Сноски/концевые сноски не поддерживаются и были пропущены.");
                        break;
                }
            }
        }

        private void ImportDrawing(
            W.Drawing drawing,
            TextChunk chunk,
            SectionModel section,
            RunProperties runProps,
            MainDocumentPart mainPart,
            Dictionary<string, byte[]> extractedImages,
            List<string> warnings)
        {
            Wp.Inline? inline = drawing.Inline;
            Wp.Anchor? anchor = drawing.Anchor;
            bool isFloating = anchor is not null;

            // У плавающего объекта (wp:anchor) графика лежит дочерним элементом:
            // отдельного свойства, как у wp:inline, у него нет.
            Dr.Graphic? graphic = inline?.Graphic ?? anchor?.GetFirstChild<Dr.Graphic>();
            long extentCx = inline?.Extent?.Cx ?? anchor?.Extent?.Cx ?? 0;
            long extentCy = inline?.Extent?.Cy ?? anchor?.Extent?.Cy ?? 0;

            var blip = graphic?.GraphicData?.Descendants<Dr.Blip>().FirstOrDefault();
            string? relId = blip?.Embed?.Value;
            if (string.IsNullOrEmpty(relId))
                return; // не растровая картинка (например, диаграмма/OLE) — пропускаем молча, это не потеря текста

            if (mainPart.GetPartById(relId!) is not ImagePart imagePart)
                return;

            byte[] data;
            using (var stream = imagePart.GetStream(FileMode.Open, FileAccess.Read))
            using (var mem = new MemoryStream())
            {
                stream.CopyTo(mem);
                data = mem.ToArray();
            }

            string ext = ContentTypeToExtension(imagePart.ContentType);
            if (ext.Length == 0)
            {
                warnings.Add("Изображение неподдерживаемого формата (векторное или неизвестное) пропущено.");
                return;
            }

            string fileName = $"img_{Guid.NewGuid():N}{ext}";
            extractedImages[fileName] = data;

            double widthPt = extentCx > 0 ? extentCx / EmuPerPoint : 100;
            double heightPt = extentCy > 0 ? extentCy / EmuPerPoint : 100;

            var image = new ImageBlock
            {
                ImageFileName = fileName,
                WidthPt = widthPt,
                HeightPt = heightPt,
                WrapMode = WrapMode.Inline
            };

            if (isFloating)
                warnings.Add("Обтекание текстом у плавающих картинок не переносится — картинка вставлена как обычная (в тексте).");

            section.InlineObjects.Add(image);
            chunk.Runs.Add(new RunModel
            {
                Text = RunModel.ObjectPlaceholder.ToString(),
                Properties = runProps,
                InlineImageId = image.Id
            });
        }

        private static string ContentTypeToExtension(string contentType) => contentType switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            "image/tiff" => ".tiff",
            "image/x-icon" => ".ico",
            "image/webp" => ".webp",
            _ => string.Empty
        };

        // ── Таблицы ─────────────────────────────────────────────────────────

        private TableBlock? ImportTable(
            W.Table table,
            SectionModel section,
            DocxFormatResolver resolver,
            DocxNumberingMap numbering,
            Dictionary<int, Guid> listIdMap,
            MainDocumentPart mainPart,
            Dictionary<string, byte[]> extractedImages,
            List<string> warnings,
            int depth)
        {
            if (depth > 0)
            {
                warnings.Add("Вложенные таблицы не поддерживаются и были пропущены.");
                return null;
            }

            var grid = table.GetFirstChild<W.TableGrid>();
            var columnWidthsTwips = grid?.Elements<W.GridColumn>()
                .Select(c => ParseLong(c.Width?.Value) ?? 0L)
                .ToList() ?? new List<long>();

            var rows = table.Elements<W.TableRow>().ToList();

            // Колонок в таблице столько, сколько их в самой широкой строке. Сумма по
            // всем строкам давала бы у таблицы 3x3 девять колонок, и ширины ячеек
            // расходились бы втрое.
            int columnCount = columnWidthsTwips.Count > 0
                ? columnWidthsTwips.Count
                : rows.Select(r => r.Elements<W.TableCell>()
                        .Sum(c => c.TableCellProperties?.GridSpan?.Val?.Value ?? 1))
                    .DefaultIfEmpty(0)
                    .Max();
            if (columnCount <= 0) columnCount = 1;

            var block = new TableBlock
            {
                RowCount = rows.Count,
                ColumnCount = columnCount
            };

            long totalWidthTwips = columnWidthsTwips.Sum();
            if (totalWidthTwips > 0)
            {
                foreach (var w in columnWidthsTwips)
                {
                    block.Columns.Add(new TableColumnDefinition
                    {
                        WidthType = TableColumnWidthType.Percent,
                        WidthValue = Math.Round(w * 100.0 / totalWidthTwips, 2)
                    });
                }
            }
            else
            {
                for (int i = 0; i < columnCount; i++)
                    block.Columns.Add(new TableColumnDefinition { WidthType = TableColumnWidthType.Auto });
            }

            var tblBorders = table.GetFirstChild<W.TableProperties>()?.GetFirstChild<W.TableBorders>();

            // vMerge отслеживается по столбцам: для каждого столбца храним последнюю
            // "главную" ячейку вертикального объединения (или null, если объединения нет).
            var openVMerge = new TableCell?[columnCount];

            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                var cells = rows[rowIndex].Elements<W.TableCell>().ToList();
                int col = 0;

                foreach (var wCell in cells)
                {
                    var cellProps = wCell.TableCellProperties;
                    int gridSpan = cellProps?.GridSpan?.Val?.Value ?? 1;
                    if (gridSpan < 1) gridSpan = 1;

                    var vMerge = cellProps?.VerticalMerge;
                    bool isMergeContinuation = vMerge is not null &&
                        (vMerge.Val is null || vMerge.Val.Value == W.MergedCellValues.Continue);

                    if (isMergeContinuation && col < columnCount && openVMerge[col] is { } masterCell)
                    {
                        masterCell.RowSpan++;
                        col += gridSpan;
                        continue;
                    }

                    var paragraphs = new List<ParagraphBlock>();
                    foreach (var cellChild in wCell.ChildElements)
                    {
                        switch (cellChild)
                        {
                            case W.Paragraph cellParagraph:
                                paragraphs.Add(ImportCellParagraph(cellParagraph, section, resolver,
                                    numbering, listIdMap, mainPart, extractedImages, warnings));
                                break;

                            case W.Table nested:
                                warnings.Add("Вложенные таблицы не поддерживаются: их содержимое " +
                                             "перенесено в ячейку обычными абзацами.");
                                foreach (var nestedParagraph in nested.Descendants<W.Paragraph>())
                                    paragraphs.Add(ImportCellParagraph(nestedParagraph, section, resolver,
                                        numbering, listIdMap, mainPart, extractedImages, warnings));
                                break;
                        }
                    }
                    if (paragraphs.Count == 0) paragraphs.Add(new ParagraphBlock());

                    var newCell = new TableCell
                    {
                        Row = rowIndex,
                        Column = col,
                        RowSpan = 1,
                        ColSpan = gridSpan,
                        Paragraphs = paragraphs,
                        Borders = ResolveCellBorders(cellProps?.TableCellBorders, tblBorders),
                        BackgroundColor = NormalizeShadingColor(cellProps?.Shading)
                    };

                    // Значения перечислений OOXML в SDK — структуры, а не enum:
                    // в шаблонах switch они непригодны, сравниваем оператором равенства.
                    var vAlign = cellProps?.TableCellVerticalAlignment?.Val?.Value;
                    if (vAlign == W.TableVerticalAlignmentValues.Center)
                        newCell.VerticalAlignment = Models.Document.VerticalAlignment.Middle;
                    else if (vAlign == W.TableVerticalAlignmentValues.Bottom)
                        newCell.VerticalAlignment = Models.Document.VerticalAlignment.Bottom;
                    else
                        newCell.VerticalAlignment = Models.Document.VerticalAlignment.Top;

                    block.Cells.Add(newCell);

                    if (vMerge is not null && (vMerge.Val is null || vMerge.Val.Value == W.MergedCellValues.Restart))
                    {
                        if (col < columnCount) openVMerge[col] = newCell;
                    }
                    else if (col < columnCount)
                    {
                        openVMerge[col] = null;
                    }

                    col += gridSpan;
                }
            }

            return block;
        }

        /// <summary>
        /// Абзац внутри ячейки таблицы. Картинки регистрируются в
        /// SectionModel.InlineObjects документа целиком, как и обычные инлайн-картинки
        /// в тексте: ячейка хранит только ссылку на них через Guid рана, поэтому сюда
        /// передаётся секция документа, а не что-то специфичное для таблицы.
        /// </summary>
        private ParagraphBlock ImportCellParagraph(
            W.Paragraph p,
            SectionModel section,
            DocxFormatResolver resolver,
            DocxNumberingMap numbering,
            Dictionary<int, Guid> listIdMap,
            MainDocumentPart mainPart,
            Dictionary<string, byte[]> extractedImages,
            List<string> warnings)
        {
            var effPara = resolver.ResolveEffectiveParagraph(p);
            string? styleName = resolver.MapStyleName(p, effPara);
            var para = new ParagraphBlock { Properties = effPara.ToParagraphProperties(styleName) };
            para.ListProperties = numbering.Resolve(effPara, para.Properties, listIdMap);

            if (effPara.BetweenBorder is { } between)
                _betweenBorders[para] = between;

            var chunk = new TextChunk();
            para.Chunks.Clear();
            para.Chunks.Add(chunk);

            foreach (var child in p.ChildElements)
            {
                if (child is W.ParagraphProperties) continue;
                AppendRunOrDrawing(child, chunk, section, resolver, effPara,
                    mainPart, extractedImages, warnings);
            }

            if (chunk.Runs.Count == 0)
                chunk.Runs.Add(BuildParagraphMarkRun(p, resolver, effPara));

            chunk.InvalidateLength();
            return para;
        }

        private static CellBorders ResolveCellBorders(W.TableCellBorders? cellBorders, W.TableBorders? tableBorders)
        {
            var result = new CellBorders();
            result.Top = ResolveBorderStyle(cellBorders?.TopBorder ?? tableBorders?.TopBorder, out var topColor, out var topThickness);
            result.Bottom = ResolveBorderStyle(cellBorders?.BottomBorder ?? tableBorders?.BottomBorder, out var bottomColor, out var bottomThickness);
            result.Left = ResolveBorderStyle(cellBorders?.LeftBorder ?? tableBorders?.LeftBorder, out var leftColor, out var leftThickness);
            result.Right = ResolveBorderStyle(cellBorders?.RightBorder ?? tableBorders?.RightBorder, out var rightColor, out var rightThickness);
            result.Color = topColor ?? bottomColor ?? leftColor ?? rightColor;
            double thickness = new[] { topThickness, bottomThickness, leftThickness, rightThickness }
                .Where(v => v > 0).DefaultIfEmpty(0.5).Average();
            result.ThicknessPt = thickness;
            return result;
        }

        private static BorderStyle ResolveBorderStyle(W.BorderType? border, out string? color, out double thicknessPt)
        {
            color = null;
            thicknessPt = 0.5;
            if (border is null || border.Val is null) return BorderStyle.Single;

            color = NormalizeHexColor(border.Color?.Value);
            if (border.Size is not null)
                thicknessPt = border.Size.Value / EighthsPerPoint;

            var borderVal = border.Val.Value;

            if (borderVal == W.BorderValues.Nil || borderVal == W.BorderValues.None)
                return BorderStyle.None;

            if (borderVal == W.BorderValues.Double)
                return BorderStyle.Double;

            if (borderVal == W.BorderValues.Dashed || borderVal == W.BorderValues.DashDotStroked)
                return BorderStyle.Dashed;

            if (borderVal == W.BorderValues.Dotted)
                return BorderStyle.Dotted;

            if (borderVal == W.BorderValues.Thick
                || borderVal == W.BorderValues.ThickThinSmallGap
                || borderVal == W.BorderValues.ThinThickSmallGap)
                return BorderStyle.Thick;

            return BorderStyle.Single;
        }

        private static string? NormalizeShadingColor(W.Shading? shading)
        {
            if (shading?.Fill is null) return null;
            return NormalizeHexColor(shading.Fill.Value);
        }

        // ── Параметры страницы (последний раздел) ──────────────────────────

        /// <summary>
        /// Лист и поля, которые Word подставляет документу без описания раздела.
        /// Файл, сохранённый без w:sectPr, ничего о странице не говорит, и текстовый
        /// редактор обязан взять те же величины, иначе текст ляжет в зону другой
        /// высоты и разбивка на страницы разойдётся с исходником: лишние пять
        /// миллиметров сверху и снизу — это минус строка на каждом листе.
        /// </summary>
        private static void ApplyWordDefaultPageSettings(DocumentModel doc)
        {
            doc.PageSettings.ApplyPaperSize(Core.Models.Print.PaperSize.A4);
            doc.PageSettings.Orientation = Core.Models.Print.PageOrientation.Portrait;

            doc.PageSettings.MarginTopMm = 20;
            doc.PageSettings.MarginBottomMm = 20;
            doc.PageSettings.MarginLeftMm = 30;
            doc.PageSettings.MarginRightMm = 15;
            doc.PageSettings.MarginGutterMm = 0;

            doc.PageSettings.HeaderDistanceMm = 12.5;
            doc.PageSettings.FooterDistanceMm = 12.5;
        }

        private void ApplyFinalSectionPageSettings(
            W.Body body, DocumentModel doc, DocxFormatResolver resolver, List<string> warnings)
        {
            // Умолчания ставятся до разбора раздела, а не вместо него: всё, что файл
            // о странице говорит, ниже их перекрывает. Раздел может описывать лист,
            // но молчать о полях (или наоборот) — тогда недосказанное остаётся
            // вордовским, а не остаётся от прежнего документа во вкладке.
            ApplyWordDefaultPageSettings(doc);

            var sectPr = body.GetFirstChild<W.SectionProperties>();
            if (sectPr is null) return;

            var pageSize = sectPr.GetFirstChild<W.PageSize>();
            if (pageSize is not null)
            {
                double widthMm = (pageSize.Width?.Value ?? 11906) / TwipsPerMm;
                double heightMm = (pageSize.Height?.Value ?? 16838) / TwipsPerMm;
                bool landscape = pageSize.Orient?.Value == W.PageOrientationValues.Landscape;

                doc.PageSettings.PaperSize = Core.Models.Print.PaperSize.Custom;
                doc.PageSettings.WidthMm = Math.Round(landscape ? heightMm : widthMm, 1);
                doc.PageSettings.HeightMm = Math.Round(landscape ? widthMm : heightMm, 1);
                doc.PageSettings.Orientation = landscape
                    ? Core.Models.Print.PageOrientation.Landscape
                    : Core.Models.Print.PageOrientation.Portrait;
            }

            var margin = sectPr.GetFirstChild<W.PageMargin>();
            if (margin is not null)
            {
                if (margin.Top is not null) doc.PageSettings.MarginTopMm = Math.Round(margin.Top.Value / TwipsPerMm, 1);
                if (margin.Bottom is not null) doc.PageSettings.MarginBottomMm = Math.Round(margin.Bottom.Value / TwipsPerMm, 1);
                if (margin.Left is not null) doc.PageSettings.MarginLeftMm = Math.Round(margin.Left.Value / TwipsPerMm, 1);
                if (margin.Right is not null) doc.PageSettings.MarginRightMm = Math.Round(margin.Right.Value / TwipsPerMm, 1);
                if (margin.Gutter is not null) doc.PageSettings.MarginGutterMm = Math.Round(margin.Gutter.Value / TwipsPerMm, 1);
                if (margin.Header is not null) doc.PageSettings.HeaderDistanceMm = Math.Round(margin.Header.Value / TwipsPerMm, 1);
                if (margin.Footer is not null) doc.PageSettings.FooterDistanceMm = Math.Round(margin.Footer.Value / TwipsPerMm, 1);
            }

            var cols = sectPr.GetFirstChild<W.Columns>();
            int? colCount = cols?.ColumnCount?.Value;
            if (colCount is int cc && cc > 1)
            {
                doc.ColumnSettings.ColumnCount = cc;

                // Значение читается через InnerText: атрибут w:space в схеме — мера в twips,
                // и разные версии SDK типизируют его по-разному. Строковое представление
                // одинаково доступно у любого из вариантов.
                string? spaceRaw = cols?.Space?.InnerText;
                if (!string.IsNullOrEmpty(spaceRaw) &&
                    double.TryParse(spaceRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var spaceTwips))
                    doc.ColumnSettings.GapMm = Math.Round(spaceTwips / TwipsPerMm, 1);
            }

            if (sectPr.Elements<W.HeaderReference>().Any() || sectPr.Elements<W.FooterReference>().Any())
                warnings.Add("Колонтитулы (верхний/нижний) не поддерживаются и не были импортированы.");
        }

        private static long? ParseLong(string? s) =>
            long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

        internal static string? NormalizeHexColor(string? val)
        {
            if (string.IsNullOrWhiteSpace(val)) return null;
            if (string.Equals(val, "auto", StringComparison.OrdinalIgnoreCase)) return null;
            val = val.TrimStart('#');
            if (val.Length == 6 && Regex.IsMatch(val, "^[0-9A-Fa-f]{6}$"))
                return "#" + val.ToUpperInvariant();
            return null;
        }

        // ── Импорт plain text (.txt) — без изменений ────────────────────────

        /// <summary>
        /// Импортирует plain text (.txt) файл.
        /// Каждая строка становится отдельным параграфом.
        /// Форматирование не применяется — используется стиль Normal.
        /// </summary>
        public async Task<ImportResult> ImportFromTxtAsync(string filePath)
        {
            try
            {
                string[] lines = await System.IO.File.ReadAllLinesAsync(filePath);
                var doc = DocumentModel.CreateNew(System.IO.Path.GetFileNameWithoutExtension(filePath));
                var section = doc.Sections[0];
                section.Blocks.Clear();

                foreach (string line in lines)
                {
                    var para = new ParagraphBlock();
                    para.Properties.StyleName = "Normal";

                    var run = new Models.Inline.RunModel { Text = line };
                    para.Chunks[0].Runs.Add(run);
                    para.Chunks[0].InvalidateLength();

                    section.Blocks.Add(para);
                }

                // Минимум один параграф если файл пустой.
                if (section.Blocks.Count == 0)
                    section.Blocks.Add(new ParagraphBlock());

                return ImportResult.Ok(doc, new[] { "Plain text imported without formatting." });
            }
            catch (Exception ex)
            {
                return ImportResult.Fail(ex.Message);
            }
        }
    }

    // ── Разрешение эффективного форматирования Word (стили + прямое) ───────

    /// <summary>
    /// Накопленное символьное форматирование одного уровня каскада (docDefaults→
    /// стиль абзаца→символьный стиль→прямое форматирование рана). На каждом уровне
    /// заданные поля перекрывают предыдущие, незаданные (null) — наследуются.
    /// Читает форматирование из любого контейнера дочерних элементов OOXML
    /// (rPr рана, rPr стиля, rPr по умолчанию) через <see cref="OpenXmlCompositeElement.GetFirstChild{T}"/> —
    /// это разные типы в SDK, но с одинаковым по смыслу набором дочерних элементов.
    /// </summary>
    internal sealed class RunFormat
    {
        private const double HalfPointsPerPoint = 2.0;

        public string? FontFamily;
        public double? FontSizePt;
        public bool? Bold;
        public bool? Italic;
        public bool? Underline;
        public bool? Strike;
        public bool? Superscript;
        public bool? Subscript;
        public bool? AllCaps;
        public bool? SmallCaps;
        public string? TextColor;
        public string? HighlightColor;
        public string? Language;

        /// <summary>Межбуквенный интервал в пунктах (w:spacing в свойствах рана).</summary>
        public double? CharacterSpacingPt;

        public void MergeFrom(
            OpenXmlCompositeElement? container,
            string? themeMajorFont = null,
            string? themeMinorFont = null)
        {
            if (container is null) return;

            var runFonts = container.GetFirstChild<W.RunFonts>();
            if (runFonts is not null)
            {
                string? font = runFonts.Ascii?.Value
                    ?? runFonts.HighAnsi?.Value
                    ?? runFonts.ComplexScript?.Value;

                // Современные документы Word ссылаются не на имя шрифта, а на шрифт
                // темы (w:asciiTheme="minorHAnsi"). Без разбора этой ссылки шрифт
                // документа терялся целиком и текст рисовался шрифтом по умолчанию.
                if (string.IsNullOrEmpty(font))
                {
                    string? themeRef = runFonts.AsciiTheme?.InnerText
                        ?? runFonts.HighAnsiTheme?.InnerText;

                    if (!string.IsNullOrEmpty(themeRef))
                        font = themeRef.StartsWith("major", StringComparison.OrdinalIgnoreCase)
                            ? themeMajorFont
                            : themeMinorFont;
                }

                if (!string.IsNullOrEmpty(font)) FontFamily = font;
            }

            var fontSize = container.GetFirstChild<W.FontSize>();
            if (fontSize?.Val?.Value is string szStr &&
                double.TryParse(szStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var szVal))
                FontSizePt = szVal / HalfPointsPerPoint;

            if (container.GetFirstChild<W.Bold>() is { } b) Bold = b.Val is null || b.Val.Value;
            if (container.GetFirstChild<W.Italic>() is { } it) Italic = it.Val is null || it.Val.Value;

            if (container.GetFirstChild<W.Underline>() is { } u)
                Underline = u.Val is not null && u.Val.Value != W.UnderlineValues.None;

            if (container.GetFirstChild<W.Strike>() is { } s) Strike = s.Val is null || s.Val.Value;

            if (container.GetFirstChild<W.VerticalTextAlignment>() is { } va)
            {
                if (va.Val?.Value == W.VerticalPositionValues.Superscript) { Superscript = true; Subscript = false; }
                else if (va.Val?.Value == W.VerticalPositionValues.Subscript) { Subscript = true; Superscript = false; }
                else { Superscript = false; Subscript = false; }
            }

            if (container.GetFirstChild<W.Caps>() is { } c) AllCaps = c.Val is null || c.Val.Value;
            if (container.GetFirstChild<W.SmallCaps>() is { } sc) SmallCaps = sc.Val is null || sc.Val.Value;

            if (container.GetFirstChild<W.Color>()?.Val?.Value is string colorVal)
                TextColor = ImportService.NormalizeHexColor(colorVal);

            var highlight = container.GetFirstChild<W.Highlight>();
            if (highlight?.Val?.Value is { } hv)
                HighlightColor = HighlightToHex(hv);
            else if (container.GetFirstChild<W.Shading>()?.Fill?.Value is string shadeVal)
                HighlightColor = ImportService.NormalizeHexColor(shadeVal);

            if (container.GetFirstChild<W.Languages>()?.Val?.Value is string lang)
                Language = lang;

            // Разрядка знаков. Хранится в двадцатых долях пункта; ноль на нижнем уровне
            // каскада снимает разрядку, заданную стилем выше.
            if (container.GetFirstChild<W.Spacing>()?.Val?.Value is int spacingTwips)
                CharacterSpacingPt = spacingTwips / 20.0;
        }

        private static string? HighlightToHex(W.HighlightColorValues v)
        {
            if (v == W.HighlightColorValues.Yellow) return "#FFFF00";
            if (v == W.HighlightColorValues.Green) return "#00FF00";
            if (v == W.HighlightColorValues.Cyan) return "#00FFFF";
            if (v == W.HighlightColorValues.Magenta) return "#FF00FF";
            if (v == W.HighlightColorValues.Blue) return "#0000FF";
            if (v == W.HighlightColorValues.Red) return "#FF0000";
            if (v == W.HighlightColorValues.DarkBlue) return "#00008B";
            if (v == W.HighlightColorValues.DarkCyan) return "#008B8B";
            if (v == W.HighlightColorValues.DarkGreen) return "#006400";
            if (v == W.HighlightColorValues.DarkMagenta) return "#8B008B";
            if (v == W.HighlightColorValues.DarkRed) return "#8B0000";
            if (v == W.HighlightColorValues.DarkYellow) return "#808000";
            if (v == W.HighlightColorValues.DarkGray) return "#A9A9A9";
            if (v == W.HighlightColorValues.LightGray) return "#D3D3D3";
            if (v == W.HighlightColorValues.Black) return "#000000";
            return null;
        }

        public RunFormat Clone() => (RunFormat)MemberwiseClone();

        public RunProperties ToRunProperties() => new()
        {
            FontFamily = FontFamily,
            FontSize = FontSizePt,
            IsBold = Bold == true,
            IsItalic = Italic == true,
            IsUnderline = Underline == true,
            IsStrikethrough = Strike == true,
            IsSuperscript = Superscript == true,
            IsSubscript = Subscript == true,
            IsAllCaps = AllCaps == true,
            IsSmallCaps = SmallCaps == true,
            CharacterSpacing = CharacterSpacingPt is double spacing && Math.Abs(spacing) > 0.001
                ? spacing
                : null,
            TextColor = TextColor,
            HighlightColor = HighlightColor,
            Language = Language
        };
    }

    /// <summary>
    /// Накопленное форматирование абзаца одного уровня каскада, вместе с базовым
    /// символьным форматированием абзаца (<see cref="BaseRun"/>) — им пользуются
    /// раны, у которых нет собственного прямого форматирования и символьного стиля.
    /// </summary>
    internal sealed class ParaFormat
    {
        private const double TwipsPerPoint = 20.0;

        public W.JustificationValues? Justification;
        public double? LeftIndentPt, RightIndentPt, FirstLineIndentPt;
        public double? SpaceBeforePt, SpaceAfterPt;
        public LineSpacingRule? LineRule;
        public double? LineValue;
        public bool? KeepTogether, KeepWithNext, PageBreakBefore;
        public int? OutlineLevel;

        /// <summary>
        /// Линии рамки абзаца по сторонам. null — уровень каскада о стороне молчит,
        /// берём от предка. Линия с видом None — сторона явно снята: абзац отказался
        /// от линии, которую дал ему стиль.
        /// </summary>
        public Models.Styles.ParagraphBorderLine? BorderTop, BorderBottom, BorderLeft, BorderRight;

        /// <summary>
        /// Линия между абзацами одной рамки (w:between). В модели её нет: Word рисует
        /// её у каждого абзаца группы, кроме первого, и импорт ставит её этим абзацам
        /// верхней линией.
        /// </summary>
        public Models.Styles.ParagraphBorderLine? BorderBetween;

        /// <summary>
        /// Заливка абзаца (w:shd в свойствах абзаца). null — уровень каскада о ней
        /// молчит; пустая строка — заливка явно снята (fill="auto", val="nil").
        /// </summary>
        public string? Shading;

        /// <summary>
        /// Нумерация абзаца (w:numPr): список и его уровень. Задаётся и самим абзацем,
        /// и его стилем — «Нумерованный список» Word держит w:numPr именно в стиле, и
        /// его абзацы своего w:numPr не несут. null — уровень каскада молчит; номер
        /// списка 0 — нумерация явно снята.
        /// </summary>
        public int? NumberingId;
        public int? NumberingLevel;

        /// <summary>
        /// Позиции табуляции абзаца. null — уровень каскада о них молчит, берём от предка.
        ///
        /// Список замещается целиком, а не сливается по позициям: в Word свой w:tabs у
        /// абзаца отменяет наследованные, и слияние дало бы строку оглавления с двумя
        /// правыми позициями — своей и стилевой.
        /// </summary>
        public List<Models.Styles.TabStop>? TabStops;

        public RunFormat BaseRun = new();

        public void MergeFrom(OpenXmlCompositeElement? container)
        {
            if (container is null) return;

            if (container.GetFirstChild<W.Justification>()?.Val?.Value is { } j) Justification = j;

            if (container.GetFirstChild<W.Indentation>() is { } ind)
            {
                if (ind.Left?.Value is string l && TryTwips(l, out var lv)) LeftIndentPt = lv;
                else if (ind.Start?.Value is string ls && TryTwips(ls, out var lsv)) LeftIndentPt = lsv;

                if (ind.Right?.Value is string r && TryTwips(r, out var rv)) RightIndentPt = rv;
                else if (ind.End?.Value is string re && TryTwips(re, out var rev)) RightIndentPt = rev;

                if (ind.Hanging?.Value is string hg && TryTwips(hg, out var hgv)) FirstLineIndentPt = -hgv;
                else if (ind.FirstLine?.Value is string fl && TryTwips(fl, out var flv)) FirstLineIndentPt = flv;
            }

            if (container.GetFirstChild<W.SpacingBetweenLines>() is { } sp)
            {
                if (sp.Before?.Value is string b && TryTwips(b, out var bv)) SpaceBeforePt = bv;
                if (sp.After?.Value is string a && TryTwips(a, out var av)) SpaceAfterPt = av;

                if (sp.Line?.Value is string ln &&
                    double.TryParse(ln, NumberStyles.Float, CultureInfo.InvariantCulture, out var lnv))
                {
                    var rule = sp.LineRule?.Value;
                    if (rule is null || rule == W.LineSpacingRuleValues.Auto)
                    {
                        // Значение в 240-х долях строки: 240 = одинарный, 360 = полуторный.
                        LineRule = Models.Styles.LineSpacingRule.Auto;
                        LineValue = lnv / 240.0;
                    }
                    else
                    {
                        LineRule = rule == W.LineSpacingRuleValues.Exact
                            ? Models.Styles.LineSpacingRule.Exact
                            : Models.Styles.LineSpacingRule.AtLeast;
                        LineValue = lnv / TwipsPerPoint;
                    }
                }
            }

            if (container.GetFirstChild<W.KeepNext>() is not null) KeepWithNext = true;
            if (container.GetFirstChild<W.KeepLines>() is not null) KeepTogether = true;
            if (container.GetFirstChild<W.PageBreakBefore>() is not null) PageBreakBefore = true;

            if (container.GetFirstChild<W.OutlineLevel>()?.Val?.Value is int ol) OutlineLevel = ol;

            MergeTabs(container.GetFirstChild<W.Tabs>());
            MergeBorders(container.GetFirstChild<W.ParagraphBorders>());

            if (container.GetFirstChild<W.Shading>() is { } shading)
                Shading = ReadParagraphShading(shading);

            // Уровень без номера списка — тоже довод: абзац стиля со списком может
            // переставить себе только уровень.
            if (container.GetFirstChild<W.NumberingProperties>() is { } numPr)
            {
                if (numPr.NumberingId?.Val?.Value is int numId) NumberingId = numId;
                if (numPr.NumberingLevelReference?.Val?.Value is int numLevel) NumberingLevel = numLevel;
            }
        }

        /// <summary>
        /// Цвет заливки абзаца. При сплошном узоре (val="solid") Word красит цветом
        /// узора, при прочих — цветом фона (fill). «auto» и nil — заливки нет: пустая
        /// строка перекрывает заливку, данную стилем.
        /// </summary>
        private static string ReadParagraphShading(W.Shading shading)
        {
            var pattern = shading.Val?.Value;
            if (pattern == W.ShadingPatternValues.Nil) return string.Empty;

            string? raw = pattern == W.ShadingPatternValues.Solid
                ? shading.Color?.Value
                : shading.Fill?.Value;

            return ImportService.NormalizeHexColor(raw) ?? string.Empty;
        }

        /// <summary>
        /// Читает рамку абзаца (w:pBdr). Стороны сливаются поштучно: абзац может снять
        /// одну линию стиля и оставить остальные. Левая и правая бывают записаны и как
        /// start/end — так пишут файлы строгой схемы.
        /// </summary>
        private void MergeBorders(W.ParagraphBorders? borders)
        {
            if (borders is null) return;

            if (ReadBorderLine(borders.GetFirstChild<W.TopBorder>()) is { } top) BorderTop = top;
            if (ReadBorderLine(borders.GetFirstChild<W.BottomBorder>()) is { } bottom) BorderBottom = bottom;

            if (ReadBorderLine(borders.GetFirstChild<W.LeftBorder>()) is { } left) BorderLeft = left;
            else if (ReadBorderLine(borders.GetFirstChild<W.StartBorder>()) is { } start) BorderLeft = start;

            if (ReadBorderLine(borders.GetFirstChild<W.RightBorder>()) is { } right) BorderRight = right;
            else if (ReadBorderLine(borders.GetFirstChild<W.EndBorder>()) is { } end) BorderRight = end;

            if (ReadBorderLine(borders.GetFirstChild<W.BetweenBorder>()) is { } between) BorderBetween = between;
        }

        /// <summary>
        /// Одна линия рамки. null — элемента нет. Снятая линия (nil, none) приходит
        /// видом None: она перекрывает линию, унаследованную от стиля.
        ///
        /// Толщина у Word — в восьмых пункта, зазор — в пунктах. «auto» в цвете значит
        /// цвет текста — в модели это пустой цвет.
        /// </summary>
        private static Models.Styles.ParagraphBorderLine? ReadBorderLine(W.BorderType? border)
        {
            if (border is null) return null;

            var line = new Models.Styles.ParagraphBorderLine
            {
                Style = MapParagraphBorderStyle(border.Val?.Value),
                WidthPt = border.Size?.Value is uint eighths ? eighths / 8.0 : 0.5,
                SpacePt = border.Space?.Value is uint space ? space : 0.0
            };

            string? color = border.Color?.Value;
            if (!string.IsNullOrWhiteSpace(color)
                && !string.Equals(color, "auto", StringComparison.OrdinalIgnoreCase))
            {
                string hex = color!.TrimStart('#');
                if (hex.Length == 6) line.Color = "#" + hex.ToUpperInvariant();
            }

            // Word не рисует линию тоньше четверти пункта — так и здесь.
            if (line.WidthPt < 0.25) line.WidthPt = 0.25;

            return line;
        }

        private static BorderStyle MapParagraphBorderStyle(W.BorderValues? value)
        {
            if (value is null) return BorderStyle.None;

            var v = value.Value;

            if (v == W.BorderValues.Nil || v == W.BorderValues.None) return BorderStyle.None;
            if (v == W.BorderValues.Double) return BorderStyle.Double;

            if (v == W.BorderValues.Dashed || v == W.BorderValues.DashSmallGap
                || v == W.BorderValues.DotDash || v == W.BorderValues.DotDotDash
                || v == W.BorderValues.DashDotStroked)
                return BorderStyle.Dashed;

            if (v == W.BorderValues.Dotted) return BorderStyle.Dotted;

            if (v == W.BorderValues.Thick
                || v == W.BorderValues.ThickThinSmallGap
                || v == W.BorderValues.ThinThickSmallGap)
                return BorderStyle.Thick;

            return BorderStyle.Single;
        }

        /// <summary>
        /// Читает позиции табуляции абзаца.
        ///
        /// Без них строка оглавления, приехавшая из Word, разваливается: там номер
        /// страницы прижат к правому полю правой позицией с точечным заполнителем, а
        /// голый символ табуляции без позиции уходит к ближайшей отметке шага по
        /// умолчанию и прилипает к названию главы.
        ///
        /// Позиция w:val="clear" снимает унаследованную отметку и своей не ставит —
        /// в список она не идёт.
        /// </summary>
        private void MergeTabs(W.Tabs? tabs)
        {
            if (tabs is null) return;

            var result = new List<Models.Styles.TabStop>();

            foreach (var tab in tabs.Elements<W.TabStop>())
            {
                var kind = tab.Val?.Value;
                if (kind == W.TabStopValues.Clear) continue;

                if (tab.Position?.Value is not int twips) continue;

                // Word умеет ставить позиции левее нуля (выносы на поле). В модели
                // отсчёт идёт от левого края текста абзаца, и отрицательная отметка
                // означала бы прыжок назад — такие пропускаем.
                double positionPt = twips / TwipsPerPoint;
                if (positionPt < 0) continue;

                result.Add(new Models.Styles.TabStop
                {
                    PositionPt = positionPt,
                    Alignment = MapTabAlignment(kind),
                    Leader = MapTabLeader(tab.Leader?.Value)
                });
            }

            // Пустой w:tabs (одни только clear) — это тоже сказанное слово: абзац
            // отказался от наследованных позиций. Отдаём пустой список, а не null.
            TabStops = result;
        }

        private static Models.Styles.TabAlignment MapTabAlignment(W.TabStopValues? kind)
        {
            if (kind == W.TabStopValues.Right) return Models.Styles.TabAlignment.Right;
            if (kind == W.TabStopValues.Center) return Models.Styles.TabAlignment.Center;
            if (kind == W.TabStopValues.Decimal) return Models.Styles.TabAlignment.Decimal;

            // Bar — вертикальная черта на позиции, а не прыжок текста. Рисовать её
            // нечем, но и терять позицию нельзя: ведёт себя как обычная левая.
            return Models.Styles.TabAlignment.Left;
        }

        private static Models.Styles.TabLeaderStyle MapTabLeader(W.TabStopLeaderCharValues? leader)
        {
            if (leader == W.TabStopLeaderCharValues.Dot) return Models.Styles.TabLeaderStyle.Dots;
            if (leader == W.TabStopLeaderCharValues.Hyphen) return Models.Styles.TabLeaderStyle.Dashes;

            // Underscore и heavy — обе сплошные линии, толщину линии модель не различает.
            if (leader == W.TabStopLeaderCharValues.Underscore
                || leader == W.TabStopLeaderCharValues.Heavy)
                return Models.Styles.TabLeaderStyle.Line;

            // MiddleDot в модели отдельного вида не имеет — ближе всего точки.
            if (leader == W.TabStopLeaderCharValues.MiddleDot)
                return Models.Styles.TabLeaderStyle.Dots;

            return Models.Styles.TabLeaderStyle.None;
        }

        private static List<Models.Styles.TabStop>? CloneTabs(List<Models.Styles.TabStop>? source)
        {
            if (source is null) return null;

            var copy = new List<Models.Styles.TabStop>(source.Count);
            foreach (var tab in source) copy.Add(tab.Clone());
            return copy;
        }

        private static bool TryTwips(string s, out double pt)
        {
            pt = 0;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var twips)) return false;
            pt = twips / TwipsPerPoint;
            return true;
        }

        /// <summary>
        /// Глубокая копия: базовое символьное форматирование копируется тоже, как и
        /// позиции табуляции — иначе уровень каскада правил бы список своего предка.
        /// </summary>
        public ParaFormat Clone()
        {
            var copy = (ParaFormat)MemberwiseClone();
            copy.BaseRun = BaseRun.Clone();
            copy.TabStops = CloneTabs(TabStops);
            copy.BorderTop = BorderTop?.Clone();
            copy.BorderBottom = BorderBottom?.Clone();
            copy.BorderLeft = BorderLeft?.Clone();
            copy.BorderRight = BorderRight?.Clone();
            copy.BorderBetween = BorderBetween?.Clone();
            return copy;
        }

        public Models.Styles.ParagraphProperties ToParagraphProperties(string? styleName)
        {
            Models.Styles.TextAlignment? alignment = null;

            if (Justification == W.JustificationValues.Center) alignment = Models.Styles.TextAlignment.Center;
            else if (Justification == W.JustificationValues.Right) alignment = Models.Styles.TextAlignment.Right;
            else if (Justification == W.JustificationValues.Both) alignment = Models.Styles.TextAlignment.Justify;
            else if (Justification == W.JustificationValues.Left) alignment = Models.Styles.TextAlignment.Left;

            // Интервалы записываются явными числами, даже когда файл о них молчит.
            // Незаполненное свойство означало бы «взять из стиля Writersword», а его
            // «Обычный» добавляет 8 пунктов после абзаца — Word в таком документе не
            // добавляет ничего, и на листе терялась целая строка. По стандарту
            // отсутствие w:spacing — это нулевые интервалы и одинарная строка.
            return new Models.Styles.ParagraphProperties
            {
                StyleName = styleName,
                Alignment = alignment,
                LeftIndent = LeftIndentPt ?? 0,
                RightIndent = RightIndentPt ?? 0,
                FirstLineIndent = FirstLineIndentPt ?? 0,
                SpaceBefore = SpaceBeforePt ?? 0,
                SpaceAfter = SpaceAfterPt ?? 0,
                LineSpacingRule = LineRule ?? Models.Styles.LineSpacingRule.Auto,
                LineSpacingValue = LineValue ?? 1.0,
                KeepTogether = KeepTogether ?? false,
                KeepWithNext = KeepWithNext ?? false,
                PageBreakBefore = PageBreakBefore ?? false,
                // Word считает уровни от нуля: outlineLvl=0 у «Заголовка 1». В модели
                // Writersword ноль означает обычный текст, а главы идут с единицы, и
                // экспорт вычитает единицу обратно. Без сдвига круг docx → рукопись →
                // docx поднимал бы каждый заголовок на уровень вверх.
                //
                // Девятка у Word — не десятый уровень, а пометка «основной текст»; такой
                // абзац заголовком не становится.
                OutlineLevel = OutlineLevel is int lvl && lvl >= 0 && lvl <= 8 ? lvl + 1 : 0,

                // Позиции копируются, а не отдаются ссылкой: каскад держит свой список
                // и переиспользует его для следующих абзацев того же стиля.
                TabStops = CloneTabs(TabStops),

                // Рамка — только из видимых сторон. Снятые стороны своё дело сделали
                // при слиянии каскада, в модели им делать нечего.
                Borders = BuildBorders(),

                // Заливка, снятая на нижнем уровне каскада, в модель не идёт вовсе.
                ShadingColor = string.IsNullOrEmpty(Shading) ? null : Shading
            };
        }

        public RunProperties ToRunProperties() => BaseRun.ToRunProperties();

        /// <summary>Рамка абзаца для модели. null — ни одной видимой линии.</summary>
        private Models.Styles.ParagraphBorders? BuildBorders()
        {
            static Models.Styles.ParagraphBorderLine? Visible(Models.Styles.ParagraphBorderLine? line)
                => line is { IsVisible: true } ? line.Clone() : null;

            var borders = new Models.Styles.ParagraphBorders
            {
                Top = Visible(BorderTop),
                Bottom = Visible(BorderBottom),
                Left = Visible(BorderLeft),
                Right = Visible(BorderRight)
            };

            return borders.IsEmpty ? null : borders;
        }

        /// <summary>Линия между абзацами одной рамки, если она видна.</summary>
        public Models.Styles.ParagraphBorderLine? VisibleBetweenBorder
            => BorderBetween is { IsVisible: true } ? BorderBetween.Clone() : null;
    }

    /// <summary>Результат разрешения каскада форматирования для одного параграфа Word.</summary>
    internal readonly struct EffectiveParagraph
    {
        public EffectiveParagraph(ParaFormat format, string? styleId)
        {
            Format = format;
            StyleId = styleId;
        }

        public ParaFormat Format { get; }

        /// <summary>Идентификатор стиля абзаца в файле Word (w:pStyle или стиль по умолчанию).</summary>
        public string? StyleId { get; }
        public int? OutlineLevel => Format.OutlineLevel;
        public RunFormat BaseRun => Format.BaseRun;
        public Models.Styles.ParagraphBorderLine? BetweenBorder => Format.VisibleBetweenBorder;
        public Models.Styles.ParagraphProperties ToParagraphProperties(string? styleName) => Format.ToParagraphProperties(styleName);
        public RunProperties ToRunProperties() => Format.ToRunProperties();
    }

    /// <summary>
    /// Разрешает эффективное форматирование параграфов и ранов Word по цепочке
    /// стилей (w:basedOn) и прямому форматированию — без учёта DocDefaults styles.xml
    /// (в подавляющем большинстве документов стиль "Normal" полностью задаёт базовое
    /// форматирование сам по себе, поэтому этим можно осознанно пренебречь).
    /// Также определяет соответствие стиля абзаца именованным стилям Writersword
    /// (Heading1–6, Quote, Code, Normal) — в первую очередь по w:outlineLvl,
    /// как наиболее надёжному признаку заголовка независимо от локализации Word.
    /// </summary>
    internal sealed class DocxFormatResolver
    {
        private readonly Dictionary<string, W.Style> _stylesById = new(StringComparer.OrdinalIgnoreCase);
        private readonly string? _defaultParagraphStyleId;
        private readonly ParaFormat _documentDefaults = new();
        private readonly string? _themeMajorFont;
        private readonly string? _themeMinorFont;

        /// <summary>Шрифт темы для заголовков: на него ссылается w:asciiTheme="majorHAnsi".</summary>
        public string? ThemeMajorFont => _themeMajorFont;

        /// <summary>Шрифт темы для основного текста: w:asciiTheme="minorHAnsi".</summary>
        public string? ThemeMinorFont => _themeMinorFont;

        public DocxFormatResolver(DocumentFormat.OpenXml.Packaging.MainDocumentPart mainPart)
        {
            // Шрифты темы: на них ссылается w:rFonts у большинства документов Word.
            var fontScheme = mainPart.ThemePart?.Theme?.ThemeElements?.FontScheme;
            _themeMajorFont = fontScheme?.MajorFont?.GetFirstChild<Dr.LatinFont>()?.Typeface?.Value;
            _themeMinorFont = fontScheme?.MinorFont?.GetFirstChild<Dr.LatinFont>()?.Typeface?.Value;

            var styles = mainPart.StyleDefinitionsPart?.Styles;
            if (styles is null) return;

            // docDefaults — основание всего каскада: шрифт и кегль документа обычно
            // заданы именно там, а стиль "Normal" их только дополняет.
            var docDefaults = styles.DocDefaults;
            _documentDefaults.MergeFrom(docDefaults?.ParagraphPropertiesDefault?.ParagraphPropertiesBaseStyle);
            _documentDefaults.BaseRun.MergeFrom(
                docDefaults?.RunPropertiesDefault?.RunPropertiesBaseStyle,
                _themeMajorFont, _themeMinorFont);

            foreach (var style in styles.Elements<W.Style>())
            {
                if (style.StyleId?.Value is string id)
                    _stylesById[id] = style;
                if (style.Type?.Value == W.StyleValues.Paragraph && style.Default?.Value == true)
                    _defaultParagraphStyleId = style.StyleId?.Value;
            }
        }

        public EffectiveParagraph ResolveEffectiveParagraph(W.Paragraph p)
        {
            string styleId = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value
                ?? _defaultParagraphStyleId
                ?? "Normal";

            var format = _documentDefaults.Clone();

            // "Normal" — фактическая база документа даже если явно не входит в цепочку BasedOn.
            if (!string.Equals(styleId, "Normal", StringComparison.OrdinalIgnoreCase))
                MergeParagraphStyle(format, "Normal");

            foreach (var id in BuildStyleChain(styleId))
                MergeParagraphStyle(format, id);

            format.MergeFrom(p.ParagraphProperties);
            return new EffectiveParagraph(format, styleId);
        }

        public RunFormat ResolveEffectiveRun(W.Run run, EffectiveParagraph effPara)
        {
            var format = effPara.BaseRun.Clone();

            string? rStyleId = run.RunProperties?.GetFirstChild<W.RunStyle>()?.Val?.Value;
            if (rStyleId is not null)
            {
                foreach (var id in BuildStyleChain(rStyleId))
                    if (_stylesById.TryGetValue(id, out var style))
                        format.MergeFrom(
                            style.GetFirstChild<W.StyleRunProperties>(), _themeMajorFont, _themeMinorFont);
            }

            format.MergeFrom(run.RunProperties, _themeMajorFont, _themeMinorFont);
            return format;
        }

        /// <summary>
        /// Определяет соответствующий встроенный стиль Writersword. w:outlineLvl
        /// (0-based, 0…5 → Heading1…Heading6) — основной признак: Word проставляет
        /// его на стиль заголовка независимо от локализации интерфейса.
        /// </summary>
        public string? MapStyleName(W.Paragraph p, EffectiveParagraph eff)
        {
            if (eff.OutlineLevel is int ol && ol is >= 0 and <= 5)
                return $"Heading{ol + 1}";

            string? id = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
            string? name = id is not null && _stylesById.TryGetValue(id, out var st)
                ? st.StyleName?.Val?.Value
                : null;

            string probe = name ?? id ?? string.Empty;

            var headingMatch = Regex.Match(probe, @"(?:heading|заголовок)\s*([1-9])", RegexOptions.IgnoreCase);
            if (headingMatch.Success
                && int.TryParse(headingMatch.Groups[1].Value, out int lvl)
                && lvl is >= 1 and <= 6)
                return $"Heading{lvl}";

            if (Regex.IsMatch(probe, "quote|цитата", RegexOptions.IgnoreCase)) return "Quote";
            if (Regex.IsMatch(probe, @"^(code|source ?code|код)$", RegexOptions.IgnoreCase)) return "Code";

            // Моноширинный шрифт на весь абзац без явного стиля кода — тоже похоже на код.
            if (eff.BaseRun.FontFamily is { } font &&
                (font.Contains("Consolas", StringComparison.OrdinalIgnoreCase) ||
                 font.Contains("Courier", StringComparison.OrdinalIgnoreCase)))
                return "Code";

            return "Normal";
        }

        /// <summary>
        /// Уровень строки оглавления по стилю абзаца: «toc 1» … «toc 9» у Word (имя
        /// встроенного стиля английское при любом языке интерфейса), «Contents 1» у
        /// LibreOffice, «Оглавление 1» у переименованных вручную. 0 — стиль не строка
        /// оглавления.
        /// </summary>
        public int WordTocLevel(W.Paragraph p)
        {
            string? id = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
            if (id is null) return 0;

            string? name = _stylesById.TryGetValue(id, out var st) ? st.StyleName?.Val?.Value : null;

            foreach (var probe in new[] { name, id })
            {
                if (string.IsNullOrEmpty(probe)) continue;

                var match = Regex.Match(probe!,
                    @"^\s*(?:toc|contents|оглавление|содержание)\s*([1-9])\s*$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

                if (match.Success) return match.Groups[1].Value[0] - '0';
            }

            return 0;
        }

        private void MergeParagraphStyle(ParaFormat format, string styleId)
        {
            if (!_stylesById.TryGetValue(styleId, out var style)) return;
            format.MergeFrom(style.GetFirstChild<W.StyleParagraphProperties>());
            format.BaseRun.MergeFrom(
                style.GetFirstChild<W.StyleRunProperties>(), _themeMajorFont, _themeMinorFont);
        }

        /// <summary>Цепочка стилей от корня (без BasedOn) к запрошенному, по ссылкам w:basedOn.</summary>
        private List<string> BuildStyleChain(string styleId)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var chain = new List<string>();
            string? current = styleId;

            while (current is not null && seen.Add(current) && _stylesById.TryGetValue(current, out var style))
            {
                chain.Add(current);
                current = style.BasedOn?.Val?.Value;
            }

            chain.Reverse();
            return chain;
        }
    }

    /// <summary>
    /// Разрешает нумерацию Word (numbering.xml: abstractNum + num) в
    /// <see cref="ListProperties"/> Writersword. Переопределения на уровне
    /// конкретного w:num (w:lvlOverride) не поддерживаются — редкий случай,
    /// достаточно базового сопоставления abstractNum → уровни.
    /// </summary>
    internal sealed class DocxNumberingMap
    {
        private sealed class LevelDef
        {
            public ListMarkerType MarkerType;
            public string? CustomMarker;
            public string? NumberPrefix;
            public string? NumberSuffix;
            public int StartAt = 1;

            /// <summary>Отступ текста уровня (w:lvl/w:pPr/w:ind left), pt. null — уровень не задаёт.</summary>
            public double? LeftIndentPt;

            /// <summary>
            /// Отступ первой строки уровня, pt: отрицательный — выступ (w:hanging), под
            /// которым стоит номер. null — уровень не задаёт.
            /// </summary>
            public double? FirstLineIndentPt;

            /// <summary>Стиль, связанный с уровнем (w:lvl/w:pStyle).</summary>
            public string? LinkedStyleId;
        }

        private readonly Dictionary<int, Dictionary<int, LevelDef>> _byNumId = new();

        public DocxNumberingMap(DocumentFormat.OpenXml.Packaging.MainDocumentPart mainPart)
        {
            var numbering = mainPart.NumberingDefinitionsPart?.Numbering;
            if (numbering is null) return;

            var abstractById = new Dictionary<int, W.AbstractNum>();
            foreach (var a in numbering.Elements<W.AbstractNum>())
                if (a.AbstractNumberId?.Value is int aid)
                    abstractById[aid] = a;

            foreach (var inst in numbering.Elements<W.NumberingInstance>())
            {
                int? numId = inst.NumberID?.Value;
                int? abstractId = inst.AbstractNumId?.Val?.Value;
                if (numId is not int nid || abstractId is not int aid || !abstractById.TryGetValue(aid, out var abs))
                    continue;

                var levels = new Dictionary<int, LevelDef>();
                foreach (var level in abs.Elements<W.Level>())
                {
                    int ilvl = level.LevelIndex?.Value ?? 0;
                    var fmt = level.NumberingFormat?.Val?.Value ?? W.NumberFormatValues.Bullet;
                    string lvlText = level.LevelText?.Val?.Value ?? string.Empty;

                    var def = new LevelDef { StartAt = level.StartNumberingValue?.Val?.Value ?? 1 };
                    MapFormat(fmt, lvlText, def);
                    ReadLevelIndentation(level, def);
                    def.LinkedStyleId = level.ParagraphStyleIdInLevel?.Val?.Value;
                    levels[ilvl] = def;
                }
                _byNumId[nid] = levels;
            }
        }

        private static void MapFormat(W.NumberFormatValues fmt, string lvlText, LevelDef def)
        {
            if (fmt == W.NumberFormatValues.Decimal) def.MarkerType = ListMarkerType.Decimal;
            else if (fmt == W.NumberFormatValues.DecimalZero) def.MarkerType = ListMarkerType.DecimalLeadingZero;
            else if (fmt == W.NumberFormatValues.LowerLetter) def.MarkerType = ListMarkerType.LowerAlpha;
            else if (fmt == W.NumberFormatValues.UpperLetter) def.MarkerType = ListMarkerType.UpperAlpha;
            else if (fmt == W.NumberFormatValues.LowerRoman) def.MarkerType = ListMarkerType.LowerRoman;
            else if (fmt == W.NumberFormatValues.UpperRoman) def.MarkerType = ListMarkerType.UpperRoman;
            else if (fmt == W.NumberFormatValues.Bullet) def.MarkerType = MapBulletChar(lvlText, out def.CustomMarker);
            else def.MarkerType = ListMarkerType.Decimal;

            bool isCounted = def.MarkerType is ListMarkerType.Decimal or ListMarkerType.DecimalLeadingZero
                or ListMarkerType.LowerAlpha or ListMarkerType.UpperAlpha
                or ListMarkerType.LowerRoman or ListMarkerType.UpperRoman;
            if (!isCounted) return;

            var m = Regex.Match(lvlText, @"^(?<prefix>[^%]*)%\d+(?<suffix>.*)$");
            if (m.Success)
            {
                def.NumberPrefix = m.Groups["prefix"].Value.Length > 0 ? m.Groups["prefix"].Value : null;
                def.NumberSuffix = m.Groups["suffix"].Value.Length > 0 ? m.Groups["suffix"].Value : ".";
            }
            else
            {
                def.NumberSuffix = ".";
            }
        }

        private static ListMarkerType MapBulletChar(string ch, out string? custom)
        {
            custom = null;
            if (ch.Length == 0) return ListMarkerType.Bullet;

            char c = ch[0];
            switch (c)
            {
                case '\u2022': case '\uF0B7': case '\u25CF': return ListMarkerType.Bullet;
                case '-': case '\u2013': case '\u2014': return ListMarkerType.Dash;
                case '\u25AA': case '\u25A0': return ListMarkerType.Square;
                case '\u25CB': case 'o': case 'O': return ListMarkerType.Circle;
                default:
                    custom = ch;
                    return ListMarkerType.Custom;
            }
        }

        /// <summary>
        /// Отступы уровня из его w:pPr. Их Word берёт, когда ни стиль, ни сам абзац
        /// отступа не задают: номер встаёт на выступ, текст — на отступ уровня.
        /// </summary>
        private static void ReadLevelIndentation(W.Level level, LevelDef def)
        {
            var ind = level.PreviousParagraphProperties?.GetFirstChild<W.Indentation>();
            if (ind is null) return;

            static double? Twips(string? value)
                => value is not null
                   && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var twips)
                    ? twips / 20.0
                    : null;

            def.LeftIndentPt = Twips(ind.Left?.Value) ?? Twips(ind.Start?.Value);

            if (Twips(ind.Hanging?.Value) is double hanging) def.FirstLineIndentPt = -hanging;
            else if (Twips(ind.FirstLine?.Value) is double firstLine) def.FirstLineIndentPt = firstLine;
        }

        /// <summary>
        /// Свойства списка для параграфа по действующей нумерации — своей или стиля;
        /// null — абзац не в списке. listIdMap переиспользуется на весь импорт документа —
        /// параграфы с одинаковым numId получают один и тот же Guid ListId (единый список).
        ///
        /// Отступы уровня ставятся абзацу, если каскад стилей и сам абзац их не задали:
        /// по стандарту нумерация стоит в каскаде ниже стиля абзаца и прямого
        /// форматирования. Номер встаёт на выступ — туда, где его ставит Word.
        /// </summary>
        public ListProperties? Resolve(
            EffectiveParagraph effPara, Models.Styles.ParagraphProperties props, Dictionary<int, Guid> listIdMap)
        {
            var format = effPara.Format;
            if (format.NumberingId is not int nid || nid == 0) return null;
            if (!_byNumId.TryGetValue(nid, out var levels)) return null;

            // Уровень: свой у абзаца или стиля, иначе тот, что связан со стилем абзаца
            // в определении списка, иначе первый.
            int ilvl = format.NumberingLevel ?? LinkedLevel(levels, effPara.StyleId) ?? 0;

            if (!levels.TryGetValue(ilvl, out var def))
                def = levels.Values.FirstOrDefault() ?? new LevelDef();

            if (format.LeftIndentPt is null && def.LeftIndentPt is double levelLeft)
                props.LeftIndent = levelLeft;

            if (format.FirstLineIndentPt is null && def.FirstLineIndentPt is double levelFirst)
                props.FirstLineIndent = levelFirst;

            // Номер — на выступе первой строки: отступ текста минус выступ. Без выступа
            // позицию считает отрисовка по своему правилу.
            double? markerIndent = null;
            double firstLine = props.FirstLineIndent ?? 0.0;
            if (firstLine < 0.0)
                markerIndent = Math.Max(0.0, (props.LeftIndent ?? 0.0) + firstLine);

            if (!listIdMap.TryGetValue(nid, out var listGuid))
            {
                listGuid = Guid.NewGuid();
                listIdMap[nid] = listGuid;
            }

            return new ListProperties
            {
                ListId = listGuid,
                Level = Math.Clamp(ilvl, 0, 8),
                MarkerType = def.MarkerType,
                CustomMarker = def.CustomMarker,
                NumberPrefix = def.NumberPrefix,
                NumberSuffix = def.NumberSuffix,
                StartAt = def.StartAt,
                MarkerIndentPt = markerIndent
            };
        }

        /// <summary>Уровень списка, связанный со стилем абзаца (w:lvl/w:pStyle).</summary>
        private static int? LinkedLevel(Dictionary<int, LevelDef> levels, string? styleId)
        {
            if (string.IsNullOrEmpty(styleId)) return null;

            foreach (var (index, def) in levels)
            {
                if (string.Equals(def.LinkedStyleId, styleId, StringComparison.OrdinalIgnoreCase))
                    return index;
            }

            return null;
        }
    }
}
