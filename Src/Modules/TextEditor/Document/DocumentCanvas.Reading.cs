using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using SkiaSharp;
using System;
using System.IO;
using Writersword.Core.Models.Print;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Page;
using Writersword.Modules.TextEditor.Models.Settings;
using Writersword.Modules.TextEditor.Rendering;
using Writersword.Modules.TextEditor.ViewModels;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Всё, что относится к чтению и не относится к перевороту страницы: бумага и
    /// её цвет, свет, шрифт чтения, размер книги на экране, ужатие содержимого,
    /// номера страниц и подвод книги к краю окна.
    ///
    /// Общее правило этого файла: ни одна настройка отсюда не доходит до модели
    /// документа. Читатель меняет то, что видит, а не то, что написано.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        // Поле вокруг книги. Оно же учитывается, когда книга крупнее окна и холст
        // растёт под неё: лист не должен упираться в край окна.
        internal const float SpreadOuterMarginPt = 26f;

        /// <summary>Настройки чтения текущего документа.</summary>
        private ReadingSettings? Reading => DocVm?.Reading;

        /// <summary>Идёт чтение — любой подачей.</summary>
        private bool ReadingActive => DocVm?.ViewMode == EditorViewMode.Reading;

        // Ширина вьюпорта в устройствах, замеренная последним проходом раскладки.
        // Размер листа считается по ней, а не по ширине холста: холст в приближённой
        // книге шире вьюпорта, и лист от него разбухал бы вместе с приближением.
        private double _readingViewportWidthPx;

        private double ReadingViewportWidthPx
            => _readingViewportWidthPx > 1 ? _readingViewportWidthPx : Math.Max(Bounds.Width, 1);

        /// <summary>
        /// Приближение книги: множитель отрисовки. Разбиение на страницы от него не
        /// зависит — книга просто ближе или дальше от глаза.
        /// </summary>
        private double ReadingViewZoom => Math.Clamp(
            Reading?.Zoom ?? 1.0, ReadingSettings.MinZoom, ReadingSettings.MaxZoom);

        /// <summary>
        /// Масштаб, которым книга вписывается в окно. Лист имеет постоянный размер
        /// (см. ComputeSpreadPageSize), а окно решает лишь то, с каким увеличением
        /// его показать. Отсюда следует главное: свернули ленту, растянули окно —
        /// книга стала крупнее или мельче, но текста на странице ровно столько же,
        /// и пересобирать раскладку не требуется.
        /// </summary>
        private double ReadingFitScale
        {
            get
            {
                if (!SpreadMode) return 1.0;
                if (_spreadPageWidthPt <= 1f || _spreadPageHeightPt <= 1f) return 1.0;

                double marginPx = SpreadOuterMarginPt * PtToPx;
                double availW = Math.Max(ReadingViewportWidthPx - marginPx * 2.0, 80.0);
                double availH = Math.Max((_viewportHeight > 0 ? _viewportHeight : Bounds.Height)
                                         - marginPx * 2.0, 80.0);

                double bookWpx = _spreadPageWidthPt * (SpreadSinglePage ? 1.0 : 2.0) * PtToPx;
                double bookHpx = _spreadPageHeightPt * PtToPx;
                if (bookWpx < 1.0 || bookHpx < 1.0) return 1.0;

                double fit = Math.Min(availW / bookWpx, availH / bookHpx);
                return Math.Clamp(fit, 0.05, 8.0);
            }
        }

        /// <summary>
        /// Принимает правку, которую достаточно перерисовать: свет, цвет бумаги,
        /// приближение книги, номера страниц. Раскладка остаётся прежней — гонять по
        /// ней полную пагинацию на каждое движение ползунка яркости нельзя.
        ///
        /// Приближение книги меняет масштаб отрисовки, поэтому нужен перемер: холст
        /// в логических точках при другом масштабе другой. В пикселях он остаётся
        /// равен вьюпорту — книга никогда не растит холст, полос прокрутки в чтении
        /// нет, и раскачки «полоса появилась — вьюпорт сузился — лист пересчитан»
        /// возникнуть не может.
        /// </summary>
        private void ApplyReadingVisualSettings()
        {
            ReleaseReadingPaperImage();

            // Снимки страниц книги сняты со старым цветом, светом и бумагой. Обычный
            // проход перерисует страницы заново, а снимок берётся один раз и живёт до
            // пересборки раскладки — и в момент переворота на экран возвращался бы
            // прежний вид: чёрный текст там, где его только что сделали цветным.
            InvalidateSpreadSnapshots();

            ResetReadingPan();
            FitCanvasToViewport();

            // Перемер нужен: приближение меняет и ширину холста, и его высоту в
            // логических точках. Пересборку раскладки он при этом не поднимает —
            // в книге отпечаток сравнивается по вьюпорту, а тот не изменился.
            InvalidateMeasure();
            InvalidateFull();
        }

        // ── Цвет бумаги и текста ──────────────────────────────────────────

        private readonly SKPaint _paintReadingPaper = new() { Color = SKColors.White };
        private readonly SKPaint _paintReadingBackdrop = new() { Color = new SKColor(0xE8, 0xE8, 0xE8) };

        /// <summary>
        /// Кисть листа. В чтении цвет бумаги задаёт выбранный её тип, в остальных
        /// режимах лист белый. Кисть переиспользуется: создавать её на каждую
        /// страницу незачем.
        /// </summary>
        private SKPaint PagePaint()
        {
            if (!SpreadMode && !ReadingActive) return _paintPageWhite;

            var color = ReadingPaperColor();
            if (_paintReadingPaper.Color != color) _paintReadingPaper.Color = color;
            return _paintReadingPaper;
        }

        /// <summary>
        /// Заливает поле вокруг книги. Вне чтения это обычный серый фон холста, в
        /// чтении — то, что задал вид.
        ///
        /// Заливка идёт целиком здесь, а не кистью наружу: и градиент, и картинка
        /// зависят от размера поля, а кисть его не знает.
        /// </summary>
        private void DrawCanvasBackdrop(SKCanvas canvas, float widthPt, float heightPt)
        {
            if (!SpreadMode && !ReadingActive)
            {
                canvas.DrawRect(0, 0, widthPt, heightPt, _paintCanvasBg);
                return;
            }

            var t = ActiveTheme;

            // Сплошной цвет ложится всегда: у градиента он служит запасным, а картинка
            // может быть прозрачной или не закрыть поле целиком — дыра в фоне выглядит
            // поломкой.
            var baseColor = ReadingBackdropColor();
            if (_paintReadingBackdrop.Color != baseColor) _paintReadingBackdrop.Color = baseColor;
            _paintReadingBackdrop.Shader = null;
            canvas.DrawRect(0, 0, widthPt, heightPt, _paintReadingBackdrop);

            if (t is null) return;

            // Градиент берётся тот же самый, что и у всякого другого цвета в программе:
            // своих видов заливки у поля нет, оно принимает выбранное как есть.
            var rect = new SKRect(0, 0, widthPt, heightPt);

            // Строится он по КВАДРАТУ, а не по всему полю. Направление в коде градиента
            // задано в долях прямоугольника: на широком поле те же доли дают совсем
            // другой наклон, и заливка ложится не так, как показывал образец в кружке.
            // Квадрат сохраняет угол, а поле просто вырезает из него свою часть.
            float side = MathF.Max(widthPt, heightPt);
            var gradientRect = new SKRect(
                (widthPt - side) / 2f, (heightPt - side) / 2f,
                (widthPt + side) / 2f, (heightPt + side) / 2f);

            using (var shader = SKTextRenderer.BuildGradientShader(t.BackdropColor, gradientRect))
            {
                if (shader is not null)
                {
                    using var paint = new SKPaint { Shader = shader };
                    canvas.DrawRect(rect, paint);
                }
            }

            if (t.UseBackdropImage) DrawBackdropImage(canvas, widthPt, heightPt, t);
        }

        private SKImage? _readingBackdropImage;
        private string? _readingBackdropImagePath;

        /// <summary>Картинка поля. Читается один раз и держится, пока путь не сменится.</summary>
        private SKImage? ReadingBackdropImage(Models.Settings.ReadingTheme t)
        {
            string? path = t.BackdropImagePath;
            if (string.IsNullOrWhiteSpace(path)) return null;

            if (_readingBackdropImage is not null
                && string.Equals(_readingBackdropImagePath, path, StringComparison.Ordinal))
                return _readingBackdropImage;

            _readingBackdropImage?.Dispose();
            _readingBackdropImage = null;
            _readingBackdropImagePath = null;

            try
            {
                // Адрес разбирается хранилищем вида: он может вести в архив
                // проекта, в данные программы или, у старых видов, прямо на диск.
                var data = Models.Settings.ReadingAssets.Read(path);
                if (data is null || data.Length == 0) return null;

                // Раскодировать сразу в пиксели, а не оставлять ленивый образ:
                // страницы книги снимаются в растровую поверхность, и образ,
                // привязавшийся к ускорителю при первой отрисовке в окно, туда
                // молча не попадает.
                using var bmp = SKBitmap.Decode(data);
                if (bmp is null) return null;
                _readingBackdropImage = SKImage.FromBitmap(bmp);
                _readingBackdropImagePath = path;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to read the background image: {Path}", path);
                _readingBackdropImage = null;
                _readingBackdropImagePath = null;
            }

            return _readingBackdropImage;
        }

        /// <summary>Кладёт картинку на поле вокруг книги.</summary>
        private void DrawBackdropImage(
            SKCanvas canvas, float widthPt, float heightPt, Models.Settings.ReadingTheme t)
        {
            var img = ReadingBackdropImage(t);
            if (img is null) return;

            byte alpha = (byte)Math.Clamp(t.BackdropImageOpacity * 255.0, 0.0, 255.0);
            if (alpha == 0) return;

            var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
            using var paint = new SKPaint { Color = new SKColor(255, 255, 255, alpha) };
            var src = new SKRect(0, 0, img.Width, img.Height);

            canvas.Save();
            canvas.ClipRect(new SKRect(0, 0, widthPt, heightPt));

            if (t.BackdropImageFit == Models.Settings.ReadingBackdropFit.Tile)
            {
                float tileW = Math.Max(img.Width, 8f);
                float tileH = Math.Max(img.Height, 8f);
                for (float y = 0f; y < heightPt; y += tileH)
                    for (float x = 0f; x < widthPt; x += tileW)
                        canvas.DrawImage(img, src, new SKRect(x, y, x + tileW, y + tileH), sampling, paint);
            }
            else if (t.BackdropImageFit == Models.Settings.ReadingBackdropFit.Stretch)
            {
                canvas.DrawImage(img, src, new SKRect(0, 0, widthPt, heightPt), sampling, paint);
            }
            else
            {
                // Cover закрывает поле целиком и режет лишнее, Contain умещает целиком
                // и оставляет цвет по краям. Разница только в том, какую из сторон брать.
                float scale = t.BackdropImageFit == Models.Settings.ReadingBackdropFit.Contain
                    ? Math.Min(widthPt / img.Width, heightPt / img.Height)
                    : Math.Max(widthPt / img.Width, heightPt / img.Height);

                float dw = img.Width * scale;
                float dh = img.Height * scale;
                float dx = (widthPt - dw) / 2f;
                float dy = (heightPt - dh) / 2f;
                canvas.DrawImage(img, src, new SKRect(dx, dy, dx + dw, dy + dh), sampling, paint);
            }

            canvas.Restore();
        }

        /// <summary>
        /// Цвет поля вокруг листа. Задан видом — берётся как есть; не задан —
        /// выводится из бумаги: под светлой книгой поле темнее её, под тёмной
        /// светлее. Лист обязан читаться как лист, а не сливаться со столом.
        /// </summary>
        private SKColor ReadingBackdropColor()
        {
            var t = ActiveTheme;
            if (t is null) return new SKColor(0xE8, 0xE8, 0xE8);

            if (!string.IsNullOrWhiteSpace(t.BackdropColor))
                return SKTextRenderer.GradientSolidColor(t.BackdropColor, new SKColor(0xE8, 0xE8, 0xE8));

            var paper = ReadingPaperColor();

            // Поле всегда темнее бумаги: так лист лежит НА столе, а не проваливается
            // в него. Исключение — почти чёрная бумага, темнее которой уже некуда:
            // там поле чуть светлее, и сдвиг меньше, чтобы ночью не бить по глазам.
            double luma = (0.2126 * paper.Red + 0.7152 * paper.Green + 0.0722 * paper.Blue) / 255.0;
            bool lighten = luma < 0.14;

            double target = lighten ? 255.0 : 0.0;

            // Сдвиг небольшой: поле, отличающееся от бумаги вдвое, спорит с ней за
            // внимание, а читать нужно книгу, а не стол под ней.
            double amount = lighten ? 0.10 : 0.16;

            return new SKColor(
                ShiftChannel(paper.Red, target, amount),
                ShiftChannel(paper.Green, target, amount),
                ShiftChannel(paper.Blue, target, amount));
        }

        /// <summary>Сдвигает канал к заданному пределу на заданную долю пути.</summary>
        private static byte ShiftChannel(byte v, double target, double amount)
            => (byte)Math.Clamp(v + (target - v) * amount, 0.0, 255.0);

        /// <summary>Активный вид чтения — то, чем рисуется книга.</summary>
        private ReadingTheme? ActiveTheme => Reading?.Active;

        /// <summary>Цвет листа выбранного вида.</summary>
        private SKColor ReadingPaperColor()
        {
            var t = ActiveTheme;
            if (t is null) return SKColors.White;
            return ParseHex(t.SheetColor, SKColors.White);
        }

        /// <summary>
        /// Цвет текста, у которого нет своего. Складывается из трёх вещей: цвета,
        /// который даёт бумага, собственного выбора читателя и контрастности.
        ///
        /// Контрастность не подкручивает яркость всей картинки, а разводит текст и
        /// бумагу: сто процентов — как задумано типом бумаги, меньше — буквы ближе к
        /// цвету листа и мягче, больше — дальше от него и резче. Так регулятор ведёт
        /// себя предсказуемо на любой бумаге, включая тёмную.
        /// </summary>
        private SKColor ReadingInkColor()
        {
            var t = ActiveTheme;
            if (t is null) return new SKColor(0x1A, 0x1A, 0x1A);

            var paper = ReadingPaperColor();
            var ink = ParseHex(t.InkColor, new SKColor(0x1A, 0x1A, 0x1A));

            double contrast = Math.Clamp(t.Contrast, 0.6, 1.6);
            if (Math.Abs(contrast - 1.0) < 0.001) return ink;

            return new SKColor(
                SpreadChannel(paper.Red, ink.Red, contrast),
                SpreadChannel(paper.Green, ink.Green, contrast),
                SpreadChannel(paper.Blue, ink.Blue, contrast),
                ink.Alpha);
        }

        /// <summary>Разводит канал текста и бумаги на заданный множитель.</summary>
        private static byte SpreadChannel(byte paper, byte ink, double factor)
        {
            double v = paper + (ink - paper) * factor;
            return (byte)Math.Clamp(v, 0.0, 255.0);
        }

        /// <summary>
        /// Служебный цвет чтения: номера страниц, маркеры списков без своего цвета,
        /// линии таблиц. Берётся у текста, но приглушается — иначе рамка таблицы
        /// на тёмной бумаге кричит громче самих букв.
        /// </summary>
        private SKColor ReadingMutedColor(byte alpha)
        {
            var ink = ReadingInkColor();
            return new SKColor(ink.Red, ink.Green, ink.Blue, alpha);
        }

        private static SKColor ParseHex(string? hex, SKColor fallback)
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            return SKColor.TryParse(hex, out var c) ? c : fallback;
        }

        // ── Свет ──────────────────────────────────────────────────────────

        /// <summary>
        /// Свет поверх готовой страницы: сперва тёплая вуаль, затем приглушение.
        /// Так же работает подсветка в читалках — она не меняет цвета документа, а
        /// убавляет свет над ним.
        ///
        /// Порядок важен: тёплота умножает цвета и должна лечь на полную яркость,
        /// иначе на приглушённой странице она почти не видна.
        /// </summary>
        private void DrawReadingDim(SKCanvas canvas, float widthPt, float heightPt)
        {
            var t = ActiveTheme;
            if (t is null) return;

            double warmth = Math.Clamp(t.Warmth, 0.0, 1.0);
            if (warmth > 0.004)
            {
                // Умножение, а не наложение: янтарь должен убирать синеву, а не
                // подмешивать белёсую пелену поверх букв.
                byte g = (byte)Math.Clamp(255.0 - warmth * 40.0, 0.0, 255.0);
                byte b = (byte)Math.Clamp(255.0 - warmth * 120.0, 0.0, 255.0);

                using var warmPaint = new SKPaint
                {
                    Color = new SKColor(255, g, b),
                    BlendMode = SKBlendMode.Multiply
                };
                canvas.DrawRect(0, 0, widthPt, heightPt, warmPaint);
            }

            double brightness = Math.Clamp(t.Brightness, 0.35, 1.0);
            if (brightness > 0.995) return;

            byte alpha = (byte)Math.Clamp((1.0 - brightness) * 255.0, 0, 200);
            using var paint = new SKPaint { Color = new SKColor(0, 0, 0, alpha) };
            canvas.DrawRect(0, 0, widthPt, heightPt, paint);
        }

        // ── Картинка бумаги ───────────────────────────────────────────────

        private SKImage? _readingPaperImage;
        private string? _readingPaperImagePath;

        /// <summary>
        /// Картинка бумаги, если она задана своей бумагой. Держится в памяти до смены
        /// пути: читать файл на каждый кадр нельзя.
        /// </summary>
        private SKImage? ReadingPaperImage()
        {
            var t = ActiveTheme;
            if (t is null) return null;

            string? path = t.ImagePath;
            if (string.IsNullOrWhiteSpace(path)) return null;

            if (_readingPaperImage is not null
                && string.Equals(_readingPaperImagePath, path, StringComparison.Ordinal))
                return _readingPaperImage;

            ReleaseReadingPaperImage();

            try
            {
                // Картинка берётся из хранилища вида, а не с диска напрямую: в
                // архиве проекта, в данных программы или по прежнему пути к
                // файлу — разбирается адрес там же, где он и заводится.
                var data = Models.Settings.ReadingAssets.Read(path);
                if (data is null || data.Length == 0) return null;

                // Раскодировать сразу в пиксели — по той же причине, что и у картинки
                // поля: ленивый образ не рисуется в растровый снимок страницы.
                using var bmp = SKBitmap.Decode(data);
                if (bmp is null) return null;
                _readingPaperImage = SKImage.FromBitmap(bmp);
                _readingPaperImagePath = path;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to read the paper image: {Path}", path);
                _readingPaperImage = null;
                _readingPaperImagePath = null;
            }

            return _readingPaperImage;
        }

        private void ReleaseReadingPaperImage()
        {
            _readingPaperImage?.Dispose();
            _readingPaperImage = null;
            _readingPaperImagePath = null;

            // Картинка поля отпускается вместе с бумагой: обе меняются одной и той же
            // правкой вида, и держать одну из них по старому пути незачем.
            _readingBackdropImage?.Dispose();
            _readingBackdropImage = null;
            _readingBackdropImagePath = null;
        }

        /// <summary>
        /// Кладёт картинку бумаги на лист. Растянутая занимает лист целиком с
        /// сохранением пропорций, замощённая повторяется в своём размере.
        /// </summary>
        private void DrawReadingPaperImage(SKCanvas canvas, float xPt, float yPt, float wPt, float hPt)
        {
            var img = ReadingPaperImage();
            if (img is null) return;

            var t = ActiveTheme;
            if (t is null) return;

            byte alpha = (byte)Math.Clamp(t.ImageOpacity * 255.0, 0.0, 255.0);
            if (alpha == 0) return;

            var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);

            canvas.Save();
            canvas.ClipRect(new SKRect(xPt, yPt, xPt + wPt, yPt + hPt));

            using var paint = new SKPaint { Color = new SKColor(255, 255, 255, alpha) };

            if (t.ImageTile)
            {
                // Замощение идёт от левого верхнего угла листа: так шов всегда в
                // одном и том же месте, и страницы разворота не расходятся рисунком.
                float tileW = Math.Max(img.Width * 0.75f, 8f);
                float tileH = Math.Max(img.Height * 0.75f, 8f);

                for (float y = yPt; y < yPt + hPt; y += tileH)
                    for (float x = xPt; x < xPt + wPt; x += tileW)
                        canvas.DrawImage(img,
                            new SKRect(0, 0, img.Width, img.Height),
                            new SKRect(x, y, x + tileW, y + tileH),
                            sampling, paint);
            }
            else
            {
                // Растягивание с сохранением пропорций: картинка покрывает лист
                // целиком, лишнее уходит под клип.
                float scale = Math.Max(wPt / img.Width, hPt / img.Height);
                float drawW = img.Width * scale;
                float drawH = img.Height * scale;
                float dx = xPt + (wPt - drawW) / 2f;
                float dy = yPt + (hPt - drawH) / 2f;

                canvas.DrawImage(img,
                    new SKRect(0, 0, img.Width, img.Height),
                    new SKRect(dx, dy, dx + drawW, dy + drawH),
                    sampling, paint);
            }

            canvas.Restore();
        }

        // ── Шрифт чтения ──────────────────────────────────────────────────

        /// <summary>
        /// Ставит рендеру текста подмены чтения: шрифт, ступень размера и цвет.
        /// Рендер статический и общий для всех канвасов, поэтому значения ставятся
        /// перед каждым проходом, а не один раз.
        ///
        /// Подменяется исключительно отрисовка. В модели документа не меняется ни
        /// один байт: ни размер, ни начертание, ни цвет — рукопись остаётся такой,
        /// какой её напечатают.
        /// </summary>
        private void PushReadingTextOverrides()
        {
            var r = Reading;
            var theme = ActiveTheme;
            bool reading = SpreadMode || ReadingActive;

            if (!reading || r is null || theme is null)
            {
                SKTextRenderer.DefaultTextColorOverride = null;
                SKTextRenderer.ReadingFontFamilyOverride = null;
                SKTextRenderer.ReadingFontScale = 1f;
                SKTextRenderer.ReadingBorderColorOverride = null;
                SKTextRenderer.ReadingMarkerColorOverride = null;
                SKTextRenderer.ReadingContentScale = 1f;
                SKTextRenderer.ReadingPaperColorOverride = null;
                return;
            }

            var ink = ReadingInkColor();

            SKTextRenderer.DefaultTextColorOverride = ink;
            SKTextRenderer.ReadingPaperColorOverride = ReadingPaperColor();
            SKTextRenderer.ReadingFontFamilyOverride =
                string.IsNullOrWhiteSpace(theme.FontFamily) ? null : theme.FontFamily;
            SKTextRenderer.ReadingFontScale = (float)r.FontScale;
            SKTextRenderer.ReadingContentScale = ReadingContentScale;

            // Маркер списка и рамка таблицы своего цвета обычно не имеют и рисуются
            // чёрным. На тёмной бумаге это чёрное по тёмному — точки списка пропадают,
            // а таблица превращается в дыру. Здесь им даётся цвет темы.
            SKTextRenderer.ReadingMarkerColorOverride = ink;
            SKTextRenderer.ReadingBorderColorOverride = ReadingMutedColor(150);
        }

        // ── Масштаб содержимого ───────────────────────────────────────────

        /// <summary>
        /// Во сколько раз содержимое ужато относительно бумажного листа. Лист чтения
        /// меньше печатной страницы, и картинка в исходном размере вылезает за
        /// колонку, а таблица с фиксированными колонками уезжает за край.
        ///
        /// Множитель тот же, что у полей (<see cref="_spreadPadScale"/>): ужимать
        /// содержимое иначе, чем поля, значит рассогласовать страницу саму с собой.
        /// </summary>
        private float ReadingContentScale
        {
            get
            {
                if (!SpreadMode) return 1f;
                var r = Reading;
                if (r is null || !r.ScaleContent) return 1f;
                if (_spreadPadScale <= 0f || _spreadPadScale >= 1f) return 1f;
                return _spreadPadScale;
            }
        }

        /// <summary>
        /// Габарит картинки с поправкой на чтение. Единственная точка, где размер
        /// картинки превращается из документного в экранный: раскладка, вёрстка
        /// строки и отрисовка обязаны спрашивать один и тот же ответ, иначе картинка
        /// нарисуется не там, где под неё оставлено место.
        /// </summary>
        private (float WidthPt, float HeightPt) ReadingImageSize(ImageBlock block)
        {
            float scale = ReadingContentScale;
            return ((float)block.WidthPt * scale, (float)block.HeightPt * scale);
        }

        // ── Номера страниц ────────────────────────────────────────────────

        // Есть ли у документа своя нумерация. Считается один раз на пересборку:
        // проход по колонтитулам всех разделов на каждый кадр не нужен.
        private bool? _documentHasOwnNumbering;

        /// <summary>Сбрасывает вывод о своей нумерации. Зовётся при пересборке.</summary>
        private void InvalidateOwnNumbering() => _documentHasOwnNumbering = null;

        /// <summary>
        /// Есть ли в документе собственная нумерация или бегущий колонтитул. Если
        /// есть — своих цифр чтение не рисует: две нумерации на одной странице
        /// выглядят ошибкой, а не удобством.
        /// </summary>
        private bool DocumentHasOwnPageNumbers()
        {
            if (_documentHasOwnNumbering is { } cached) return cached;

            bool has = false;
            var doc = DocVm?.Document;
            if (doc is not null)
            {
                foreach (var section in doc.Sections)
                {
                    if (HeaderFooterHasContent(section.Footer) || HeaderFooterHasContent(section.Header))
                    {
                        has = true;
                        break;
                    }
                }
            }

            _documentHasOwnNumbering = has;
            return has;
        }

        private static bool HeaderFooterHasContent(HeaderFooterModel? hf)
        {
            if (hf is null || !hf.IsEnabled) return false;
            foreach (var para in hf.Paragraphs)
                if (!string.IsNullOrWhiteSpace(para.GetPlainText())) return true;
            return false;
        }

        /// <summary>
        /// Еле заметный номер у нижнего внешнего угла листа. Внешний — потому что у
        /// корешка цифру не видно: там сгиб, и в бумажной книге номера тоже стоят по
        /// краям разворота.
        /// Координаты — левый верхний угол листа в текущей системе канваса.
        /// </summary>
        private void DrawReadingPageNumber(
            SKCanvas canvas, int pageIdx, float xPt, float yPt, float wPt, float hPt)
        {
            var r = Reading;
            if (r is null || !r.ShowPageNumbers) return;
            if (!SpreadMode) return;
            if (DocumentHasOwnPageNumbers()) return;
            if (pageIdx < 0) return;

            string text = (pageIdx + 1).ToString();

            float size = Math.Clamp(wPt * 0.026f, 7f, 11f);

            var typeface = ReadingNumberTypeface();
            using var font = new SKFont(typeface, size);
            using var paint = new SKPaint
            {
                Color = ReadingMutedColor(96),
                IsAntialias = true
            };

            float textW = font.MeasureText(text);
            float inset = Math.Max(wPt * 0.055f, 12f);
            float baseline = yPt + hPt - Math.Max(hPt * 0.035f, 10f);

            // Чётная страница разворота лежит слева, нечётная справа — внешний край
            // у них разный. Одиночный лист внешним считает правый: он и ближе к руке.
            bool leftSheet = !SpreadSinglePage && (pageIdx & 1) == 0;

            float x = leftSheet
                ? xPt + inset
                : xPt + wPt - inset - textW;

            canvas.DrawText(text, x, baseline, font, paint);
        }

        // Начертание номеров. Одно на весь канвас: создавать SKTypeface на каждую
        // страницу каждого кадра — это обращение к системному менеджеру шрифтов
        // десятки раз в секунду.
        private SKTypeface? _readingNumberTypeface;

        private SKTypeface ReadingNumberTypeface()
        {
            if (_readingNumberTypeface is not null) return _readingNumberTypeface;

            _readingNumberTypeface = SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default;
            return _readingNumberTypeface;
        }

        // ── Панорамирование приближённой книги ────────────────────────────

        // Сдвиг книги относительно центра видимой области, в пунктах. Пока книга
        // помещается в окно, он всегда нулевой.
        private float _readingPanXPt;
        private float _readingPanYPt;

        // Куда книгу зовёт указатель. Сама она идёт туда не мгновенно, а догоняя:
        // события мыши приходят неровно, и книга, повторяющая их один в один, дёргается
        // даже при спокойном движении руки. Тот же приём, что и у листа под рукой.
        private float _readingPanAimXPt;
        private float _readingPanAimYPt;

        private DispatcherTimer? _readingPanTimer;

        // Какую долю оставшегося пути книга проходит за такт. Меньше — мягче и дольше.
        private const float ReadingPanFollow = 0.22f;

        // Мёртвая зона по центру: пока указатель в ней, книга стоит. Без неё она
        // ползла бы от любого движения мыши над текстом.
        private const double ReadingPanDeadZone = 0.18;

        /// <summary>
        /// Видимая область книги в пунктах — то, что реально показано на экране при
        /// текущем приближении. Холст всегда равен вьюпорту, поэтому область считается
        /// прямо по нему.
        /// </summary>
        private (float WidthPt, float HeightPt) SpreadViewAreaPt()
        {
            float wPt = (float)(_canvasWidth * PxToPt);
            float hPt = (float)(Math.Max(_viewportHeight, 200) / Math.Max(Zoom, 0.01) * PxToPt);
            return (wPt, hPt);
        }

        /// <summary>
        /// Насколько книга может двигаться в каждую сторону. Ноль означает, что она
        /// целиком помещается по этой оси и двигать нечего.
        /// </summary>
        private (float X, float Y) ReadingPanRange()
        {
            if (!SpreadMode || _spreadPageWidthPt <= 1f) return (0f, 0f);

            var (viewWPt, viewHPt) = SpreadViewAreaPt();
            float totalW = (SpreadSinglePage ? 1f : 2f) * _spreadPageWidthPt;

            float freeX = Math.Max((totalW - viewWPt) / 2f + SpreadOuterMarginPt, 0f);
            float freeY = Math.Max((_spreadPageHeightPt - viewHPt) / 2f + SpreadOuterMarginPt, 0f);

            // Поле добавляется только там, где двигаться и так есть куда: иначе книга,
            // ровно помещающаяся в окно, начинала бы ёрзать на пустом месте.
            if (freeX <= SpreadOuterMarginPt + 0.5f) freeX = 0f;
            if (freeY <= SpreadOuterMarginPt + 0.5f) freeY = 0f;

            return (freeX, freeY);
        }

        /// <summary>Возвращает сдвиг книги в допустимые пределы.</summary>
        private void ClampReadingPan()
        {
            var (freeX, freeY) = ReadingPanRange();
            _readingPanXPt = Math.Clamp(_readingPanXPt, -freeX, freeX);
            _readingPanYPt = Math.Clamp(_readingPanYPt, -freeY, freeY);
        }

        /// <summary>Ставит книгу по центру. Зовётся при смене приближения и подачи.</summary>
        private void ResetReadingPan()
        {
            StopReadingPanTimer();
            _readingPanXPt = 0f;
            _readingPanYPt = 0f;
            _readingPanAimXPt = 0f;
            _readingPanAimYPt = 0f;
        }

        private void StartReadingPanTimer()
        {
            if (_readingPanTimer is null)
            {
                _readingPanTimer = new DispatcherTimer(DispatcherPriority.Render)
                {
                    Interval = TimeSpan.FromMilliseconds(1000.0 / 60.0)
                };
                _readingPanTimer.Tick += OnReadingPanTick;
            }
            if (!_readingPanTimer.IsEnabled) _readingPanTimer.Start();
        }

        private void StopReadingPanTimer()
        {
            if (_readingPanTimer is { IsEnabled: true }) _readingPanTimer.Stop();
        }

        /// <summary>
        /// Такт подвода: книга проходит долю оставшегося до цели пути. Шаг
        /// пропорционален расстоянию, поэтому книга трогается мягко и мягко встаёт.
        /// </summary>
        private void OnReadingPanTick(object? sender, EventArgs e)
        {
            if (!SpreadMode) { StopReadingPanTimer(); return; }

            float dx = _readingPanAimXPt - _readingPanXPt;
            float dy = _readingPanAimYPt - _readingPanYPt;

            if (MathF.Abs(dx) < 0.2f && MathF.Abs(dy) < 0.2f)
            {
                bool moved = dx != 0f || dy != 0f;
                _readingPanXPt = _readingPanAimXPt;
                _readingPanYPt = _readingPanAimYPt;
                StopReadingPanTimer();
                if (moved) { ClampReadingPan(); InvalidateFull(); }
                return;
            }

            _readingPanXPt += dx * ReadingPanFollow;
            _readingPanYPt += dy * ReadingPanFollow;
            ClampReadingPan();
            InvalidateFull();
        }

        /// <summary>
        /// Ведёт книгу за указателем, когда она крупнее окна. Курсор у левого края —
        /// показан левый край книги, у правого — правый; в середине книга стоит на
        /// месте. Прокрутки здесь нет намеренно: холст в чтении равен окну, полос по
        /// краям книги быть не должно, а достать её дальний угол всё равно нужно.
        /// </summary>
        private void UpdateReadingEdgePan(Point pointerPx)
        {
            if (!SpreadMode) return;

            var (freeX, freeY) = ReadingPanRange();
            if (freeX <= 0f && freeY <= 0f)
            {
                if (_readingPanXPt != 0f || _readingPanYPt != 0f)
                {
                    ResetReadingPan();
                    InvalidateFull();
                }
                return;
            }

            double w = Math.Max(_canvasWidth * Math.Max(Zoom, 0.01), 1.0);
            double h = Math.Max(_viewportHeight > 0 ? _viewportHeight : Bounds.Height, 1.0);

            float targetX = freeX > 0f ? (float)(-freeX * AxisAim(pointerPx.X / w)) : 0f;
            float targetY = freeY > 0f ? (float)(-freeY * AxisAim(pointerPx.Y / h)) : 0f;

            // Малые шевеления цель не двигают: полный кадр чтения — это весь текст
            // разворота, и гнать его от дрожания руки нельзя.
            if (Math.Abs(targetX - _readingPanAimXPt) < 0.75f
                && Math.Abs(targetY - _readingPanAimYPt) < 0.75f) return;

            // Указатель задаёт только цель. Саму книгу подтягивает такт таймера.
            _readingPanAimXPt = targetX;
            _readingPanAimYPt = targetY;
            StartReadingPanTimer();
        }

        /// <summary>
        /// Куда смотрит указатель по одной оси: -1 у начала, 0 в середине, +1 у конца.
        /// Мёртвая зона по центру вырезана, за её краями значение растёт до предела.
        /// </summary>
        private static double AxisAim(double t)
        {
            t = Math.Clamp(t, 0.0, 1.0) * 2.0 - 1.0;

            double dead = ReadingPanDeadZone;
            if (Math.Abs(t) <= dead) return 0.0;

            double sign = t < 0 ? -1.0 : 1.0;
            return sign * Math.Clamp((Math.Abs(t) - dead) / (1.0 - dead), 0.0, 1.0);
        }

        // ── Загнутый уголок ───────────────────────────────────────────────

        // Насколько поднят уголок под указателем: 0 — лежит ровно, 1 — отогнут
        // полностью. Сторона: -1 левая половина разворота, +1 правая.
        private float _spreadCornerHint;
        private int _spreadCornerSide;

        // Радиус, в котором уголок начинает отзываться на приближение указателя.
        private const float SpreadCornerReachPt = 110f;

        // Размер полностью отогнутого уголка.
        private const float SpreadCornerMaxPt = 46f;

        private bool _spreadCornerCursor;

        /// <summary>
        /// Ведёт уголок за указателем. Подведёшь к нижнему внешнему углу — бумага
        /// приподнимается, показывая, что лист можно взять и перевернуть. Это же и
        /// подсказка: без неё догадаться, что страницу тянут рукой, неоткуда.
        /// </summary>
        private void UpdateSpreadCornerHint(Point pointerPx)
        {
            if (!SpreadMode || SpreadSinglePage || _spreadFlipDir != 0 || _pages.Count == 0)
            {
                ClearSpreadCornerHint();
                return;
            }

            double zoom = Math.Max(Zoom, 0.01);
            float xPt = (float)(pointerPx.X / zoom * PxToPt);
            float yPt = (float)(pointerPx.Y / zoom * PxToPt);

            int idx = Math.Clamp(_spreadLeftPage, 0, _pages.Count - 1);
            var pg = _pages[idx];
            var (x, y) = SpreadPlacement(idx, true);

            float bottom = y + pg.HeightPt;
            float leftCornerX = x;
            float rightCornerX = x + pg.WidthPt * 2f;

            float dLeft = Distance(xPt, yPt, leftCornerX, bottom);
            float dRight = Distance(xPt, yPt, rightCornerX, bottom);

            int side = dRight <= dLeft ? 1 : -1;
            float d = Math.Min(dLeft, dRight);

            // К краю книги, за который идти некуда, уголок не поднимается: обещать
            // переворот, которого не будет, — обман.
            bool canTurn = side > 0 ? SpreadHasNext : SpreadHasPrev;

            float hint = canTurn && d < SpreadCornerReachPt
                ? 1f - d / SpreadCornerReachPt
                : 0f;

            // Мягкая кривая: у самого угла уголок поднимается заметно, а на подходе
            // почти не шевелится — иначе он дёргается от любого движения по странице.
            hint = hint * hint;

            SetSpreadCornerHint(side, hint);
        }

        private static float Distance(float x1, float y1, float x2, float y2)
        {
            float dx = x1 - x2;
            float dy = y1 - y2;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        private void SetSpreadCornerHint(int side, float hint)
        {
            hint = Math.Clamp(hint, 0f, 1f);

            bool sameEnough = _spreadCornerSide == side
                && MathF.Abs(_spreadCornerHint - hint) < 0.02f;

            bool wantCursor = hint > 0.12f;
            if (wantCursor != _spreadCornerCursor)
            {
                _spreadCornerCursor = wantCursor;
                Cursor = new Avalonia.Input.Cursor(
                    wantCursor ? StandardCursorType.Hand : StandardCursorType.Arrow);
            }

            if (sameEnough) return;

            _spreadCornerSide = side;
            _spreadCornerHint = hint;

            // Уголок рисуется поверх готового снимка страницы — как каретка. Полная
            // пересборка кадра ради него не нужна, иначе книга перерисовывалась бы
            // на каждое движение мыши.
            _caretOnlyRedraw = true;
            InvalidateVisual();
        }

        private void ClearSpreadCornerHint()
        {
            if (_spreadCornerCursor)
            {
                _spreadCornerCursor = false;
                Cursor = new Avalonia.Input.Cursor(StandardCursorType.Arrow);
            }

            if (_spreadCornerHint <= 0f) return;

            _spreadCornerHint = 0f;
            _caretOnlyRedraw = true;
            InvalidateVisual();
        }

        /// <summary>
        /// Рисует отогнутый уголок: треугольник изнанки листа и тень под ним.
        /// Координаты — те же, в которых нарисован разворот.
        /// </summary>
        private void DrawSpreadCornerHint(SKCanvas canvas)
        {
            if (!SpreadMode || SpreadSinglePage || _spreadFlipDir != 0) return;
            if (_spreadCornerHint <= 0.01f || _pages.Count == 0) return;

            int idx = Math.Clamp(_spreadLeftPage, 0, _pages.Count - 1);
            var pg = _pages[idx];
            var (x, y) = SpreadPlacement(idx, true);

            float size = SpreadCornerMaxPt * _spreadCornerHint;
            if (size < 2f) return;

            float bottom = y + pg.HeightPt;
            bool right = _spreadCornerSide > 0;
            float cornerX = right ? x + pg.WidthPt * 2f : x;
            float innerX = right ? cornerX - size : cornerX + size;

            // Тень под поднятой бумагой — она и создаёт ощущение, что угол отошёл
            // от страницы, а не нарисован на ней.
            using (var shadow = new SKPaint
            {
                Color = new SKColor(0, 0, 0, (byte)(70 * _spreadCornerHint)),
                IsAntialias = true,
                ImageFilter = SKImageFilter.CreateBlur(size * 0.18f, size * 0.18f)
            })
            using (var shadowPath = new SKPath())
            {
                shadowPath.MoveTo(cornerX, bottom - size);
                shadowPath.LineTo(cornerX, bottom);
                shadowPath.LineTo(innerX, bottom);
                shadowPath.Close();
                canvas.DrawPath(shadowPath, shadow);
            }

            // Сам уголок: изнанка листа. Цвет бумаги, притемнённый к сгибу, — так же
            // выглядит отогнутая страница на свету.
            var paper = ReadingPaperColor();
            var deep = new SKColor(
                (byte)(paper.Red * 0.82f),
                (byte)(paper.Green * 0.82f),
                (byte)(paper.Blue * 0.82f));

            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(cornerX, bottom),
                new SKPoint(innerX, bottom - size),
                new[] { paper, deep },
                new[] { 0f, 1f },
                SKShaderTileMode.Clamp);

            using var paint = new SKPaint { Shader = shader, IsAntialias = true };
            using var path = new SKPath();
            path.MoveTo(cornerX, bottom - size);
            path.LineTo(cornerX, bottom);
            path.LineTo(innerX, bottom);
            path.Close();
            canvas.DrawPath(path, paint);

            // Тонкая линия сгиба: без неё уголок сливается с бумагой на светлой теме.
            using var edge = new SKPaint
            {
                Color = ReadingMutedColor((byte)(90 * _spreadCornerHint)),
                IsStroke = true,
                StrokeWidth = 0.8f,
                IsAntialias = true
            };
            canvas.DrawLine(cornerX, bottom - size, innerX, bottom, edge);
        }

        // ── Клавиши чтения ────────────────────────────────────────────────

        /// <summary>
        /// Нажали клавишу выхода. В книге все нажатия разбирает канвас, наружу они не
        /// уходят, и решение — выйти из полного экрана или из чтения — принимает вью.
        /// </summary>
        public Action? ReadingEscapePressed { get; set; }

        /// <summary>Нажали клавишу полноэкранного режима.</summary>
        public Action? ReadingFullscreenTogglePressed { get; set; }

        /// <summary>
        /// Указатель ушёл с канваса — на ленту сверху, за нижний край, куда угодно.
        ///
        /// Книга при этом остаётся там, куда её довели. Раньше она возвращалась на
        /// середину, и выходило нелепо: читатель ведёт курсор к верхнему краю книги,
        /// доводит до самого верха, курсор переходит на ленту — и книга рывком
        /// прыгает обратно в центр, показав ровно не то, к чему её вели. То же самое
        /// у нижнего края. Указатель у края потому там и оказался, что человек
        /// смотрит на край: держать край — единственное разумное поведение.
        ///
        /// Ведение возобновляется само, как только курсор возвращается на книгу.
        /// </summary>
        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            ClearSpreadCornerHint();
        }

        /// <summary>
        /// Отпускает всё, что держит чтение: подвод книги и картинку бумаги.
        /// Зовётся при отсоединении канваса от дерева.
        /// </summary>
        private void ReleaseReadingResources()
        {
            StopReadingPanTimer();
            ResetReadingPan();
            _spreadCornerHint = 0f;
            _spreadCornerCursor = false;
            ReleaseReadingPaperImage();
        }
    }
}
