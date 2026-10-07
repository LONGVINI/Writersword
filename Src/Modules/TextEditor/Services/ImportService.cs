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
    public sealed partial class ImportService
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
        /// картинки в тексте, разрывы страниц, параметры страницы финального раздела,
        /// колонтитулы и нумерация страниц (ImportService.HeaderFooter).
        /// Не поддерживается (игнорируется с предупреждением в Warnings):
        /// многораздельные документы (кроме последнего раздела),
        /// сноски/концевые сноски, комментарии, правки строк таблиц (w:trPr/w:ins,
        /// w:del), вложенные таблицы, векторные картинки (WMF/EMF), обтекание
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
            BeginSectionTracking();

            _compatibilityMode = ApplyWordCompatibility(mainPart, doc);
            ApplyTrackRevisionsSetting(mainPart, doc);

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

            // Колонтитулы — после разбора тела: правила разделов привязываются к
            // абзацам, с которых разделы начинаются.
            doc.HeaderFooter = ImportHeaderFooter(body, mainPart, section, resolver, warnings);

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

        /// <summary>
        /// Режим совместимости Word из настроек документа (w:compatibilityMode): 12 —
        /// Word 2007, 14 — Word 2010, 15 — Word 2013 и новее. Документ без режима Word
        /// открывает как Word 2007.
        /// </summary>
        private int _compatibilityMode = 12;

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

                    // Ячейки таблиц, вложенных в ячейки, — такие же отдельные потоки.
                    foreach (var nestedTable in table.NestedTablesDeep())
                        foreach (var nestedCell in nestedTable.Cells)
                            NormalizeBorderGroups(nestedCell.Paragraphs);
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

                        // Абзац с параметрами раздела закрывает раздел Word.
                        if (HasOwnSectionProperties(p))
                            NoteSectionBreak(p, section);
                        break;

                    case W.Table t:
                        var tableBlock = ImportTable(t, section, resolver, numbering, listIdMap,
                            mainPart, extractedImages, warnings, depth: 0);
                        if (tableBlock is not null)
                        {
                            // Своё выравнивание у строк таблицы (w:jc у w:trPr или у
                            // w:tblPrEx) Word 2013 и новее на листе не показывает: в режиме
                            // совместимости 15 таблица стоит целиком по выравниванию самой
                            // таблицы, все строки одна под другой. Документы старых режимов
                            // по-прежнему делятся по выравниванию строк.
                            if (_compatibilityMode >= 15)
                            {
                                section.Blocks.Add(tableBlock);
                            }
                            else
                            {
                                foreach (var part in SplitTableByRowAlignment(t, tableBlock))
                                    section.Blocks.Add(part);
                            }
                        }
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

            // Разрыв колонки в разделе из одной колонки Word ведёт на следующую страницу:
            // следующая колонка там — колонка нового листа. Вёрстка Writersword колонок не
            // строит, поэтому в разделе из нескольких колонок разрыв остаётся переносом
            // строки: страница на каждый разрыв колонки растянула бы документ вдвое.
            // Раздел ищется только у абзаца с разрывом колонки: поиск идёт по всему телу
            // документа, и для каждого абзаца подряд импорт стал бы квадратичным.
            bool columnBreakIsPage = p.Elements<W.Run>()
                    .SelectMany(r => r.Elements<W.Break>())
                    .Any(b => b.Type?.Value == W.BreakValues.Column)
                && IsInSingleColumnSection(p);

            var splits = new List<BreakSplit>();
            var segments = SplitRunsByPageBreak(p, columnBreakIsPage, splits);

            // Разрыв, поставленный за предыдущим куском: у Word он внутри этого абзаца.
            BreakBlock? previousBreak = null;

            for (int i = 0; i < segments.Count; i++)
            {
                var para = new ParagraphBlock();
                para.Properties = effPara.ToParagraphProperties(styleName);
                para.ListProperties = numbering.Resolve(effPara, para.Properties, listIdMap);

                // Кусок после разрыва — продолжение того же абзаца Word, а не новый абзац:
                // номера списка у него нет, отступа первой строки и интервала перед абзацем
                // тоже — строка встаёт по левому отступу, как все строки абзаца после первой.
                // Иначе хвост пункта списка получал собственный номер и сбивал счёт у всех
                // следующих пунктов.
                if (i > 0)
                {
                    para.ListProperties = null;
                    para.Properties.FirstLineIndent = 0.0;
                    para.Properties.SpaceBefore = 0.0;
                    para.Properties.PageBreakBefore = false;
                }

                var chunk = new TextChunk();
                para.Chunks.Clear();
                para.Chunks.Add(chunk);
                chunk.Runs.Clear();

                // Плавающие картинки абзаца ставятся в поток блоками перед ним: место
                // блока в потоке даёт раскладке и страницу, и верх абзаца-опоры.
                _pendingFloats.Clear();
                _floatingAllowed = true;
                try
                {
                    foreach (var runElement in segments[i])
                        AppendRunOrDrawing(runElement, chunk, section, resolver, effPara,
                            mainPart, extractedImages, warnings);
                }
                finally
                {
                    _floatingAllowed = false;
                }
                FlushPendingFloats(section);

                // Пустой хвост после разрыва в конце абзаца: у Word 2013 и новее знак абзаца
                // остаётся на строке разрыва, и новая страница начинается сразу со следующего
                // абзаца. Отдельным пустым абзацем хвост давал лишнюю строку в начале листа,
                // а перед абзацем «с новой страницы» — целый лишний пустой лист. Хвост
                // остаётся, когда за абзацем нет ничего — иначе новой странице не на чем
                // держать каретку, — и когда абзац закрывает раздел.
                if (i > 0 && i == segments.Count - 1 && chunk.Runs.Count == 0
                    && _compatibilityMode >= 15
                    && !HasOwnSectionProperties(p)
                    && HasFollowingBlock(p))
                {
                    // Абзац кончается разрывом: знак абзаца стоит на строке разрыва.
                    if (previousBreak is not null) previousBreak.ContinuesParagraph = false;
                    break;
                }

                if (chunk.Runs.Count == 0)
                    chunk.Runs.Add(BuildParagraphMarkRun(p, resolver, effPara));

                // Правки абзаца. Знак абзаца стоит в его последнем куске: куски до
                // разрыва страницы кончаются разрывом, а не знаком.
                ApplyParagraphRevisions(p, para, resolver);
                if (i < segments.Count - 1)
                {
                    para.Properties.MarkInserted = null;
                    para.Properties.MarkDeleted = null;
                }

                chunk.InvalidateLength();
                section.Blocks.Add(para);

                if (effPara.BetweenBorder is { } between)
                    _betweenBorders[para] = between;

                if (i < segments.Count - 1)
                {
                    var split = i < splits.Count ? splits[i] : default;
                    previousBreak = new BreakBlock
                    {
                        BreakType = BreakType.Page,
                        InParagraph = !split.IsEditorBreak,
                        ContinuesParagraph = true,
                        FromColumnBreak = split.IsColumn
                    };
                    section.Blocks.Add(previousBreak);
                }
            }
        }

        /// <summary>
        /// Делит содержимое параграфа на сегменты по разрывам страниц (w:br type="page").
        /// Каждый сегмент — список дочерних элементов Run/Hyperlink/... между разрывами.
        /// </summary>
        /// <param name="columnBreakIsPage">
        /// Разрыв колонки (w:br type="column") тоже режет абзац как разрыв страницы:
        /// так Word ведёт себя в разделе из одной колонки.
        /// </param>
        /// <param name="splits">
        /// Для каждого места разреза по порядку: чем разрезан абзац.
        /// </param>
        private static List<List<OpenXmlElement>> SplitRunsByPageBreak(
            W.Paragraph p, bool columnBreakIsPage, List<BreakSplit> splits)
        {
            var segments = new List<List<OpenXmlElement>> { new() };

            bool IsPageBreak(W.Break b) =>
                b.Type?.Value == W.BreakValues.Page
                || (columnBreakIsPage && b.Type?.Value == W.BreakValues.Column);

            foreach (var child in p.ChildElements)
            {
                if (child is W.ParagraphProperties) continue;

                if (child is W.Run run && run.Elements<W.Break>().Any(IsPageBreak))
                {
                    // Ран может содержать текст ДО разрыва и после — в большинстве
                    // документов разрыв страницы занимает ран целиком, но на всякий
                    // случай текст до/после разрыва распределяем по сегментам.
                    var before = new W.Run(run.RunProperties?.CloneNode(true) ?? new W.RunProperties());
                    var after = new W.Run(run.RunProperties?.CloneNode(true) ?? new W.RunProperties());
                    bool seenBreak = false;
                    var split = default(BreakSplit);
                    foreach (var rc in run.ChildElements)
                    {
                        if (rc is W.Break brk && IsPageBreak(brk))
                        {
                            if (!seenBreak)
                                split = new BreakSplit(
                                    brk.Type?.Value == W.BreakValues.Column,
                                    IsEditorBreak(brk));
                            seenBreak = true;
                            continue;
                        }
                        if (rc is W.RunProperties) continue;
                        (seenBreak ? after : before).AppendChild(rc.CloneNode(true));
                    }

                    if (before.ChildElements.Count > 0) segments[^1].Add(before);
                    splits.Add(split);
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
        /// Чем разрезан абзац: разрывом колонки (иначе страницы) и поставлен ли разрыв в
        /// редакторе Writersword (пометка wsx, которую пишет экспорт).
        /// </summary>
        private readonly record struct BreakSplit(bool IsColumn, bool IsEditorBreak);

        /// <summary>
        /// Разрыв поставлен в редакторе Writersword, а не в Word: у него пометка wsx.
        /// Такой разрыв после открытия файла рисуется своей отметкой редактора.
        /// </summary>
        private static bool IsEditorBreak(W.Break brk)
        {
            foreach (var attribute in brk.GetAttributes())
            {
                if (attribute.NamespaceUri == DocxTextEffects.WsxNamespace
                    && attribute.LocalName == ExportService.EditorBreakAttribute)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Абзац тела документа стоит в разделе из одной колонки. Раздел абзаца закрывает
        /// первый абзац с параметрами раздела, начиная с него самого, а для последнего
        /// раздела — параметры в конце тела. Число колонок — w:cols/@w:num или число
        /// заданных колонок w:col, если колонки неравной ширины.
        /// </summary>
        private static bool IsInSingleColumnSection(W.Paragraph p)
        {
            var body = p.Ancestors<W.Body>().FirstOrDefault();
            if (body is null) return false;

            W.SectionProperties? sectPr = null;
            bool reached = false;
            foreach (var candidate in body.Descendants<W.Paragraph>())
            {
                if (!reached)
                {
                    if (!ReferenceEquals(candidate, p)) continue;
                    reached = true;
                }

                if (candidate.ParagraphProperties?.SectionProperties is { } own)
                {
                    sectPr = own;
                    break;
                }
            }

            sectPr ??= body.GetFirstChild<W.SectionProperties>();

            var cols = sectPr?.GetFirstChild<W.Columns>();
            int columnCount = Math.Max(cols?.ColumnCount?.Value ?? 1, cols?.Elements<W.Column>().Count() ?? 0);
            return columnCount <= 1;
        }

        /// <summary>
        /// За абзацем в его контейнере есть ещё абзац, таблица или блок содержимого:
        /// новой странице после разрыва есть чем начаться.
        /// </summary>
        private static bool HasFollowingBlock(W.Paragraph p)
        {
            for (var next = p.NextSibling(); next is not null; next = next.NextSibling())
            {
                if (next is W.Paragraph or W.Table or W.SdtBlock) return true;
            }

            return false;
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
        private static int ApplyWordCompatibility(MainDocumentPart mainPart, DocumentModel doc)
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
            return compatibilityMode;
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
            // Правки рецензирования и границы перемещений (ImportService.Revisions).
            if (TryAppendRevisionElement(element, chunk, section, resolver, effPara,
                    mainPart, extractedImages, warnings))
                return;

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

                case W.SimpleField simpleField:
                    // В w:fldSimple Word хранит последнее вычисленное значение поля
                    // обычными ранами: код поля не нужен, а значение — это видимый
                    // текст документа, и терять его нельзя.
                    foreach (var innerRun in simpleField.Elements<W.Run>())
                        AppendRun(innerRun, chunk, section, resolver, effPara, mainPart, extractedImages, warnings);
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
            var effectiveRun = resolver.ResolveEffectiveRun(run, effPara);
            var runProps = effectiveRun.ToRunProperties();

            // Смена оформления под рецензированием: прежнее оформление остаётся при ране.
            runProps.FormatChange = ReadRunFormatChange(run, resolver, effPara);

            // Знаки вне ASCII (кириллица, латиница с диакритикой, типографские знаки)
            // Word набирает шрифтом w:hAnsi, а не w:ascii. Когда шрифты разные, текст
            // рана делится на куски по этому признаку.
            RunProperties? nonAsciiProps = null;
            string? hAnsiFont = effectiveRun.HAnsiFontFamily;
            if (!string.IsNullOrEmpty(hAnsiFont)
                && !string.Equals(hAnsiFont, runProps.FontFamily, StringComparison.Ordinal))
            {
                nonAsciiProps = runProps.Clone();
                nonAsciiProps.FontFamily = hAnsiFont;
            }

            // Знаки сложных письменностей (иврит, арабский…) Word набирает своими
            // свойствами: шрифтом w:cs, кеглем w:szCs, жирностью w:bCs, курсивом w:iCs.
            // Ран справа налево (w:rtl) или помеченный w:cs набирается ими целиком.
            RunProperties? complexProps = effectiveRun.ToComplexScriptProperties(runProps);
            bool wholeRunComplex = effectiveRun.RightToLeftRun == true || effectiveRun.ComplexScriptRun == true;

            foreach (var child in run.ChildElements)
            {
                switch (child)
                {
                    case W.Text t:
                        if (wholeRunComplex && complexProps is not null)
                            chunk.Runs.Add(new RunModel { Text = t.Text, Properties = complexProps });
                        else
                            AppendTextByFontSlot(chunk, t.Text, runProps, nonAsciiProps, complexProps);
                        break;

                    // Текст удалённой правки (w:delText) — тот же текст: он остаётся в
                    // абзаце до принятия правки, отметку удаления ставит её контейнер.
                    case W.DeletedText deletedText:
                        if (wholeRunComplex && complexProps is not null)
                            chunk.Runs.Add(new RunModel { Text = deletedText.Text, Properties = complexProps });
                        else
                            AppendTextByFontSlot(chunk, deletedText.Text, runProps, nonAsciiProps, complexProps);
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

                    case W.SymbolChar sym:
                        AppendSymbolChar(chunk, sym, runProps);
                        break;

                    case W.NoBreakHyphen:
                        // Неразрывный дефис (Ctrl+Shift+-): рисуется дефисом, но строку по
                        // нему не рвут. В тексте — U+2011, тот же знак, что пишет Word.
                        chunk.Runs.Add(new RunModel { Text = "\u2011", Properties = runProps });
                        break;

                    case W.SoftHyphen:
                        // Мягкий перенос (Ctrl+-): место, где слово разрешено разорвать; виден
                        // дефисом только там, где по нему разорвано слово. В тексте — U+00AD.
                        chunk.Runs.Add(new RunModel { Text = "\u00AD", Properties = runProps });
                        break;

                    case W.Drawing drawing:
                        ImportDrawing(drawing, chunk, section, runProps, resolver, mainPart, extractedImages, warnings);
                        break;

                    // Фигуры Word лежат в mc:AlternateContent: современный вид и запасной.
                    case OpenXmlElement alternate when alternate.LocalName == "AlternateContent":
                        ImportAlternateContent(alternate, chunk, section, runProps, resolver,
                            mainPart, extractedImages, warnings);
                        break;

                    // Картинка старого вида (w:pict): опознаётся по имени элемента.
                    case OpenXmlElement legacyPicture when legacyPicture.LocalName == "pict":
                        ImportVmlPicture(legacyPicture, chunk, section, runProps, mainPart, extractedImages, warnings);
                        break;

                    case W.FootnoteReference:
                    case W.EndnoteReference:
                        warnings.Add("Сноски/концевые сноски не поддерживаются и были пропущены.");
                        break;
                }
            }
        }

        /// <summary>
        /// Знак из символьного шрифта (w:sym: Symbol, Wingdings, Webdings…) — «Вставка →
        /// Символ» у Word. Знак хранится кодом шрифта, а не буквой Юникода: код F0D6 у
        /// Symbol — корень, у Wingdings тот же код — совсем другой знак. Поэтому знак
        /// берётся ровно так, как его рисует Word, — кодом из области U+F000–U+F0FF и
        /// своим шрифтом: символьные шрифты отдают глифы именно по этим кодам.
        /// Без этого знак пропадал из текста совсем.
        /// </summary>
        private static void AppendSymbolChar(TextChunk chunk, W.SymbolChar sym, RunProperties runProps)
        {
            string? hex = sym.Char?.Value;
            if (string.IsNullOrWhiteSpace(hex)
                || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code)
                || code <= 0)
                return;

            // Код может прийти и коротким (D6) — у символьного шрифта это тот же F0D6.
            if (code <= 0xFF) code += 0xF000;
            if (code > 0xFFFF) return;

            var props = runProps.Clone();
            if (!string.IsNullOrWhiteSpace(sym.Font?.Value))
                props.FontFamily = sym.Font!.Value;

            chunk.Runs.Add(new RunModel { Text = ((char)code).ToString(), Properties = props });
        }

        /// <summary>
        /// Текст рана в фрагмент. Word выбирает свойства для каждого знака: знаки
        /// U+0000–U+007F — шрифтом ascii, остальные европейские — шрифтом hAnsi, знаки
        /// сложных письменностей — своими свойствами (complexProps). Когда эти свойства
        /// различаются, текст делится на куски по ним.
        /// </summary>
        private static void AppendTextByFontSlot(
            TextChunk chunk, string text, RunProperties asciiProps, RunProperties? nonAsciiProps,
            RunProperties? complexProps = null)
        {
            if ((nonAsciiProps is null && complexProps is null) || text.Length == 0)
            {
                chunk.Runs.Add(new RunModel { Text = text, Properties = asciiProps });
                return;
            }

            RunProperties PropsFor(char c)
            {
                if (c <= '\u007F') return asciiProps;
                if (complexProps is not null && IsComplexScriptChar(c)) return complexProps;
                return nonAsciiProps ?? asciiProps;
            }

            int start = 0;
            var startProps = PropsFor(text[0]);

            for (int i = 1; i <= text.Length; i++)
            {
                var props = i < text.Length ? PropsFor(text[i]) : null;
                if (props is not null && ReferenceEquals(props, startProps)) continue;

                chunk.Runs.Add(new RunModel
                {
                    Text = text.Substring(start, i - start),
                    Properties = startProps
                });

                if (props is null) break;
                start = i;
                startProps = props;
            }
        }

        /// <summary>
        /// Знак сложной письменности, который Word набирает свойствами w:cs, w:szCs,
        /// w:bCs и w:iCs: иврит, арабский, сирийский, тана, нко, письменности Индии,
        /// тайский и лаосский, формы представления иврита и арабского.
        /// </summary>
        private static bool IsComplexScriptChar(char c) =>
            (c >= '\u0590' && c <= '\u08FF')
            || (c >= '\u0900' && c <= '\u0DFF')
            || (c >= '\u0E00' && c <= '\u0EFF')
            || (c >= '\uFB1D' && c <= '\uFDFF')
            || (c >= '\uFE70' && c <= '\uFEFC');

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

        /// <summary>
        /// Сколько уровней таблиц в таблицах строится. Глубже содержимое таблицы
        /// переносится в ячейку абзацами: защита от файла с бесконечной вложенностью.
        /// </summary>
        private const int MaxNestedTableDepth = 6;

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

            // Ширины колонок — как у Word. Сетка (w:tblGrid) — это ширины, которые Word сам
            // посчитал и сохранил, в том числе для автоподбора: колонки «авто» в ней уже
            // ужаты по содержимому. Поэтому ширины берутся из сетки как есть, в мм, а не
            // долями от ширины полосы: таблица 3×3000 twips шире полосы набора в 419 пт и
            // у Word выступает за правое поле, а растянутая долями во всю полосу она
            // становилась уже, чем в Word. Таблица в процентах (w:tblW type="pct") держит
            // свою долю полосы, и колонки делят эту долю по сетке.
            var tableWidth = table.GetFirstChild<W.TableProperties>()?.GetFirstChild<W.TableWidth>();
            bool percentTable = tableWidth?.Type?.Value == W.TableWidthUnitValues.Pct;
            double tablePercent = 100.0;
            if (percentTable && TryParseTableWidth(tableWidth?.Width?.Value, out double pctValue) && pctValue > 0)
                tablePercent = pctValue / 50.0;

            long totalWidthTwips = columnWidthsTwips.Sum();
            if (totalWidthTwips > 0 && percentTable)
            {
                block.WidthPercent = Math.Round(tablePercent, 2);
                foreach (var w in columnWidthsTwips)
                {
                    block.Columns.Add(new TableColumnDefinition
                    {
                        WidthType = TableColumnWidthType.Percent,
                        WidthValue = Math.Round(w * tablePercent / totalWidthTwips, 2)
                    });
                }
            }
            else if (totalWidthTwips > 0)
            {
                foreach (var w in columnWidthsTwips)
                {
                    block.Columns.Add(new TableColumnDefinition
                    {
                        WidthType = TableColumnWidthType.Fixed,
                        WidthValue = Math.Round(w / TwipsPerMm, 2)
                    });
                }
            }
            else if (tableWidth?.Type?.Value == W.TableWidthUnitValues.Dxa
                     && TryParseTableWidth(tableWidth.Width?.Value, out double tableTwips) && tableTwips > 0)
            {
                // Сетки нет, ширина таблицы задана: колонки делят её поровну.
                for (int i = 0; i < columnCount; i++)
                    block.Columns.Add(new TableColumnDefinition
                    {
                        WidthType = TableColumnWidthType.Fixed,
                        WidthValue = Math.Round(tableTwips / columnCount / TwipsPerMm, 2)
                    });
            }
            else
            {
                if (percentTable) block.WidthPercent = Math.Round(tablePercent, 2);
                for (int i = 0; i < columnCount; i++)
                    block.Columns.Add(new TableColumnDefinition
                    {
                        WidthType = percentTable ? TableColumnWidthType.Percent : TableColumnWidthType.Auto,
                        WidthValue = percentTable ? Math.Round(tablePercent / columnCount, 2) : 0
                    });
            }

            // Границы и заливка таблицы с учётом её стиля (w:tblStyle) и стиля таблиц по
            // умолчанию: у Word оформление таблицы из стиля такое же своё, как прямое.
            var tableBorders = ResolveTableBorders(table, mainPart);

            // Отступ таблицы от поля (w:tblInd): таблица сдвигается вправо.
            var tableIndent = table.GetFirstChild<W.TableProperties>()?.GetFirstChild<W.TableIndentation>();
            if (tableIndent?.Width?.Value is int indentTwips
                && (tableIndent.Type is null || tableIndent.Type.Value == W.TableWidthUnitValues.Dxa))
                block.LeftIndentPt = indentTwips / 20.0;

            // Выравнивание таблицы (w:jc у w:tblPr): по центру и справа таблица встаёт по
            // ширине текстовой области. Значения сравниваются по тексту: у переходной и
            // строгой схем свои имена (right/end, left/start).
            string? tableJc = table.GetFirstChild<W.TableProperties>()?
                .GetFirstChild<W.TableJustification>()?.Val?.InnerText;
            block.Alignment = tableJc switch
            {
                "center" => TableBlockAlignment.Center,
                "right" or "end" => TableBlockAlignment.Right,
                _ => TableBlockAlignment.Left
            };
            string? styleFill = ResolveTableStyleFill(table, mainPart);

            // Таблица с обтеканием текстом (w:tblpPr): стоит в своей точке листа, и
            // текст обходит её с обеих сторон.
            var floating = table.GetFirstChild<W.TableProperties>()?.GetFirstChild<W.TablePositionProperties>();
            if (floating is not null)
                block.FloatPosition = ReadTableFloatPosition(floating);

            // Границы ячеек считаются, когда известна вся сетка: сторона ячейки у края
            // таблицы берёт внешнюю границу, внутри — внутреннюю (insideH/insideV), а
            // объединённая по вертикали ячейка узнаёт свой низ только в конце.
            var pendingBorders = new List<(TableCell Cell, W.TableCellBorders? Own, bool LastInRow)>();

            // Границы каждой строки: границы таблицы с исключениями строки (w:tblPrEx).
            var rowBorders = new TableBorderSet[rows.Count];

            // Поля ячеек: умолчания Word, стиль таблицы по умолчанию, стиль самой таблицы,
            // её w:tblCellMar; дальше строка (w:tblPrEx) и ячейка (w:tcMar).
            var tableMargins = ResolveTableCellMargins(table, mainPart);

            // vMerge отслеживается по столбцам: для каждого столбца храним последнюю
            // "главную" ячейку вертикального объединения (или null, если объединения нет).
            var openVMerge = new TableCell?[columnCount];

            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                var cells = rows[rowIndex].Elements<W.TableCell>().ToList();
                int col = 0;

                var rowMargins = ApplyCellMarginDefault(
                    tableMargins, rows[rowIndex].TablePropertyExceptions?.TableCellMarginDefault);

                // Высота строки (w:trHeight): «не менее» и «точно» — нижняя граница высоты.
                var rowHeight = rows[rowIndex].TableRowProperties?.GetFirstChild<W.TableRowHeight>();
                if (rowHeight?.Val?.Value is uint rowHeightTwips && rowHeightTwips > 0
                    && rowHeight.HeightType?.Value != W.HeightRuleValues.Auto)
                {
                    block.SetRowMinHeightPt(rowIndex, rowHeightTwips / 20.0);

                    // «Точно»: строка ровно этой высоты, лишний текст срезается.
                    if (rowHeight.HeightType?.Value == W.HeightRuleValues.Exact)
                        block.SetRowHeightExact(rowIndex, true);
                }

                // Строка-заголовок (w:tblHeader) у первой строки — повтор шапки на
                // каждой странице, как у Word.
                if (rowIndex == 0
                    && rows[rowIndex].TableRowProperties?.GetFirstChild<W.TableHeader>() is { } header
                    && (header.Val is null || header.Val.Value != W.OnOffOnlyValues.Off))
                    block.RepeatHeader = true;

                // Разрыв строки между страницами. У Word строка разрывается, если ей не
                // запрещено (w:cantSplit): её начало остаётся на странице, остаток уходит
                // на следующую. Без этого таблица из одной высокой строки уезжала на новую
                // страницу целиком и уводила за собой заголовок, который держится за неё.
                if (rowIndex == 0)
                {
                    var cantSplit = rows[rowIndex].TableRowProperties?.GetFirstChild<W.CantSplit>();
                    bool keepsWhole = cantSplit is not null
                        && (cantSplit.Val is null || cantSplit.Val.Value != W.OnOffOnlyValues.Off);
                    block.SplitMode = keepsWhole ? TableSplitMode.ByRow : TableSplitMode.ByCell;
                }

                // Границы строки (w:tblPrEx/w:tblBorders) — поверх границ таблицы: Word
                // пишет их, когда строки одной таблицы оформлены по-разному, например у
                // двух таблиц, слитых в одну.
                rowBorders[rowIndex] = tableBorders.With(rows[rowIndex].TablePropertyExceptions?.TableBorders);

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

                    // Таблицы внутри ячейки. У Word таблица в ячейке стоит между абзацами,
                    // и после неё всегда идёт абзац: к нему таблица и привязывается.
                    // pendingNested — таблицы, за которыми абзац ещё не встретился.
                    List<NestedTable>? nestedTables = null;
                    var pendingNested = new List<TableBlock>();

                    // Плавающие объекты ячейки (картинки и фигуры с обтеканием).
                    List<CellFloat>? cellFloats = null;

                    void AttachPendingNested(ParagraphBlock anchor, int anchorIndex)
                    {
                        if (pendingNested.Count == 0) return;

                        nestedTables ??= new List<NestedTable>();
                        foreach (var pendingTable in pendingNested)
                        {
                            nestedTables.Add(new NestedTable
                            {
                                BeforeParagraphId = anchor.Id,
                                BeforeParagraphIndex = anchorIndex,
                                Table = pendingTable
                            });
                        }
                        pendingNested.Clear();
                    }

                    foreach (var cellChild in wCell.ChildElements)
                    {
                        switch (cellChild)
                        {
                            case W.Paragraph cellParagraph:
                            {
                                // Плавающие картинки и фигуры абзаца остаются плавающими
                                // внутри ячейки и привязываются к этому абзацу.
                                var outerCollector = _cellFloatCollector;
                                var collectedFloats = new List<BlockModel>();
                                _cellFloatCollector = collectedFloats;

                                ParagraphBlock importedParagraph;
                                try
                                {
                                    importedParagraph = ImportCellParagraph(cellParagraph, section, resolver,
                                        numbering, listIdMap, mainPart, extractedImages, warnings);
                                }
                                finally
                                {
                                    _cellFloatCollector = outerCollector;
                                }

                                paragraphs.Add(importedParagraph);
                                AttachPendingNested(importedParagraph, paragraphs.Count - 1);

                                foreach (var floatingObject in collectedFloats)
                                {
                                    cellFloats ??= new List<CellFloat>();
                                    cellFloats.Add(new CellFloat
                                    {
                                        AnchorParagraphId = importedParagraph.Id,
                                        AnchorParagraphIndex = paragraphs.Count - 1,
                                        Object = floatingObject
                                    });
                                }
                                break;
                            }

                            case W.Table nested:
                                var nestedBlock = depth < MaxNestedTableDepth
                                    ? ImportTable(nested, section, resolver, numbering, listIdMap,
                                        mainPart, extractedImages, warnings, depth + 1)
                                    : null;

                                if (nestedBlock is not null)
                                {
                                    pendingNested.Add(nestedBlock);
                                    break;
                                }

                                // Глубже предела вложенности таблица не строится: её текст
                                // остаётся в ячейке обычными абзацами и не теряется.
                                warnings.Add("Таблица вложена слишком глубоко: её содержимое " +
                                             "перенесено в ячейку обычными абзацами.");
                                foreach (var nestedParagraph in nested.Descendants<W.Paragraph>())
                                    paragraphs.Add(ImportCellParagraph(nestedParagraph, section, resolver,
                                        numbering, listIdMap, mainPart, extractedImages, warnings));
                                break;
                        }
                    }
                    if (paragraphs.Count == 0) paragraphs.Add(new ParagraphBlock());

                    // Таблица в самом конце ячейки (в файлах не от Word): абзац за ней
                    // добавляется, как его добавил бы Word.
                    if (pendingNested.Count > 0)
                    {
                        var closingParagraph = new ParagraphBlock();
                        paragraphs.Add(closingParagraph);
                        AttachPendingNested(closingParagraph, paragraphs.Count - 1);
                    }

                    var cellMargins = ApplyCellMargin(rowMargins, cellProps?.TableCellMargin);

                    var newCell = new TableCell
                    {
                        Row = rowIndex,
                        Column = col,
                        RowSpan = 1,
                        ColSpan = gridSpan,
                        Paragraphs = paragraphs,
                        NestedTables = nestedTables,
                        Floats = cellFloats,
                        BackgroundColor = cellProps?.Shading is not null
                            ? NormalizeShadingColor(cellProps.Shading)
                            : styleFill,
                        ShadingPattern = ReadCellShadingPattern(cellProps?.Shading),
                        ShadingPatternColor = ReadCellShadingPattern(cellProps?.Shading) is not null
                            ? NormalizeHexColor(cellProps!.Shading!.Color?.Value)
                            : null,
                        PaddingTopPt = cellMargins.Top,
                        PaddingBottomPt = cellMargins.Bottom,
                        PaddingLeftPt = cellMargins.Left,
                        PaddingRightPt = cellMargins.Right
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

                    // Направление текста (w:textDirection): btLr — снизу вверх, tbRl и
                    // вертикальные варианты для восточноазиатского письма — сверху вниз.
                    newCell.TextDirection = ResolveCellTextDirection(
                        cellProps?.GetFirstChild<W.TextDirection>()?.Val?.InnerText);

                    block.Cells.Add(newCell);
                    pendingBorders.Add((newCell, cellProps?.TableCellBorders, ReferenceEquals(wCell, cells[^1])));

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

            foreach (var (cell, own, lastInRow) in pendingBorders)
            {
                cell.Borders = ResolveCellBorders(
                    own, cell.Row >= 0 && cell.Row < rowBorders.Length ? rowBorders[cell.Row] : tableBorders,
                    firstRow: cell.Row == 0,
                    lastRow: cell.Row + cell.RowSpan >= rows.Count,
                    firstColumn: cell.Column == 0,
                    lastColumn: lastInRow || cell.Column + cell.ColSpan >= columnCount);
            }

            // Таблица «справа налево» (w:bidiVisual): Word показывает первую колонку
            // справа. Колонки переставляются в видимый порядок; левая сторона в разметке
            // Word у такой таблицы — ведущая, то есть правая, поэтому границы и поля
            // меняются местами вместе с колонками. Выравнивание тоже логическое: «к
            // началу» — это к правому полю.
            var bidiVisual = table.GetFirstChild<W.TableProperties>()?.GetFirstChild<W.BiDiVisual>();
            // w:val у w:bidiVisual — on/off (CT_OnOff в схеме SDK — OnOffOnlyValues): без
            // значения элемент включает свойство, «off» его снимает.
            if (bidiVisual is not null
                && (bidiVisual.Val is null || bidiVisual.Val.Value == W.OnOffOnlyValues.On))
            {
                block.BidiVisual = true;
                block.MirrorColumns();
                block.Alignment = block.Alignment switch
                {
                    TableBlockAlignment.Left => TableBlockAlignment.Right,
                    TableBlockAlignment.Right => TableBlockAlignment.Left,
                    _ => TableBlockAlignment.Center
                };
                if (block.Alignment == TableBlockAlignment.Left)
                    block.LeftIndentPt = 0;
            }

            return block;
        }

        /// <summary>
        /// Выравнивание строк таблицы. Word сливает идущие подряд таблицы в одну, и у
        /// строк бывшей второй и третьей таблицы остаётся своё выравнивание (w:jc у
        /// w:trPr или у w:tblPrEx). На листе это по-прежнему отдельные таблицы — слева,
        /// по центру, справа. Строка без своего выравнивания берёт выравнивание таблицы.
        /// </summary>
        private static TableBlockAlignment?[] ReadRowAlignments(W.Table table)
        {
            var rows = table.Elements<W.TableRow>().ToList();
            var result = new TableBlockAlignment?[rows.Count];

            for (int i = 0; i < rows.Count; i++)
            {
                string? jc = rows[i].TableRowProperties?.GetFirstChild<W.TableJustification>()?.Val?.InnerText
                    ?? rows[i].TablePropertyExceptions?.GetFirstChild<W.TableJustification>()?.Val?.InnerText;

                result[i] = jc switch
                {
                    "center" => TableBlockAlignment.Center,
                    "right" or "end" => TableBlockAlignment.Right,
                    "left" or "start" => TableBlockAlignment.Left,
                    _ => null
                };
            }

            return result;
        }

        /// <summary>
        /// Делит таблицу на части по смене выравнивания строк — так, как их видно в
        /// Word. Граница части не может пройти через объединённую по вертикали ячейку:
        /// такие строки остаются вместе. Таблица без своих выравниваний у строк
        /// возвращается как есть.
        /// </summary>
        private static List<TableBlock> SplitTableByRowAlignment(W.Table source, TableBlock block)
        {
            var result = new List<TableBlock>();
            var rowAlignments = ReadRowAlignments(source);

            // Таблица с обтеканием стоит в одной точке листа и на части не делится.
            if (rowAlignments.Length != block.RowCount
                || rowAlignments.All(a => a is null)
                || block.BidiVisual
                || block.FloatPosition is not null)
            {
                result.Add(block);
                return result;
            }

            var effective = new TableBlockAlignment[block.RowCount];
            for (int r = 0; r < block.RowCount; r++)
                effective[r] = rowAlignments[r] ?? block.Alignment;

            // Возможные точки раздела: перед строкой r, если выравнивание меняется и
            // ни одна ячейка не тянется через эту границу.
            var starts = new List<int> { 0 };
            for (int r = 1; r < block.RowCount; r++)
            {
                if (effective[r] == effective[r - 1]) continue;
                bool crossed = block.Cells.Any(c => c.Row < r && c.Row + c.RowSpan > r);
                if (!crossed) starts.Add(r);
            }

            if (starts.Count == 1)
            {
                block.Alignment = effective[0];
                if (block.Alignment != TableBlockAlignment.Left) block.LeftIndentPt = 0;
                result.Add(block);
                return result;
            }

            for (int part = 0; part < starts.Count; part++)
            {
                int from = starts[part];
                int to = part + 1 < starts.Count ? starts[part + 1] : block.RowCount;

                var piece = new TableBlock
                {
                    RowCount = to - from,
                    ColumnCount = block.ColumnCount,
                    StyleName = block.StyleName,
                    WidthPercent = block.WidthPercent,
                    Alignment = effective[from],
                    LeftIndentPt = effective[from] == TableBlockAlignment.Left ? block.LeftIndentPt : 0,
                    RepeatHeader = part == 0 && block.RepeatHeader,
                    SplitMode = block.SplitMode,
                    BreakLabel = block.BreakLabel,
                    ContinuationLabel = block.ContinuationLabel
                };

                foreach (var column in block.Columns)
                    piece.Columns.Add(new TableColumnDefinition
                    {
                        WidthType = column.WidthType,
                        WidthValue = column.WidthValue
                    });

                for (int r = from; r < to; r++)
                {
                    double heightPt = block.GetRowMinHeightPt(r);
                    if (heightPt > 0)
                    {
                        piece.SetRowMinHeightPt(r - from, heightPt);
                        if (block.IsRowHeightExact(r)) piece.SetRowHeightExact(r - from, true);
                    }
                }

                foreach (var cell in block.Cells)
                {
                    if (cell.Row < from || cell.Row >= to) continue;
                    cell.Row -= from;
                    piece.Cells.Add(cell);
                }

                result.Add(piece);
            }

            return result;
        }

        /// <summary>
        /// Положение таблицы с обтеканием (w:tblpPr). Атрибуты читаются по именам: это
        /// числа в двадцатых долях пункта и слова-опоры. Незаданная опора у Word — текст
        /// по горизонтали и поле по вертикали. Сторона опоры (tblpXSpec, tblpYSpec)
        /// главнее смещения: при ней смещение Word не учитывает.
        /// </summary>
        private static TableFloatPosition ReadTableFloatPosition(OpenXmlElement element)
        {
            var result = new TableFloatPosition
            {
                HorizontalAnchor = TableFloatAnchor.Text,
                VerticalAnchor = TableFloatAnchor.Margin
            };

            foreach (var attribute in element.GetAttributes())
            {
                string value = attribute.Value ?? string.Empty;

                switch (attribute.LocalName)
                {
                    case "horzAnchor":
                        result.HorizontalAnchor = ReadTableFloatAnchor(value, result.HorizontalAnchor);
                        break;

                    case "vertAnchor":
                        result.VerticalAnchor = ReadTableFloatAnchor(value, result.VerticalAnchor);
                        break;

                    case "tblpX":
                        if (TryReadTwipsAsPoints(value, out double xPt)) result.XPt = xPt;
                        break;

                    case "tblpY":
                        if (TryReadTwipsAsPoints(value, out double yPt)) result.YPt = yPt;
                        break;

                    case "tblpXSpec":
                        result.HorizontalAlign = value switch
                        {
                            "left" or "inside" => TableFloatAlign.Start,
                            "center" => TableFloatAlign.Center,
                            "right" or "outside" => TableFloatAlign.End,
                            _ => TableFloatAlign.Offset
                        };
                        break;

                    case "tblpYSpec":
                        result.VerticalAlign = value switch
                        {
                            "top" or "inside" => TableFloatAlign.Start,
                            "center" => TableFloatAlign.Center,
                            "bottom" or "outside" => TableFloatAlign.End,
                            _ => TableFloatAlign.Offset
                        };
                        break;

                    case "leftFromText":
                        if (TryReadTwipsAsPoints(value, out double leftPt)) result.LeftFromTextPt = Math.Max(0, leftPt);
                        break;

                    case "rightFromText":
                        if (TryReadTwipsAsPoints(value, out double rightPt)) result.RightFromTextPt = Math.Max(0, rightPt);
                        break;

                    case "topFromText":
                        if (TryReadTwipsAsPoints(value, out double topPt)) result.TopFromTextPt = Math.Max(0, topPt);
                        break;

                    case "bottomFromText":
                        if (TryReadTwipsAsPoints(value, out double bottomPt)) result.BottomFromTextPt = Math.Max(0, bottomPt);
                        break;
                }
            }

            return result;
        }

        private static TableFloatAnchor ReadTableFloatAnchor(string value, TableFloatAnchor fallback) => value switch
        {
            "text" => TableFloatAnchor.Text,
            "margin" => TableFloatAnchor.Margin,
            "page" => TableFloatAnchor.Page,
            _ => fallback
        };

        /// <summary>Число в двадцатых долях пункта — в пункты.</summary>
        private static bool TryReadTwipsAsPoints(string value, out double points)
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double twips))
            {
                points = twips / 20.0;
                return true;
            }

            points = 0;
            return false;
        }

        /// <summary>Границы таблицы: внешние стороны и внутренние линии.</summary>
        private sealed class TableBorderSet
        {
            public W.BorderType? Top, Bottom, Left, Right, InsideH, InsideV;

            /// <summary>Стороны из w:tblBorders поверх уже известных: незаданная сторона не меняется.</summary>
            public void Apply(W.TableBorders? source)
            {
                if (source is null) return;
                Top = (W.BorderType?)source.TopBorder ?? Top;
                Bottom = (W.BorderType?)source.BottomBorder ?? Bottom;
                Left = (W.BorderType?)source.LeftBorder ?? (W.BorderType?)source.StartBorder ?? Left;
                Right = (W.BorderType?)source.RightBorder ?? (W.BorderType?)source.EndBorder ?? Right;
                InsideH = (W.BorderType?)source.InsideHorizontalBorder ?? InsideH;
                InsideV = (W.BorderType?)source.InsideVerticalBorder ?? InsideV;
            }

            /// <summary>
            /// Этот же набор с исключениями строки поверх. Без исключений возвращается сам
            /// набор: копия нужна только строке, у которой границы свои.
            /// </summary>
            public TableBorderSet With(W.TableBorders? exceptions)
            {
                if (exceptions is null) return this;

                var result = (TableBorderSet)MemberwiseClone();
                result.Apply(exceptions);
                return result;
            }
        }

        /// <summary>
        /// Стили таблицы от самого общего к самому стилю: стиль таблиц по умолчанию, затем
        /// цепочка w:basedOn стиля таблицы от базового к нему.
        /// </summary>
        private static List<W.Style> TableStyleChain(W.Table table, MainDocumentPart mainPart)
        {
            var result = new List<W.Style>();
            var styles = mainPart.StyleDefinitionsPart?.Styles;
            if (styles is null) return result;

            var tableStyles = styles.Elements<W.Style>()
                .Where(s => s.Type?.Value == W.StyleValues.Table)
                .ToList();

            var defaultStyle = tableStyles.FirstOrDefault(s => s.Default?.Value == true);

            string? styleId = table.GetFirstChild<W.TableProperties>()?.TableStyle?.Val?.Value;
            var chain = new List<W.Style>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (styleId is not null && seen.Add(styleId))
            {
                var style = tableStyles.FirstOrDefault(s =>
                    string.Equals(s.StyleId?.Value, styleId, StringComparison.OrdinalIgnoreCase));
                if (style is null) break;
                chain.Add(style);
                styleId = style.BasedOn?.Val?.Value;
            }

            if (defaultStyle is not null && !chain.Contains(defaultStyle)) result.Add(defaultStyle);
            for (int i = chain.Count - 1; i >= 0; i--) result.Add(chain[i]);
            return result;
        }

        /// <summary>
        /// Границы таблицы: стиль таблиц по умолчанию, стиль самой таблицы, её w:tblBorders.
        /// Сторона, которую никто не задал, у Word без линии: таблица без w:tblBorders и
        /// без стиля с границами стоит вовсе без рамки.
        /// </summary>
        private static TableBorderSet ResolveTableBorders(W.Table table, MainDocumentPart mainPart)
        {
            var result = new TableBorderSet();
            foreach (var style in TableStyleChain(table, mainPart))
                result.Apply(style.StyleTableProperties?.TableBorders);

            result.Apply(table.GetFirstChild<W.TableProperties>()?.GetFirstChild<W.TableBorders>());
            return result;
        }

        /// <summary>Заливка ячеек из стиля таблицы (w:style/w:tcPr/w:shd); null — стиль не заливает.</summary>
        private static string? ResolveTableStyleFill(W.Table table, MainDocumentPart mainPart)
        {
            string? fill = null;
            foreach (var style in TableStyleChain(table, mainPart))
            {
                var shading = style.StyleTableCellProperties?.GetFirstChild<W.Shading>();
                if (shading is not null) fill = NormalizeShadingColor(shading);
            }

            return fill;
        }

        /// <summary>Поля ячейки таблицы в пунктах.</summary>
        private readonly record struct CellMargins(double Top, double Bottom, double Left, double Right);

        /// <summary>
        /// Поля ячеек таблицы до строк и ячеек. Без всяких указаний у Word сверху и снизу
        /// ноль, слева и справа по 108 twips (5,4 пт) — так задан встроенный стиль «Обычная
        /// таблица». Поверх идут стиль таблиц по умолчанию, цепочка стиля самой таблицы и
        /// её w:tblCellMar. Прежде поля не читались вовсе, и ячейка получала поля
        /// редактора 4/6 пт: строки выходили выше вордовских, а текст отступал от рамки,
        /// когда в файле поле в полпункта.
        /// </summary>
        private static CellMargins ResolveTableCellMargins(W.Table table, MainDocumentPart mainPart)
        {
            var margins = new CellMargins(0.0, 0.0, 5.4, 5.4);

            var styles = mainPart.StyleDefinitionsPart?.Styles;
            if (styles is not null)
            {
                var tableStyles = styles.Elements<W.Style>()
                    .Where(s => s.Type?.Value == W.StyleValues.Table)
                    .ToList();

                var defaultStyle = tableStyles.FirstOrDefault(s => s.Default?.Value == true);
                if (defaultStyle is not null)
                    margins = ApplyCellMarginDefault(margins, defaultStyle.StyleTableProperties?.TableCellMarginDefault);

                // Цепочка стиля таблицы — от базового к самому стилю.
                string? styleId = table.GetFirstChild<W.TableProperties>()?.TableStyle?.Val?.Value;
                var chain = new List<W.Style>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (styleId is not null && seen.Add(styleId))
                {
                    var style = tableStyles.FirstOrDefault(s =>
                        string.Equals(s.StyleId?.Value, styleId, StringComparison.OrdinalIgnoreCase));
                    if (style is null) break;
                    chain.Add(style);
                    styleId = style.BasedOn?.Val?.Value;
                }

                for (int i = chain.Count - 1; i >= 0; i--)
                    margins = ApplyCellMarginDefault(margins, chain[i].StyleTableProperties?.TableCellMarginDefault);
            }

            return ApplyCellMarginDefault(
                margins, table.GetFirstChild<W.TableProperties>()?.GetFirstChild<W.TableCellMarginDefault>());
        }

        /// <summary>Поля из w:tblCellMar поверх уже известных: незаданная сторона не меняется.</summary>
        private static CellMargins ApplyCellMarginDefault(CellMargins margins, W.TableCellMarginDefault? source)
        {
            if (source is null) return margins;

            return new CellMargins(
                MarginPt(source.TopMargin?.Width?.Value, source.TopMargin?.Type?.Value) ?? margins.Top,
                MarginPt(source.BottomMargin?.Width?.Value, source.BottomMargin?.Type?.Value) ?? margins.Bottom,
                MarginPt(source.TableCellLeftMargin?.Width?.Value.ToString(CultureInfo.InvariantCulture),
                        source.TableCellLeftMargin?.Type?.Value)
                    ?? MarginPt(source.StartMargin?.Width?.Value, source.StartMargin?.Type?.Value)
                    ?? margins.Left,
                MarginPt(source.TableCellRightMargin?.Width?.Value.ToString(CultureInfo.InvariantCulture),
                        source.TableCellRightMargin?.Type?.Value)
                    ?? MarginPt(source.EndMargin?.Width?.Value, source.EndMargin?.Type?.Value)
                    ?? margins.Right);
        }

        /// <summary>Поля ячейки (w:tcMar) поверх полей строки.</summary>
        private static CellMargins ApplyCellMargin(CellMargins margins, W.TableCellMargin? source)
        {
            if (source is null) return margins;

            return new CellMargins(
                MarginPt(source.TopMargin?.Width?.Value, source.TopMargin?.Type?.Value) ?? margins.Top,
                MarginPt(source.BottomMargin?.Width?.Value, source.BottomMargin?.Type?.Value) ?? margins.Bottom,
                MarginPt(source.LeftMargin?.Width?.Value, source.LeftMargin?.Type?.Value)
                    ?? MarginPt(source.StartMargin?.Width?.Value, source.StartMargin?.Type?.Value)
                    ?? margins.Left,
                MarginPt(source.RightMargin?.Width?.Value, source.RightMargin?.Type?.Value)
                    ?? MarginPt(source.EndMargin?.Width?.Value, source.EndMargin?.Type?.Value)
                    ?? margins.Right);
        }

        /// <summary>
        /// Поле в пунктах: twips (dxa или без единицы) пополам на 20, nil — ноль. Доли
        /// (pct) у полей Word не применяет — такое поле пропускается.
        /// </summary>
        private static double? MarginPt(string? width, W.TableWidthUnitValues? type)
        {
            if (type == W.TableWidthUnitValues.Nil) return 0.0;
            if (type is not null && type != W.TableWidthUnitValues.Dxa) return null;
            if (!TryParseTableWidth(width, out double twips)) return null;
            return Math.Max(twips, 0.0) / 20.0;
        }

        /// <summary>
        /// Поле левой или правой стороны из w:tblCellMar: у него своя единица — только
        /// twips (dxa) или nil.
        /// </summary>
        private static double? MarginPt(string? width, W.TableWidthValues? type)
        {
            if (type == W.TableWidthValues.Nil) return 0.0;
            if (!TryParseTableWidth(width, out double twips)) return null;
            return Math.Max(twips, 0.0) / 20.0;
        }

        /// <summary>Число ширины OOXML: целое или дробное, у процентов бывает со знаком «%».</summary>
        private static bool TryParseTableWidth(string? value, out double result)
        {
            result = 0;
            if (string.IsNullOrWhiteSpace(value)) return false;
            string text = value.Trim();
            bool percentSign = text.EndsWith("%", StringComparison.Ordinal);
            if (percentSign) text = text.Substring(0, text.Length - 1);
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out result)) return false;
            // «50%» — это 50 процентов, то есть 2500 пятидесятых долей.
            if (percentSign) result *= 50.0;
            return true;
        }

        /// <summary>
        /// Направление текста ячейки по значению w:textDirection. Сравнение по тексту:
        /// у переходной схемы (btLr, tbRl, tbRlV, tbLrV, lrTbV) и у строгой (tb, rl, lr,
        /// tbV, rlV, lrV) имена разные, а перечисление SDK знает не все из них.
        /// </summary>
        private static CellTextDirection ResolveCellTextDirection(string? value)
        {
            return value switch
            {
                "btLr" or "lr" => CellTextDirection.BottomToTop,
                "tbRl" or "tbRlV" or "tbLrV" or "rl" or "rlV" or "lrV" => CellTextDirection.TopToBottom,
                _ => CellTextDirection.Horizontal
            };
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

            ApplyParagraphRevisions(p, para, resolver);

            chunk.InvalidateLength();
            return para;
        }

        /// <summary>
        /// Границы ячейки. Своя граница ячейки (w:tcBorders) главнее таблицы; без неё
        /// сторона у края таблицы берёт внешнюю границу, а внутри — внутреннюю линию:
        /// insideH сверху и снизу, insideV слева и справа. Прежде каждая ячейка брала
        /// внешние стороны таблицы, и внутренние линии рисовались внешней рамкой — двойной
        /// красной вместо серого пунктира. Сторона, которую никто не задал, — без линии.
        /// У каждой стороны свои цвет и толщина.
        /// </summary>
        private static CellBorders ResolveCellBorders(
            W.TableCellBorders? cellBorders, TableBorderSet tableBorders,
            bool firstRow, bool lastRow, bool firstColumn, bool lastColumn)
        {
            W.BorderType? top = (W.BorderType?)cellBorders?.TopBorder
                ?? (firstRow ? tableBorders.Top : tableBorders.InsideH);
            W.BorderType? bottom = (W.BorderType?)cellBorders?.BottomBorder
                ?? (lastRow ? tableBorders.Bottom : tableBorders.InsideH);
            W.BorderType? left = (W.BorderType?)cellBorders?.LeftBorder ?? (W.BorderType?)cellBorders?.StartBorder
                ?? (firstColumn ? tableBorders.Left : tableBorders.InsideV);
            W.BorderType? right = (W.BorderType?)cellBorders?.RightBorder ?? (W.BorderType?)cellBorders?.EndBorder
                ?? (lastColumn ? tableBorders.Right : tableBorders.InsideV);

            var result = new CellBorders();
            result.Top = ResolveSideBorder(top, out var topColor, out var topThickness);
            result.Bottom = ResolveSideBorder(bottom, out var bottomColor, out var bottomThickness);
            result.Left = ResolveSideBorder(left, out var leftColor, out var leftThickness);
            result.Right = ResolveSideBorder(right, out var rightColor, out var rightThickness);
            result.Color = topColor ?? bottomColor ?? leftColor ?? rightColor;
            double thickness = new[] { topThickness, bottomThickness, leftThickness, rightThickness }
                .Where(v => v > 0).DefaultIfEmpty(0.5).Average();
            result.ThicknessPt = thickness;

            // Сторона с другим цветом или толщиной помнит свои.
            if (result.Top != BorderStyle.None)
            {
                if (!string.Equals(topColor, result.Color, StringComparison.OrdinalIgnoreCase)) result.TopColor = topColor;
                if (Math.Abs(topThickness - thickness) > 0.001) result.TopThicknessPt = topThickness;
            }
            if (result.Bottom != BorderStyle.None)
            {
                if (!string.Equals(bottomColor, result.Color, StringComparison.OrdinalIgnoreCase)) result.BottomColor = bottomColor;
                if (Math.Abs(bottomThickness - thickness) > 0.001) result.BottomThicknessPt = bottomThickness;
            }
            if (result.Left != BorderStyle.None)
            {
                if (!string.Equals(leftColor, result.Color, StringComparison.OrdinalIgnoreCase)) result.LeftColor = leftColor;
                if (Math.Abs(leftThickness - thickness) > 0.001) result.LeftThicknessPt = leftThickness;
            }
            if (result.Right != BorderStyle.None)
            {
                if (!string.Equals(rightColor, result.Color, StringComparison.OrdinalIgnoreCase)) result.RightColor = rightColor;
                if (Math.Abs(rightThickness - thickness) > 0.001) result.RightThicknessPt = rightThickness;
            }

            return result;
        }

        /// <summary>Сторона ячейки: никем не заданная сторона — без линии, как у Word.</summary>
        private static BorderStyle ResolveSideBorder(W.BorderType? border, out string? color, out double thicknessPt)
        {
            if (border is null)
            {
                color = null;
                thicknessPt = 0;
                return BorderStyle.None;
            }

            var style = ResolveBorderStyle(border, out color, out thicknessPt);
            if (style == BorderStyle.None) thicknessPt = 0;
            return style;
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

            if (borderVal == W.BorderValues.Dashed)
                return BorderStyle.Dashed;

            if (borderVal == W.BorderValues.Dotted)
                return BorderStyle.Dotted;

            if (borderVal == W.BorderValues.Thick)
                return BorderStyle.Thick;

            if (borderVal == W.BorderValues.Triple)
                return BorderStyle.Triple;

            if (borderVal == W.BorderValues.Wave) return BorderStyle.Wave;
            if (borderVal == W.BorderValues.DoubleWave) return BorderStyle.DoubleWave;

            // Штрихпунктирные линии — каждая своим видом, а не общим «штрихом».
            if (borderVal == W.BorderValues.DotDash) return BorderStyle.DotDash;
            if (borderVal == W.BorderValues.DotDotDash) return BorderStyle.DotDotDash;
            if (borderVal == W.BorderValues.DashSmallGap) return BorderStyle.DashSmallGap;
            if (borderVal == W.BorderValues.DashDotStroked) return BorderStyle.DashDotStroked;

            // «Тонкая и толстая»: две или три черты разной толщины с просветом.
            if (borderVal == W.BorderValues.ThinThickSmallGap) return BorderStyle.ThinThickSmallGap;
            if (borderVal == W.BorderValues.ThickThinSmallGap) return BorderStyle.ThickThinSmallGap;
            if (borderVal == W.BorderValues.ThinThickThinSmallGap) return BorderStyle.ThinThickThinSmallGap;
            if (borderVal == W.BorderValues.ThinThickMediumGap) return BorderStyle.ThinThickMediumGap;
            if (borderVal == W.BorderValues.ThickThinMediumGap) return BorderStyle.ThickThinMediumGap;
            if (borderVal == W.BorderValues.ThinThickThinMediumGap) return BorderStyle.ThinThickThinMediumGap;
            if (borderVal == W.BorderValues.ThinThickLargeGap) return BorderStyle.ThinThickLargeGap;
            if (borderVal == W.BorderValues.ThickThinLargeGap) return BorderStyle.ThickThinLargeGap;
            if (borderVal == W.BorderValues.ThinThickThinLargeGap) return BorderStyle.ThinThickThinLargeGap;

            // Объёмные рамки: светлая и тёмная половины.
            if (borderVal == W.BorderValues.ThreeDEmboss) return BorderStyle.ThreeDEmboss;
            if (borderVal == W.BorderValues.ThreeDEngrave) return BorderStyle.ThreeDEngrave;
            if (borderVal == W.BorderValues.Outset) return BorderStyle.Outset;
            if (borderVal == W.BorderValues.Inset) return BorderStyle.Inset;

            return BorderStyle.Single;
        }

        /// <summary>
        /// Цвет заливки ячейки. При сплошном узоре (val="solid") Word красит цветом узора,
        /// при прочих — цветом фона (fill). nil и «auto» — заливки нет.
        /// </summary>
        private static string? NormalizeShadingColor(W.Shading? shading)
        {
            if (shading is null) return null;

            var pattern = shading.Val?.Value;
            if (pattern == W.ShadingPatternValues.Nil) return null;
            if (pattern == W.ShadingPatternValues.Solid)
                return NormalizeHexColor(shading.Color?.Value) ?? NormalizeHexColor(shading.Fill?.Value);

            if (shading.Fill is null) return null;
            return NormalizeHexColor(shading.Fill.Value);
        }

        /// <summary>
        /// Узор заливки ячейки — имя из w:val (pct25, diagStripe…). null — узора нет:
        /// clear, solid (сплошной цвет уже взят как фон) и nil.
        /// </summary>
        private static string? ReadCellShadingPattern(W.Shading? shading)
        {
            string? name = shading?.Val?.InnerText;
            if (string.IsNullOrWhiteSpace(name)) return null;

            if (string.Equals(name, "clear", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "solid", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "nil", StringComparison.OrdinalIgnoreCase))
                return null;

            return name;
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

        /// <summary>Шрифт знаков ASCII (w:ascii). Им же — шрифт рана в модели.</summary>
        public string? FontFamily;

        /// <summary>
        /// Шрифт знаков вне ASCII (w:hAnsi): кириллицы, латиницы с диакритикой,
        /// типографских знаков. Null — уровень каскада о нём молчит, берётся <see cref="FontFamily"/>.
        /// </summary>
        public string? HAnsiFontFamily;

        public double? FontSizePt;
        public bool? Bold;
        public bool? Italic;

        /// <summary>
        /// Шрифт сложных письменностей (w:cs): иврита, арабского, письменностей Индии,
        /// тайского. Ими Word набирает такие знаки вместо w:ascii и w:hAnsi.
        /// </summary>
        public string? ComplexFontFamily;

        /// <summary>
        /// Кегль, жирность и курсив сложных письменностей (w:szCs, w:bCs, w:iCs). У
        /// Word это свойства, отдельные от w:sz, w:b и w:i: иврит в жирном ране без
        /// w:bCs остаётся не жирным.
        /// </summary>
        public double? ComplexFontSizePt;
        public bool? ComplexBold;
        public bool? ComplexItalic;

        /// <summary>
        /// Ран справа налево (w:rtl) или ран сложной письменности (w:cs): Word берёт
        /// для всех его знаков свойства сложных письменностей, какими бы знаки ни были.
        /// </summary>
        public bool? RightToLeftRun;
        public bool? ComplexScriptRun;

        /// <summary>Вид подчёркивания. Null — уровень каскада о подчёркивании молчит.</summary>
        public UnderlineStyle? UnderlineKind;

        /// <summary>
        /// Цвет линии подчёркивания. Приходит вместе с видом из одного элемента w:u,
        /// поэтому уровень, задавший вид, задаёт и цвет — в том числе «авто» (null).
        /// </summary>
        public string? UnderlineColor;
        public bool? Strike;
        public bool? DoubleStrike;
        public bool? Superscript;
        public bool? Subscript;
        public bool? AllCaps;
        public bool? SmallCaps;
        public string? TextColor;
        public string? HighlightColor;
        public string? Language;

        /// <summary>Межбуквенный интервал в пунктах (w:spacing в свойствах рана).</summary>
        public double? CharacterSpacingPt;

        /// <summary>Масштаб знаков по ширине в процентах (w:w).</summary>
        public int? CharacterScalePct;

        /// <summary>Смещение от базовой линии в пунктах, плюс — вверх (w:position).</summary>
        public double? BaselineOffsetPt;

        /// <summary>Скрытый текст (w:vanish).</summary>
        public bool? Hidden;

        /// <summary>Контур, тень, рельеф и гравировка букв (w:outline, w:shadow, w:emboss, w:imprint).</summary>
        public bool? Outline;
        public bool? Shadow;
        public bool? Emboss;
        public bool? Imprint;

        /// <summary>Знак ударения (w:em).</summary>
        public EmphasisMark? Emphasis;

        /// <summary>
        /// Рамка вокруг знаков (w:bdr): толщина в пунктах и цвет. Толщина ноль — уровень
        /// каскада рамку снимает (w:val="none").
        /// </summary>
        public double? CharBorderWidthPt;
        public string? CharBorderColor;

        /// <summary>Вид линии рамки знаков (w:bdr w:val).</summary>
        public CharBorderStyle? CharBorderKind;

        /// <summary>
        /// Настраиваемые эффекты Word 2010+ (w14): свечение, тень, отражение, контур и
        /// полые буквы (w14:textFill без заливки). Читаются DocxTextEffects.
        /// </summary>
        public TextGlowEffect? GlowEffect;
        public TextShadowEffect? ShadowEffect;
        public TextReflectionEffect? ReflectionEffect;
        public TextOutlineEffect? OutlineEffect;
        public bool? HollowFill;

        /// <summary>
        /// Полные настройки эффектов, записанные Writersword (wsx:effects). Если есть —
        /// берутся вместо упрощённых w14.
        /// </summary>
        public TextEffects? ExactEffects;

        public void MergeFrom(
            OpenXmlCompositeElement? container,
            string? themeMajorFont = null,
            string? themeMinorFont = null)
        {
            if (container is null) return;

            var runFonts = container.GetFirstChild<W.RunFonts>();
            if (runFonts is not null)
            {
                // Word выбирает шрифт по знаку: w:ascii — для U+0000–U+007F, w:hAnsi —
                // для остальных европейских знаков, в том числе кириллицы. w:eastAsia и
                // w:cs — только для восточноазиатских и сложных письменностей (арабской,
                // иврита): латиницы и кириллицы они не касаются и здесь не читаются.
                // Прежде w:cs подставлялся, когда ascii и hAnsi не заданы, и текст,
                // который Word набирает шрифтом стиля, уходил в шрифт cs.
                //
                // Современные документы ссылаются не на имя шрифта, а на шрифт темы
                // (w:asciiTheme="minorHAnsi"). Ссылка на тему сильнее имени — так
                // велит OOXML и так делает Word.
                string? ascii = ThemeFontFor(runFonts.AsciiTheme?.InnerText, themeMajorFont, themeMinorFont)
                    ?? runFonts.Ascii?.Value;
                string? hAnsi = ThemeFontFor(runFonts.HighAnsiTheme?.InnerText, themeMajorFont, themeMinorFont)
                    ?? runFonts.HighAnsi?.Value;

                if (!string.IsNullOrEmpty(ascii)) FontFamily = ascii;
                if (!string.IsNullOrEmpty(hAnsi)) HAnsiFontFamily = hAnsi;

                // Шрифт сложных письменностей. Ссылку на него в теме (w:cstheme) не
                // разрешаем: шрифтов письменностей темы здесь нет, и такой уровень
                // каскада молчит.
                string? complex = runFonts.ComplexScript?.Value;
                if (!string.IsNullOrEmpty(complex) && runFonts.ComplexScriptTheme is null)
                    ComplexFontFamily = complex;
            }

            var fontSize = container.GetFirstChild<W.FontSize>();
            if (fontSize?.Val?.Value is string szStr &&
                double.TryParse(szStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var szVal))
                FontSizePt = szVal / HalfPointsPerPoint;

            if (container.GetFirstChild<W.Bold>() is { } b) Bold = b.Val is null || b.Val.Value;
            if (container.GetFirstChild<W.Italic>() is { } it) Italic = it.Val is null || it.Val.Value;

            var complexSize = container.GetFirstChild<W.FontSizeComplexScript>();
            if (complexSize?.Val?.Value is string szCsStr &&
                double.TryParse(szCsStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var szCsVal))
                ComplexFontSizePt = szCsVal / HalfPointsPerPoint;

            if (container.GetFirstChild<W.BoldComplexScript>() is { } bCs) ComplexBold = bCs.Val is null || bCs.Val.Value;
            if (container.GetFirstChild<W.ItalicComplexScript>() is { } iCs) ComplexItalic = iCs.Val is null || iCs.Val.Value;
            if (container.GetFirstChild<W.RightToLeftText>() is { } rtl) RightToLeftRun = rtl.Val is null || rtl.Val.Value;
            if (container.GetFirstChild<W.ComplexScript>() is { } cs) ComplexScriptRun = cs.Val is null || cs.Val.Value;

            if (container.GetFirstChild<W.Underline>() is { } u)
            {
                UnderlineKind = u.Val is null ? UnderlineStyle.None : UnderlineFromOoxml(u.Val.Value);
                UnderlineColor = UnderlineKind != UnderlineStyle.None
                    && u.Color?.Value is string underlineColor
                    && !string.Equals(underlineColor, "auto", StringComparison.OrdinalIgnoreCase)
                        ? ImportService.NormalizeHexColor(underlineColor)
                        : null;
            }

            if (container.GetFirstChild<W.Strike>() is { } s) Strike = s.Val is null || s.Val.Value;
            if (container.GetFirstChild<W.DoubleStrike>() is { } ds) DoubleStrike = ds.Val is null || ds.Val.Value;

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

            // Масштаб по ширине и смещение от базовой линии — тоже со вкладки «Интервал».
            // Смещение хранится в половинах пункта, со знаком.
            if (container.GetFirstChild<W.CharacterScale>()?.Val?.Value is long scalePct)
                CharacterScalePct = (int)Math.Clamp(scalePct, 1L, 600L);

            if (container.GetFirstChild<W.Position>()?.Val?.Value is string positionVal
                && double.TryParse(positionVal, NumberStyles.Float, CultureInfo.InvariantCulture, out var positionHalfPoints))
                BaselineOffsetPt = positionHalfPoints / HalfPointsPerPoint;

            // Скрытый текст и эффекты букв — переключатели, как жирность: элемент без
            // w:val включает, w:val="0" снимает унаследованное.
            if (container.GetFirstChild<W.Vanish>() is { } vanish) Hidden = vanish.Val is null || vanish.Val.Value;
            if (container.GetFirstChild<W.Outline>() is { } outline) Outline = outline.Val is null || outline.Val.Value;
            if (container.GetFirstChild<W.Shadow>() is { } shadow) Shadow = shadow.Val is null || shadow.Val.Value;
            if (container.GetFirstChild<W.Emboss>() is { } emboss) Emboss = emboss.Val is null || emboss.Val.Value;
            if (container.GetFirstChild<W.Imprint>() is { } imprint) Imprint = imprint.Val is null || imprint.Val.Value;

            if (container.GetFirstChild<W.Emphasis>()?.Val?.Value is { } emphasisVal)
                Emphasis = EmphasisFromOoxml(emphasisVal);

            // Рамка знаков: толщина w:sz — в восьмых долях пункта.
            if (container.GetFirstChild<W.Border>() is { } charBorder)
            {
                bool none = charBorder.Val is null
                    || charBorder.Val.Value == W.BorderValues.None
                    || charBorder.Val.Value == W.BorderValues.Nil;

                if (none)
                {
                    CharBorderWidthPt = 0;
                    CharBorderColor = null;
                }
                else
                {
                    double eighths = charBorder.Size?.Value ?? 4;
                    CharBorderWidthPt = Math.Max(0.25, eighths / 8.0);
                    CharBorderColor = charBorder.Color?.Value is string borderColor
                        && !string.Equals(borderColor, "auto", StringComparison.OrdinalIgnoreCase)
                            ? ImportService.NormalizeHexColor(borderColor)
                            : null;
                    CharBorderKind = CharBorderStyleFromOoxml(charBorder.Val!.Value);
                }
            }

            // Настраиваемые эффекты Word 2010+ и полные настройки Writersword.
            DocxTextEffects.Read(container, this);
        }

        /// <summary>
        /// Вид подчёркивания Word в вид модели. Неизвестное значение читается как
        /// одинарная линия: подчёркивание в документе есть, и терять его нельзя.
        /// </summary>
        internal static UnderlineStyle UnderlineFromOoxml(W.UnderlineValues v)
        {
            if (v == W.UnderlineValues.None) return UnderlineStyle.None;
            if (v == W.UnderlineValues.Single) return UnderlineStyle.Single;
            if (v == W.UnderlineValues.Words) return UnderlineStyle.Words;
            if (v == W.UnderlineValues.Double) return UnderlineStyle.Double;
            if (v == W.UnderlineValues.Thick) return UnderlineStyle.Thick;
            if (v == W.UnderlineValues.Dotted) return UnderlineStyle.Dotted;
            if (v == W.UnderlineValues.DottedHeavy) return UnderlineStyle.DottedHeavy;
            if (v == W.UnderlineValues.Dash) return UnderlineStyle.Dash;
            if (v == W.UnderlineValues.DashedHeavy) return UnderlineStyle.DashedHeavy;
            if (v == W.UnderlineValues.DashLong) return UnderlineStyle.DashLong;
            if (v == W.UnderlineValues.DashLongHeavy) return UnderlineStyle.DashLongHeavy;
            if (v == W.UnderlineValues.DotDash) return UnderlineStyle.DotDash;
            if (v == W.UnderlineValues.DashDotHeavy) return UnderlineStyle.DashDotHeavy;
            if (v == W.UnderlineValues.DotDotDash) return UnderlineStyle.DotDotDash;
            if (v == W.UnderlineValues.DashDotDotHeavy) return UnderlineStyle.DashDotDotHeavy;
            if (v == W.UnderlineValues.Wave) return UnderlineStyle.Wave;
            if (v == W.UnderlineValues.WavyHeavy) return UnderlineStyle.WavyHeavy;
            if (v == W.UnderlineValues.WavyDouble) return UnderlineStyle.WavyDouble;
            return UnderlineStyle.Single;
        }

        /// <summary>
        /// Вид линии рамки знаков Word в вид модели. Виды, которых у модели нет,
        /// читаются ближайшим: двойные и тройные — двойной, штрих-пунктиры — штрихами.
        /// </summary>
        private static CharBorderStyle CharBorderStyleFromOoxml(W.BorderValues v)
        {
            if (v == W.BorderValues.Double || v == W.BorderValues.Triple
                || v == W.BorderValues.DoubleWave
                || v == W.BorderValues.ThinThickSmallGap || v == W.BorderValues.ThickThinSmallGap
                || v == W.BorderValues.ThinThickMediumGap || v == W.BorderValues.ThickThinMediumGap
                || v == W.BorderValues.ThinThickLargeGap || v == W.BorderValues.ThickThinLargeGap)
                return CharBorderStyle.Double;
            if (v == W.BorderValues.Dotted) return CharBorderStyle.Dotted;
            if (v == W.BorderValues.Dashed || v == W.BorderValues.DashSmallGap
                || v == W.BorderValues.DotDash || v == W.BorderValues.DotDotDash
                || v == W.BorderValues.DashDotStroked)
                return CharBorderStyle.Dashed;
            if (v == W.BorderValues.Thick) return CharBorderStyle.Thick;
            return CharBorderStyle.Single;
        }

        /// <summary>Знак ударения Word в вид модели.</summary>
        private static EmphasisMark EmphasisFromOoxml(W.EmphasisMarkValues v)
        {
            if (v == W.EmphasisMarkValues.Dot) return EmphasisMark.Dot;
            if (v == W.EmphasisMarkValues.Comma) return EmphasisMark.Comma;
            if (v == W.EmphasisMarkValues.Circle) return EmphasisMark.Circle;
            if (v == W.EmphasisMarkValues.UnderDot) return EmphasisMark.UnderDot;
            return EmphasisMark.None;
        }

        /// <summary>
        /// Шрифт темы по ссылке w:asciiTheme / w:hAnsiTheme: major… — шрифт заголовков,
        /// остальное — шрифт текста. Null — ссылки нет или тема шрифта не задаёт.
        /// </summary>
        private static string? ThemeFontFor(string? themeRef, string? themeMajorFont, string? themeMinorFont)
        {
            if (string.IsNullOrEmpty(themeRef)) return null;

            string? font = themeRef.StartsWith("major", StringComparison.OrdinalIgnoreCase)
                ? themeMajorFont
                : themeMinorFont;

            return string.IsNullOrEmpty(font) ? null : font;
        }

        /// <summary>
        /// Цвет маркера Word по имени. Палитра — вордовская (16 цветов VGA), а не
        /// одноимённые цвета CSS: у CSS «darkGray» светлее «gray», «darkBlue» и
        /// «darkGreen» другие, и маркер выходил не того цвета, что в Word.
        /// </summary>
        private static string? HighlightToHex(W.HighlightColorValues v)
        {
            if (v == W.HighlightColorValues.Yellow) return "#FFFF00";
            if (v == W.HighlightColorValues.Green) return "#00FF00";
            if (v == W.HighlightColorValues.Cyan) return "#00FFFF";
            if (v == W.HighlightColorValues.Magenta) return "#FF00FF";
            if (v == W.HighlightColorValues.Blue) return "#0000FF";
            if (v == W.HighlightColorValues.Red) return "#FF0000";
            if (v == W.HighlightColorValues.DarkBlue) return "#000080";
            if (v == W.HighlightColorValues.DarkCyan) return "#008080";
            if (v == W.HighlightColorValues.DarkGreen) return "#008000";
            if (v == W.HighlightColorValues.DarkMagenta) return "#800080";
            if (v == W.HighlightColorValues.DarkRed) return "#800000";
            if (v == W.HighlightColorValues.DarkYellow) return "#808000";
            if (v == W.HighlightColorValues.DarkGray) return "#808080";
            if (v == W.HighlightColorValues.LightGray) return "#C0C0C0";
            if (v == W.HighlightColorValues.Black) return "#000000";
            return null;
        }

        public RunFormat Clone() => (RunFormat)MemberwiseClone();

        /// <summary>
        /// Свойства знаков сложных письменностей этого рана: шрифт w:cs, кегль w:szCs,
        /// жирность w:bCs и курсив w:iCs поверх остальных свойств рана. Шрифт и кегль,
        /// которых каскад не задал, берутся обычные. null — у сложных письменностей
        /// те же шрифт, кегль, жирность и курсив, что у остального текста, и делить
        /// ран незачем.
        /// </summary>
        public RunProperties? ToComplexScriptProperties(RunProperties baseProps)
        {
            string? family = string.IsNullOrEmpty(ComplexFontFamily) ? baseProps.FontFamily : ComplexFontFamily;
            double? size = ComplexFontSizePt ?? baseProps.FontSize;
            bool bold = ComplexBold == true;
            bool italic = ComplexItalic == true;

            bool same = string.Equals(family, baseProps.FontFamily, StringComparison.Ordinal)
                && Nullable.Equals(size, baseProps.FontSize)
                && bold == (baseProps.IsBold == true)
                && italic == (baseProps.IsItalic == true);
            if (same) return null;

            var props = baseProps.Clone();
            props.FontFamily = family;
            props.FontSize = size;
            props.IsBold = bold;
            props.IsItalic = italic;
            return props;
        }

        public RunProperties ToRunProperties() => new()
        {
            FontFamily = FontFamily,
            FontSize = FontSizePt,
            IsBold = Bold == true,
            IsItalic = Italic == true,
            UnderlineStyle = UnderlineKind ?? UnderlineStyle.None,
            UnderlineColor = (UnderlineKind ?? UnderlineStyle.None) != UnderlineStyle.None ? UnderlineColor : null,
            IsStrikethrough = Strike == true,
            IsDoubleStrikethrough = DoubleStrike == true,
            IsSuperscript = Superscript == true,
            IsSubscript = Subscript == true,
            IsAllCaps = AllCaps == true,
            IsSmallCaps = SmallCaps == true,
            CharacterSpacing = CharacterSpacingPt is double spacing && Math.Abs(spacing) > 0.001
                ? spacing
                : null,
            CharacterScale = CharacterScalePct is int scale && scale > 0 && scale != 100 ? scale : null,
            BaselineOffset = BaselineOffsetPt is double offset && Math.Abs(offset) > 0.001 ? offset : null,
            IsHidden = Hidden == true,
            IsOutline = Outline == true,
            IsShadow = Shadow == true,
            IsEmboss = Emboss == true,
            IsImprint = Imprint == true,
            EmphasisMark = Emphasis ?? EmphasisMark.None,
            CharBorderWidthPt = CharBorderWidthPt is double borderWidth && borderWidth > 0 ? borderWidth : null,
            CharBorderColor = CharBorderWidthPt is double borderWidthForColor && borderWidthForColor > 0 ? CharBorderColor : null,
            CharBorderStyle = CharBorderWidthPt is double borderWidthForStyle && borderWidthForStyle > 0
                ? CharBorderKind ?? CharBorderStyle.Single
                : CharBorderStyle.Single,
            Effects = ExactEffects ?? TextEffects.Normalize(new TextEffects
            {
                Outline = OutlineEffect is { } outline && HollowFill == true ? outline with { Hollow = true } : OutlineEffect,
                Shadow = ShadowEffect,
                Glow = GlowEffect,
                Reflection = ReflectionEffect
            }),
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

        /// <summary>Интервал «Авто» до и после абзаца, который подставляет Word, в пунктах.</summary>
        private const double AutoParagraphSpacingPt = 14.0;

        public LineSpacingRule? LineRule;
        public double? LineValue;
        public bool? KeepTogether, KeepWithNext, PageBreakBefore;
        public int? OutlineLevel;

        /// <summary>
        /// Выравнивание знаков по высоте строки (w:textAlignment). null — уровень
        /// каскада молчит.
        /// </summary>
        public Models.Styles.LineTextAlignment? LineTextAlign;

        /// <summary>Абзац справа налево (w:bidi). null — уровень каскада молчит.</summary>
        public bool? Bidi;

        /// <summary>
        /// «Не добавлять интервал между абзацами одного стиля» (w:contextualSpacing).
        /// null — уровень каскада молчит. Word держит его обычно в стиле («Абзац
        /// списка»), и абзацы стиля своего флага не несут.
        /// </summary>
        public bool? ContextualSpacing;

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
        /// Узор заливки (w:shd w:val, кроме clear/solid/nil) и его цвет (w:color).
        /// Уровень каскада, у которого есть w:shd, задаёт их вместе с цветом заливки:
        /// заливка у Word — одно свойство, и узор стиля под сплошной заливкой абзаца
        /// не просвечивает. Пустая строка — узора нет.
        /// </summary>
        public string? ShadingPattern;
        public string? ShadingPatternColor;

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

                // Автоинтервал («Авто» в окне «Абзац», w:beforeAutospacing / w:afterAutospacing)
                // перекрывает число в w:before / w:after: Word ставит на его место 14 пт, как
                // отступы абзаца в HTML. Без этого абзац терял оба интервала и слипался с
                // соседями.
                if (sp.BeforeAutoSpacing?.Value == true) SpaceBeforePt = AutoParagraphSpacingPt;
                if (sp.AfterAutoSpacing?.Value == true) SpaceAfterPt = AutoParagraphSpacingPt;

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

            // Флаг-переключатель: без w:val включён, w:val="0" (false, off) — снят, и
            // абзац так отказывается от правила, данного ему стилем.
            if (container.GetFirstChild<W.ContextualSpacing>() is { } contextual)
                ContextualSpacing = contextual.Val?.Value ?? true;

            // Флаг-переключатель: w:val="0" снимает направление, данное стилем.
            if (container.GetFirstChild<W.BiDi>() is { } bidi)
                Bidi = bidi.Val?.Value ?? true;

            if (container.GetFirstChild<W.TextAlignment>()?.Val?.InnerText is { Length: > 0 } textAlign)
                LineTextAlign = MapLineTextAlignment(textAlign);

            if (container.GetFirstChild<W.OutlineLevel>()?.Val?.Value is int ol) OutlineLevel = ol;

            MergeTabs(container.GetFirstChild<W.Tabs>());
            MergeBorders(container.GetFirstChild<W.ParagraphBorders>());

            if (container.GetFirstChild<W.Shading>() is { } shading)
            {
                Shading = ReadParagraphShading(shading);
                ShadingPattern = ReadParagraphShadingPattern(shading) ?? string.Empty;
                ShadingPatternColor = ShadingPattern.Length > 0
                    ? ImportService.NormalizeHexColor(shading.Color?.Value) ?? string.Empty
                    : string.Empty;
            }

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
        /// Значение w:textAlignment в модель. Незнакомое — «авто».
        /// </summary>
        private static Models.Styles.LineTextAlignment MapLineTextAlignment(string value)
        {
            if (string.Equals(value, "top", StringComparison.OrdinalIgnoreCase))
                return Models.Styles.LineTextAlignment.Top;
            if (string.Equals(value, "center", StringComparison.OrdinalIgnoreCase))
                return Models.Styles.LineTextAlignment.Center;
            if (string.Equals(value, "bottom", StringComparison.OrdinalIgnoreCase))
                return Models.Styles.LineTextAlignment.Bottom;
            if (string.Equals(value, "baseline", StringComparison.OrdinalIgnoreCase))
                return Models.Styles.LineTextAlignment.Baseline;

            return Models.Styles.LineTextAlignment.Auto;
        }

        /// <summary>
        /// Узор заливки — имя из w:val, как его пишет Word (pct25, diagStripe,
        /// thinHorzCross…). null — узора нет: clear (только цвет фона), solid (сплошной
        /// цвет узора, его уже взял <see cref="ReadParagraphShading"/>) и nil.
        /// </summary>
        private static string? ReadParagraphShadingPattern(W.Shading shading)
        {
            string? name = shading.Val?.InnerText;
            if (string.IsNullOrWhiteSpace(name)) return null;

            if (string.Equals(name, "clear", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "solid", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "nil", StringComparison.OrdinalIgnoreCase))
                return null;

            return name;
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
            if (v == W.BorderValues.Triple) return BorderStyle.Triple;

            // Волна и двойная волна: у рамки абзаца одна волнистая линия.
            if (v == W.BorderValues.Wave || v == W.BorderValues.DoubleWave) return BorderStyle.Wave;

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

            // Bar — вертикальная черта на позиции, а не прыжок текста: вёрстка рисует
            // её через строки абзаца, а табуляцию ведёт мимо неё.
            if (kind == W.TabStopValues.Bar) return Models.Styles.TabAlignment.Bar;

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
            // Выравнивание записывается явно, даже когда файл о нём молчит, — по той
            // же причине, что и интервалы ниже. Пустое свойство значит «взять из стиля
            // Writersword», а стиль, в который лёг абзац Word, может выравнивать иначе:
            // «Цитата» Writersword выровнена по ширине, а Quote и Intense Quote в Word
            // — по левому краю. Без явного значения обе цитаты расползались по ширине.
            // По стандарту отсутствие w:jc — выравнивание по левому краю.
            Models.Styles.TextAlignment alignment = Models.Styles.TextAlignment.Left;

            if (Justification == W.JustificationValues.Center) alignment = Models.Styles.TextAlignment.Center;
            else if (Justification == W.JustificationValues.Right) alignment = Models.Styles.TextAlignment.Right;
            else if (Justification == W.JustificationValues.Both) alignment = Models.Styles.TextAlignment.Justify;
            else if (Justification == W.JustificationValues.Left) alignment = Models.Styles.TextAlignment.Left;

            // Логические края: у абзаца, набранного слева направо, начало строки —
            // левый край, конец — правый.
            else if (Justification == W.JustificationValues.Start) alignment = Models.Styles.TextAlignment.Left;
            else if (Justification == W.JustificationValues.End) alignment = Models.Styles.TextAlignment.Right;

            // Растянутое — по ширине вместе с последней строкой, её буквы Word разводит
            // на всю ширину. Тайская растяжка — то же самое для тайского письма.
            else if (Justification == W.JustificationValues.Distribute
                     || Justification == W.JustificationValues.ThaiDistribute)
                alignment = Models.Styles.TextAlignment.Distribute;

            // Растяжки арабского письма (кашида): ближе всего к ним выравнивание по ширине.
            else if (Justification == W.JustificationValues.HighKashida
                     || Justification == W.JustificationValues.MediumKashida
                     || Justification == W.JustificationValues.LowKashida)
                alignment = Models.Styles.TextAlignment.Justify;

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
                ContextualSpacing = ContextualSpacing ?? false,
                // Word считает уровни от нуля: outlineLvl=0 у «Заголовка 1». В модели
                // Writersword ноль означает обычный текст, а главы идут с единицы, и
                // экспорт вычитает единицу обратно. Без сдвига круг docx → рукопись →
                // docx поднимал бы каждый заголовок на уровень вверх.
                //
                // Девятка у Word — не десятый уровень, а пометка «основной текст»; такой
                // абзац заголовком не становится.
                OutlineLevel = OutlineLevel is int lvl && lvl >= 0 && lvl <= 8 ? lvl + 1 : 0,

                LineTextAlignment = LineTextAlign ?? Models.Styles.LineTextAlignment.Auto,

                RightToLeft = Bidi ?? false,

                // Позиции копируются, а не отдаются ссылкой: каскад держит свой список
                // и переиспользует его для следующих абзацев того же стиля.
                TabStops = CloneTabs(TabStops),

                // Рамка — только из видимых сторон. Снятые стороны своё дело сделали
                // при слиянии каскада, в модели им делать нечего.
                Borders = BuildBorders(),

                // Заливка, снятая на нижнем уровне каскада, в модель не идёт вовсе.
                ShadingColor = string.IsNullOrEmpty(Shading) ? null : Shading,
                ShadingPattern = string.IsNullOrEmpty(ShadingPattern) ? null : ShadingPattern,
                ShadingPatternColor = string.IsNullOrEmpty(ShadingPattern) || string.IsNullOrEmpty(ShadingPatternColor)
                    ? null
                    : ShadingPatternColor
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
    /// <see cref="ListProperties"/> Writersword.
    ///
    /// Счёт у Word ведёт определение нумерации (w:abstractNum), а не экземпляр (w:num):
    /// абзацы разных w:numId на одном определении продолжают один и тот же счёт. Поэтому
    /// один список Writersword — одно определение. Экземпляр может переопределить уровень
    /// (w:lvlOverride): начальный номер (w:startOverride) перезапускает счёт с первого
    /// своего абзаца на этом уровне, а свой w:lvl меняет вид номера у своих абзацев.
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

            /// <summary>Уровень целиком, как его рисует Word.</summary>
            public WordListLevel Word = new();
        }

        /// <summary>Экземпляр нумерации (w:num) с уже применёнными переопределениями уровней.</summary>
        private sealed class NumDef
        {
            public int AbstractId;
            public Dictionary<int, LevelDef> Levels = new();

            /// <summary>Перезапуск счёта уровня (w:lvlOverride/w:startOverride): уровень → номер.</summary>
            public Dictionary<int, int> StartOverrides = new();
        }

        private readonly Dictionary<int, NumDef> _byNumId = new();

        /// <summary>
        /// Уровни экземпляров, чей перезапуск счёта уже применён: w:startOverride действует
        /// один раз — на первом абзаце экземпляра на этом уровне, дальше счёт продолжается.
        /// </summary>
        private readonly HashSet<(int NumId, int Level)> _startOverrideApplied = new();

        /// <summary>Язык документа (w:docDefaults/w:lang): от него слова в номерах «Один», «1-й».</summary>
        private readonly string? _language;

        public DocxNumberingMap(DocumentFormat.OpenXml.Packaging.MainDocumentPart mainPart)
        {
            _language = mainPart.StyleDefinitionsPart?.Styles?.DocDefaults?.RunPropertiesDefault
                ?.RunPropertiesBaseStyle?.GetFirstChild<W.Languages>()?.Val?.Value;

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

                var def = new NumDef { AbstractId = aid };
                foreach (var level in abs.Elements<W.Level>())
                {
                    int ilvl = level.LevelIndex?.Value ?? 0;
                    def.Levels[ilvl] = ParseLevel(level, previous: null);
                }

                // Переопределения экземпляра: свой уровень целиком и/или перезапуск счёта.
                foreach (var levelOverride in inst.Elements<W.LevelOverride>())
                {
                    int ilvl = levelOverride.LevelIndex?.Value ?? 0;

                    if (levelOverride.GetFirstChild<W.Level>() is { } overrideLevel)
                    {
                        def.Levels.TryGetValue(ilvl, out var baseLevel);
                        def.Levels[ilvl] = ParseLevel(overrideLevel, baseLevel);
                    }

                    if (levelOverride.StartOverrideNumberingValue?.Val?.Value is int startOverride)
                        def.StartOverrides[ilvl] = startOverride;
                }

                _byNumId[nid] = def;
            }
        }

        /// <summary>
        /// Уровень Word. Уровень из переопределения экземпляра наследует у уровня
        /// определения всё, чего сам не задаёт.
        /// </summary>
        private static LevelDef ParseLevel(W.Level level, LevelDef? previous)
        {
            var fmt = level.NumberingFormat?.Val?.Value
                ?? (previous is null ? W.NumberFormatValues.Bullet : (W.NumberFormatValues?)null);
            string? lvlTextRaw = level.LevelText?.Val?.Value;
            string lvlText = lvlTextRaw ?? previous?.Word.Text ?? string.Empty;

            var def = new LevelDef
            {
                StartAt = level.StartNumberingValue?.Val?.Value ?? previous?.StartAt ?? 1
            };

            if (fmt is W.NumberFormatValues format)
            {
                MapFormat(format, lvlText, def);
                def.Word.Format = MapWordFormat(format, def.MarkerType);
            }
            else
            {
                def.MarkerType = previous!.MarkerType;
                def.CustomMarker = previous.CustomMarker;
                def.NumberPrefix = previous.NumberPrefix;
                def.NumberSuffix = previous.NumberSuffix;
                def.Word.Format = previous.Word.Format;
            }

            def.Word.Start = def.StartAt;
            def.Word.Text = lvlText;
            def.Word.Alignment = MapAlignment(level.LevelJustification?.Val?.InnerText)
                ?? previous?.Word.Alignment ?? ListMarkerAlignment.Left;
            def.Word.Suffix = MapSuffix(level.LevelSuffix?.Val?.InnerText)
                ?? previous?.Word.Suffix ?? ListMarkerSuffix.Tab;

            var fonts = level.NumberingSymbolRunProperties?.GetFirstChild<W.RunFonts>();
            string? markerFont = fonts?.Ascii?.Value ?? fonts?.HighAnsi?.Value;
            def.Word.FontFamily = !string.IsNullOrWhiteSpace(markerFont) ? markerFont : previous?.Word.FontFamily;

            // Маркированный уровень из символьного шрифта: знак для списков Writersword —
            // его юникодный двойник, а сам уровень Word хранит исходный знак и шрифт.
            if (!def.Word.IsNumbered && def.MarkerType == ListMarkerType.Custom
                && SymbolFontMarkers.ToUnicode(def.Word.FontFamily, lvlText) is { } unicodeMarker)
                def.CustomMarker = unicodeMarker;

            if (previous is not null)
            {
                def.LeftIndentPt = previous.LeftIndentPt;
                def.FirstLineIndentPt = previous.FirstLineIndentPt;
                def.LinkedStyleId = previous.LinkedStyleId;
            }

            ReadLevelIndentation(level, def);
            if (level.ParagraphStyleIdInLevel?.Val?.Value is { } linkedStyle)
                def.LinkedStyleId = linkedStyle;

            return def;
        }

        /// <summary>
        /// Формат номера уровня Word. Форматы, которых у списков Writersword нет, получают
        /// свои значения; незнакомый формат пишется цифрами, как у Word без поддержки языка.
        /// «Без номера» (none) — цифры с пустым шаблоном: счёт идёт, номер не виден.
        /// </summary>
        private static ListMarkerType MapWordFormat(W.NumberFormatValues fmt, ListMarkerType mapped)
        {
            if (fmt == W.NumberFormatValues.Ordinal) return ListMarkerType.Ordinal;
            if (fmt == W.NumberFormatValues.CardinalText) return ListMarkerType.CardinalText;
            if (fmt == W.NumberFormatValues.OrdinalText) return ListMarkerType.OrdinalText;
            if (fmt == W.NumberFormatValues.RussianLower) return ListMarkerType.RussianLower;
            if (fmt == W.NumberFormatValues.RussianUpper) return ListMarkerType.RussianUpper;
            if (fmt == W.NumberFormatValues.Chicago) return ListMarkerType.Chicago;
            if (fmt == W.NumberFormatValues.Bullet) return mapped;
            return (int)mapped >= 10 ? mapped : ListMarkerType.Decimal;
        }

        private static ListMarkerAlignment? MapAlignment(string? value) => value switch
        {
            "left" or "start" => ListMarkerAlignment.Left,
            "center" => ListMarkerAlignment.Center,
            "right" or "end" => ListMarkerAlignment.Right,
            _ => null
        };

        private static ListMarkerSuffix? MapSuffix(string? value) => value switch
        {
            "tab" => ListMarkerSuffix.Tab,
            "space" => ListMarkerSuffix.Space,
            "nothing" => ListMarkerSuffix.Nothing,
            _ => null
        };

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

        /// <summary>
        /// Знак маркированного уровня. Тип Writersword даётся, только когда его знак тот же,
        /// что у Word: «o» из Courier New, «○», дефис — это не «◦» и не тире, и рисовать их
        /// надо как есть. Иначе знак остаётся своим (Custom).
        /// </summary>
        private static ListMarkerType MapBulletChar(string ch, out string? custom)
        {
            custom = null;
            if (ch.Length == 0)
            {
                custom = string.Empty;
                return ListMarkerType.Custom;
            }

            switch (ch)
            {
                case "•": return ListMarkerType.Bullet;
                case "–": return ListMarkerType.Dash;
                case "▪": return ListMarkerType.Square;
                case "◦": return ListMarkerType.Circle;
                case "➤": return ListMarkerType.Arrow;
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

            def.LeftIndentPt = Twips(ind.Left?.Value) ?? Twips(ind.Start?.Value) ?? def.LeftIndentPt;

            if (Twips(ind.Hanging?.Value) is double hanging) def.FirstLineIndentPt = -hanging;
            else if (Twips(ind.FirstLine?.Value) is double firstLine) def.FirstLineIndentPt = firstLine;
        }

        /// <summary>
        /// Свойства списка для параграфа по действующей нумерации — своей или стиля;
        /// null — абзац не в списке. listIdMap переиспользуется на весь импорт документа —
        /// абзацы на одном определении нумерации (w:abstractNum) получают один и тот же
        /// Guid ListId: у Word это один счёт, какой бы w:numId ни стоял у абзаца.
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
            if (!_byNumId.TryGetValue(nid, out var num)) return null;
            var levels = num.Levels;

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

            if (!listIdMap.TryGetValue(num.AbstractId, out var listGuid))
            {
                listGuid = Guid.NewGuid();
                listIdMap[num.AbstractId] = listGuid;
            }

            // Уровни Word по порядку: номер уровня 2 в «%1.%2.» собирается и из уровня 1.
            int maxLevel = levels.Count > 0 ? levels.Keys.Max() : 0;
            var wordLevels = new List<WordListLevel>(maxLevel + 1);
            for (int i = 0; i <= maxLevel; i++)
                wordLevels.Add(levels.TryGetValue(i, out var levelDef) ? levelDef.Word.Clone() : new WordListLevel { Format = ListMarkerType.Decimal, Text = string.Empty });

            var result = new ListProperties
            {
                ListId = listGuid,
                Level = Math.Clamp(ilvl, 0, 8),
                MarkerType = def.MarkerType,
                CustomMarker = def.CustomMarker,
                NumberPrefix = def.NumberPrefix,
                NumberSuffix = def.NumberSuffix,
                StartAt = def.StartAt,
                MarkerIndentPt = markerIndent,
                WordLevels = wordLevels,
                NumberLanguage = _language
            };

            // Перезапуск счёта экземпляром — на первом его абзаце этого уровня.
            if (num.StartOverrides.TryGetValue(ilvl, out int startOverride)
                && _startOverrideApplied.Add((nid, ilvl)))
            {
                result.ContinueNumbering = false;
                result.StartAt = startOverride;
            }

            return result;
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

    /// <summary>
    /// Знаки маркеров из символьных шрифтов Word (Symbol, Wingdings) и их юникодные
    /// двойники. Word хранит такой маркер кодом шрифта — U+F0B7 в Symbol, U+F0A7 в
    /// Wingdings, — и без самого шрифта этот код рисуется пустым квадратом.
    /// </summary>
    internal static class SymbolFontMarkers
    {
        private static readonly Dictionary<int, string> SymbolMap = new()
        {
            [0xB7] = "•", // маркер
            [0xA8] = "♦", // ромб
            [0xAA] = "♠", // пика
            [0xA9] = "♥", // червы
            [0xA7] = "♣", // трефы
            [0xD8] = "¬",
            [0x2D] = "−",
            [0x2A] = "∗",
            [0xAE] = "→"
        };

        private static readonly Dictionary<int, string> WingdingsMap = new()
        {
            [0xA7] = "▪", // малый квадрат
            [0xA8] = "◻", // квадрат
            [0x6C] = "●", // круг
            [0x6E] = "■", // квадрат залитый
            [0x71] = "❑",
            [0x75] = "◆", // ромб
            [0x76] = "❖",
            [0xA1] = "○", // окружность
            [0xAB] = "★", // звезда
            [0xD8] = "➢", // стрелка
            [0xE0] = "→",
            [0xFC] = "✔", // галочка
            [0xFB] = "✖",
            [0xFE] = "☑",
            [0x9F] = "•"
        };

        /// <summary>Шрифт символьный: его коды — не юникод, а номера знаков шрифта.</summary>
        public static bool IsSymbolFont(string? family) =>
            family is not null
            && (family.Equals("Symbol", StringComparison.OrdinalIgnoreCase)
                || family.StartsWith("Wingdings", StringComparison.OrdinalIgnoreCase)
                || family.StartsWith("Webdings", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Юникодный двойник маркера символьного шрифта; null — шрифт не символьный или
        /// знак неизвестен (тогда рисуется как есть).
        /// </summary>
        public static string? ToUnicode(string? family, string text)
        {
            if (!IsSymbolFont(family) || text.Length != 1) return null;

            int code = text[0];
            if (code >= 0xF000 && code <= 0xF0FF) code -= 0xF000;

            var map = family!.Equals("Symbol", StringComparison.OrdinalIgnoreCase) ? SymbolMap : WingdingsMap;
            return map.TryGetValue(code, out var unicode) ? unicode : null;
        }
    }
}
