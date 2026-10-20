using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Models.Styles;

namespace Writersword.Modules.TextEditor.Rendering
{
    /// <summary>
    /// Что из правок рецензирования видно на листе при выбранном виде
    /// (<see cref="RevisionView"/>). Одни правила на экран, печать и PDF.
    ///
    ///   «Все исправления» — видно всё: вставленное подчёркнуто, удалённое
    ///   зачёркнуто, цветом автора; у строк с правками на поле черта.
    ///   «Простая разметка» и «Без исправлений» — текст как после принятия всех
    ///   правок: удалённого нет; у первой на поле красная черта.
    ///   «Исходный документ» — текст как до правок: вставленного нет, у сменивших
    ///   оформление фрагментов и абзацев — прежнее оформление.
    ///
    /// Спрятанное правкой остаётся в абзаце и не занимает места, как скрытый текст:
    /// каретка его перешагивает.
    /// </summary>
    public static class RevisionDisplay
    {
        /// <summary>Пометки правок (подчёркивание, зачёркивание, цвет автора) рисуются.</summary>
        public static bool ShowsMarkup(RevisionView view) => view == RevisionView.AllMarkup;

        /// <summary>У строк с правками на поле рисуется черта.</summary>
        public static bool ShowsChangeBars(RevisionView view)
            => view == RevisionView.AllMarkup || view == RevisionView.SimpleMarkup;

        /// <summary>Вид показывает текст как до правок.</summary>
        public static bool ShowsOriginal(RevisionView view) => view == RevisionView.Original;

        // Двойники картинок и фигур со сменой вида для «Исходного документа»: копия
        // с прежним видом. Двойник пересоздаётся, когда сменился вид объекта или его
        // прежний вид.
        private sealed class ObjectTwin
        {
            public string Key = string.Empty;
            public BlockModel Block = null!;
        }

        private static readonly ConditionalWeakTable<BlockModel, ObjectTwin> Twins = new();
        private static readonly ConditionalWeakTable<BlockModel, BlockModel> TwinOwners = new();
        private static readonly object TwinsLock = new();

        /// <summary>
        /// Картинка или фигура для вёрстки и рисования. В «Исходном документе» объект,
        /// которому правка сменила вид, показывается прежним: на прежнем месте, в
        /// прежнем размере, с прежним поворотом и обрезкой — двойником с тем же Id.
        /// Двойник только для показа: выделить и править его нельзя
        /// (<see cref="IsDisplayTwin"/>).
        /// </summary>
        public static BlockModel DisplayObject(BlockModel block, RevisionView view)
        {
            if (view != RevisionView.Original || block is not IAppearanceTracked { Change: { } change })
                return block;

            string key = change.Previous + "\u0001" + Services.ObjectAppearance.Capture(block)
                         + "\u0001" + (block as ImageBlock)?.ImageFileName;

            lock (TwinsLock)
            {
                if (Twins.TryGetValue(block, out var cached) && cached.Key == key)
                    return cached.Block;

                var twin = Services.DocumentCloner.CloneBlock(block);
                Services.ObjectAppearance.Restore(twin, change.Previous);

                Twins.AddOrUpdate(block, new ObjectTwin { Key = key, Block = twin });
                TwinOwners.AddOrUpdate(twin, block);
                return twin;
            }
        }

        /// <summary>Картинка для вёрстки и рисования — см. <see cref="DisplayObject"/>.</summary>
        public static ImageBlock DisplayImage(ImageBlock image, RevisionView view)
            => DisplayObject(image, view) as ImageBlock ?? image;

        /// <summary>Фигура для вёрстки и рисования — см. <see cref="DisplayObject"/>.</summary>
        public static ShapeBlock DisplayShape(ShapeBlock shape, RevisionView view)
            => DisplayObject(shape, view) as ShapeBlock ?? shape;

        /// <summary>Блок — двойник объекта для показа прежнего вида, а не сам объект документа.</summary>
        public static bool IsDisplayTwin(BlockModel block)
        {
            lock (TwinsLock) return TwinOwners.TryGetValue(block, out _);
        }

        /// <summary>Объект документа, которому принадлежит двойник; не двойник — сам блок.</summary>
        public static BlockModel RealObject(BlockModel block)
        {
            lock (TwinsLock) return TwinOwners.TryGetValue(block, out var owner) ? owner : block;
        }

        /// <summary>
        /// Блоки потока для вёрстки: в «Исходном документе» картинки и фигуры со
        /// сменой вида заменены двойниками с прежним видом. Без таких объектов —
        /// тот же список.
        /// </summary>
        public static List<BlockModel> DisplayBlocks(List<BlockModel> blocks, RevisionView view)
        {
            if (view != RevisionView.Original) return blocks;

            List<BlockModel>? shown = null;
            for (int i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                if (block is not IAppearanceTracked { Change: not null }) continue;

                shown ??= new List<BlockModel>(blocks);
                shown[i] = DisplayObject(block, view);
            }

            return shown ?? blocks;
        }

        /// <summary>
        /// Таблица для вёрстки. В «Исходном документе» таблица со сменой оформления
        /// верстается с прежним: двойник с теми же ячейками по сетке и Id, у которого
        /// оформление возвращено из записи, а абзацы, вложенные таблицы и объекты ячеек —
        /// те же, что у таблицы документа. Правка текста в такой таблице идёт в сам
        /// документ: раскладка ячеек отдаёт абзацы документа, не двойника.
        /// </summary>
        public static TableBlock DisplayTable(TableBlock table, RevisionView view)
        {
            if (view != RevisionView.Original || table.FormatChange is not { } change) return table;
            if (Services.DocumentCloner.CloneBlock(table) is not TableBlock twin
                || twin.Cells.Count != table.Cells.Count)
                return table;

            for (int i = 0; i < twin.Cells.Count; i++)
            {
                var own = table.Cells[i];
                var shown = twin.Cells[i];
                shown.Paragraphs = own.Paragraphs;
                shown.NestedTables = own.NestedTables;
                shown.Floats = own.Floats;
            }

            Services.TableFrame.Restore(twin, change.Previous);
            twin.FormatChange = null;
            return twin;
        }

        /// <summary>
        /// Параметры раздела, который кончается этим разрывом, для вёрстки листов. В
        /// «Исходном документе» раздел со сменой параметров верстается с прежними: лист,
        /// поля, колонки, выравнивание по высоте, рамка и номера строк — из записи
        /// правки. Иначе — параметры самого раздела.
        /// </summary>
        public static Models.Page.SectionLayout? DisplaySection(BreakBlock sectionEnd, RevisionView view)
        {
            if (view != RevisionView.Original || sectionEnd.Section?.FormatChange is not { } change)
                return sectionEnd.Section;

            var twin = new BreakBlock { BreakType = sectionEnd.BreakType, Section = sectionEnd.Section.Clone() };
            twin.Section.FormatChange = null;
            Services.SectionFrame.Restore(twin, change.Previous);
            return twin.Section;
        }

        /// <summary>
        /// Прежние параметры последнего раздела для вёрстки «Исходного документа»: лист,
        /// колонки и остальное из записи правки. Null — вид другой или параметры правкой
        /// не менялись.
        /// </summary>
        public static (Models.Page.TextEditorPageSettings Page, Models.Page.SectionColumns Columns, Models.Page.SectionLayout Layout)?
            DisplayFinalSection(DocumentModel document, RevisionView view)
        {
            if (view != RevisionView.Original || document.PageSetupChange is not { } change) return null;
            if (Services.SectionFrame.Parse(change.Previous) is not { } look) return null;

            var layout = document.FinalSection?.Clone() ?? new Models.Page.SectionLayout();
            layout.FormatChange = null;
            layout.Page = look.Page.Clone();
            layout.Columns = look.Columns.Clone();
            layout.VerticalAlignment = look.VerticalAlignment;
            layout.PageBorders = look.PageBorders?.Clone();
            layout.LineNumbering = look.LineNumbering?.Clone();
            return (layout.Page, layout.Columns, layout);
        }

        /// <summary>Фрагмент спрятан правкой при этом виде.</summary>
        public static bool IsRunHidden(RunProperties? props, RevisionView view)
        {
            if (props is null) return false;

            return view switch
            {
                RevisionView.Original => props.Inserted is not null,
                RevisionView.SimpleMarkup or RevisionView.NoMarkup => props.Deleted is not null,
                _ => false
            };
        }

        /// <summary>Знак абзаца спрятан правкой при этом виде.</summary>
        public static bool IsMarkHidden(ParagraphProperties props, RevisionView view)
        {
            return view switch
            {
                RevisionView.Original => props.MarkInserted is not null,
                RevisionView.SimpleMarkup or RevisionView.NoMarkup => props.MarkDeleted is not null,
                _ => false
            };
        }

        /// <summary>
        /// Блок, вставленный или удалённый правкой целиком (плавающая картинка,
        /// фигура, разрыв), спрятан при этом виде: вставленный — в «Исходном
        /// документе», удалённый — без пометок и в простой разметке. Спрятанный разрыв
        /// и лист не начинает.
        /// </summary>
        public static bool IsBlockHidden(Models.Document.IRevisionMarked block, RevisionView view)
        {
            if (block.Inserted is null && block.Deleted is null) return false;

            return view switch
            {
                RevisionView.Original => block.Inserted is not null,
                RevisionView.SimpleMarkup or RevisionView.NoMarkup => block.Deleted is not null,
                _ => false
            };
        }

        /// <summary>
        /// Абзац спрятан целиком: его знак абзаца спрятан правкой и ни одного видимого
        /// знака в нём не осталось. Так выглядит удалённый целиком абзац в «Без
        /// исправлений» и вставленный целиком — в «Исходном документе».
        /// </summary>
        public static bool IsParagraphHidden(ParagraphBlock paragraph, RevisionView view)
        {
            if (view == RevisionView.AllMarkup) return false;
            if (!IsMarkHidden(paragraph.Properties, view)) return false;

            foreach (var chunk in paragraph.Chunks)
                foreach (var run in chunk.Runs)
                {
                    if (string.IsNullOrEmpty(run.Text)) continue;
                    if (!IsRunHidden(run.Properties, view)) return false;
                }

            return true;
        }

        /// <summary>
        /// Свойства фрагмента для показа: в «Исходном документе» — оформление до
        /// правки, если оно менялось. Отметки правки сохраняются.
        /// </summary>
        public static RunProperties? DisplayProperties(RunProperties? props, RevisionView view)
        {
            if (props?.FormatChange is not { } change || view != RevisionView.Original) return props;

            var previous = change.Previous?.Clone() ?? new RunProperties();
            previous.Inserted = props.Inserted;
            previous.Deleted = props.Deleted;
            previous.FormatChange = props.FormatChange;
            return previous;
        }

        /// <summary>
        /// Абзац для вёрстки: в «Исходном документе» абзац, сменивший оформление,
        /// верстается с прежним. Подставной абзац делит с настоящим текст и список —
        /// меняется только оформление.
        /// </summary>
        public static ParagraphBlock DisplayParagraph(ParagraphBlock paragraph, RevisionView view)
        {
            if (view != RevisionView.AllMarkup
                && IsMarkHidden(paragraph.Properties, view)
                && Joins.TryGetValue(paragraph, out var joined)
                && joined.Length > 0)
                return JoinedParagraph(paragraph, joined, view);

            return WithPreviousFormatting(paragraph, view);
        }

        private static ParagraphBlock WithPreviousFormatting(ParagraphBlock paragraph, RevisionView view)
        {
            if (view != RevisionView.Original
                || paragraph.Properties.FormatChange?.Previous is not { } previous)
                return paragraph;

            var properties = previous.Clone();
            properties.MarkInserted = paragraph.Properties.MarkInserted;
            properties.MarkDeleted = paragraph.Properties.MarkDeleted;
            properties.FormatChange = paragraph.Properties.FormatChange;

            return new ParagraphBlock
            {
                Id = paragraph.Id,
                Chunks = paragraph.Chunks,
                Properties = properties,
                ListProperties = paragraph.ListProperties,
                SuppressSpaceBefore = paragraph.SuppressSpaceBefore,
                SuppressSpaceAfter = paragraph.SuppressSpaceAfter
            };
        }

        // Абзацы, к которым при выбранном виде подклеены следующие: знак абзаца между
        // ними спрятан правкой. И абзацы, ушедшие внутрь предыдущего.
        private static readonly ConditionalWeakTable<ParagraphBlock, ParagraphBlock[]> Joins = new();
        private static readonly ConditionalWeakTable<ParagraphBlock, object> Absorbed = new();
        private static readonly object AbsorbedMark = new();

        /// <summary>
        /// Размечает склейку абзацев для вида показа: абзац, чей знак спрятан правкой
        /// (вставленный знак — в «Исходном документе», удалённый — в «Простой разметке»
        /// и «Без исправлений»), продолжается текстом следующего абзаца, как у Word.
        /// Склеенный абзац берёт оформление последнего знака — того, что остался.
        /// Вызывается перед раскладкой при каждой смене вида или текста.
        /// </summary>
        public static void PrepareJoins(DocumentModel? document, RevisionView view)
        {
            if (document is null) return;

            foreach (var section in document.Sections)
            {
                var blocks = section.Blocks;
                for (int i = 0; i < blocks.Count; i++)
                {
                    if (blocks[i] is not ParagraphBlock paragraph) continue;

                    Joins.Remove(paragraph);
                    Absorbed.Remove(paragraph);
                }
            }

            if (view == RevisionView.AllMarkup) return;

            foreach (var section in document.Sections)
            {
                var blocks = section.Blocks;
                for (int i = 0; i < blocks.Count; i++)
                {
                    if (blocks[i] is not ParagraphBlock paragraph) continue;
                    if (Absorbed.TryGetValue(paragraph, out _)) continue;
                    if (!IsMarkHidden(paragraph.Properties, view)) continue;
                    if (IsParagraphHidden(paragraph, view)) continue;

                    var chain = new List<ParagraphBlock>();
                    int j = i + 1;
                    while (j < blocks.Count && blocks[j] is ParagraphBlock next)
                    {
                        chain.Add(next);
                        Absorbed.AddOrUpdate(next, AbsorbedMark);
                        if (!IsMarkHidden(next.Properties, view)) break;
                        j++;
                    }

                    if (chain.Count > 0) Joins.AddOrUpdate(paragraph, chain.ToArray());
                }
            }
        }

        /// <summary>Абзац ушёл внутрь предыдущего: знак абзаца перед ним спрятан правкой.</summary>
        public static bool IsAbsorbed(ParagraphBlock paragraph) => Absorbed.TryGetValue(paragraph, out _);

        /// <summary>
        /// Абзац для показа вместе с подклеенными: свой текст, затем текст следующих
        /// абзацев (отметкой «только для показа»), оформление — последнего знака абзаца.
        /// </summary>
        private static ParagraphBlock JoinedParagraph(ParagraphBlock paragraph, ParagraphBlock[] joined, RevisionView view)
        {
            var last = WithPreviousFormatting(joined[^1], view);
            var own = WithPreviousFormatting(paragraph, view);

            var chunks = new List<TextChunk>(own.Chunks);
            foreach (var next in joined)
            {
                var tail = new TextChunk();
                tail.Runs.Clear();
                foreach (var chunk in next.Chunks)
                {
                    foreach (var run in chunk.Runs)
                    {
                        if (string.IsNullOrEmpty(run.Text)) continue;
                        tail.Runs.Add(new Models.Inline.RunModel
                        {
                            Id = run.Id,
                            Text = run.Text,
                            Properties = run.Properties,
                            InlineImageId = run.InlineImageId,
                            DisplayTail = true
                        });
                    }
                }

                if (tail.Runs.Count == 0) continue;
                tail.InvalidateLength();
                chunks.Add(tail);
            }

            return new ParagraphBlock
            {
                Id = paragraph.Id,
                Chunks = chunks,
                Properties = last.Properties,
                ListProperties = last.ListProperties,
                SuppressSpaceBefore = own.SuppressSpaceBefore,
                SuppressSpaceAfter = last.SuppressSpaceAfter
            };
        }

        /// <summary>В абзаце есть хоть одна правка — фрагмента, знака абзаца или оформления.</summary>
        public static bool HasRevisions(ParagraphBlock paragraph)
        {
            if (paragraph.Properties.HasRevision) return true;

            foreach (var chunk in paragraph.Chunks)
                foreach (var run in chunk.Runs)
                    if (run.Properties?.HasRevision == true)
                        return true;

            return false;
        }
    }
}
