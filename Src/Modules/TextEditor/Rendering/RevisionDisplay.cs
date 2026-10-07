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
