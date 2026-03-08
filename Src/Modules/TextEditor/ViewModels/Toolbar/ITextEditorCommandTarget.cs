using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Models.Page;
using Writersword.Modules.TextEditor.Models.Styles;

namespace Writersword.Modules.TextEditor.ViewModels.Toolbar
{
    /// <summary>
    /// Контракт между Ribbon (командный источник) и DocumentViewModel (исполнитель).
    /// Все операции редактирования проходят через этот интерфейс.
    /// </summary>
    public interface ITextEditorCommandTarget
    {
        // --- Форматирование символов ---
        void ToggleBold();
        void ToggleItalic();
        void ToggleUnderline();
        void ToggleStrikethrough();
        void ToggleSuperscript();
        void ToggleSubscript();
        void ToggleAllCaps();
        void ToggleSmallCaps();
        void ClearFormatting();
        void SetTextColor(string color);
        void SetHighlightColor(string? color);
        void SetFontFamily(string fontFamily);
        void SetFontSize(double size);
        void IncreaseFontSize();
        void DecreaseFontSize();

        // --- Форматирование абзаца ---
        void SetAlignment(TextAlignment alignment);
        void IncreaseIndent();
        void DecreaseIndent();
        void SetLineSpacing(double multiplier);
        void SetSpaceBefore(double pt);
        void SetSpaceAfter(double pt);
        void ApplyStyle(string styleName);

        // --- Списки ---
        void ToggleBulletList();
        void ToggleNumberedList();
        void ToggleMultilevelList();

        // --- Буфер обмена ---
        void Cut();
        void Copy();
        void Paste();
        void SelectAll();
        void Undo();
        void Redo();

        // --- Вставка ---
        void InsertTable(int rows, int columns);
        void InsertImage(string filePath);
        void InsertShape(ShapeType shapeType);
        void InsertFloatingTextBox();
        void InsertPageBreak();
        void InsertSectionBreak(BreakType breakType);
        void InsertFootnote();
        void InsertEndnote();
        void InsertBookmark(string name);
        void InsertHyperlink(string url, string? displayText);
        void InsertTOC();
        void InsertComment(string text);

        // --- Макет ---
        void SetPageSize(PaperSize size);
        void SetPageOrientation(PageOrientation orientation);
        void SetPageMargins(double top, double bottom, double left, double right);
        void SetColumns(int count);

        // --- Вид ---
        void SetZoom(double zoom);
        void SetViewMode(EditorViewMode mode);
        void ToggleFullscreen();
        void ToggleFocusMode();
        void SetCanvasTheme(CanvasThemePreset preset);
        void SetCanvasColors(string pageBackground, string textColor);

        // --- Поиск ---
        void OpenFind();
        void OpenFindReplace();

        // --- Инструменты ---
        void RunSpellCheck();
        void ShowWordCount();
        void Print();
        void ExportToPdf();
        void ExportToDocx();
        void ExportToTxt();
        void ExportToMarkdown();
    }
}
