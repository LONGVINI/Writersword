using SkiaSharp;
using System;
using System.Collections.Generic;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Кадр во время жеста масштаба: прежний снимок, растянутый под новый масштаб.
    ///
    /// Смена масштаба делала снимок кадра негодным, и каждый кадр шёл полным
    /// рендером: все видимые листы заново, в битмап высотой в три окна, с копией всех
    /// его пикселей. Это десятки миллисекунд, а на плотных листах и больше. Пока
    /// масштаб менялся раз в щелчок, это был один рывок на щелчок. Плавная анимация
    /// гонит десяток кадров на щелчок, и каждый из них становился рывком — масштаб
    /// шёл ступеньками.
    ///
    /// Во время жеста снимок не перерисовывается, а кладётся на экран с тем же
    /// преобразованием, с каким изменилась геометрия листа: по вертикали — растяжение
    /// от верха холста, по горизонтали — растяжение с поправкой на центрирование
    /// листа в окне. Это одна операция видеокарты на кадр. Подложка с пустыми листами
    /// рисуется по живому масштабу и закрывает то, что снимок не покрыл. Когда жест
    /// закончился, приходит обычный полный кадр — уже чёткий.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        // Левый край содержимого (px холста) в момент снятия текущего _displayImage.
        // По нему и по тому же краю при живом масштабе строится горизонтальное
        // преобразование снимка.
        private double _displayImageLeftPx;

        // Линейная фильтрация: кубическая подчёркивает контуры глифов, и при плавном
        // изменении масштаба буквы будто пульсируют.
        private static readonly SKSamplingOptions ZoomPreviewSampling =
            new(SKFilterMode.Linear, SKMipmapMode.None);

        // Диагностика жеста масштаба: сколько кадров нарисовано растяжением снимка,
        // сколько ушло в полный рендер и почему. Счётчики пишутся на потоке отрисовки,
        // читаются и сбрасываются в конце жеста (DocumentCanvas.WheelZoom).
        private int _zoomDiagPreviewFrames;
        private int _zoomDiagFullFrames;
        private string _zoomDiagFullReason = string.Empty;

        /// <summary>
        /// Левый край содержимого в px холста при заданной ширине холста и масштабе.
        /// Те же формулы, что у отрисовки листов:
        /// — листы по одному в режиме страниц центрируются по живой ширине холста
        ///   (до-центрирование в RenderPageModeCore);
        /// — листы в ряд центрируются рядом (PageVisualDelta), край — левый край
        ///   первого листа;
        /// — в режиме потока край у левой границы холста.
        /// </summary>
        private double ContentLeftPx(double canvasWidth, double zoom)
        {
            float leftPt = _layoutPageXPt;

            bool pageLayout = DocVm?.ViewMode == EditorViewMode.Page || SpreadMode || ReadingRibbon;
            if (pageLayout)
            {
                if (_pagesPerRow <= 1 && !SpreadMode)
                {
                    float canvasWPt = (float)(canvasWidth * PxToPt);
                    leftPt = Math.Max((canvasWPt - GetPageWidthPt()) / 2f, 0f);
                }
                else
                {
                    // Список листов подменяется целиком при пересборке, а не правится
                    // на месте, — ссылку можно взять без замка отрисовки: здесь он
                    // может быть уже занят в обратном порядке с замком снимка.
                    var pages = _pages;
                    if (pages.Count > 0)
                    {
                        var (dx, _) = PageVisualDelta(0, pages);
                        leftPt = pages[0].PadLeftPt + dx;
                    }
                }
            }
            else
            {
                leftPt = 0f;
            }

            return leftPt * PtToPx * zoom;
        }

        /// <summary>
        /// Рисует кадр жеста масштаба из прежнего снимка. false — случай не наш (жеста
        /// нет, снимка нет, содержимое менялось, книга или лента), и кадр идёт обычным
        /// путём.
        /// </summary>
        private bool TryDrawZoomPreview(
            SKCanvas canvas,
            List<ParaLayout> layouts,
            List<PageRect> pages,
            double canvasWidth,
            double zoom,
            float scale)
        {
            if (!_zooming) return false;
            if (SpreadMode || ReadingActive) return false;

            if (_contentDirty)
            {
                NoteZoomFullFrame("содержимое изменилось");
                return false;
            }

            bool drew = false;
            string missReason = string.Empty;

            lock (_bitmapLock)
            {
                var image = _displayImage;
                double zoom0 = _displayImageZoom;

                if (image is not null
                    && !double.IsNaN(zoom0)
                    && zoom0 > 0
                    && Math.Abs(zoom0 - zoom) >= 1e-9)
                {
                    double a = zoom / zoom0;
                    double left0 = _displayImageLeftPx;
                    double left1 = ContentLeftPx(canvasWidth, zoom);

                    // X1 = left1 + (X0 − left0)·a, Y1 = Y0·a; снимок лежит от x = 0.
                    float destX = (float)(left1 - left0 * a);
                    float destY = (float)(_lastFullRenderScrollY * a);
                    var dest = SKRect.Create(
                        destX, destY,
                        (float)(image.Width * a), (float)(image.Height * a));

                    using var paint = new SKPaint { IsAntialias = false };
                    canvas.DrawImage(
                        image,
                        new SKRect(0, 0, image.Width, image.Height),
                        dest,
                        ZoomPreviewSampling,
                        paint);

                    drew = true;
                }
                else
                {
                    // Масштаб снимка совпал с текущим — кадр обычный, из кэша, и
                    // полным рендером не считается.
                    missReason = image is null
                        ? "снимка нет"
                        : Math.Abs(zoom0 - zoom) < 1e-9 ? string.Empty : "масштаб снимка неизвестен";
                }
            }

            if (!drew)
            {
                if (missReason.Length > 0) NoteZoomFullFrame(missReason);
                return false;
            }

            System.Threading.Interlocked.Increment(ref _zoomDiagPreviewFrames);

            _caretOnlyRedraw = false;

            // Выделение и каретка в снимке не лежат — кладём их поверх по живому
            // масштабу, как на кадре из кэша.
            canvas.Save();
            canvas.Scale(scale, scale);
            DrawSelectionOverlay(canvas, layouts, pages, canvasWidth);
            DrawHeadingToggles(canvas, layouts, pages, canvasWidth);
            canvas.Restore();

            if (CaretDrawable)
            {
                canvas.Save();
                canvas.Scale(scale, scale);
                DrawCaretOnCanvas(canvas, layouts, pages, canvasWidth);
                canvas.Restore();
            }

            PerfCount("r.zoompreview");
            return true;
        }

        private void NoteZoomFullFrame(string reason)
        {
            System.Threading.Interlocked.Increment(ref _zoomDiagFullFrames);
            _zoomDiagFullReason = reason;
        }
    }
}
