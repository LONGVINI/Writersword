using System;
using System.Linq;
using System.Threading.Tasks;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Models.Styles;
using Writersword.Modules.TextEditor.Rendering;
using Writersword.Modules.TextEditor.Rendering.Formulas;
using Writersword.Modules.TextEditor.Views.Dialogs;

namespace Writersword.Modules.TextEditor.ViewModels
{
    /// <summary>
    /// Формулы: вставка своей формулы и правка любой формулы документа, в том числе
    /// пришедшей из Word.
    ///
    /// Формула набирается в окне формулы текстом и хранится формулой Word — тем же
    /// XML, что у формулы из .docx: на листе её верстает тот же набор, в .docx она
    /// уходит без перевода, и Word открывает её своей формулой.
    ///
    /// Формула в строке — символ абзаца, как картинка в строке. Формула отдельной
    /// строкой — свой абзац по центру: в пустом абзаце она встаёт в него, в абзаце с
    /// текстом абзац разрезается в месте каретки.
    /// </summary>
    public sealed partial class DocumentViewModel
    {
        public void InsertFormula() => _ = InsertFormulaAsync(null);

        /// <summary>Справка по записи формул — отдельным окном.</summary>
        public void ShowFormulaHelp() => FormulaHelpWindow.ShowHelp();

        /// <summary>
        /// Открывает окно новой формулы уже с заготовкой или знаком — с вкладки
        /// «Формулы» ленты. Каретка окна встаёт в первое пустое место заготовки.
        /// </summary>
        public void InsertFormulaTemplate(string latex)
        {
            if (string.IsNullOrEmpty(latex)) return;
            _ = InsertFormulaAsync(null, latex);
        }

        /// <summary>
        /// Вставляет готовую формулу отдельной строкой без окна, как готовые формулы
        /// Word. Поправить её можно двойным щелчком.
        /// </summary>
        public void InsertReadyFormula(string latex)
        {
            if (string.IsNullOrWhiteSpace(latex)) return;
            _ = InsertFormulaAsync(latex);
        }

        /// <summary>Открывает окно правки формулы: по двойному щелчку на формуле.</summary>
        public void RequestFormulaEdit(FormulaBlock formula)
        {
            if (formula is null) return;
            _ = EditFormulaAsync(formula);
        }

        private async Task InsertFormulaAsync(string? ready, string startLatex = "")
        {
            try
            {
                if (IsReadOnly || _document.Sections.Count == 0) return;

                // Место формулы запоминается до окна: окно забирает фокус.
                var target = GetCaretTargetDelegate?.Invoke();
                ParagraphBlock? para = target?.Para;
                int at = target?.CharIndex ?? 0;

                if (para is null && _activeParagraph is not null)
                {
                    para = _activeParagraph.Model;
                    at = _activeParagraph.SelectionStart;
                }

                if (para is null) return;

                FormulaEditResult? result;
                if (ready is not null)
                {
                    string omml;
                    try
                    {
                        omml = FormulaText.ToOmml(ready);
                    }
                    catch (FormulaSyntaxException ex)
                    {
                        _log.Error(ex, "[FORMULA] Готовая формула не разобралась");
                        return;
                    }

                    result = new FormulaEditResult(omml, Display: true, Delete: false);
                }
                else
                {
                    result = await FormulaEditorDialog.ShowAsync(startLatex, display: false, canDelete: false);
                }

                if (result is null || result.Delete || IsReadOnly) return;

                if (!EnumerateAllParagraphs().Any(p => ReferenceEquals(p, para))) return;
                at = Math.Max(0, Math.Min(at, para.TotalLength));

                var section = _document.Sections[0];
                var runProperties = RunPropertiesAt(para, at);

                var formula = new FormulaBlock
                {
                    Omml = result.Omml,
                    IsDisplay = result.Display,
                    FontSizePt = FormulaFontSizeAt(para, runProperties)
                };

                bool emptyParagraph = para.GetPlainText().Length == 0;
                bool topLevel = section.Blocks.Contains(para);

                if (result.Display && !emptyParagraph && topLevel)
                {
                    // Формула отдельной строкой в абзаце с текстом: свой абзац по центру
                    // в месте каретки, текст по сторонам остаётся своими абзацами.
                    section.InlineObjects.Add(formula);

                    var properties = para.Properties.Clone();
                    properties.Alignment = TextAlignment.Center;
                    properties.FirstLineIndent = 0.0;
                    properties.MarkInserted = null;
                    properties.MarkDeleted = null;
                    properties.FormatChange = null;

                    var formulaParagraph = new ParagraphBlock { Properties = properties };
                    formulaParagraph.Chunks[0].Runs.Clear();
                    formulaParagraph.Chunks[0].Runs.Add(new RunModel
                    {
                        Text = RunModel.ObjectPlaceholder.ToString(),
                        InlineImageId = formula.Id,
                        Properties = runProperties?.WithoutRevisions()
                    });
                    formulaParagraph.Chunks[0].InvalidateLength();

                    InsertBlockAtCaret(formulaParagraph);
                    StructureChanged?.Invoke();
                    return;
                }

                BeginEditDelegate?.Invoke("Вставка формулы");

                section.InlineObjects.Add(formula);
                para.InsertInlineObject(at, formula.Id);

                // Формула отдельной строкой в пустом абзаце: абзац встаёт по центру.
                if (result.Display && emptyParagraph)
                {
                    para.Properties.Alignment = TextAlignment.Center;
                    para.Properties.FirstLineIndent = 0.0;
                }

                InlineImageInserted?.Invoke(para, at);

                CommitEditDelegate?.Invoke();
                StructureChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[FORMULA] Формулу вставить не удалось");
            }
        }

        private async Task EditFormulaAsync(FormulaBlock formula)
        {
            try
            {
                if (IsReadOnly) return;

                string latex;
                try
                {
                    latex = FormulaText.FromOmml(formula.Omml);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[FORMULA] Формулу не удалось записать текстом для правки");
                    latex = string.Empty;
                }

                var result = await FormulaEditorDialog.ShowAsync(latex, formula.IsDisplay, canDelete: true);
                if (result is null || IsReadOnly) return;

                // Формула могла уйти из документа, пока окно было открыто.
                if (!ReferenceEquals(FindInlineFormulaById(formula.Id), formula)) return;

                var owner = FindFormulaOwner(formula);

                if (result.Delete)
                {
                    BeginEditDelegate?.Invoke("Удаление формулы");

                    if (owner is { } found)
                        found.Para.SpliceText(found.CharIndex, found.CharIndex + 1, string.Empty);

                    foreach (var section in _document.Sections)
                        section.InlineObjects.Remove(formula);

                    if (owner is { } changed) InlineObjectsChanged?.Invoke(changed.Para);

                    CommitEditDelegate?.Invoke();
                    StructureChanged?.Invoke();
                    return;
                }

                if (result.Omml == formula.Omml && result.Display == formula.IsDisplay) return;

                BeginEditDelegate?.Invoke("Правка формулы");

                formula.Omml = result.Omml;
                formula.IsDisplay = result.Display;

                // Ширина формулы сменилась, а текст абзаца нет: абзац перевёрстывается.
                if (owner is { } edited) InlineObjectsChanged?.Invoke(edited.Para);

                CommitEditDelegate?.Invoke();
                StructureChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[FORMULA] Формулу изменить не удалось");
            }
        }

        /// <summary>
        /// Формула у каретки: знак формулы прямо перед кареткой или сразу за ней, а при
        /// выделении — внутри выделенного.
        /// </summary>
        private bool HasFormulaNear(ParagraphBlock block, int selectionStart, int selectionEnd)
        {
            int from;
            int to;
            if (selectionEnd > selectionStart)
            {
                from = selectionStart;
                to = selectionEnd;
            }
            else
            {
                from = selectionStart - 1;
                to = selectionStart + 1;
            }

            int offset = 0;
            foreach (var chunk in block.Chunks)
            {
                foreach (var run in chunk.Runs)
                {
                    int runStart = offset;
                    int runEnd = offset + (run.InlineImageId.HasValue ? 1 : run.Text.Length);
                    offset = runEnd;

                    if (runEnd <= from) continue;
                    if (runStart >= to) return false;

                    if (run.InlineImageId is Guid id && FindInlineFormulaById(id) is not null)
                        return true;
                }
            }

            return false;
        }

        /// <summary>Абзац, в строке которого стоит формула, и позиция её символа.</summary>
        private (ParagraphBlock Para, int CharIndex)? FindFormulaOwner(FormulaBlock formula)
        {
            foreach (var para in EnumerateAllParagraphs())
            {
                int index = para.IndexOfInlineObject(formula.Id);
                if (index >= 0) return (para, index);
            }
            return null;
        }

        /// <summary>Оформление текста в месте каретки: символа перед ней, иначе после неё.</summary>
        private static RunProperties? RunPropertiesAt(ParagraphBlock para, int at)
        {
            var cells = para.ToCharCells();
            if (at > 0 && at - 1 < cells.Count && cells[at - 1].Props is { } before) return before;
            if (at < cells.Count && cells[at].Props is { } after) return after;

            foreach (var chunk in para.Chunks)
                foreach (var run in chunk.Runs)
                    if (run.Properties is not null) return run.Properties;

            return null;
        }

        /// <summary>
        /// Кегль новой формулы — кегль текста в месте каретки: формула в строке стоит
        /// вровень с буквами, как у Word.
        /// </summary>
        private double FormulaFontSizeAt(ParagraphBlock para, RunProperties? runProperties)
        {
            if (runProperties?.FontSize is double size && size > 0.0) return size;

            var styles = new StyleResolver(_document.Styles);
            float styleSize = styles.ResolveFontSize(para.Properties.StyleName);
            return styleSize > 0f ? styleSize : 11.0;
        }
    }
}
