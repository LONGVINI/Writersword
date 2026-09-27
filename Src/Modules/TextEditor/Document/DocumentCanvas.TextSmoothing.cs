using SkiaSharp;
using Writersword.Modules.TextEditor.Rendering;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// ClearType в снимке кадра (<see cref="ScreenTextSmoothing"/>).
    ///
    /// Шрифты рендера текста просят субпиксельное сглаживание всегда, а включает
    /// его поверхность: у холста без порядка субпикселей Skia сама рисует буквы
    /// серым. Поэтому решение принимается здесь, одно на снимок, — каким холстом
    /// его рисовать.
    ///
    /// Субпиксельная буква смешивается с тем, что под ней, каждым цветом отдельно,
    /// и под ней обязана лежать непрозрачная краска. На прозрачном пикселе её
    /// цветные полоски потом накладываются окном на фон как попало — отсюда цветная
    /// грязь вокруг букв. Поэтому ClearType включается только там, где снимок под
    /// текстом заведомо непрозрачен: в режиме страниц, где снимок сам заливает поле
    /// и листы. Черновик и чтение колонкой рисуют текст на прозрачном снимке поверх
    /// подложки окна; книга и лента рисуются мимо снимка или с полупрозрачным
    /// светом поверх — там текст остаётся серым, как был.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        // Порядок субпикселей для снимков кадра. Считается в начале каждого кадра на
        // render-потоке; фоновая дорисовка берёт его в момент запуска.
        private SKPixelGeometry _snapshotPixelGeometry = SKPixelGeometry.Unknown;

        /// <summary>
        /// Пересчитывает порядок субпикселей снимка для кадра, который окно выводит
        /// холстом windowCanvas.
        /// </summary>
        private void UpdateSnapshotPixelGeometry(SKCanvas windowCanvas)
        {
            _snapshotPixelGeometry = SnapshotBackgroundOpaque()
                ? ScreenTextSmoothing.GeometryFor(windowCanvas.TotalMatrix)
                : SKPixelGeometry.Unknown;
        }

        /// <summary>
        /// Снимок под всем своим текстом непрозрачен: проход страниц заливает поле
        /// и листы сплошной краской до того, как класть на них буквы.
        /// </summary>
        private bool SnapshotBackgroundOpaque()
        {
            var mode = DocVm?.ViewMode ?? EditorViewMode.Draft;
            if (mode != EditorViewMode.Page || SpreadMode || ReadingRibbon) return false;

            if (!ThemedSurface)
                return _paintCanvasBg.Color.Alpha == 255 && _paintPageWhite.Color.Alpha == 255;

            // Фон правки картинкой: поле в снимке не заливается ничем, его рисует
            // слой под канвасом.
            if (WindowBackdropActive) return false;

            return ReadingBackdropColor().Alpha == 255 && ReadingPaperColor().Alpha == 255;
        }
    }
}
