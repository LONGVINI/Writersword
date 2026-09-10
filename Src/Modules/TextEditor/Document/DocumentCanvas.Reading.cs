using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using SkiaSharp;
using System;
using System.Collections.Generic;
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

        /// <summary>
        /// Чтение лентой: тот же самый документ, что и в режиме страниц, только листы
        /// склеены встык. Раскладка у ленты страничная — та же пагинация, те же поля,
        /// те же места картинок и таблиц; лентой её делает одно отображение: страницы
        /// ставятся друг за другом без зазоров и без верхних и нижних полей, а всё,
        /// что выходит за низ листа, обрезается его краем.
        /// </summary>
        private bool ReadingRibbon => ReadingActive && !SpreadMode;

        /// <summary>Поле сверху и снизу всей ленты. Постраничных полей у неё нет.</summary>
        private const float ReadingRibbonPadPt = 26f;

        /// <summary>Наименьшее поле между полосой ленты и краем окна.</summary>
        private const float ReadingRibbonSideMarginPt = 18f;

        // Курсор ленты. Обычная стрелка, и создаётся один раз: указатель шлёт движения
        // десятками в секунду, и новый системный курсор на каждое из них — работа на
        // ровном месте.
        private static readonly Avalonia.Input.Cursor ReadingRibbonCursor =
            new Avalonia.Input.Cursor(StandardCursorType.Arrow);

        /// <summary>
        /// Клавиатура ленты. Читалка, а не редактор: наружу уходит только прокрутка,
        /// всё остальное здесь и заканчивается — иначе горячие клавиши правки работали
        /// бы прямо посреди чтения.
        ///
        /// Возвращает true, если нажатие разобрано и дальше идти не должно.
        /// </summary>
        private bool HandleReadingRibbonKey(KeyEventArgs e)
        {
            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

            // Ctrl с плюсом и минусом подводит и отводит ленту — то же, что и в книге.
            // Размер шрифта рукописи при этом не меняется: за него отвечает ступень
            // кегля чтения.
            if (ctrl)
            {
                switch (e.Key)
                {
                    case Key.OemPlus:
                    case Key.Add:
                        ChangeBookZoom(1);
                        return true;

                    case Key.OemMinus:
                    case Key.Subtract:
                        ChangeBookZoom(-1);
                        return true;

                    case Key.D0:
                    case Key.NumPad0:
                        SetBookZoom(1.0);
                        return true;
                }

                // Остальные сочетания с Ctrl — это горячие клавиши правки: вставка,
                // отмена, форматирование. В чтении им делать нечего.
                return true;
            }

            switch (e.Key)
            {
                case Key.Escape:
                    // Выход из чтения и из полного экрана разбирает вью: она одна знает,
                    // раскрыт ли модуль поверх окна и что сейчас нужно закрыть.
                    ReadingEscapePressed?.Invoke();
                    return true;

                case Key.F11:
                    ReadingFullscreenTogglePressed?.Invoke();
                    return true;

                case Key.Space:
                    ScrollReadingRibbonByScreen(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                    return true;

                // Прокрутка ленты — дело полосы прокрутки: нажатие уходит наружу
                // неразобранным, и её обработчик двигает ленту сам.
                case Key.Up:
                case Key.Down:
                case Key.Left:
                case Key.Right:
                case Key.PageUp:
                case Key.PageDown:
                case Key.Home:
                case Key.End:
                    return false;

                default:
                    return true;
            }
        }

        /// <summary>Прокрутка ленты на экран вниз или вверх.</summary>
        private void ScrollReadingRibbonByScreen(int dir)
        {
            var sv = _parentScrollViewer;
            if (sv is null || dir == 0) return;

            // Экран без пары строк: так последняя строка предыдущего экрана остаётся
            // видна сверху, и читатель не теряет место, на котором остановился.
            double step = Math.Max(sv.Viewport.Height - FallbackLinePt * PtToPx * 2.0, 40.0);
            double max = Math.Max(sv.Extent.Height - sv.Viewport.Height, 0.0);

            sv.Offset = new Vector(sv.Offset.X, Math.Clamp(sv.Offset.Y + step * dir, 0.0, max));
        }

        /// <summary>
        /// Рисовать ли рамки, маркеры и заливку выделения.
        ///
        /// В чтении — нет. Выделенная картинка, выделенная фигура и выделенный текст
        /// живут в модели канваса и переживают вход в книгу: правил человек рукопись,
        /// открыл чтение — и на странице висит рамка с маркерами размера, взяться
        /// которым там неоткуда. Само выделение при этом не сбрасывается: выйдя из
        /// книги, человек находит свою работу там же, где оставил.
        /// </summary>
        private bool SelectionDrawable => !ReadingActive;

        /// <summary>
        /// Показывать ли пометки редактора о том, что объект промахнулся мимо листа:
        /// бледность и красную штриховку, а с ними и снятый клип.
        ///
        /// Они нужны при правке — объект в документе есть, и человеку надо его найти
        /// и вернуть. В чтении возвращать нечего: там книга, а не рукопись, и
        /// заштрихованный прямоугольник в поле над страницей читается как поломка.
        /// Поэтому в книге такой объект просто обрезается своим листом, как всё
        /// остальное.
        /// </summary>
        private bool OffPageMarkersVisible => !ReadingActive;

        /// <summary>Вид рабочей области при правке.</summary>
        private EditorViewSettings? EditorView => DocVm?.EditorView;

        /// <summary>
        /// Фон правки рисует не канвас, а окно: под ним лежит слой с картинкой.
        ///
        /// Канвас для этого не годится ничем. Холст в режиме страниц высотой во
        /// весь документ, и картинка, вписанная в него, сжимается в полоску;
        /// вписанная в видимое окно — дрожит при прокрутке, потому что канвас
        /// перерисовывается не на каждый её пиксель, а слой окна не прокручивается
        /// вовсе. Поэтому здесь поле просто не заливается: сквозь него виден слой.
        ///
        /// Чтения это не касается — там холст равен окну, книга не прокручивается,
        /// и фон остаётся на канвасе, где и был.
        /// </summary>
        private bool WindowBackdropActive
            => EditorThemeActive
               && EditorView?.Active is { UseBackdropImage: true } t
               && !string.IsNullOrWhiteSpace(t.BackdropImagePath);

        /// <summary>
        /// Лист правки перекрашен выбранным видом. Чтение сюда не входит: там свой
        /// вид и свои настройки, и решает за него <see cref="ReadingActive"/>.
        /// </summary>
        private bool EditorThemeActive
            => !ReadingActive && !SpreadMode && EditorView is { ThemeEnabled: true, Active: not null };

        /// <summary>
        /// Лист рисуется не белым: либо идёт чтение, либо вид назначен правке.
        /// Одна проверка на весь файл — цвет бумаги, поле, чернила и свет обязаны
        /// включаться и выключаться вместе, иначе получается тёмный лист с чёрным
        /// текстом.
        /// </summary>
        private bool ThemedSurface => SpreadMode || ReadingActive || EditorThemeActive;

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
                // Лента вписывается в окно только по ширине и только вниз: лист у неё
                // документный, и на узком окне он иначе не влезал бы вовсе — появлялась
                // бы горизонтальная прокрутка поперёк чтения. Крупнее собственного
                // размера полоса не становится: увеличение — дело читателя.
                if (ReadingRibbon)
                {
                    float ribbonSheetWPt = GetPageWidthPt();
                    if (ribbonSheetWPt <= 1f) return 1.0;

                    double ribbonAvailPx = Math.Max(
                        ReadingViewportWidthPx - ReadingRibbonSideMarginPt * 2.0 * PtToPx, 80.0);
                    double ribbonSheetPx = ribbonSheetWPt * PtToPx;
                    if (ribbonSheetPx < 1.0) return 1.0;

                    return Math.Clamp(ribbonAvailPx / ribbonSheetPx, 0.05, 1.0);
                }

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

            // Панорама и подгонка холста под вьюпорт — книжные вещи. При правке
            // холст равен высоте документа, и подгонка обрезала бы его до одного
            // экрана: прокрутка упёрлась бы в текущую страницу до следующей
            // пересборки раскладки. Сменить вид листа она при этом не мешает —
            // перерисовки ниже хватает.
            // Лента сюда не входит вместе с книгой: у неё холст высотой во весь текст,
            // и подгонка под окно отняла бы у неё прокрутку.
            if (SpreadMode)
            {
                ResetReadingPan();
                FitCanvasToViewport();
            }
            else if (ReadingRibbon)
            {
                // Приближение меняет и ширину полосы, и длину всей ленты. Полоса от
                // этого уезжала влево, а место в тексте — вверх: холст стал выше, а
                // прокрутка осталась на прежнем числе точек. Держим и то, и другое:
                // полоса встаёт по центру окна, а лента — на той же доле длины.
                KeepReadingRibbonPlace();
            }

            // Перемер нужен: приближение меняет и ширину холста, и его высоту в
            // логических точках. Пересборку раскладки он при этом не поднимает —
            // в книге отпечаток сравнивается по вьюпорту, а тот не изменился.
            InvalidateMeasure();
            InvalidateFull();
        }

        // ── Место в ленте ─────────────────────────────────────────────────
        // Страниц у ленты нет, и мерить место в ней нечем, кроме доли всей длины.
        // Ползунок и поле в ленте чтения работают именно по ней: не «страница 7 из
        // 40», а «41 %».

        /// <summary>Доля прочитанного в ленте: 0 — начало, 100 — конец.</summary>
        public double ReadingPercent
        {
            get
            {
                var sv = _parentScrollViewer;
                if (sv is null) return 0.0;

                double span = sv.Extent.Height - sv.Viewport.Height;
                if (span <= 0.5) return 0.0;

                return Math.Clamp(sv.Offset.Y / span * 100.0, 0.0, 100.0);
            }
        }

        /// <summary>Место в ленте изменилось. Число — доля, 0..100.</summary>
        public Action<double>? ReadingPercentChanged { get; set; }

        /// <summary>
        /// Ставит ленту на долю всей длины, 0..100.
        ///
        /// <paramref name="takeFocus"/> — забрать клавиатуру у ленты чтения. Просьба
        /// из поля ввода приходит с ним: фокус остался бы в поле, и прокрутка с
        /// клавиатуры молчала бы до первого щелчка мимо. Ползунок приходит без него —
        /// отнятый фокус оборвал бы перетаскивание.
        /// </summary>
        public void GoReadingPercent(double percent, bool takeFocus)
        {
            var sv = _parentScrollViewer;
            if (sv is null || !ReadingRibbon) return;

            double span = Math.Max(sv.Extent.Height - sv.Viewport.Height, 0.0);
            double target = span * Math.Clamp(percent, 0.0, 100.0) / 100.0;

            if (Math.Abs(sv.Offset.Y - target) >= 0.5)
                sv.Offset = new Vector(sv.Offset.X, target);

            if (takeFocus) Focus();
        }

        /// <summary>Сообщает наружу, где сейчас стоит лента.</summary>
        private void NotifyReadingPercent()
        {
            if (!ReadingRibbon) return;
            ReadingPercentChanged?.Invoke(ReadingPercent);
        }

        /// <summary>
        /// Возвращает ленту на прежнее место после того, как холст сменил размер:
        /// полосу — по центру окна, текст — на ту же долю длины, на которой читатель
        /// остановился.
        ///
        /// Доля снимается сейчас, а ставится следующим проходом разметки: холст ещё не
        /// перемерен, и новых границ прокрутки в этот момент не существует.
        /// </summary>
        private void KeepReadingRibbonPlace()
        {
            var sv = _parentScrollViewer;
            if (sv is null) return;

            double maxY = Math.Max(sv.Extent.Height - sv.Viewport.Height, 0.0);
            double place = maxY > 0.5 ? Math.Clamp(sv.Offset.Y / maxY, 0.0, 1.0) : 0.0;

            Dispatcher.UIThread.Post(() =>
            {
                var view = _parentScrollViewer;
                if (view is null || !ReadingRibbon) return;

                double spanX = Math.Max(view.Extent.Width - view.Viewport.Width, 0.0);
                double spanY = Math.Max(view.Extent.Height - view.Viewport.Height, 0.0);

                view.Offset = new Vector(spanX / 2.0, spanY * place);
                NotifyReadingPercent();
            }, DispatcherPriority.Loaded);
        }

        // ── Цвет бумаги и текста ──────────────────────────────────────────

        private readonly SKPaint _paintReadingPaper = new() { Color = SKColors.White };
        private readonly SKPaint _paintReadingBackdrop = new() { Color = new SKColor(0xE8, 0xE8, 0xE8) };

        /// <summary>
        /// Кисть листа. Цвет бумаги задаёт выбранный вид — в чтении свой, в правке
        /// свой; вида нет — лист белый. Кисть переиспользуется: создавать её на
        /// каждую страницу незачем.
        /// </summary>
        private SKPaint PagePaint()
        {
            if (!ThemedSurface) return _paintPageWhite;

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
            if (!ThemedSurface)
            {
                canvas.DrawRect(0, 0, widthPt, heightPt, _paintCanvasBg);
                return;
            }

            var t = ActiveTheme;

            // Фон правки задан картинкой — поле не заливается ничем. И цвет, и
            // картинку рисует слой под канвасом: он не прокручивается вместе с
            // рукописью, поэтому и не дрожит. Залить здесь хоть чем-нибудь значит
            // закрыть его собой.
            if (WindowBackdropActive) return;

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

            // В чтении картинка поля остаётся на канвасе: холст там равен окну,
            // книга не прокручивается, и дрожать нечему.
            if (t.UseBackdropImage)
                DrawBackdropImage(canvas, new SKRect(0, 0, widthPt, heightPt), t);
        }

        // Замок на картинки вида. Их читает и рисует поток отрисовки, а освобождает
        // поток правки: смена вида, цвета или пути к файлу выбрасывает прежний образ,
        // и сделать это она может ровно в тот миг, когда им рисуют. Освобождённый
        // образ роняет процесс прямо в нативном коде Skia — без стека и без шанса
        // догадаться, откуда прилетело. Поэтому и чтение с отрисовкой, и освобождение
        // идут под одним замком — так же, как у снимков страниц (_spreadCacheLock).
        private readonly object _readingImageLock = new();

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
                if (data is null || data.Length == 0)
                {
                    // Молчать здесь нельзя. Адрес у картинки есть, а на экране
                    // ровный цвет — и человек видит не «фон не задан», а «фон не
                    // работает». Причина всегда одна из двух: файл не уложился в
                    // хранилище вида или хранилище сейчас недоступно.
                    _logger.Warning(
                        "Background image is set but unreadable: {Path}", path);
                    return null;
                }

                // Раскодировать сразу в пиксели, а не оставлять ленивый образ:
                // страницы книги снимаются в растровую поверхность, и образ,
                // привязавшийся к ускорителю при первой отрисовке в окно, туда
                // молча не попадает.
                using var bmp = SKBitmap.Decode(data);
                if (bmp is null)
                {
                    _logger.Warning(
                        "Background image could not be decoded: {Path} ({Size} bytes)",
                        path, data.Length);
                    return null;
                }
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

        /// <summary>
        /// Кладёт картинку фона в заданное окно. Как она в него ложится, решает вид:
        /// заполнить с обрезкой, уместить целиком, растянуть или замостить.
        /// </summary>
        private void DrawBackdropImage(
            SKCanvas canvas, SKRect area, Models.Settings.ReadingTheme t)
        {
            // Образ держится под замком всё время, пока им рисуют — см. _readingImageLock.
            lock (_readingImageLock)
            {
                DrawBackdropImageLocked(canvas, area, t);
            }
        }

        private void DrawBackdropImageLocked(
            SKCanvas canvas, SKRect area, Models.Settings.ReadingTheme t)
        {
            var img = ReadingBackdropImage(t);
            if (img is null) return;

            byte alpha = (byte)Math.Clamp(t.BackdropImageOpacity * 255.0, 0.0, 255.0);
            if (alpha == 0) return;

            float widthPt = area.Width;
            float heightPt = area.Height;
            if (widthPt < 1f || heightPt < 1f) return;

            var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
            using var paint = new SKPaint { Color = new SKColor(255, 255, 255, alpha) };
            var src = new SKRect(0, 0, img.Width, img.Height);

            canvas.Save();
            canvas.ClipRect(area);

            if (t.BackdropImageFit == Models.Settings.ReadingBackdropFit.Tile)
            {
                float tileW = Math.Max(img.Width, 8f);
                float tileH = Math.Max(img.Height, 8f);

                // Замощение идёт от левого верхнего угла окна, а не холста: иначе
                // рисунок ползёт под рукописью при каждой прокрутке.
                for (float y = area.Top; y < area.Bottom; y += tileH)
                    for (float x = area.Left; x < area.Right; x += tileW)
                        canvas.DrawImage(img, src, new SKRect(x, y, x + tileW, y + tileH), sampling, paint);
            }
            else if (t.BackdropImageFit == Models.Settings.ReadingBackdropFit.Stretch)
            {
                canvas.DrawImage(img, src, area, sampling, paint);
            }
            else
            {
                // Cover закрывает окно целиком и режет лишнее, Contain умещает целиком
                // и оставляет цвет по краям. Разница только в том, какую из сторон брать.
                float scale = t.BackdropImageFit == Models.Settings.ReadingBackdropFit.Contain
                    ? Math.Min(widthPt / img.Width, heightPt / img.Height)
                    : Math.Max(widthPt / img.Width, heightPt / img.Height);

                float dw = img.Width * scale;
                float dh = img.Height * scale;
                float dx = area.Left + (widthPt - dw) / 2f;
                float dy = area.Top + (heightPt - dh) / 2f;
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

        /// <summary>
        /// Активный вид — то, чем рисуется лист. В чтении это вид чтения, при правке
        /// с включённым видом — вид правки. Ни один другой код в этом файле про два
        /// источника не знает: он спрашивает вид и получает тот, который сейчас в
        /// работе.
        /// </summary>
        private ReadingTheme? ActiveTheme
        {
            get
            {
                if (SpreadMode || ReadingActive) return Reading?.Active;
                return EditorThemeActive ? EditorView?.Active : null;
            }
        }

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
            // Освобождение идёт под тем же замком, что и отрисовка: поток отрисовки
            // может держать образ прямо сейчас, и выдернутый из-под него он валит
            // процесс в нативном коде Skia.
            lock (_readingImageLock)
            {
                _readingPaperImage?.Dispose();
                _readingPaperImage = null;
                _readingPaperImagePath = null;

                // Картинка поля отпускается вместе с бумагой: обе меняются одной и той
                // же правкой вида, и держать одну из них по старому пути незачем.
                _readingBackdropImage?.Dispose();
                _readingBackdropImage = null;
                _readingBackdropImagePath = null;
            }
        }

        /// <summary>
        /// Кладёт картинку бумаги на лист. Растянутая занимает лист целиком с
        /// сохранением пропорций, замощённая повторяется в своём размере.
        /// </summary>
        private void DrawReadingPaperImage(SKCanvas canvas, float xPt, float yPt, float wPt, float hPt)
        {
            // Образ держится под замком всё время, пока им рисуют — см. _readingImageLock.
            lock (_readingImageLock)
            {
                DrawReadingPaperImageLocked(canvas, xPt, yPt, wPt, hPt);
            }
        }

        private void DrawReadingPaperImageLocked(
            SKCanvas canvas, float xPt, float yPt, float wPt, float hPt)
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

            if ((!reading && !EditorThemeActive) || theme is null)
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

            // Шрифт и кегль подменяются только в чтении. Правка обязана показывать
            // рукопись такой, какой её напечатают: чужая гарнитура и чужой размер
            // здесь означали бы, что человек верстает вслепую — строки на экране
            // рвутся не там, где на бумаге.
            if (reading && r is not null)
            {
                SKTextRenderer.ReadingFontFamilyOverride =
                    string.IsNullOrWhiteSpace(theme.FontFamily) ? null : theme.FontFamily;
                SKTextRenderer.ReadingFontScale = (float)r.FontScale;
                SKTextRenderer.ReadingContentScale = ReadingContentScale;
            }
            else
            {
                SKTextRenderer.ReadingFontFamilyOverride = null;
                SKTextRenderer.ReadingFontScale = 1f;
                SKTextRenderer.ReadingContentScale = 1f;
            }

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

        /// <summary>
        /// Габарит фигуры с той же поправкой, что и у картинки. Фигура на листе
        /// чтения обязана ужиматься вместе с ним: лист меньше печатного, а рамка или
        /// стрелка в исходном размере на нём выглядит вдвое крупнее, чем в рукописи.
        /// Нижний предел ставится ПОСЛЕ ужатия — иначе крошечная фигура на карманном
        /// листе выросла бы вместо того, чтобы уменьшиться.
        /// </summary>
        private (float WidthPt, float HeightPt) ReadingShapeSize(ShapeBlock block)
        {
            float scale = ReadingContentScale;
            return (
                Math.Max((float)block.WidthPt * scale, ShapeMinSidePt),
                Math.Max((float)block.HeightPt * scale, ShapeMinSidePt));
        }

        /// <summary>
        /// Смещение плавающего или привязанного объекта по горизонтали, приведённое к
        /// листу чтения.
        ///
        /// Размер объекта ужимается вместе с листом, а смещение до сих пор оставалось
        /// печатным: на карманном листе, вдвое более узком, чем бумага документа,
        /// картинка, стоявшая у правого поля А4, уезжала за обрез. Считается оно в
        /// долях текстовой области — объект, стоявший на трети её ширины, там и
        /// остаётся, на каком бы листе книгу ни открыли.
        /// </summary>
        private float ReadingOffsetXPt(double offsetPt)
            => (float)offsetPt * (ReadingGeometryScaled ? _spreadOffsetScaleX : 1f);

        /// <summary>
        /// Лист книги отличается от бумаги документа настолько, что геометрию
        /// плавающих объектов нужно пересчитывать.
        ///
        /// При формате «как у документа» лист книги — тот же самый лист, и трогать на
        /// нём НЕЧЕГО: книга обязана показывать картинки ровно там же, где они стоят
        /// на обычной странице. Всякий пересчёт здесь — искажение, а не подгонка.
        /// </summary>
        private bool ReadingGeometryScaled
        {
            get
            {
                if (!SpreadMode) return false;
                if (Reading is not { ScaleContent: true }) return false;

                return Math.Abs(_spreadOffsetScaleX - 1f) > 0.002f
                    || Math.Abs(_spreadOffsetScaleY - 1f) > 0.002f;
            }
        }

        /// <summary>
        /// То же по вертикали, но с разбором на листы.
        ///
        /// Ось отдельная не из вредности: форматы книги сжимают лист по-разному —
        /// «широкий» почти той же ширины, что бумага, но вдвое ниже, и общий множитель
        /// уводил бы картинку за нижний край.
        ///
        /// Одним множителем здесь тоже нельзя, и это стоило отдельной поломки.
        /// Вертикальное смещение отсчитывается от страницы БЛОКА в потоке, а картинку
        /// разрешено утащить на несколько листов вниз — тогда смещение хранит в себе
        /// высоты пройденных страниц. Ужатое целиком, оно стягивало такие картинки к
        /// началу документа, и они сходились в кучу на первом же листе.
        ///
        /// Поэтому смещение разбирается на две части: сколько листов вниз — их место
        /// занимают листы книги во всю свою высоту, — и где объект стоит на самом
        /// листе; ужимается только вторая.
        /// </summary>
        private float ReadingOffsetYPt(double offsetPt)
        {
            if (!ReadingGeometryScaled) return (float)offsetPt;
            if (_spreadDocPageStepPt <= 1f || _spreadReadPageStepPt <= 1f)
                return (float)(offsetPt * _spreadOffsetScaleY);

            double sign = offsetPt < 0.0 ? -1.0 : 1.0;
            double abs = Math.Abs(offsetPt);

            double pages = Math.Floor(abs / _spreadDocPageStepPt);
            double rest = abs - pages * _spreadDocPageStepPt;

            return (float)(sign * (pages * _spreadReadPageStepPt + rest * _spreadOffsetScaleY));
        }

        /// <summary>
        /// Загоняет плавающий или привязанный объект в лист чтения.
        ///
        /// Смещения приведены к листу по долям (см. ReadingOffsetXPt/YPt), но этого
        /// мало: объект, стоявший вплотную к краю бумаги, на другом листе всё равно
        /// вылезает за обрез, а высокая картинка не помещается по высоте — лист книги
        /// ниже печатного. Здесь габарит ужимается до текстовой области, а затем
        /// возвращается внутрь её границ.
        ///
        /// Считается по AABB повёрнутого прямоугольника: на экране за край выходит
        /// именно он, а не стороны самой картинки.
        ///
        /// В правке ничего не делает: там лист документа, и место объекта на нём —
        /// решение автора, а не раскладки.
        /// </summary>
        private (float XPt, float YPt, float WidthPt, float HeightPt) FitFloatingToSheet(
            float xPt, float yPt, float wPt, float hPt, double rotationDeg,
            float sheetXPt, float sheetYPt, float sheetWPt, float sheetHPt)
        {
            // Лист тот же, что и в документе — объект остаётся ровно там, где автор его
            // поставил, даже если он и там вылезал за поле: это его вёрстка, а не
            // ошибка книги.
            if (!ReadingGeometryScaled || wPt <= 0f || hPt <= 0f)
                return (xPt, yPt, wPt, hPt);

            var (ml, mt, mr, mb) = GetPagePaddingPt();

            float left = sheetXPt + ml;
            float top = sheetYPt + mt;
            float availW = Math.Max(sheetWPt - ml - mr, 1f);
            float availH = Math.Max(sheetHPt - mt - mb, 1f);

            double rad = rotationDeg * Math.PI / 180.0;
            float absCos = (float)Math.Abs(Math.Cos(rad));
            float absSin = (float)Math.Abs(Math.Sin(rad));

            float boxW = wPt * absCos + hPt * absSin;
            float boxH = wPt * absSin + hPt * absCos;
            if (boxW <= 0f || boxH <= 0f) return (xPt, yPt, wPt, hPt);

            // Ужимается объект целиком и в одной пропорции: разное сжатие по осям
            // растянуло бы картинку, а этого не просил никто.
            float k = Math.Min(1f, Math.Min(availW / boxW, availH / boxH));
            if (k < 1f)
            {
                wPt *= k; hPt *= k;
                boxW *= k; boxH *= k;
            }

            // Центр записи и центр её габарита — одна и та же точка: запись хранит
            // неповёрнутый прямоугольник, а поворот идёт вокруг центра.
            float cx = xPt + wPt / 2f;
            float cy = yPt + hPt / 2f;

            float boxLeft = Math.Clamp(cx - boxW / 2f, left, left + availW - boxW);
            float boxTop = Math.Clamp(cy - boxH / 2f, top, top + availH - boxH);

            cx = boxLeft + boxW / 2f;
            cy = boxTop + boxH / 2f;

            return (cx - wPt / 2f, cy - hPt / 2f, wPt, hPt);
        }

        // Зазор между разведёнными объектами. Ноль поставил бы их вплотную, и на
        // экране они читались бы как один слипшийся блок.
        private const float ReadingObjectGapPt = 10f;

        // Сколько раз объект пробует отойти вниз. Каждый шаг уводит его ниже уже
        // размещённого соседа, и упереться в потолок раньше десятка попыток он может
        // только на листе, забитом объектами целиком.
        private const int ReadingAvoidSteps = 12;

        /// <summary>
        /// Разводит плавающий объект с теми, что уже стоят на этом листе.
        ///
        /// В книге лист другой формы, чем бумага документа: то, что на А4 стояло рядом
        /// и не задевало друг друга, здесь налезает — картинка на картинку, фигура на
        /// таблицу. Каждый объект считает своё место сам и о соседях не знает, поэтому
        /// знание о них приходит отсюда: объект уступает и отходит вниз, пока не
        /// перестанет задевать чужой габарит.
        ///
        /// Вниз, а не в сторону: колонка одна, и сдвиг вбок увёл бы объект из-под
        /// текста, который его обтекает. Не поместился до низа листа — остаётся там,
        /// где стоял: лучше наложение, чем объект, вытесненный с листа вовсе.
        ///
        /// В правке не работает: там настоящая страница документа, и место объекта на
        /// ней — решение автора.
        /// </summary>
        private (float XPt, float YPt) AvoidReadingOverlap(
            float xPt, float yPt, float wPt, float hPt, double rotationDeg,
            int pageIdx,
            List<PageRect> pages,
            List<ImageEntry> images,
            List<ShapeEntry> shapes,
            List<TableEntry> tables,
            object? self)
        {
            if (!SpreadMode || pages.Count == 0) return (xPt, yPt);
            if (wPt <= 0f || hPt <= 0f) return (xPt, yPt);

            int idx = Math.Clamp(pageIdx, 0, pages.Count - 1);
            var page = pages[idx];

            var (ml, mt, mr, mb) = GetPagePaddingPt();
            float sheetTop = page.Ypt + mt;
            float sheetBottom = page.Ypt + page.HeightPt - mb;

            double rad = rotationDeg * Math.PI / 180.0;
            float absCos = (float)Math.Abs(Math.Cos(rad));
            float absSin = (float)Math.Abs(Math.Sin(rad));
            float boxW = wPt * absCos + hPt * absSin;
            float boxH = wPt * absSin + hPt * absCos;

            float cx = xPt + wPt / 2f;
            float cy = yPt + hPt / 2f;
            float boxLeft = cx - boxW / 2f;
            float boxTop = cy - boxH / 2f;

            // Ниже низа листа отходить некуда, и объект остаётся на месте.
            if (boxH >= sheetBottom - sheetTop) return (xPt, yPt);

            var busy = new List<SKRect>();

            foreach (var ie in images)
            {
                if (ie.PageIndex != idx) continue;
                if (ie.InLine) continue;
                if (ReferenceEquals(ie.Block, self)) continue;

                double r = ie.Block.RotationDeg * Math.PI / 180.0;
                float c = (float)Math.Abs(Math.Cos(r));
                float sn = (float)Math.Abs(Math.Sin(r));
                float bw = ie.WidthPt * c + ie.HeightPt * sn;
                float bh = ie.WidthPt * sn + ie.HeightPt * c;
                float icx = ie.XPt + ie.WidthPt / 2f;
                float icy = ie.Ypt + ie.HeightPt / 2f;

                busy.Add(new SKRect(icx - bw / 2f, icy - bh / 2f, icx + bw / 2f, icy + bh / 2f));
            }

            foreach (var se in shapes)
            {
                if (se.PageIndex != idx) continue;
                if (ReferenceEquals(se.Block, self)) continue;

                double r = se.Block.RotationDeg * Math.PI / 180.0;
                float c = (float)Math.Abs(Math.Cos(r));
                float sn = (float)Math.Abs(Math.Sin(r));
                float bw = se.WidthPt * c + se.HeightPt * sn;
                float bh = se.WidthPt * sn + se.HeightPt * c;
                float scx = se.XPt + se.WidthPt / 2f;
                float scy = se.Ypt + se.HeightPt / 2f;

                busy.Add(new SKRect(scx - bw / 2f, scy - bh / 2f, scx + bw / 2f, scy + bh / 2f));
            }

            // Таблица — такой же занятый прямоугольник: ромб поверх её клеток выглядит
            // ничуть не лучше, чем поверх картинки.
            foreach (var te in tables)
            {
                if (te.PageIndex != idx) continue;

                busy.Add(new SKRect(
                    te.XPt, te.Ypt,
                    te.XPt + te.Layout.TotalWidthPt,
                    te.Ypt + te.Layout.TotalHeightPt));
            }

            if (busy.Count == 0) return (xPt, yPt);

            float startTop = boxTop;

            for (int step = 0; step < ReadingAvoidSteps; step++)
            {
                float lowest = float.MinValue;

                foreach (var rect in busy)
                {
                    bool overlaps = boxLeft < rect.Right && boxLeft + boxW > rect.Left
                                 && boxTop < rect.Bottom && boxTop + boxH > rect.Top;
                    if (!overlaps) continue;

                    if (rect.Bottom > lowest) lowest = rect.Bottom;
                }

                if (lowest <= float.MinValue) break;

                float nextTop = lowest + ReadingObjectGapPt;

                // Ниже листа места нет — объект возвращается туда, где стоял.
                if (nextTop + boxH > sheetBottom) return (xPt, yPt);

                boxTop = nextTop;
            }

            if (Math.Abs(boxTop - startTop) < 0.01f) return (xPt, yPt);

            cy = boxTop + boxH / 2f;
            return (xPt, cy - hPt / 2f);
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
            // Пока взятый лист лежит плашмя, приподнятый уголок остаётся: он часть той
            // же картинки, что и в покое, и гасить его в момент нажатия значит менять
            // вид страницы до того, как читатель что-то сделал.
            if (!SpreadMode || SpreadSinglePage || SpreadLeafLifted || _pages.Count == 0)
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
            if (!SpreadMode || SpreadSinglePage || SpreadLeafLifted) return;
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

        /// <summary>
        /// Нажали клавишу выхода в режиме фокуса. Правка идёт обычным порядком —
        /// каретка, выделение, ввод, — поэтому Esc разбирается здесь же, где и
        /// остальные клавиши, а решение принимает вью.
        /// </summary>
        public Action? FocusEscapePressed { get; set; }

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
