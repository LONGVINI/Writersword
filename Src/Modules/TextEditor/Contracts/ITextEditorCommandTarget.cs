using Writersword.Core.Models.Print;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Models.Page;
using Writersword.Modules.TextEditor.Models.Styles;

namespace Writersword.Modules.TextEditor.Contracts
{
    /// <summary>
    /// Контракт между Ribbon (командный источник) и DocumentViewModel (исполнитель).
    /// Все операции редактирования проходят через этот интерфейс.
    /// PaperSize и PageOrientation берутся из Core.Models.Print.
    /// </summary>
    public interface ITextEditorCommandTarget
    {
        // ── Форматирование символов ───────────────────────────────────────
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

        // ── Форматирование абзаца ─────────────────────────────────────────
        void SetAlignment(TextAlignment alignment);
        void IncreaseIndent();
        void DecreaseIndent();
        void SetLineSpacing(double multiplier);
        void SetSpaceBefore(double pt);
        void SetSpaceAfter(double pt);
        void ApplyStyle(string styleName);

        // ── Списки ────────────────────────────────────────────────────────
        void ToggleBulletList();
        void ToggleNumberedList();
        void ToggleMultilevelList();

        // ── Буфер обмена ─────────────────────────────────────────────────
        void Cut();
        void Copy();
        void Paste();
        void SelectAll();
        void Undo();
        void Redo();

        // ── Вставка ──────────────────────────────────────────────────────
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

        // ── Макет страницы ────────────────────────────────────────────────
        void SetPageSize(PaperSize size);
        void SetPageOrientation(PageOrientation orientation);
        void SetPageMargins(double top, double bottom, double left, double right);
        void SetColumns(int count);

        // ── Вид ───────────────────────────────────────────────────────────
        void SetZoom(double zoom);
        void SetViewMode(EditorViewMode mode);
        void ToggleFullscreen();
        void ToggleFocusMode();
        void SetCanvasTheme(CanvasThemePreset preset);
        void SetCanvasColors(string pageBackground, string textColor);

        /// <summary>Увеличить масштаб на один шаг.</summary>
        void ZoomIn();
        /// <summary>Уменьшить масштаб на один шаг.</summary>
        void ZoomOut();
        /// <summary>Сбросить масштаб к 100%.</summary>
        void ZoomReset();

        // ── Поиск ────────────────────────────────────────────────────────
        void OpenFind();
        void OpenFindReplace();

        // ── Инструменты ──────────────────────────────────────────────────
        void RunSpellCheck();
        void ShowWordCount();

        // ── Печать и экспорт ─────────────────────────────────────────────
        void Print();
        void ExportToPdf();
        void ExportToDocx();
        void ExportToTxt();
        void ExportToMarkdown();

        // ── Структурные операции с таблицей ──────────────────────────────

        /// <summary>Добавить строку выше (above=true) или ниже (above=false) текущей.</summary>
        void TableAddRow(bool above);
        /// <summary>Добавить столбец слева (left=true) или справа (left=false) от текущего.</summary>
        void TableAddColumn(bool left);
        /// <summary>Удалить текущую строку таблицы.</summary>
        void TableDeleteRow();
        /// <summary>Удалить текущий столбец таблицы.</summary>
        void TableDeleteColumn();
        /// <summary>Удалить всю таблицу.</summary>
        void TableDelete();

        // ── Объединение / разбиение ячеек ─────────────────────────────────
        /// <summary>Объединить выделенные ячейки (или все ячейки в текущей строке).</summary>
        void TableMergeCells();
        /// <summary>Разбить текущую объединённую ячейку на исходные.</summary>
        void TableSplitCell();

        // ── Выравнивание содержимого ячейки ──────────────────────────────
        /// <summary>Выравнивание по горизонтали внутри ячейки.</summary>
        void TableSetCellHAlign(Writersword.Modules.TextEditor.Models.Styles.TextAlignment align);
        /// <summary>Выравнивание по вертикали внутри ячейки (0=Top,1=Middle,2=Bottom).</summary>
        void TableSetCellVAlign(int vAlign);

        // ── Оформление ячейки ─────────────────────────────────────────────
        /// <summary>Заливка фона ячейки. null — убрать заливку.</summary>
        void TableSetCellBackground(string? color);
        /// <summary>Установить стиль границы ячейки (all/top/bottom/left/right/inner/outer).</summary>
        void TableSetCellBorder(string side, BorderStyle style, double thicknessPt, string? color);

        // ── Размер ячейки ─────────────────────────────────────────────────
        /// <summary>Задать ширину текущего столбца в мм.</summary>
        void TableSetColumnWidth(double widthMm);
        /// <summary>Задать высоту текущей строки в pt (0 = авто).</summary>
        void TableSetRowHeight(double heightPt);
        /// <summary>Автоподбор ширины столбцов по содержимому.</summary>
        void TableAutoFit();
        /// <summary>Растянуть все столбцы равномерно по ширине таблицы.</summary>
        void TableDistributeColumns();
        /// <summary>Выровнять высоты всех строк равномерно.</summary>
        void TableDistributeRows();

        // ── Сортировка ────────────────────────────────────────────────────
        /// <summary>Сортировать таблицу по указанному столбцу.</summary>
        void TableSort(int columnIndex, bool ascending);

        // ── Заголовок ─────────────────────────────────────────────────────
        /// <summary>Переключить повторение первой строки как заголовка на каждой странице.</summary>
        void TableToggleRepeatHeader();
        /// <summary>Возвращает текущее состояние повторения заголовка.</summary>
        bool TableGetRepeatHeader();

        // ── Режим разбивки ────────────────────────────────────────────────
        /// <summary>Переключить режим разбивки: ByRow / ByCell.</summary>
        void TableToggleSplitMode();
        bool TableGetSplitModeByCell();

        // ── Метки продолжения ─────────────────────────────────────────────
        void TableSetBreakLabel(string? text);
        void TableSetContinuationLabel(string? text);
        string? TableGetBreakLabel();
        string? TableGetContinuationLabel();
    }
}