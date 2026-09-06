using SkiaSharp;
using System;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Document
{
    public sealed partial class DocumentCanvas
    {
        // ── Одна лента на картинку и фигуру ───────────────────────────────
        //
        // Лента «Формат» одна на оба объекта, и зовёт она по-прежнему методы с
        // именами SetImage*/GetSelectedImage*. Разводит вызовы по объекту канвас:
        // только он знает, что сейчас выделено, и одновременно активен ровно один
        // объект (см. SelectShape и ветку выделения картинки в OnPointerPressed).
        //
        // Сделано так намеренно. Разводить в самой ленте значило бы удвоить каждую
        // команду и каждое поле — а свойства у объектов общие: размер, поворот,
        // обтекание, положение, прозрачность и линия по контуру. Разница ровно в
        // содержимом: у картинки файл и обрезка, у фигуры — вид и заливка.
        //
        // Здесь же лежат правки фигуры, вызываемые общей лентой: у неё «рамка» —
        // это обводка, а «форма» — вид фигуры.

        /// <summary>Выделена ли сейчас фигура (а не картинка).</summary>
        private bool IsShapeActive => _selectedShape is not null;

        /// <summary>Выделен ли хоть какой-то плавающий объект.</summary>
        private bool IsFloatingActive => _selectedImage is not null || _selectedShape is not null;

        // ── Содержимое: заливка фигуры картинкой ↔ файл картинки ──────────

        /// <summary>
        /// Кнопка «Картинка» в группе «Содержимое». У фигуры кладёт картинку внутрь
        /// контура, у картинки — заменяет её файл. Действие одно и то же по смыслу:
        /// сказать объекту, что в нём нарисовано.
        ///
        /// Пустой путь у фигуры снимает заливку и возвращает одноцветную. У картинки
        /// снимать нечего: файл — это и есть сам объект, и без него остался бы
        /// пустой прямоугольник вместо удаления. Поэтому лента там гасит «убрать»
        /// (CanClearFillImage), а сюда пустой путь просто не доходит.
        /// </summary>
        private void SetFloatingFillImage(string? filePath)
        {
            if (IsShapeActive) { SetSelectedShapeFillImage(filePath); return; }
            if (string.IsNullOrEmpty(filePath)) return;
            ReplaceSelectedImageFile(filePath!);
        }

        /// <summary>
        /// Меняет файл выделенной картинки, сохраняя её габарит, поворот, обрезку,
        /// отражение и оформление: рамка на листе остаётся той же, меняется только
        /// то, что в ней нарисовано.
        /// </summary>
        private void ReplaceSelectedImageFile(string filePath)
        {
            if (_selectedImage is null || IsEditingBlocked) return;

            string? stored = DocVm?.StoreImageFile(filePath);
            if (string.IsNullOrEmpty(stored)) return;
            if (_selectedImage.ImageFileName == stored) return;

            BeginImageEdit("Замена картинки");
            _selectedImage.ImageFileName = stored!;
            CommitImageEdit();

            // Пропорции нового файла другие, а габарит остался прежним: раскладка
            // пересобирается, иначе картинка на листе показывалась бы по старому
            // измерению до первой правки рядом.
            RebuildLayouts();
            InvalidateMeasure();
            InvalidateFull();
        }

        // ── Оформление линии: рамка картинки ↔ обводка фигуры ─────────────

        /// <summary>
        /// Цвет и толщина линии по контуру объекта. У картинки это рамка, у фигуры —
        /// обводка: на ленте это одна и та же пара полей.
        /// </summary>
        private void SetFloatingBorder(string? colorHex, double thicknessPt)
        {
            if (_selectedShape is { } shape)
            {
                string? color = NormalizeBorderColor(colorHex);
                double thick = Math.Clamp(thicknessPt, 0.0, 72.0);

                // Толщина без цвета рисовала бы невидимую линию: пользователь меняет
                // число, а на листе ничего не появляется. Правило то же, что у рамки.
                if (thick > 0.0 && color is null) color = "#000000";
                if (shape.StrokeColor == color
                    && Math.Abs(shape.StrokeThicknessPt - thick) < 0.01) return;

                EditSelectedShape("Обводка фигуры", s =>
                {
                    s.StrokeColor = color;
                    s.StrokeThicknessPt = thick;
                });
                return;
            }

            SetSelectedImageBorder(colorHex, thicknessPt);
        }

        private void SetFloatingBorderDash(ShapeDashStyle dash)
        {
            if (IsShapeActive) { SetSelectedShapeDash(dash); return; }
            SetSelectedImageBorderDash(dash);
        }

        private ShapeDashStyle? GetFloatingBorderDash()
            => _selectedShape?.DashStyle ?? GetSelectedImageBorderDash();

        /// <summary>
        /// Положение линии по контуру: внутрь, по границе или наружу. У картинки
        /// это её рамка, у фигуры — обводка; правило и последствия одни и те же:
        /// меняется габарит пятна на листе, а с ним и зона обтекания.
        /// </summary>
        private void SetFloatingOutlineAlign(ImageBorderAlign align)
        {
            if (IsShapeActive) { SetSelectedShapeStrokeAlign(align); return; }
            SetSelectedImageBorderAlign(align);
        }

        private ImageBorderAlign? GetFloatingOutlineAlign()
            => _selectedShape?.StrokeAlign ?? GetSelectedImageBorderAlign();

        // ── Отражение ─────────────────────────────────────────────────────

        /// <summary>
        /// Зеркало по горизонтали. У фигуры оно тоже осмысленно: разворачивает
        /// хвост выноски, остриё стрелки и картинку-заливку.
        /// </summary>
        private void ToggleFloatingFlipHorizontal()
        {
            if (IsShapeActive) { ToggleSelectedShapeFlipHorizontal(); return; }
            ToggleSelectedImageFlipHorizontal();
        }

        private void ToggleFloatingFlipVertical()
        {
            if (IsShapeActive) { ToggleSelectedShapeFlipVertical(); return; }
            ToggleSelectedImageFlipVertical();
        }

        // ── Форма ─────────────────────────────────────────────────────────

        private void SetFloatingShapeType(ShapeType type)
        {
            if (IsShapeActive) { SetSelectedShapeType(type); return; }
            SetSelectedImageShapeType(type);
        }

        private ShapeType? GetFloatingShapeType()
            => _selectedShape?.ShapeType ?? GetSelectedImageShapeType();

        private void SetFloatingCornerRadius(double radiusPt)
        {
            if (IsShapeActive) { SetSelectedShapeCornerRadius(radiusPt); return; }
            SetSelectedImageCornerRadius(radiusPt);
        }

        private double? GetFloatingCornerRadius()
            => _selectedShape?.CornerRadiusPt ?? GetSelectedImageCornerRadius();

        // ── Геометрия ─────────────────────────────────────────────────────

        private void SetFloatingWidth(double widthPt)
        {
            if (IsShapeActive) { SetSelectedShapeWidth(widthPt); return; }
            SetSelectedImageWidth(widthPt);
        }

        private void SetFloatingHeight(double heightPt)
        {
            if (IsShapeActive) { SetSelectedShapeHeight(heightPt); return; }
            SetSelectedImageHeight(heightPt);
        }

        private void SetFloatingRotation(double degrees)
        {
            if (IsShapeActive) { SetSelectedShapeRotation(degrees); return; }
            SetSelectedImageRotation(degrees);
        }

        private double? GetFloatingRotation()
            => _selectedShape?.RotationDeg ?? GetSelectedImageRotation();

        private void SetFloatingLockAspect(bool locked)
        {
            if (IsShapeActive) { SetSelectedShapeLockAspect(locked); return; }
            SetSelectedImageLockAspect(locked);
        }

        private void SetFloatingOpacity(double opacity)
        {
            if (IsShapeActive) { SetSelectedShapeOpacity(opacity); return; }
            SetSelectedImageOpacity(opacity);
        }

        // ── Обтекание и положение ─────────────────────────────────────────

        private void SetFloatingWrapMode(WrapMode mode)
        {
            if (IsShapeActive) { SetSelectedShapeWrapMode(mode); return; }
            SetSelectedImageWrapMode(mode);
        }

        private void SetFloatingWrapSide(WrapSide side)
        {
            if (IsShapeActive) { SetSelectedShapeWrapSide(side); return; }
            SetSelectedImageWrapSide(side);
        }

        private WrapSide? GetFloatingWrapSide()
            => _selectedShape?.WrapSide ?? GetSelectedImageWrapSide();

        private void SetFloatingWrapPadding(double topPt, double bottomPt, double leftPt, double rightPt)
        {
            if (IsShapeActive)
            {
                SetSelectedShapeWrapPadding(topPt, bottomPt, leftPt, rightPt);
                return;
            }
            SetSelectedImageWrapPadding(topPt, bottomPt, leftPt, rightPt);
        }

        private (double TopPt, double BottomPt, double LeftPt, double RightPt)? GetFloatingWrapPadding()
        {
            if (_selectedShape is { } s)
                return (s.WrapPadTopPt, s.WrapPadBottomPt, s.WrapPadLeftPt, s.WrapPadRightPt);
            return GetSelectedImageWrapPadding();
        }

        /// <summary>
        /// Привязка к странице. У картинки лента передаёт номер листа, у фигуры
        /// закрепление логическое — она встаёт на тот лист, где стоит сейчас.
        /// Ноль в обоих случаях снимает привязку.
        /// </summary>
        private void SetFloatingPinnedPage(int page)
        {
            if (IsShapeActive) { SetSelectedShapePinned(page > 0); return; }
            SetSelectedImagePinnedPage(page);
        }

        private int? GetFloatingPinnedPage()
            => _selectedShape is { } s ? s.PinnedPage : GetSelectedImagePinnedPage();

        private int? GetFloatingCurrentPage()
            => IsShapeActive ? GetSelectedShapePage() : GetSelectedImageCurrentPage();

        /// <summary>
        /// Выравнивание в колонке. Возвращает true, если правку забрал выделенный
        /// объект: по этому ответу DocumentViewModel решает, применять ли
        /// выравнивание к абзацу. Без него команда срабатывала бы дважды — и на
        /// объекте, и на тексте под ним.
        /// </summary>
        private bool SetFloatingAlignment(Models.Styles.TextAlignment alignment)
        {
            if (IsShapeActive)
            {
                SetSelectedShapeAlignment(alignment);
                return true;
            }
            return TrySetSelectedImageAlignment(alignment);
        }

        private void DeleteSelectedFloating()
        {
            if (IsShapeActive) { DeleteSelectedShape(); return; }
            DeleteSelectedImageFromCanvas();
        }

        // ── Сводки для ленты ──────────────────────────────────────────────

        /// <summary>
        /// Обтекание, замок пропорций и выравнивание выделенного объекта.
        /// Лента по этой сводке подсвечивает переключатели.
        /// </summary>
        private (WrapMode Wrap, bool LockAspect, Models.Styles.TextAlignment Align)? GetFloatingInfo()
        {
            if (_selectedShape is { } s)
                return (s.WrapMode, s.LockAspectRatio, s.Alignment);
            return GetSelectedImageInfo();
        }

        /// <summary>
        /// Размер, прозрачность и линия по контуру выделенного объекта.
        /// У фигуры роль рамки играет обводка.
        /// </summary>
        private (double WidthPt, double HeightPt, double Opacity,
                 string? BorderColor, double BorderThicknessPt)? GetFloatingStyle()
        {
            if (_selectedShape is { } s)
                return (s.WidthPt, s.HeightPt, s.Opacity, s.StrokeColor, s.StrokeThicknessPt);
            return GetSelectedImageStyle();
        }

        /// <summary>
        /// Что именно выделено: лента по этому признаку решает, какие инструменты
        /// сейчас применимы — заливка и наконечники есть только у фигуры, обрезка
        /// пока только у картинки. Отражение и положение линии по контуру в этот
        /// список больше не входят: они работают на обоих объектах.
        /// </summary>
        private (bool HasShape, bool HasImage, bool HasFillImage, bool IsLine)? GetFloatingKind()
        {
            if (_selectedShape is { } s)
                return (true, false, !string.IsNullOrEmpty(s.FillImageFileName),
                        s.ShapeType is ShapeType.Line or ShapeType.Arrow);

            if (_selectedImage is not null)
                return (false, true, true, false);

            return null;
        }
    }
}
