using System;
using System.Collections.Generic;
using Avalonia.Threading;
using SkiaSharp;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Книжный разворот режима чтения: перелистывание страницы с перспективой.
    ///
    /// Раскладка здесь не своя — страницы верстает та же пагинация, что и режим
    /// страниц, только лист виртуальный (см. ComputeSpreadPageSize). Этот файл
    /// отвечает за три вещи: какие страницы показаны, как лист летит вокруг
    /// корешка и как этим листом управляет рука.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        // ── Состояние переворота ──────────────────────────────────────────

        // Направление идущего переворота: 0 — покоя нет, +1 — вперёд, -1 — назад.
        private int _spreadFlipDir;

        // Угол листа в градусах: 0 — лежит на своей стороне, 180 — лёг на другую.
        private float _spreadFlipAngle;

        // На какой разворот лист ложится. Обычно это соседний, но при переходе по
        // номеру страницы — любой: книга открывается одним движением, а не сотней
        // перевёрнутых листов подряд.
        private int _spreadFlipTargetLeft = -1;

        // Лист ведёт рука: анимация не идёт, угол берётся из положения указателя.
        private bool _spreadDragging;

        // Доля пути, на которой лист подхватили, и сдвинулась ли рука с тех пор.
        // Угол отсчитывается ОТ этой точки: иначе лист прыгал бы на десятки градусов
        // в момент нажатия — тем сильнее, чем ближе к корешку взялись.
        private float _spreadDragStartTravel;
        private bool _spreadDragMoved;

        // Угол, которого хочет рука. Лист идёт к нему не мгновенно, а догоняя: события
        // указателя приходят неравномерно и с дрожанием, и лист, повторяющий их один
        // в один, заметно дёргается даже при ровном движении.
        private float _spreadDragTargetAngle;

        // Полёт после отпускания: от текущего угла к цели за отведённое время.
        private bool _spreadReleasing;
        private float _spreadReleaseFrom;
        private float _spreadReleaseTo;
        private double _spreadReleaseMs;
        private long _spreadReleaseStartTicks;

        private DispatcherTimer? _spreadTimer;

        // ── Скольжение одиночного листа ───────────────────────────────────
        // У отдельной страницы нет корешка, вокруг которого поворачиваться, поэтому
        // и переворота у неё быть не может. Зато смена страницы не должна случаться
        // рывком: уходящая уезжает в сторону, приходящая занимает её место — так же,
        // как листают читалки на телефоне.

        // Направление скольжения: 0 — покой, +1 вперёд, -1 назад.
        private int _singleSlideDir;

        // Страницы, участвующие в скольжении: та, что уходит, и та, что приходит.
        private int _singleSlideFrom = -1;
        private int _singleSlideTo = -1;

        // Доля пройденного пути, уже со сглаживанием: 0 — страница на месте, 1 — смена
        // завершена.
        private float _singleSlideProgress;
        private long _singleSlideStartTicks;

        private const double SingleSlideMs = 420.0;

        /// <summary>Идёт смена одиночной страницы.</summary>
        private bool SingleSliding => _singleSlideDir != 0;

        // Снимки страниц, участвующих в перевороте. Ключ — индекс страницы.
        // Рендерить содержимое на каждый кадр нельзя: это полная отрисовка текста,
        // таблиц и картинок шестьдесят раз в секунду.
        private readonly Dictionary<int, SKImage> _spreadPageCache = new();

        // Замок на снимки. Их читает и рисует поток отрисовки, а освобождает поток
        // правки: смена цвета или света выбрасывает устаревшие снимки, и сделать это
        // она может ровно в тот миг, когда лист уже летит и рисуется. Освобождённый
        // образ роняет процесс прямо в нативном коде Skia — без стека и без шанса
        // догадаться, откуда прилетело. Поэтому и чтение с отрисовкой, и освобождение
        // идут под одним замком.
        private readonly object _spreadCacheLock = new();

        // Страницы, снимок которых не получился. Повторять попытку внутри одного
        // переворота бессмысленно и очень дорого.
        private readonly HashSet<int> _spreadSnapshotFailed = new();

        // Страницы, которые обычный проход не рисует: они летят как отдельный лист.
        private int _spreadFlyFront = -1;
        private int _spreadFlyBack = -1;

        // Открыть книгу на странице каретки — один раз при входе в режим.
        private bool _spreadNeedsCaretSync = true;

        // Последняя объявленная наружу пара «разворот и число страниц»: подпись
        // обновляется только когда что-то из этого изменилось.
        private int _spreadLabelPage = -1;
        private int _spreadLabelCount = -1;

        // Удалённость камеры от плоскости страницы, в долях ширины листа. Меньше —
        // резче перспектива. Значение подобрано под ощущение бумаги: лист заметно
        // сужается к корешку, но не выглядит падающим в колодец.
        private const float SpreadCameraDistFactor = 3.4f;

        // Порог доводки: за этим углом отпущенный лист идёт до конца, не дойдя —
        // возвращается на место.
        private const float SpreadCommitAngle = 62f;

        private const double SpreadFullFlipMs = 460.0;

        // Изгиб бумаги. Корешок держит лист, а ведёт его свободный край — основание
        // всегда отстаёт от края на несколько градусов, и именно поэтому бумага
        // выглядит бумагой, а не поворачивающейся доской. Значение — наибольшее
        // расхождение между началом и концом листа, в градусах.
        private const float SpreadMaxBendDeg = 34f;

        // Полосы, из которых набирается изогнутый лист. Skia кладёт картинку только
        // на плоский четырёхугольник, поэтому изгиб собирается из узких плоских
        // кусков: чем круче изгиб, тем больше полос нужно, чтобы грани не читались.
        private const int LeafStripsMax = 40;

        // Профиль изгиба и посчитанный по нему хребет листа. Массивы живут в поле, а
        // не создаются на кадр: анимация идёт шестьдесят раз в секунду, и мусор от
        // неё сборщик убирал бы прямо посреди переворота.
        private readonly float[] _leafAngle = new float[LeafStripsMax + 1];
        private readonly SKPoint[] _leafTop = new SKPoint[LeafStripsMax + 1];
        private readonly SKPoint[] _leafBottom = new SKPoint[LeafStripsMax + 1];

        /// <summary>Сколько страниц уходит за один переворот: разворот или одиночный лист.</summary>
        private int SpreadStep => SpreadSinglePage ? 1 : 2;

        /// <summary>Есть ли следующий разворот.</summary>
        private bool SpreadHasNext => _spreadLeftPage + SpreadStep < _pages.Count;

        /// <summary>Есть ли предыдущий разворот.</summary>
        private bool SpreadHasPrev => _spreadLeftPage - SpreadStep >= 0;

        /// <summary>Номер первой страницы текущего разворота, начиная с единицы.</summary>
        public int SpreadPageNumber => _spreadLeftPage + 1;

        /// <summary>Всего страниц в книге.</summary>
        public int SpreadPageCount => _pages.Count;

        /// <summary>Сообщает наружу, что разворот сменился — для подписи и позиции.</summary>
        public Action? SpreadPageChanged { get; set; }

        // ── Переходы ──────────────────────────────────────────────────────

        /// <summary>
        /// Начинает переворот в заданную сторону. Возвращает false, если идти некуда
        /// или переворот уже идёт.
        /// </summary>
        private bool BeginSpreadFlip(int dir)
        {
            if (!SpreadMode || dir == 0) return false;
            if (SpreadSinglePage) return false;
            if (_spreadFlipDir != 0) return false;
            if (dir > 0 && !SpreadHasNext) return false;
            if (dir < 0 && !SpreadHasPrev) return false;

            return BeginSpreadFlipTo(_spreadLeftPage + (dir > 0 ? SpreadStep : -SpreadStep));
        }

        /// <summary>
        /// Начинает переворот к заданному развороту — не обязательно соседнему.
        /// Одним движением листа книга открывается на любой странице: перелистывать
        /// сотню разворотов подряд ради перехода по номеру никто не станет смотреть.
        /// Возвращает false, если идти некуда или переворот уже идёт.
        /// </summary>
        private bool BeginSpreadFlipTo(int targetLeft)
        {
            if (!SpreadMode) return false;

            // Одиночный лист — не книга. Перспектива, летящая бумага и тень сгиба
            // это свойства разворота: у отдельной страницы нет ни корешка, ни
            // второй половины, вокруг которой ей поворачиваться. Здесь страница
            // просто сменяется, как в читалке.
            if (SpreadSinglePage) return false;

            if (_spreadFlipDir != 0) return false;
            if (_pages.Count == 0) return false;

            targetLeft = SpreadLeftOf(Math.Clamp(targetLeft, 0, _pages.Count - 1));
            if (targetLeft == _spreadLeftPage) return false;

            int dir = targetLeft > _spreadLeftPage ? 1 : -1;

            _spreadFlipDir = dir;
            _spreadFlipTargetLeft = targetLeft;
            _spreadFlipAngle = 0f;

            // Пока лист летит, подсказка не нужна: уголок уже поднят по-настоящему.
            _spreadCornerHint = 0f;

            // Какие страницы уходят вместе с листом. Вперёд переворачивается правая
            // страница разворота, и её изнанкой оказывается левая целевого; назад —
            // зеркально. При переходе к соседнему развороту это те же страницы, что и
            // раньше; при дальнем прыжке лист сразу несёт на изнанке нужную.
            if (dir > 0)
            {
                _spreadFlyFront = _spreadLeftPage + 1;
                _spreadFlyBack = targetLeft;
            }
            else
            {
                _spreadFlyFront = _spreadLeftPage;
                _spreadFlyBack = targetLeft + 1;
            }

            // Снимаются только стороны самого листа: он деформируется перспективой, и
            // рисовать его содержимое заново на каждом кадре незачем. Половины под ним
            // неподвижны и остаются за обычным векторным проходом — снимок подменил бы
            // им текст растровым, и страница, которая не двигалась вовсе, дёрнулась бы
            // на глазах.
            CacheSpreadPage(_spreadFlyFront);
            CacheSpreadPage(_spreadFlyBack);
            return true;
        }

        /// <summary>
        /// Страницы, лежащие под летящим листом: то, что открывается по ходу переворота.
        /// </summary>
        private (int Left, int Right) SpreadUnderPages()
        {
            if (SpreadSinglePage)
            {
                // Одиночный лист: под ним видна та страница, к которой он ложится.
                if (_spreadFlipDir > 0) return (_spreadLeftPage + 1, -1);
                if (_spreadFlipDir < 0) return (_spreadLeftPage - 1, -1);
                return (_spreadLeftPage, -1);
            }

            if (_spreadFlipDir > 0) return (_spreadLeftPage, _spreadFlipTargetLeft + 1);
            if (_spreadFlipDir < 0) return (_spreadFlipTargetLeft, _spreadLeftPage + 1);
            return (_spreadLeftPage, _spreadLeftPage + 1);
        }

        /// <summary>Доводит переворот до конца или возвращает лист на место.</summary>
        private void ReleaseSpreadFlip(bool commit)
        {
            if (_spreadFlipDir == 0) return;

            _spreadDragging = false;
            _spreadReleasing = true;
            _spreadReleaseFrom = _spreadFlipAngle;
            _spreadReleaseTo = commit ? 180f : 0f;

            // Время пропорционально остатку пути: короткий доворот не должен идти
            // столько же, сколько полный переворот.
            float remain = Math.Abs(_spreadReleaseTo - _spreadReleaseFrom);
            _spreadReleaseMs = Math.Max(140.0, SpreadFullFlipMs * (remain / 180f));
            _spreadReleaseStartTicks = DateTime.UtcNow.Ticks;

            StartSpreadTimer();
        }

        /// <summary>Завершение переворота: разворот меняется, снимки освобождаются.</summary>
        private void FinishSpreadFlip(bool committed)
        {
            if (committed && _spreadFlipTargetLeft >= 0)
            {
                _spreadLeftPage = SpreadLeftOf(Math.Clamp(
                    _spreadFlipTargetLeft, 0, Math.Max(0, _pages.Count - 1)));
            }

            _spreadFlipDir = 0;
            _spreadFlipTargetLeft = -1;
            _spreadFlipAngle = 0f;
            _spreadDragging = false;
            _spreadDragMoved = false;
            _spreadDragStartTravel = 0f;
            _spreadDragTargetAngle = 0f;
            _spreadReleasing = false;
            _spreadFlyFront = -1;
            _spreadFlyBack = -1;

            StopSpreadTimer();

            // Снимки не выбрасываются: при чтении подряд следующий переворот берёт
            // те же страницы, и снимать их заново — это вся отрисовка листа на каждое
            // нажатие. Остаются только соседние, остальные освобождаются.
            TrimSpreadCache();

            if (committed)
            {
                _spreadLabelPage = _spreadLeftPage;
                _spreadLabelCount = _pages.Count;
                SyncCaretToSpread();
                SpreadPageChanged?.Invoke();
            }

            InvalidateFull();
            SchedulePrefetchSpreadNeighbours();
        }

        /// <summary>
        /// Сбрасывает всё, что относится к идущему перевороту. Вызывается при смене
        /// режима и при пересборке раскладки: снимки страниц сделаны под прежний лист
        /// и после пересчёта показывали бы старую вёрстку.
        /// </summary>
        private void ResetSpreadState()
        {
            _spreadFlipDir = 0;
            _spreadFlipTargetLeft = -1;
            _spreadFlipAngle = 0f;
            _spreadDragging = false;
            _spreadDragMoved = false;
            _spreadDragStartTravel = 0f;
            _spreadDragTargetAngle = 0f;
            _spreadReleasing = false;
            _spreadFlyFront = -1;
            _spreadFlyBack = -1;
            _spreadNeedsCaretSync = true;
            _singleSlideDir = 0;
            _singleSlideFrom = -1;
            _singleSlideTo = -1;
            _singleSlideProgress = 0f;
            ResetReadingPan();
            StopSpreadTimer();
            ClearSpreadCache();
        }

        /// <summary>
        /// Переносит каретку на первый абзац открытой левой страницы. Каретка здесь —
        /// не место ввода, а закладка: её позицию сохраняет сессия, и выйдя из книги,
        /// пользователь окажется там же, где читал.
        /// </summary>
        private void SyncCaretToSpread()
        {
            for (int i = 0; i < _layouts.Count; i++)
            {
                if (_layouts[i].PageIndex != _spreadLeftPage) continue;
                _caretPara = i;
                _caretChar = 0;
                return;
            }
        }

        /// <summary>
        /// Листание. У разворота это переворот листа с анимацией, у одиночной
        /// страницы — просто следующая страница: анимировать нечему.
        /// </summary>
        public void SpreadTurn(int dir)
        {
            if (!SpreadMode || dir == 0) return;

            if (SpreadSinglePage)
            {
                BeginSingleSlide(dir);
                return;
            }

            if (!BeginSpreadFlip(dir)) return;
            ReleaseSpreadFlip(true);
        }

        /// <summary>Подводит книгу на шаг ближе или дальше.</summary>
        public void ChangeBookZoom(int direction)
        {
            if (DocVm is null || direction == 0) return;
            SetBookZoom(DocVm.Reading.Zoom * (direction > 0 ? 1.12 : 1.0 / 1.12));
        }

        /// <summary>
        /// Задаёт приближение книги. Раскладка при этом НЕ пересобирается: лист на
        /// экране становится больше или меньше, но текста на нём столько же, и
        /// страница остаётся той же страницей. Иначе каждое движение ползунка
        /// перекраивало бы книгу заново, и читатель терял бы место в тексте.
        ///
        /// Крупная книга перестаёт помещаться в окно — тогда появляются полосы
        /// прокрутки, а указатель у края ведёт её сам (см. UpdateReadingEdgePan).
        /// </summary>
        public void SetBookZoom(double zoom)
        {
            if (DocVm is null) return;

            double clamped = Math.Clamp(zoom,
                Models.Settings.ReadingSettings.MinZoom,
                Models.Settings.ReadingSettings.MaxZoom);
            if (Math.Abs(clamped - DocVm.Reading.Zoom) < 0.0005) return;

            DocVm.Reading.Zoom = clamped;
            DocVm.RaiseReadingVisualChanged();
        }

        /// <summary>
        /// Принимает изменённые настройки чтения: пересобирает раскладку под новый лист
        /// и открывает книгу там же, где читатель остановился.
        /// </summary>
        public void ApplyReadingSettings()
        {
            if (DocVm is null) return;

            ResetSpreadState();
            _layoutCache.Clear();
            InvalidateCellLayoutCaches();
            _spreadNeedsCaretSync = true;

            RebuildLayouts();
            InvalidateMeasure();
            InvalidateFull();
        }

        /// <summary>
        /// Начинает смену одиночной страницы. Возвращает false, если идти некуда или
        /// смена уже идёт.
        /// </summary>
        private bool BeginSingleSlide(int dir)
        {
            if (dir == 0) return false;
            return BeginSingleSlideTo(Math.Clamp(_spreadLeftPage, 0, Math.Max(0, _pages.Count - 1)) + dir);
        }

        /// <summary>
        /// Начинает смену одиночной страницы на заданную — не обязательно соседнюю.
        /// Возвращает false, если идти некуда или смена уже идёт.
        /// </summary>
        private bool BeginSingleSlideTo(int target)
        {
            if (!SpreadMode || !SpreadSinglePage) return false;
            if (_singleSlideDir != 0) return false;
            if (_pages.Count == 0) return false;

            int from = Math.Clamp(_spreadLeftPage, 0, _pages.Count - 1);
            int to = target;
            if (to < 0 || to >= _pages.Count || to == from) return false;

            int dir = to > from ? 1 : -1;

            _singleSlideDir = dir;
            _singleSlideFrom = from;
            _singleSlideTo = to;
            _singleSlideProgress = 0f;
            _singleSlideStartTicks = DateTime.UtcNow.Ticks;

            // Обе страницы снимаются в картинку: перерисовывать текст обеих по
            // шестьдесят раз в секунду нельзя, а снимок рисуется одним выводом.
            CacheSpreadPage(from);
            CacheSpreadPage(to);

            StartSpreadTimer();
            return true;
        }

        /// <summary>Такт скольжения: доводит страницу до места и заканчивает смену.</summary>
        private void TickSingleSlide()
        {
            double elapsed = (DateTime.UtcNow.Ticks - _singleSlideStartTicks) / TimeSpan.TicksPerMillisecond;
            float t = (float)Math.Clamp(elapsed / SingleSlideMs, 0.0, 1.0);

            // Разгон и торможение, а не только торможение: валик, стартующий с полной
            // скоростью, отрывается от края рывком — бумага так не гнётся. Кривая та
            // же, что у переворота разворота, поэтому обе подачи листаются одинаково
            // на ощупь.
            _singleSlideProgress = t < 0.5f
                ? 4f * t * t * t
                : 1f - MathF.Pow(-2f * t + 2f, 3f) / 2f;

            if (t >= 1f)
            {
                FinishSingleSlide();
                return;
            }

            InvalidateVisual();
        }

        private void FinishSingleSlide()
        {
            int to = _singleSlideTo;

            _singleSlideDir = 0;
            _singleSlideFrom = -1;
            _singleSlideTo = -1;
            _singleSlideProgress = 0f;
            StopSpreadTimer();

            if (to >= 0)
            {
                _spreadLeftPage = Math.Clamp(to, 0, Math.Max(0, _pages.Count - 1));
                _spreadLabelPage = _spreadLeftPage;
                _spreadLabelCount = _pages.Count;
                SyncCaretToSpread();
                SpreadPageChanged?.Invoke();
            }

            TrimSpreadCache();
            InvalidateFull();
        }

        /// <summary>
        /// Рисует смену одиночной страницы.
        ///
        /// У отдельного листа нет корешка, вокруг которого поворачиваться, поэтому он
        /// не переворачивается, а сворачивается: бумага отстаёт от правого края и
        /// уходит влево валиком, открывая из-под себя следующую страницу. Дойдя до
        /// левого края, валик уносит лист за пределы бумаги — ровно так уходит из-под
        /// руки страница, которую сняли со стопки.
        ///
        /// Назад — та же анимация, проигранная наоборот, и сворачивается уже
        /// приходящая страница: она раскатывается обратно на своё место.
        /// </summary>
        private void DrawSingleSlide(SKCanvas canvas, float canvasHeightPt, double canvasWidth)
        {
            if (_pages.Count == 0) return;

            // Фон закрашивается здесь же: скелет страниц, нарисованный до этого,
            // относится к обычной раскладке и к сворачиваемому листу отношения не имеет.
            float bgWPt = (float)(canvasWidth * PxToPt) + 2f;
            float bgHPt = Math.Max(canvasHeightPt,
                (float)(Bounds.Height / (PtToPx * Math.Max(Zoom, 0.01)))) + 2f;
            DrawCanvasBackdrop(canvas, bgWPt, bgHPt);

            int anchor = Math.Clamp(_spreadLeftPage, 0, _pages.Count - 1);
            var pg = _pages[anchor];
            var (x, y) = SpreadPlacement(anchor, true);

            float w = pg.WidthPt;
            float h = pg.HeightPt;
            float p = Math.Clamp(_singleSlideProgress, 0f, 1f);
            bool forward = _singleSlideDir > 0;

            // Вперёд сворачивается уходящая страница, назад — приходящая, и время у
            // неё идёт вспять.
            int leafPage = forward ? _singleSlideFrom : _singleSlideTo;
            int underPage = forward ? _singleSlideTo : _singleSlideFrom;
            float curl = forward ? p : 1f - p;

            DrawSlideSheet(canvas, underPage, x, y, w, h);

            if (leafPage < 0 || leafPage >= _pages.Count)
            {
                DrawReadingDim(canvas, bgWPt, bgHPt);
                return;
            }

            // Последняя доля пути — растворение. Свёрнутый лист к этому моменту уже
            // за краем страницы, но обрывать его одним кадром нельзя: глаз ловит
            // именно исчезновение, а не то, что исчезло.
            float leafAlpha = 1f - Math.Clamp((curl - 0.86f) / 0.14f, 0f, 1f);
            if (leafAlpha <= 0.004f)
            {
                DrawReadingDim(canvas, bgWPt, bgHPt);
                return;
            }

            // Радиус валика. Тонкий читается как разрез поперёк страницы, толстый
            // съедает бумагу целиком, не дойдя и до середины.
            float radius = Math.Max(w * 0.085f, 1f);
            float arc = MathF.PI * radius;

            // Линия сгиба идёт от правого края листа за левый. Пока она правее бумаги,
            // лист лежит плоско; когда уходит за левый край — свёрнут целиком.
            float fold = w - curl * (w + arc);

            const int Strips = 34;
            for (int i = 0; i <= Strips; i++)
            {
                float u = w * i / Strips;

                // До сгиба бумага лежит, на сгибе поворачивается на полоборота вокруг
                // валика, за ним — идёт обратно уже изнанкой вверх.
                _leafAngle[i] = Math.Clamp((u - fold) / radius, 0f, MathF.PI);
            }

            BuildLeafPoints(Strips, x, y, y + h, w, 1);

            float lift = Math.Clamp((w - fold) / Math.Max(w, 1f), 0f, 1f);

            // Тень начинается там, где бумага отрывается от страницы: плоская часть
            // листа лежит на ней вплотную и не отбрасывает ничего.
            int liftFrom = 0;
            while (liftFrom < Strips && _leafAngle[liftFrom + 1] < 0.02f) liftFrom++;

            DrawLeafShadow(canvas, liftFrom, Strips, lift * leafAlpha);

            // Снимок держится под замком всё время, пока им рисуют — см. _spreadCacheLock.
            lock (_spreadCacheLock)
            {
                CacheSpreadPageLocked(leafPage);
                _spreadPageCache.TryGetValue(leafPage, out var img);
                DrawLeafStrips(canvas, img, null, Strips, w, h, true, true, leafAlpha);
            }

            DrawReadingDim(canvas, bgWPt, bgHPt);
        }

        /// <summary>Один лист скольжения: тень, бумага и снимок содержимого.</summary>
        private void DrawSlideSheet(SKCanvas canvas, int pageIdx, float x, float y, float w, float h)
        {
            if (pageIdx < 0 || pageIdx >= _pages.Count) return;

            canvas.DrawRect(x + 3f, y + 3f, w, h, _paintPageShadow);

            // Снимок держится под замком всё время, пока им рисуют — см. _spreadCacheLock.
            lock (_spreadCacheLock)
            {
                CacheSpreadPageLocked(pageIdx);
                _spreadPageCache.TryGetValue(pageIdx, out var img);

                if (img is null)
                {
                    // Снимок не получился — лист всё равно должен ехать, иначе смена
                    // страницы выглядит как её исчезновение.
                    canvas.DrawRect(x, y, w, h, PagePaint());
                    return;
                }

                using var paint = new SKPaint { IsAntialias = true };
                canvas.DrawImage(
                    img,
                    new SKRect(0, 0, img.Width, img.Height),
                    new SKRect(x, y, x + w, y + h),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear),
                    paint);
            }
        }

        /// <summary>Ставит разворот на страницу с указанным индексом, без анимации.</summary>
        public void SpreadGoToPage(int pageIdx) => SpreadGoToPage(pageIdx, false);

        /// <summary>
        /// Открывает книгу на странице с указанным индексом.
        ///
        /// С анимацией переход виден: лист переворачивается или сворачивается ровно
        /// так же, как при обычном листании, и читатель понимает, что оказался не
        /// там же, где был. Без анимации — мгновенно: так ведёт себя ползунок, когда
        /// его тащат по всей книге, и анимировать каждое его положение значило бы
        /// превратить перемотку в кашу.
        /// </summary>
        public void SpreadGoToPage(int pageIdx, bool animate)
        {
            if (!SpreadMode) return;
            int last = Math.Max(0, _pages.Count - 1);
            int target = SpreadLeftOf(Math.Clamp(pageIdx, 0, last));

            // Просьба открыть то, что уже открыто, ничего не делает.
            //
            // Такая просьба приходит чаще, чем кажется: в развороте страницы идут
            // парами, и вторая страница пары — это тот же разворот, что и первая.
            // Раньше здесь всё равно шёл полный перерисов книги с пересбором кадра, и
            // на экране это выглядело как вспышка на месте перехода, которого не было.
            // Место в книге при этом сообщается наружу: лента могла показать номер,
            // которого книга не приняла, и вернуть её к правде больше некому.
            if (target == _spreadLeftPage && _singleSlideDir == 0 && _spreadFlipDir == 0)
            {
                _spreadLabelPage = _spreadLeftPage;
                _spreadLabelCount = _pages.Count;
                SpreadPageChanged?.Invoke();
                return;
            }

            if (animate && target != _spreadLeftPage && _singleSlideDir == 0 && _spreadFlipDir == 0)
            {
                if (SpreadSinglePage)
                {
                    if (BeginSingleSlideTo(target)) return;
                }
                else if (BeginSpreadFlipTo(target))
                {
                    ReleaseSpreadFlip(true);
                    return;
                }
            }

            _spreadLeftPage = target;
            _spreadLabelPage = _spreadLeftPage;
            _spreadLabelCount = _pages.Count;
            SyncCaretToSpread();
            SpreadPageChanged?.Invoke();
            InvalidateFull();
            TrimSpreadCache();
            SchedulePrefetchSpreadNeighbours();
        }

        // ── Таймер анимации ───────────────────────────────────────────────

        private void StartSpreadTimer()
        {
            if (_spreadTimer is null)
            {
                _spreadTimer = new DispatcherTimer(DispatcherPriority.Render)
                {
                    Interval = TimeSpan.FromMilliseconds(1000.0 / 60.0)
                };
                _spreadTimer.Tick += OnSpreadTick;
            }
            if (!_spreadTimer.IsEnabled) _spreadTimer.Start();
        }

        private void StopSpreadTimer()
        {
            if (_spreadTimer is { IsEnabled: true }) _spreadTimer.Stop();
        }

        private void OnSpreadTick(object? sender, EventArgs e)
        {
            // Одиночная страница меняется скольжением, а не переворотом: у неё свой
            // счёт времени, и он идёт первым.
            if (_singleSlideDir != 0)
            {
                TickSingleSlide();
                return;
            }

            if (_spreadFlipDir == 0)
            {
                StopSpreadTimer();
                return;
            }

            // Ведение рукой: лист догоняет цель долей оставшегося пути за такт. Шаг
            // пропорционален расстоянию, поэтому лист трогается мягко и мягко
            // останавливается, не отставая при быстром движении.
            if (_spreadDragging)
            {
                float diff = _spreadDragTargetAngle - _spreadFlipAngle;
                if (MathF.Abs(diff) < 0.05f) return;

                _spreadFlipAngle += diff * 0.4f;
                InvalidateVisual();
                return;
            }

            if (!_spreadReleasing)
            {
                StopSpreadTimer();
                return;
            }

            double elapsed = (DateTime.UtcNow.Ticks - _spreadReleaseStartTicks) / TimeSpan.TicksPerMillisecond;
            float t = (float)Math.Clamp(elapsed / Math.Max(_spreadReleaseMs, 1.0), 0.0, 1.0);

            // Бумага не тормозит рывком: замедление к концу пути, лёгкое ускорение в начале.
            float eased = t < 0.5f
                ? 4f * t * t * t
                : 1f - MathF.Pow(-2f * t + 2f, 3f) / 2f;

            _spreadFlipAngle = _spreadReleaseFrom + (_spreadReleaseTo - _spreadReleaseFrom) * eased;

            if (t >= 1f)
            {
                FinishSpreadFlip(_spreadReleaseTo > 90f);
                return;
            }

            // Кадр анимации рисуется отдельным путём и кэш содержимого не трогает,
            // поэтому помечать его грязным незачем — достаточно попросить перерисовку.
            InvalidateVisual();
        }

        // ── Снимки страниц ────────────────────────────────────────────────

        /// <summary>
        /// Рисует страницу в отдельное изображение. Во время съёмки визуальная дельта
        /// этой страницы обнуляется — внутри снимка координаты логические, и лист
        /// ложится в начало координат.
        /// </summary>
        private void CacheSpreadPage(int pageIdx)
        {
            if (pageIdx < 0 || pageIdx >= _pages.Count) return;

            lock (_spreadCacheLock) CacheSpreadPageLocked(pageIdx);
        }

        private void CacheSpreadPageLocked(int pageIdx)
        {
            if (_spreadPageCache.ContainsKey(pageIdx)) return;

            // Неудачная попытка запоминается: без этого страница, которую не удалось
            // снять (нет памяти, вырожденный размер), пересоздавалась бы на каждом
            // кадре анимации — а это отрисовка всего её содержимого в новую поверхность.
            if (!_spreadSnapshotFailed.Add(pageIdx)) return;

            var page = _pages[pageIdx];

            // Снимок берётся с запасом по разрешению: поднятый край листа идёт к
            // читателю и увеличивается, а снятая один в один страница на этом
            // растяжении заметно мылится.
            //
            // Запас ровно такой, какой даёт перспектива, и ни точкой больше. При
            // двукратном запасе лист А4 на обычном экране весит порядка десяти
            // мегабайт, и два таких снимка создавались на каждое нажатие — отсюда
            // и брался провал при перелистывании. Ближний край при выбранной
            // удалённости камеры (SpreadCameraDistFactor) вырастает примерно на треть.
            const double SpreadSnapshotOversample = 1.35;

            double pxPerPt = PtToPx * Math.Max(Zoom, 0.05) * SpreadSnapshotOversample;
            int wPx = (int)Math.Ceiling(page.WidthPt * pxPerPt);
            int hPx = (int)Math.Ceiling(page.HeightPt * pxPerPt);
            if (wPx <= 0 || hPx <= 0 || (long)wPx * hPx > 64_000_000L) return;

            SKSurface? surface = null;
            try
            {
                surface = SKSurface.Create(new SKImageInfo(wPx, hPx, SKColorType.Bgra8888, SKAlphaType.Premul));
                if (surface is null) return;

                var c = surface.Canvas;
                c.Clear(SKColors.Transparent);
                c.Scale((float)pxPerPt);
                c.DrawRect(0, 0, page.WidthPt, page.HeightPt, PagePaint());
                DrawReadingPaperImage(c, 0, 0, page.WidthPt, page.HeightPt);

                int savedOffscreen = _spreadOffscreenPagePlusOne;
                _spreadOffscreenPagePlusOne = pageIdx + 1;
                try
                {
                    c.Save();
                    c.Translate(-page.PadLeftPt, -page.Ypt);
                    RenderPageContent(c, _layouts, _pages, _tables, _images, pageIdx, pageIdx, false);
                    c.Restore();
                }
                finally
                {
                    _spreadOffscreenPagePlusOne = savedOffscreen;
                }

                // Тень сгиба запекается прямо в снимок. Отдельным проходом поверх
                // разворота она держалась только пока страницы стоят на месте: стоило
                // листу подняться, как половины начинали двигаться, а тень оставалась
                // висеть по центру экрана — и переворот терял объём в самый заметный
                // момент. В снимке она принадлежит странице и едет вместе с ней.
                // Сгиб есть только у разворота: у одиночной страницы тень вдоль
                // края читалась бы как грязь на бумаге.
                if (!SpreadSinglePage)
                    DrawSpineShadowOnPage(c, pageIdx, page.WidthPt, page.HeightPt);

                // Номер запекается в снимок вместе со страницей: он часть листа и
                // обязан лететь с ним, а не оставаться висеть на месте.
                DrawReadingPageNumber(c, pageIdx, 0, 0, page.WidthPt, page.HeightPt);

                _spreadPageCache[pageIdx] = surface.Snapshot();
                _spreadSnapshotFailed.Remove(pageIdx);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Снимок страницы разворота не удался: page={Page}", pageIdx);
            }
            finally
            {
                surface?.Dispose();
            }
        }

        /// <summary>
        /// Тень сгиба на одной странице: полоса вдоль того края, которым страница
        /// уходит в корешок. У левой страницы разворота это правый край, у правой —
        /// левый; чётность индекса и определяет сторону.
        /// Координаты локальные — от левого верхнего угла листа.
        /// </summary>
        private void DrawSpineShadowOnPage(SKCanvas canvas, int pageIdx, float widthPt, float heightPt)
        {
            bool spineOnRight = (pageIdx & 1) == 0;
            float band = Math.Max(widthPt * 0.075f, 9f);

            float from = spineOnRight ? widthPt : 0f;
            float to = spineOnRight ? widthPt - band : band;

            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(from, 0f),
                new SKPoint(to, 0f),
                new[]
                {
                    new SKColor(0x24, 0x1C, 0x14, 58),
                    new SKColor(0x28, 0x20, 0x18, 18),
                    new SKColor(0x2A, 0x20, 0x18, 0)
                },
                new[] { 0f, 0.35f, 1f },
                SKShaderTileMode.Clamp);

            using var paint = new SKPaint { Shader = shader };

            float left = spineOnRight ? widthPt - band : 0f;
            canvas.DrawRect(left, 0f, band, heightPt, paint);
        }

        /// <summary>
        /// Выбрасывает снимки страниц книги. Зовётся, когда картинка дочиталась с
        /// диска: снимок берётся один раз и живёт до пересборки раскладки, поэтому
        /// снятый до загрузки остался бы с пустым местом на месте картинки навсегда.
        /// </summary>
        internal void InvalidateSpreadSnapshots()
        {
            lock (_spreadCacheLock)
            {
                if (_spreadPageCache.Count == 0 && _spreadSnapshotFailed.Count == 0) return;
                ClearSpreadCache();
            }

            InvalidateVisual();
        }

        private void ClearSpreadCache()
        {
            lock (_spreadCacheLock)
            {
                foreach (var img in _spreadPageCache.Values) img.Dispose();
                _spreadPageCache.Clear();
                _spreadSnapshotFailed.Clear();
            }
        }

        /// <summary>
        /// Страницы, снимок которых имеет смысл держать: текущий разворот и по одному
        /// в каждую сторону от него. Дальше читатель за один переворот всё равно не
        /// уйдёт, а память под лист немаленькая.
        /// </summary>
        private bool IsSpreadPageWorthKeeping(int pageIdx)
        {
            int step = SpreadStep;
            return pageIdx >= _spreadLeftPage - step
                && pageIdx <= _spreadLeftPage + step * 2 - 1;
        }

        /// <summary>Освобождает снимки страниц, до которых отсюда уже не дотянуться.</summary>
        private void TrimSpreadCache()
        {
            lock (_spreadCacheLock) TrimSpreadCacheLocked();
        }

        private void TrimSpreadCacheLocked()
        {
            if (_spreadPageCache.Count == 0) return;

            List<int>? drop = null;
            foreach (var key in _spreadPageCache.Keys)
            {
                if (IsSpreadPageWorthKeeping(key)) continue;
                (drop ??= new List<int>()).Add(key);
            }

            if (drop is null) return;

            foreach (int key in drop)
            {
                if (!_spreadPageCache.Remove(key, out var img)) continue;
                img.Dispose();
                _spreadSnapshotFailed.Remove(key);
            }
        }

        /// <summary>
        /// Заранее снимает страницы, которые понадобятся следующему перевороту.
        /// Делается в простое диспетчера: в момент нажатия снимок уже готов, и
        /// переворот начинается сразу, а не после отрисовки двух листов.
        /// </summary>
        private void SchedulePrefetchSpreadNeighbours()
        {
            if (!SpreadMode) return;

            if (_spreadPrefetchQueued) return;

            _spreadPrefetchQueued = true;
            Dispatcher.UIThread.Post(() =>
            {
                _spreadPrefetchQueued = false;
                if (!SpreadMode || _spreadFlipDir != 0 || _singleSlideDir != 0) return;

                // Одиночной странице нужны соседи по обе стороны: скольжение берёт
                // и уходящую, и приходящую страницу снимками.
                if (SpreadSinglePage)
                {
                    CacheSpreadPage(_spreadLeftPage);
                    CacheSpreadPage(_spreadLeftPage + 1);
                    CacheSpreadPage(_spreadLeftPage - 1);
                    TrimSpreadCache();
                    InvalidateVisual();
                    return;
                }

                int step = SpreadStep;

                // Вперёд: уходит правая половина, под ней открывается левая следующего.
                int fwdFront = SpreadSinglePage ? _spreadLeftPage : _spreadLeftPage + 1;
                CacheSpreadPage(fwdFront);
                CacheSpreadPage(fwdFront + 1);

                // Назад: уходит левая половина, под ней открывается правая предыдущего.
                if (_spreadLeftPage - step >= 0)
                {
                    CacheSpreadPage(_spreadLeftPage);
                    CacheSpreadPage(_spreadLeftPage - 1);
                }

                TrimSpreadCache();

                // Кадр, который мог быть нарисован пока шла съёмка, обновляется:
                // страховка на случай, если он застал разворот в промежуточном виде.
                InvalidateVisual();
            }, DispatcherPriority.Background);
        }

        // Предварительный снимок уже поставлен в очередь — второй раз не ставим.
        private bool _spreadPrefetchQueued;

        // ── Геометрия перспективы ─────────────────────────────────────────

        /// <summary>
        /// Считает хребет листа по профилю изгиба, лежащему в <see cref="_leafAngle"/>.
        /// Профиль — это касательный угол бумаги в n+1 точке от корешка к свободному
        /// краю: ноль — лист лежит в плоскости страницы, π/2 — стоит вертикально,
        /// π — лёг обратной стороной вверх.
        ///
        /// Шаг между точками одинаков по ДЛИНЕ бумаги, а не по её тени на странице:
        /// лист гнётся, но не тянется, и на экране он обязан укорачиваться ровно
        /// настолько, насколько ушёл в глубину. Отсюда и накопление по отрезкам
        /// вместо прямой формулы — при переменном угле её попросту нет.
        ///
        /// Поднятая часть идёт К читателю: чем выше она над страницей, тем ближе к
        /// глазу и тем крупнее на экране. Этим и создаётся объём.
        /// </summary>
        private void BuildLeafPoints(
            int n, float spineX, float topY, float bottomY, float widthPt, int dir)
        {
            if (n < 1) return;
            n = Math.Min(n, LeafStripsMax);

            float ds = widthPt / n;
            float dist = Math.Max(widthPt * SpreadCameraDistFactor, 1f);
            float cy = (topY + bottomY) / 2f;

            // Положение точки в плоскости «вбок от корешка × вверх от страницы».
            float ax = 0f, az = 0f;

            for (int i = 0; i <= n; i++)
            {
                if (i > 0)
                {
                    // Угол берётся посередине отрезка: так ломаная идёт по дуге, а не
                    // срезает её углы.
                    float a = (_leafAngle[i - 1] + _leafAngle[i]) * 0.5f;
                    ax += MathF.Cos(a) * ds;
                    az += MathF.Sin(a) * ds;
                }

                float k = dist / MathF.Max(dist - az, dist * 0.2f);
                float px = spineX + dir * ax * k;

                _leafTop[i] = new SKPoint(px, cy + (topY - cy) * k);
                _leafBottom[i] = new SKPoint(px, cy + (bottomY - cy) * k);
            }
        }

        /// <summary>
        /// Тень, которую поднятая бумага роняет на страницу под собой. Это главное,
        /// что отличает переворот от отрывания: без тени лист не связан с тем, над чем
        /// висит, и читается как отдельный предмет. Чем выше он поднят, тем дальше
        /// уходит тень и тем она мягче.
        ///
        /// Тень строится не по всему листу, а начиная с <paramref name="from"/>: часть
        /// бумаги, лежащая на странице вплотную, не отбрасывает ничего, и размывать
        /// её силуэт каждый кадр — впустую потраченное время кадра.
        /// </summary>
        private void DrawLeafShadow(SKCanvas canvas, int from, int n, float lift)
        {
            if (lift < 0.05f) return;
            n = Math.Min(n, LeafStripsMax);
            from = Math.Clamp(from, 0, n - 1);

            using var path = new SKPath();
            path.MoveTo(_leafTop[from]);
            for (int i = from + 1; i <= n; i++) path.LineTo(_leafTop[i]);
            for (int i = n; i >= from; i--) path.LineTo(_leafBottom[i]);
            path.Close();

            byte alpha = (byte)Math.Clamp(lift * 70f, 0f, 70f);
            if (alpha < 2) return;

            using var blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.5f + lift * 3f);
            using var paint = new SKPaint
            {
                Color = new SKColor(0x0E, 0x0A, 0x06, alpha),
                IsAntialias = true,
                MaskFilter = blur
            };

            canvas.Save();
            canvas.Translate(2f + lift * 5f, 3f + lift * 7f);
            canvas.DrawPath(path, paint);
            canvas.Restore();
        }

        /// <summary>
        /// Матрица, кладущая прямоугольник (0,0)-(w,h) на четырёхугольник по углам
        /// в порядке: левый верх, правый верх, правый низ, левый низ.
        ///
        /// Skia умеет перспективу (Persp0/Persp1), но готового отображения
        /// четырёхугольника в четырёхугольник в SkiaSharp нет, поэтому матрица
        /// собирается вручную: сначала классическое отображение единичного квадрата
        /// на нужный четырёхугольник, затем сжатие исходника в этот квадрат.
        /// </summary>
        private static SKMatrix QuadMatrix(
            float w, float h,
            SKPoint p0, SKPoint p1, SKPoint p2, SKPoint p3)
        {
            float dx1 = p1.X - p2.X, dx2 = p3.X - p2.X, dx3 = p0.X - p1.X + p2.X - p3.X;
            float dy1 = p1.Y - p2.Y, dy2 = p3.Y - p2.Y, dy3 = p0.Y - p1.Y + p2.Y - p3.Y;

            float a, b, c, d, e, f, g, hh;

            if (Math.Abs(dx3) < 1e-6f && Math.Abs(dy3) < 1e-6f)
            {
                // Стороны параллельны: перспективы нет, отображение аффинное.
                a = p1.X - p0.X; b = p2.X - p1.X; c = p0.X;
                d = p1.Y - p0.Y; e = p2.Y - p1.Y; f = p0.Y;
                g = 0f; hh = 0f;
            }
            else
            {
                float den = dx1 * dy2 - dx2 * dy1;
                if (Math.Abs(den) < 1e-9f) return SKMatrix.CreateIdentity();

                g = (dx3 * dy2 - dx2 * dy3) / den;
                hh = (dx1 * dy3 - dx3 * dy1) / den;

                a = p1.X - p0.X + g * p1.X;
                b = p3.X - p0.X + hh * p3.X;
                c = p0.X;
                d = p1.Y - p0.Y + g * p1.Y;
                e = p3.Y - p0.Y + hh * p3.Y;
                f = p0.Y;
            }

            // Исходник сжимается в единичный квадрат прямо в коэффициентах: умножение
            // на масштабирующую матрицу дало бы то же самое, но результат зависел бы от
            // того, в каком порядке SKMatrix.Concat перемножает свои аргументы.
            float sw = Math.Max(w, 0.001f);
            float sh = Math.Max(h, 0.001f);

            return new SKMatrix(
                a / sw, b / sh, c,
                d / sw, e / sh, f,
                g / sw, hh / sh, 1f);
        }

        /// <summary>
        /// Произведение матриц: сначала применяется <paramref name="inner"/>, затем
        /// <paramref name="outer"/>. Пишется вручную, потому что порядок аргументов у
        /// готовых методов Skia между версиями трактуется по-разному, а ошибка здесь
        /// даёт не сбой, а тихо перекрученную картинку.
        /// </summary>
        private static SKMatrix MultiplyMatrix(SKMatrix outer, SKMatrix inner)
        {
            return new SKMatrix(
                outer.ScaleX * inner.ScaleX + outer.SkewX * inner.SkewY + outer.TransX * inner.Persp0,
                outer.ScaleX * inner.SkewX + outer.SkewX * inner.ScaleY + outer.TransX * inner.Persp1,
                outer.ScaleX * inner.TransX + outer.SkewX * inner.TransY + outer.TransX * inner.Persp2,

                outer.SkewY * inner.ScaleX + outer.ScaleY * inner.SkewY + outer.TransY * inner.Persp0,
                outer.SkewY * inner.SkewX + outer.ScaleY * inner.ScaleY + outer.TransY * inner.Persp1,
                outer.SkewY * inner.TransX + outer.ScaleY * inner.TransY + outer.TransY * inner.Persp2,

                outer.Persp0 * inner.ScaleX + outer.Persp1 * inner.SkewY + outer.Persp2 * inner.Persp0,
                outer.Persp0 * inner.SkewX + outer.Persp1 * inner.ScaleY + outer.Persp2 * inner.Persp1,
                outer.Persp0 * inner.TransX + outer.Persp1 * inner.TransY + outer.Persp2 * inner.Persp2);
        }

        /// <summary>
        /// Кладёт снимок страницы на посчитанный хребет — узкими плоскими полосами,
        /// каждая со своей перспективной матрицей.
        ///
        /// Треугольную сетку (DrawVertices) здесь применять нельзя, хотя шва у неё нет
        /// по построению. Skia натягивает текстуру на треугольник АФФИННО, без деления
        /// на глубину: внутри каждого четырёхугольника картинка ломается по диагонали,
        /// и ровные строки текста идут зигзагом. Полоса же кладётся проективной
        /// матрицей — перспектива внутри неё честная, и текст остаётся прямым.
        ///
        /// Со швами между полосами борются двумя вещами сразу:
        ///   отрезки с одинаковым углом сливаются в одну полосу — у свёрнутого листа
        ///   плоская часть занимает бо́льшую его долю, и швов там не остаётся вовсе;
        ///   сглаживание у полос отключено — именно оно давало светлые нити: соседние
        ///   куски делят ребро, но растеризуются порознь, и на общей грани покрытие
        ///   складывалось чуть меньше единицы. Без сглаживания пиксель достаётся
        ///   ровно одной полосе, а нахлёст закрывает остаток.
        ///
        /// Сглаживание остаётся только у крайних полос — от них силуэт листа, и
        /// лесенка по наклонному краю видна.
        /// </summary>
        /// <summary>
        /// Вуаль изнанки и затемнение одной полосой листа — как фильтр цвета для
        /// её картинки.
        ///
        /// Оба слоя кладутся поверх точек картинки в том же порядке, в каком они
        /// рисовались прямоугольниками: сначала бумага изнанки, потом свет. null —
        /// накладывать нечего, полоса рисуется как есть.
        /// </summary>
        private static SKColorFilter? LeafTint(SKColor paper, byte back, byte shade)
        {
            SKColorFilter? wash = back > 2
                ? SKColorFilter.CreateBlendMode(paper.WithAlpha(back), SKBlendMode.SrcOver)
                : null;

            SKColorFilter? light = shade > 2
                ? SKColorFilter.CreateBlendMode(
                    new SKColor(0x1B, 0x14, 0x0D, shade), SKBlendMode.SrcOver)
                : null;

            if (wash is null) return light;
            if (light is null) return wash;

            // Внешний фильтр применяется после внутреннего: свет ложится на уже
            // забелённую изнанку, а не наоборот.
            var composed = SKColorFilter.CreateCompose(light, wash);
            wash.Dispose();
            light.Dispose();
            return composed;
        }

        private void DrawLeafStrips(
            SKCanvas canvas, SKImage? frontImg, SKImage? backImg, int n,
            float widthPt, float heightPt,
            bool frontSpineLeft, bool backSpineLeft, float leafAlpha)
        {
            if (n < 1) return;
            n = Math.Min(n, LeafStripsMax);

            float ds = widthPt / n;
            float grow = ds * 0.12f + 0.6f;

            // Перспектива каждой полосы домножается на уже накопленную трансформацию
            // канваса, а не заменяет её: выше по стеку лежат масштаб и центрирование
            // разворота. Матрица берётся один раз — внутри цикла она подменяется.
            var baseMatrix = canvas.TotalMatrix;

            // Линейная фильтрация, а не кубическая: кубическая подчёркивает контуры
            // глифов, и при плавном изменении масштаба буквы будто пульсируют.
            var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
            var paper = ReadingPaperColor();

            byte leaf255 = (byte)Math.Clamp(leafAlpha * 255f, 0f, 255f);

            using var imgPaint = new SKPaint { Color = new SKColor(0, 0, 0, leaf255) };
            using var backPaint = new SKPaint { IsAntialias = false };
            using var shadePaint = new SKPaint { IsAntialias = false };

            int i = 0;
            while (i < n)
            {
                // Докуда тянется кусок с одним и тем же углом.
                int j = i + 1;
                while (j < n
                       && MathF.Abs(_leafAngle[j] - _leafAngle[i]) < 1e-4f
                       && MathF.Abs(_leafAngle[j + 1] - _leafAngle[i]) < 1e-4f) j++;

                // Какой стороной полоса повёрнута к читателю. За прямым углом лист
                // показывает изнанку — а это уже другая страница, со своим снимком и
                // своей стороной корешка. Выбор идёт по каждой полосе отдельно: на
                // середине пути лист виден с обеих сторон разом, и лицо с изнанкой
                // сходятся на той полосе, что встала ровно ребром.
                float ang = (_leafAngle[i] + _leafAngle[j]) * 0.5f;
                float facing = MathF.Cos(ang);
                bool showsBack = facing < 0f;

                // Снимка изнанки может и не быть — так у одиночного листа, который
                // сворачивается сам в себя. Тогда полосу закрывает бумага.
                bool hasBack = backImg is not null;
                var img = showsBack && hasBack ? backImg : frontImg;
                bool spineOnLeftOfImage = showsBack && hasBack ? backSpineLeft : frontSpineLeft;
                bool washPaper = showsBack && !hasBack;

                float sc = img is null ? 1f : img.Width / Math.Max(widthPt, 0.001f);

                // Углы полосы в порядке, которого ждёт QuadMatrix: левый верх, правый
                // верх, правый низ, левый низ — считая по исходной картинке. Если
                // корешок у неё справа, порядок зеркальный.
                SKPoint q0, q1, q2, q3;
                if (spineOnLeftOfImage)
                {
                    q0 = _leafTop[i]; q1 = _leafTop[j];
                    q2 = _leafBottom[j]; q3 = _leafBottom[i];
                }
                else
                {
                    q0 = _leafTop[j]; q1 = _leafTop[i];
                    q2 = _leafBottom[i]; q3 = _leafBottom[j];
                }

                float u0 = i * ds;
                float u1 = j * ds;
                float band = u1 - u0;

                // Полоса, вставшая ровно ребром, вырождается в отрезок: матрицы для
                // неё нет, а рисовать там всё равно нечего.
                if (MathF.Abs(q1.X - q0.X) < 0.03f) { i = j; continue; }

                var m = QuadMatrix(band, heightPt, q0, q1, q2, q3);

                float sxA = spineOnLeftOfImage ? u0 * sc : (widthPt - u1) * sc;
                float sxB = spineOnLeftOfImage ? u1 * sc : (widthPt - u0) * sc;

                // Сглаживание нужно всем полосам, а не только крайним.
                //
                // Верхняя и нижняя кромки листа складываются из отрезков между
                // соседними полосами, и без сглаживания вся кромка идёт лесенкой:
                // изгиб гнёт лист по всей высоте, а каждая полоса обрезается по
                // целым точкам экрана. Лесенка тем заметнее, чем круче изгиб.
                //
                // Раньше сглаживание держали только на крайних полосах, потому что
                // на внутренних оно давало швы. Швы шли не от него: картинка
                // рисовалась сглаженной, а тень ложилась поверх неё отдельным
                // жёстким прямоугольником, и на их стыке оставался незатенённый
                // столбец. Теперь тень едет фильтром внутри того же вызова, край у
                // полосы один, и держать её жёсткой больше незачем.
                //
                // Фон сквозь стык не просвечивает: полосы перекрываются на grow, и
                // сглаженный край новой полосы ложится не на фон, а на непрозрачную
                // соседку, нарисованную до неё.
                bool edge = i == 0 || j == n;
                imgPaint.IsAntialias = true;

                // Перекрытие прячет шов с соседней полосой. Крайней полосе оно ни к
                // чему: соседа с этой стороны у неё нет, а натянутая на пустое место
                // текстура делает край листа шире, чем он есть, и слегка растягивает
                // на нём буквы.
                //
                // С какой стороны идёт перекрытие, решает не номер полосы, а то, где
                // у снимка корешок: при зеркальном порядке углов ширина
                // прямоугольника растёт в сторону меньших номеров, а не больших.
                bool overHasNeighbour = spineOnLeftOfImage ? j < n : i > 0;
                float over = overHasNeighbour ? grow : 0f;

                var dst = new SKRect(0f, 0f, band + over, heightPt);

                // Изнанка без своего снимка: бумага непрозрачна, лицевая сторона
                // сквозь неё едва проступает — как настоящий лист на просвет.
                byte back = washPaper
                    ? (byte)Math.Clamp(-facing * 236f * leafAlpha, 0f, 236f)
                    : (byte)0;

                // Свет: чем ближе бумага к ребру, тем меньше его на неё попадает.
                byte shade = (byte)Math.Clamp((1f - MathF.Abs(facing)) * 104f * leafAlpha, 0f, 104f);

                canvas.Save();
                canvas.SetMatrix(MultiplyMatrix(baseMatrix, m));

                if (img is not null)
                {
                    float imgW = img.Width;
                    var src = new SKRect(
                        Math.Clamp(sxA, 0f, imgW),
                        0f,
                        Math.Clamp(sxB + over * sc, 0f, imgW),
                        img.Height);

                    if (src.Width > 0.5f)
                    {
                        // Вуаль и затемнение накладываются на саму картинку, а не
                        // рисуются поверх неё отдельными прямоугольниками.
                        //
                        // Отдельными они и давали разрыв. У крайних полос картинка
                        // рисуется со сглаживанием — иначе силуэт листа выходит
                        // ступенькой, — а прямоугольник тени клался жёстким краем.
                        // Сглаженный край занимает пиксель наполовину, жёсткий берёт
                        // его целиком или не берёт вовсе, и на их стыке оставался
                        // столбец, куда бумага легла, а тень нет. Соседние столбцы
                        // затемнены, этот нет — на светлой бумаге он читается как
                        // разрыв листа, и виден тем лучше, чем ближе к краю.
                        //
                        // Фильтр цвета правит уже отобранные точки, и покрытие
                        // накладывается на результат один раз. Разойтись нечему.
                        var tint = LeafTint(paper, back, shade);
                        imgPaint.ColorFilter = tint;

                        canvas.DrawImage(img, src, dst, sampling, imgPaint);

                        imgPaint.ColorFilter = null;
                        tint?.Dispose();
                    }
                }
                else
                {
                    // Снимок не получился — бумага всё равно должна лететь, иначе
                    // переворот выглядит как исчезновение страницы.
                    //
                    // Здесь вуаль и тень остаются отдельными прямоугольниками:
                    // накладывать их не на что, а рисуются они по тому же
                    // прямоугольнику и с тем же сглаживанием, что и заливка.
                    canvas.DrawRect(dst, PagePaint());

                    if (back > 2)
                    {
                        backPaint.IsAntialias = edge;
                        backPaint.Color = paper.WithAlpha(back);
                        canvas.DrawRect(dst, backPaint);
                    }

                    if (shade > 2)
                    {
                        shadePaint.IsAntialias = edge;
                        shadePaint.Color = new SKColor(0x1B, 0x14, 0x0D, shade);
                        canvas.DrawRect(dst, shadePaint);
                    }
                }

                canvas.Restore();
                i = j;
            }
        }

        // ── Отрисовка ─────────────────────────────────────────────────────

        /// <summary>
        /// Рисует летящий лист поверх уже отрисованного разворота. Вызывается в конце
        /// прохода страниц: под листом к этому моменту лежит то, что из-под него
        /// открывается.
        /// </summary>
        private void DrawSpreadFlip(SKCanvas canvas)
        {
            if (!SpreadMode || _spreadFlipDir == 0) return;
            if (_pages.Count == 0) return;

            // Геометрия разворота берётся с той же страницы, что и в раскладке:
            // корешок стоит там, где сходятся две половины.
            int anchorIdx = Math.Clamp(_spreadLeftPage, 0, _pages.Count - 1);
            var anchor = _pages[anchorIdx];
            var (adx, ady) = SpreadPlacement(anchorIdx, true);

            float leftX = adx;
            float topY = ady;
            float bottomY = topY + anchor.HeightPt;
            float spineX = leftX + anchor.WidthPt;
            float widthPt = anchor.WidthPt;

            float angle = Math.Clamp(_spreadFlipAngle, 0f, 180f);

            // Лист поворачивается вокруг корешка в одну сторону всю дорогу: вперёд —
            // справа налево, назад — слева направо. Никакого перескока на середине.
            //
            // Раньше на прямом угле всё менялось разом: сторона отсчёта, знак изгиба и
            // снимок. Изогнутый лист у вертикали проецируется на треть ширины страницы —
            // и в один кадр он перепрыгивал эту треть с одной стороны корешка на другую,
            // заодно подменяя текст. Со стороны это и выглядело как «текст сменился,
            // хотя я ещё не перевернул».
            int faceDir = _spreadFlipDir > 0 ? 1 : -1;

            // Изгиб. Корешок держит бумагу, ведёт её свободный край — основание всегда
            // отстаёт от края. Расхождение наибольшее на середине пути и сходит на нет
            // к обоим концам: и в начале, и в конце страница лежит плоско.
            float lift = MathF.Sin(angle * MathF.PI / 180f);
            float bend = SpreadMaxBendDeg * MathF.PI / 180f * lift;
            float angleRad = angle * MathF.PI / 180f;

            const int Strips = 24;
            for (int i = 0; i <= Strips; i++)
            {
                float t = (float)i / Strips;
                _leafAngle[i] = Math.Clamp(angleRad - bend * (1f - t), 0f, MathF.PI);
            }

            BuildLeafPoints(Strips, spineX, topY, bottomY, widthPt, faceDir);

            // Лицевая сторона правой страницы примыкает к корешку левым краем,
            // изнанка — правым: отсюда и сторона, с которой у каждого снимка корешок.
            bool frontSpineLeft = _spreadFlipDir > 0;
            bool backSpineLeft = _spreadFlipDir < 0;

            DrawLeafShadow(canvas, 0, Strips, lift);

            // Снимки держатся под замком всё время, пока ими рисуют: освободить их
            // может поток правки, и между взятием ссылки и отрисовкой её хватило бы,
            // чтобы образ перестал существовать.
            lock (_spreadCacheLock)
            {
                // Обе стороны нужны сразу: на середине пути лист виден и лицом, и
                // изнанкой — где-то на нём проходит полоса, вставшая ровно ребром.
                if (_spreadFlyFront >= 0) CacheSpreadPageLocked(_spreadFlyFront);
                if (_spreadFlyBack >= 0) CacheSpreadPageLocked(_spreadFlyBack);

                SKImage? frontImg = null;
                SKImage? backImg = null;
                if (_spreadFlyFront >= 0) _spreadPageCache.TryGetValue(_spreadFlyFront, out frontImg);
                if (_spreadFlyBack >= 0) _spreadPageCache.TryGetValue(_spreadFlyBack, out backImg);

                DrawLeafStrips(
                    canvas, frontImg, backImg, Strips,
                    widthPt, anchor.HeightPt,
                    frontSpineLeft, backSpineLeft, 1f);
            }
        }

        /// <summary>
        /// Щель переплёта: тёмная полоса ровно по корешку, поверх которой лежат
        /// страницы. Нужна на середине переворота — там лист виден с ребра и почти
        /// исчезает, а под ним открывалась белая пустота, будто книга разъехалась.
        /// Полоса не глухо чёрная, а слегка прозрачная: это темнота в глубине сгиба,
        /// сквозь которую угадывается бумага.
        /// </summary>
        private void DrawSpreadGutter(SKCanvas canvas, float liftFactor)
        {
            if (!SpreadMode || _pages.Count == 0) return;

            int idx = Math.Clamp(_spreadLeftPage, 0, _pages.Count - 1);
            var pg = _pages[idx];
            var (x, y) = SpreadPlacement(idx, true);
            float spineX = x + pg.WidthPt;

            float lift = Math.Clamp(liftFactor, 0f, 1f);

            // Щель чуть расходится при подъёме листа, но остаётся узкой: широкая полоса
            // по центру читается не как сгиб, а как разрыв между двумя листами.
            float slot = Math.Max(pg.WidthPt * 0.0035f, 1f) * (1f + lift * 0.8f);
            byte slotAlpha = (byte)Math.Clamp(92f + lift * 50f, 0f, 200f);

            using (var slotPaint = new SKPaint { Color = new SKColor(0x0F, 0x0B, 0x08, slotAlpha) })
                canvas.DrawRect(spineX - slot / 2f, y, slot, pg.HeightPt, slotPaint);

            if (lift < 0.02f) return;

            // Отсвет темноты на соседних страницах — он и заменяет тень поднятого
            // листа, оставаясь одной заливкой вместо копий его формы.
            float band = Math.Max(pg.WidthPt * 0.032f, 6f) * (0.6f + lift);
            byte bandAlpha = (byte)Math.Clamp(lift * 44f, 0f, 44f);

            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(spineX - band, 0f),
                new SKPoint(spineX + band, 0f),
                new[]
                {
                    new SKColor(0x14, 0x0E, 0x09, 0),
                    new SKColor(0x14, 0x0E, 0x09, bandAlpha),
                    new SKColor(0x14, 0x0E, 0x09, 0)
                },
                new[] { 0f, 0.5f, 1f },
                SKShaderTileMode.Clamp);

            using var paint = new SKPaint { Shader = shader };
            canvas.DrawRect(spineX - band, y, band * 2f, pg.HeightPt, paint);
        }

        /// <summary>
        /// Экранная позиция левого верхнего угла страницы разворота. Отдельный метод
        /// нужен затем, что во время переворота страницы под листом стоят не парами,
        /// и общий расчёт дельты обязан знать это же размещение.
        /// </summary>
        private (float X, float Y) SpreadPlacement(int pageIdx, bool leftSlot)
        {
            if (_pages.Count == 0) return (0f, 0f);
            var pg = _pages[Math.Clamp(pageIdx, 0, _pages.Count - 1)];

            var (viewWPt, viewHPt) = SpreadViewAreaPt();

            // Книга центруется по видимой области, а не прижимается к её краю. При
            // приближении она эту область перерастает, и половины уходят за обрез
            // симметрично — а какую именно часть показать, решает панорамирование.
            float totalW = SpreadSinglePage ? pg.WidthPt : pg.WidthPt * 2f;
            float marginX = (viewWPt - totalW) / 2f;

            float x = marginX + (SpreadSinglePage || leftSlot ? 0f : pg.WidthPt) + _readingPanXPt;
            float y = (viewHPt - pg.HeightPt) / 2f + _readingPanYPt;
            return (x, y);
        }

        /// <summary>
        /// Рисует корешок: тонкая тень по обе стороны от места, где сходятся страницы.
        /// Без неё две половины читаются как два отдельных листа, а не как книга.
        /// </summary>
        private void DrawSpreadSpine(SKCanvas canvas)
        {
            if (!SpreadMode || _pages.Count == 0) return;

            // Тень рисуется теми же двумя полосами, что запекаются в снимок летящего
            // листа. Одна широкая полоса по центру выглядела бы иначе, и в момент
            // отрыва листа сгиб заметно мигал бы, меняя форму.
            //
            // Страницы берутся текущие: пока лист в полёте, под ним лежит уже другая
            // пара, и тень обязана быть на ней, а не на прежнем развороте.
            var (left, right) = SpreadUnderPages();
            DrawSpineShadowForVisiblePage(canvas, left, true);
            DrawSpineShadowForVisiblePage(canvas, right, false);
        }

        /// <summary>Тень сгиба для страницы, стоящей на своём месте в развороте.</summary>
        private void DrawSpineShadowForVisiblePage(SKCanvas canvas, int pageIdx, bool leftSlot)
        {
            if (pageIdx < 0 || pageIdx >= _pages.Count) return;

            var pg = _pages[pageIdx];
            var (x, y) = SpreadPlacement(pageIdx, leftSlot);

            canvas.Save();
            canvas.Translate(x, y);
            DrawSpineShadowOnPage(canvas, pageIdx, pg.WidthPt, pg.HeightPt);
            canvas.Restore();

        }

        // ── Управление рукой ──────────────────────────────────────────────

        /// <summary>
        /// Начало жеста в развороте. Возвращает true, если лист подхвачен и обычная
        /// обработка указателя не нужна.
        /// </summary>
        private bool SpreadPointerPressed(float xPt, float yPt)
        {
            if (!SpreadMode || _spreadFlipDir != 0) return false;
            if (_pages.Count == 0) return false;

            int idx = Math.Clamp(_spreadLeftPage, 0, _pages.Count - 1);
            var pg = _pages[idx];
            var (x, y) = SpreadPlacement(idx, true);

            float sheetsW = pg.WidthPt * (SpreadSinglePage ? 1f : 2f);

            if (SpreadSinglePage)
            {
                // Половина в одну сторону, половина в другую — и так же за пределами
                // листа. Делить страницу на трети и оставлять мёртвую середину значит
                // заставлять целиться: читатель щёлкает не по кнопке, а «куда-то
                // левее» или «куда-то правее», и попасть должно всегда.
                float centerX = x + pg.WidthPt / 2f;
                SpreadTurn(xPt < centerX ? -1 : 1);

                // Дальше нажатие не идёт: каретке и выделению в чтении делать нечего.
                return true;
            }

            // Разворот: за его пределами щелчок листает в ту сторону, где щёлкнули.
            // Внутри книга берётся рукой — там лист подхватывается за край.
            if (xPt < x) { SpreadTurn(-1); return true; }
            if (xPt > x + sheetsW) { SpreadTurn(1); return true; }

            if (yPt < y || yPt > y + pg.HeightPt) return false;

            float spineX = x + pg.WidthPt;
            int dir = xPt >= spineX ? 1 : -1;

            if (!BeginSpreadFlip(dir)) return false;

            _spreadDragging = true;
            _spreadReleasing = false;
            _spreadDragMoved = false;
            _spreadDragStartTravel = SpreadTravelFromPointer(xPt);
            _spreadFlipAngle = 0f;
            _spreadDragTargetAngle = 0f;

            InvalidateVisual();
            return true;
        }

        /// <summary>Ведение листа рукой.</summary>
        private bool SpreadPointerMoved(float xPt)
        {
            if (!_spreadDragging || _spreadFlipDir == 0) return false;

            float angle = SpreadAngleFromPointer(xPt);
            if (!_spreadDragMoved && angle > 1.5f) _spreadDragMoved = true;

            // Рука задаёт только цель. Сам лист подтягивается к ней в такте таймера,
            // ровными шагами — иначе каждый скачок указателя виден как рывок бумаги.
            _spreadDragTargetAngle = angle;
            StartSpreadTimer();
            return true;
        }

        /// <summary>
        /// Отпускание листа. Если рука так и не сдвинулась, это было обычное нажатие —
        /// страница переворачивается целиком, как от щелчка по краю бумажной книги.
        /// </summary>
        private bool SpreadPointerReleased()
        {
            if (!_spreadDragging || _spreadFlipDir == 0) return false;

            bool commit = !_spreadDragMoved || _spreadFlipAngle >= SpreadCommitAngle;
            ReleaseSpreadFlip(commit);
            return true;
        }

        /// <summary>
        /// Клавиши книги: листание вперёд и назад, к началу и к концу. Возвращает true,
        /// если нажатие разобрано здесь.
        /// </summary>
        private bool HandleSpreadKey(Avalonia.Input.KeyEventArgs e)
        {
            bool ctrl = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control);

            // Ctrl с плюсом и минусом подводит и отводит книгу. Размер шрифта в чтении
            // не меняется: рукопись остаётся такой, какой её напечатают.
            if (ctrl)
            {
                switch (e.Key)
                {
                    case Avalonia.Input.Key.OemPlus:
                    case Avalonia.Input.Key.Add:
                        ChangeBookZoom(1);
                        return true;

                    case Avalonia.Input.Key.OemMinus:
                    case Avalonia.Input.Key.Subtract:
                        ChangeBookZoom(-1);
                        return true;

                    case Avalonia.Input.Key.D0:
                    case Avalonia.Input.Key.NumPad0:
                        SetBookZoom(1.0);
                        return true;
                }
            }

            switch (e.Key)
            {
                case Avalonia.Input.Key.Right:
                case Avalonia.Input.Key.Down:
                case Avalonia.Input.Key.PageDown:
                case Avalonia.Input.Key.Space:
                    SpreadTurn(1);
                    return true;

                case Avalonia.Input.Key.Left:
                case Avalonia.Input.Key.Up:
                case Avalonia.Input.Key.PageUp:
                    SpreadTurn(-1);
                    return true;

                case Avalonia.Input.Key.Home:
                    SpreadGoToPage(0);
                    return true;

                case Avalonia.Input.Key.End:
                    SpreadGoToPage(Math.Max(0, _pages.Count - 1));
                    return true;

                case Avalonia.Input.Key.Escape:
                    // Выход из книги и из полного экрана разбирает вью: она одна знает,
                    // раскрыт ли модуль поверх окна и что сейчас нужно закрыть.
                    ReadingEscapePressed?.Invoke();
                    return true;

                case Avalonia.Input.Key.F11:
                    ReadingFullscreenTogglePressed?.Invoke();
                    return true;

                default:
                    // Остальные нажатия в книге не делают ничего, но и наружу не уходят:
                    // ввод текста здесь запрещён, а горячие клавиши правки бессмысленны.
                    return true;
            }
        }

        /// <summary>
        /// Угол листа по положению указателя: доля пути от своего края до чужого.
        /// Считается по горизонтали, потому что лист поворачивается вокруг корешка,
        /// и вертикаль на угол не влияет.
        /// </summary>
        private float SpreadAngleFromPointer(float xPt)
        {
            float travel = SpreadTravelFromPointer(xPt) - _spreadDragStartTravel;
            return Math.Clamp(travel * 90f, 0f, 180f);
        }

        /// <summary>
        /// Доля пути листа от своего края к чужому: 0 — лежит на месте, 1 — стоит
        /// вертикально над корешком, 2 — лёг на другую сторону.
        /// </summary>
        private float SpreadTravelFromPointer(float xPt)
        {
            if (_pages.Count == 0) return 0f;

            int idx = Math.Clamp(_spreadLeftPage, 0, _pages.Count - 1);
            var pg = _pages[idx];
            var (x, _) = SpreadPlacement(idx, true);
            float spineX = x + pg.WidthPt;
            float w = Math.Max(pg.WidthPt, 1f);

            // Вперёд палец идёт от правого края к левому, назад — наоборот.
            return _spreadFlipDir > 0
                ? (spineX + w - xPt) / w
                : (xPt - (spineX - w)) / w;
        }
    }
}
