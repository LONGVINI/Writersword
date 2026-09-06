using Writersword.Core.Models.Print;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Models.Page;
using Writersword.Modules.TextEditor.Models.Styles;

namespace Writersword.Modules.TextEditor.Contracts
{
    /// <summary>
    /// Режим смены регистра выделенного текста.
    /// </summary>
    public enum TextCaseMode
    {
        Sentence,
        Lower,
        Upper,
        Title,
        Toggle
    }

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
        void BeginFontPreview();
        void PreviewFontFamily(string fontFamily);
        void EndFontPreview(bool commit);
        void SetFontSize(double size);
        void IncreaseFontSize();
        void DecreaseFontSize();
        void ChangeCase(TextCaseMode mode);

        // ── Форматирование абзаца ─────────────────────────────────────────
        void SetAlignment(TextAlignment alignment);
        void IncreaseIndent();
        void DecreaseIndent();
        void SetLineSpacing(double multiplier);
        void SetSpaceBefore(double pt);
        void SetSpaceAfter(double pt);
        void ApplyStyle(string styleName);

        // Снимок свойств текущего абзаца для пред-заполнения окна «Абзац» (null — нет абзаца).
        ParagraphProperties? GetActiveParagraphProperties();
        // Применяет поля окна «Абзац» к выделенным абзацам одной командой отмены.
        void ApplyParagraphSettings(ParagraphProperties settings);
        // Ставит выделенным абзацам структурный уровень (0 — основной текст, 1…9).
        void SetOutlineLevel(int level);

        // ── Списки ────────────────────────────────────────────────────────
        void ToggleBulletList();
        void ToggleNumberedList();
        void ToggleMultilevelList();

        /// <summary>Применяет к выделенным абзацам список с заданным типом маркера (единый список).</summary>
        void ApplyListType(ListMarkerType markerType);
        /// <summary>Применяет маркированный список с произвольным символом маркера.</summary>
        void ApplyCustomBulletList(string marker);
        /// <summary>Снимок свойств списка активного абзаца (null — не элемент списка).</summary>
        ListProperties? GetActiveListProperties();
        /// <summary>Применяет параметры списка (символ/система/отступы/нумерация) к выделению.</summary>
        void ApplyListSettings(ListProperties settings);
        /// <summary>Двигает позицию маркера (выступ) активного списка от левого поля, pt.</summary>
        void SetListMarkerIndentPt(double pt);
        /// <summary>Двигает позицию текста активного списка от левого поля, pt.</summary>
        void SetListTextIndentPt(double pt);
        /// <summary>Применяет многоуровневый список со схемой по умолчанию.</summary>
        void ApplyMultilevelList();
        /// <summary>Применяет многоуровневый список с заданной схемой типов маркеров по уровням.</summary>
        void ApplyMultilevelScheme(System.Collections.Generic.List<ListMarkerType> scheme);

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

        // ── Работа с выделенной фигурой ───────────────────────────────────

        /// <summary>Сводка по выделенной фигуре для ленты, либо null.</summary>
        (ShapeType Type, WrapMode Wrap, WrapSide WrapSide, ShapeDashStyle Dash, ShapeArrowHead StartArrow, ShapeArrowHead EndArrow, string? FillColor, string? StrokeColor, double StrokeThicknessPt, double CornerRadiusPt, double Opacity, double WidthPt, double HeightPt, double RotationDeg, bool LockAspect, int PinnedPage, bool HasFillImage, bool FillImageStretch)? GetSelectedShapeInfo();

        /// <summary>Меняет вид уже вставленной фигуры, сохраняя её положение и оформление.</summary>
        void SetShapeType(ShapeType type);

        /// <summary>Цвет заливки в hex, либо null — заливки нет.</summary>
        void SetShapeFill(string? hexColor);

        /// <summary>Цвет обводки в hex, либо null — обводки нет.</summary>
        void SetShapeStroke(string? hexColor);

        /// <summary>Толщина обводки в пунктах.</summary>
        void SetShapeStrokeThickness(double thicknessPt);

        /// <summary>Штрих обводки.</summary>
        void SetShapeDash(ShapeDashStyle dash);

        /// <summary>Скругление углов прямоугольника и выноски в пунктах.</summary>
        void SetShapeCornerRadius(double radiusPt);

        /// <summary>Наконечники на концах линии.</summary>
        void SetShapeArrows(ShapeArrowHead start, ShapeArrowHead end);

        /// <summary>Непрозрачность фигуры: 1 — полностью видима, 0 — невидима.</summary>
        void SetShapeOpacity(double opacity);

        /// <summary>Ширина фигуры в пунктах.</summary>
        void SetShapeWidth(double widthPt);

        /// <summary>Высота фигуры в пунктах.</summary>
        void SetShapeHeight(double heightPt);

        /// <summary>Угол поворота фигуры в градусах.</summary>
        void SetShapeRotation(double degrees);

        /// <summary>Блокировка пропорций при изменении размера.</summary>
        void SetShapeLockAspect(bool locked);

        /// <summary>Режим обтекания текстом.</summary>
        void SetShapeWrapMode(WrapMode mode);

        /// <summary>С какой стороны от фигуры идёт текст при обтекании.</summary>
        void SetShapeWrapSide(WrapSide side);

        /// <summary>Отступы текста от фигуры при обтекании, в пунктах.</summary>
        void SetShapeWrapPadding(double topPt, double bottomPt, double leftPt, double rightPt);

        /// <summary>Закрепляет фигуру за страницей, на которой она стоит, либо снимает закрепление.</summary>
        void SetShapePinned(bool pinned);

        /// <summary>Порядок перекрытия: на передний план или на задний.</summary>
        void SetShapeZOrder(bool toFront);

        /// <summary>Кладёт картинку внутрь фигуры. Пустой путь снимает заливку картинкой.</summary>
        void SetShapeFillImage(string? filePath);

        /// <summary>Растягивать картинку-заливку на весь габарит фигуры.</summary>
        void SetShapeFillImageStretch(bool stretch);

        /// <summary>Удаляет выделенную фигуру.</summary>
        void DeleteSelectedShape();

        // ── Работа с выделенным изображением ──────────────────────────────

        /// <summary>Штрих рамки картинки: сплошная, пунктир, точки, штрихпунктир.</summary>
        void SetImageBorderDash(ShapeDashStyle dash);

        /// <summary>Штрих рамки выделенной картинки, либо null.</summary>
        ShapeDashStyle? GetSelectedImageBorderDash();

        /// <summary>
        /// Форма картинки: по её контуру картинка обрезается и по нему же идёт рамка.
        /// </summary>
        void SetImageShapeType(ShapeType type);

        /// <summary>Форма выделенной картинки, либо null.</summary>
        ShapeType? GetSelectedImageShapeType();

        /// <summary>Скругление углов формы картинки в пунктах.</summary>
        void SetImageCornerRadius(double radiusPt);

        /// <summary>Скругление углов рамки выделенной картинки, либо null.</summary>
        double? GetSelectedImageCornerRadius();

        void SetImageWrapMode(WrapMode mode);

        /// <summary>С какой стороны от картинки идёт текст при обтекании.</summary>
        void SetImageWrapSide(WrapSide side);

        /// <summary>Сторона обтекания выделенной картинки, либо null если ничего не выделено.</summary>
        WrapSide? GetSelectedImageWrapSide();

        /// <summary>
        /// Жёсткая привязка картинки к номеру страницы (1-based). 0 — привязки нет,
        /// картинка переезжает между страницами вслед за своим местом в потоке.
        /// </summary>
        void SetImagePinnedPage(int page);

        /// <summary>Номер страницы привязки выделенной картинки (0 — нет), либо null.</summary>
        int? GetSelectedImagePinnedPage();

        /// <summary>Номер страницы, на которой картинка сейчас находится (1-based), либо null.</summary>
        int? GetSelectedImageCurrentPage();
        void SetImageLockAspect(bool locked);
        void DeleteSelectedImage();
        (WrapMode Wrap, bool LockAspect, Writersword.Modules.TextEditor.Models.Styles.TextAlignment Align)? GetSelectedImageInfo();
        void SetImageRotation(double degrees);
        double? GetSelectedImageRotation();
        void SetImageWidth(double widthPt);
        void SetImageHeight(double heightPt);
        void SetImageOpacity(double opacity);
        void SetImageBorder(string? colorHex, double thicknessPt);

        /// <summary>Куда ложится рамка картинки: внутрь, по границе или наружу.</summary>
        void SetImageBorderAlign(ImageBorderAlign align);

        /// <summary>Положение рамки выделенной картинки, либо null если ничего не выделено.</summary>
        ImageBorderAlign? GetSelectedImageBorderAlign();
        (double WidthPt, double HeightPt, double Opacity, string? BorderColor, double BorderThicknessPt)? GetSelectedImageStyle();
        void ToggleImageFlipHorizontal();
        void ToggleImageFlipVertical();
        void SetImageCropMode(bool on);
        bool GetImageCropMode();
        void SetImageWrapPadding(double topPt, double bottomPt, double leftPt, double rightPt);
        (double TopPt, double BottomPt, double LeftPt, double RightPt)? GetSelectedImageWrapPadding();

        /// <summary>
        /// Что сейчас выделено на листе. Лента «Формат» одна на картинку и фигуру,
        /// и по этому признаку показывает группы, которые есть только у одного
        /// из них: заливка и наконечники у фигуры, обрезка и отражение у картинки.
        /// Null — не выделено ничего.
        /// </summary>
        (bool HasShape, bool HasImage, bool HasFillImage, bool IsLine)? GetSelectedFloatingKind();

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

        // ── Импорт ───────────────────────────────────────────────────────

        /// <summary>
        /// Импортирует внешний документ, заменяя содержимое текущего.
        /// Формат определяется по расширению файла (.docx, .txt).
        /// Пользователя спрашивают о замене содержимого перед импортом.
        /// </summary>
        void ImportFromFile(string filePath);

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
        /// <summary>
        /// Разделить текущую ячейку пополам: vertical = true — вертикальной чертой
        /// (два столбца), false — горизонтальной (две строки).
        /// </summary>
        void TableDivideCell(bool vertical);

        // ── Выравнивание содержимого ячейки ──────────────────────────────
        /// <summary>Выравнивание по горизонтали внутри ячейки.</summary>
        void TableSetCellHAlign(Writersword.Modules.TextEditor.Models.Styles.TextAlignment align);
        /// <summary>Выравнивание по вертикали внутри ячейки (0=Top,1=Middle,2=Bottom).</summary>
        void TableSetCellVAlign(int vAlign);

        /// <summary>
        /// Внутренние поля ячейки в пунктах. Применяются ко всем целевым ячейкам
        /// одной операцией — один шаг отмены на изменение.
        /// </summary>
        void TableSetCellPadding(double topPt, double bottomPt, double leftPt, double rightPt);

        /// <summary>
        /// Поля целевых ячеек. null — каретка не в таблице либо в выделении
        /// значения разные и показывать в полях нечего.
        /// </summary>
        (double TopPt, double BottomPt, double LeftPt, double RightPt)? TableGetCellPadding();

        /// <summary>
        /// Инструмент рисования границ: 0 — обычная работа с текстом, 1 — карандаш
        /// (проводит линию по границе), 2 — ластик (убирает линию). Режим держится
        /// до повторного нажатия кнопки или Escape, поэтому передаётся отдельно от
        /// команд, а не разовым действием.
        /// </summary>
        void TableSetLineTool(int tool);

        /// <summary>Текущий инструмент рисования границ.</summary>
        int TableGetLineTool();

        /// <summary>
        /// Задаёт обе координаты выравнивания ячейки одной операцией.
        /// Нужна именно совмещённая: два отдельных вызова кладут в стек отмены
        /// два снимка, и одно нажатие кнопки сетки приходилось бы отменять дважды.
        /// </summary>
        void TableSetCellAlign(int vAlign,
            Writersword.Modules.TextEditor.Models.Styles.TextAlignment hAlign);

        /// <summary>
        /// Текущее выравнивание по горизонтали в целевых ячейках.
        /// null — каретка не в таблице либо в выделении разные значения: в этом
        /// случае ни одна кнопка выравнивания не должна выглядеть активной.
        /// </summary>
        Writersword.Modules.TextEditor.Models.Styles.TextAlignment? TableGetCellHAlign();

        /// <summary>
        /// Текущее выравнивание по вертикали в целевых ячейках (0=Top,1=Middle,2=Bottom).
        /// null — каретка не в таблице либо в выделении разные значения.
        /// </summary>
        int? TableGetCellVAlign();

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