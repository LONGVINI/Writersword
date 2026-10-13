using System;
using System.Collections.Generic;
using System.Threading;
using SkiaSharp;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Кадр набора по одному листу.
    ///
    /// Буква меняет абзац на одном листе, а кадр перерисовывал все видимые листы — при
    /// трёх листах в ряду это девять листов и полсотни миллисекунд на каждую букву.
    /// Битмап синхронного кадра хранит прошлый кадр той же полосы; если с него
    /// поменялся ровно один лист, он и перерисовывается — под клипом по своему
    /// прямоугольнику, остальные листы остаются из прошлого кадра.
    ///
    /// Какой лист поменялся, сообщают быстрые пути набора: перекладка абзаца без
    /// изменения его высоты (QuickUpdateParagraphLayout) и перекладка таблицы на месте
    /// (TryQuickTableUpdate). Всё остальное, что меняет содержимое, — полная раскладка,
    /// вставка абзаца, любой другой запрос полного кадра — требует перерисовать полосу
    /// целиком. Сомнение решается в пользу полного кадра: состояние «ничего не
    /// отмечено» тоже значит полный кадр.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        // Что поменялось с прошлого синхронного кадра: лист (≥ 0), нужен полный кадр
        // (PartialFull) или ничего не отмечено (PartialNone). Пишет поток интерфейса,
        // забирает поток отрисовки — поэтому одним числом и атомарно.
        private const long PartialFull = -1;
        private const long PartialNone = -2;
        private long _partialState = PartialFull;

        // Что лежит в битмапе синхронного кадра: сам битмап, его полоса, масштаб и
        // листы снимка. Пишется и читается только на потоке отрисовки.
        private SKBitmap? _partialTarget;
        private float _partialTargetTop;
        private int _partialTargetWidth;
        private int _partialTargetHeight;
        private double _partialTargetZoom = double.NaN;
        private int _partialTargetFirstPage = -1;
        private int _partialTargetLastPage = -1;

        // InvalidateFull сам зовёт InvalidateVisual: такой запрос — не отдельная правка.
        private bool _inInvalidateFull;

        // Кто зовёт InvalidateFull в ходе набора, не меняя ничего сверх отмеченного листа.
        private static readonly HashSet<string> PartialSafeInvalidators = new(StringComparer.Ordinal)
        {
            nameof(QuickUpdateParagraphLayout),
            nameof(TryQuickTableUpdate),
            nameof(ScheduleRebuild),
            nameof(ScheduleDeferredFullRebuild),
            nameof(RefreshTrackedParagraphs),
            nameof(RebuildAfterCellEdit),
            "PlaceCaret"
        };

        /// <summary>Набор поменял содержимое одного листа.</summary>
        private void NotePartialPage(int pageIndex)
        {
            if (pageIndex < 0)
            {
                BlockPartial();
                return;
            }

            while (true)
            {
                long current = Interlocked.Read(ref _partialState);
                long next = current == PartialNone || current == pageIndex ? pageIndex : PartialFull;
                if (Interlocked.CompareExchange(ref _partialState, next, current) == current) return;
            }
        }

        /// <summary>Следующий кадр перерисовывает полосу целиком.</summary>
        private void BlockPartial() => Interlocked.Exchange(ref _partialState, PartialFull);

        /// <summary>Запрос полного кадра от InvalidateFull: правка вне набора — полный кадр.</summary>
        private void NoteFullInvalidation(string caller)
        {
            if (!PartialSafeInvalidators.Contains(caller)) BlockPartial();
        }

        /// <summary>
        /// Забирает отмеченное к этому кадру и решает, можно ли перерисовать один лист.
        /// -1 — кадр полный. Отмеченное сбрасывается в любом случае: кадр, полный или
        /// нет, отражает всё, что было отмечено до него.
        /// </summary>
        private int TakePartialPage(
            bool allowed, SKBitmap? target, float bandTopPx, int widthPx, int heightPx, double zoom, int pageCount)
        {
            long state = Interlocked.Exchange(ref _partialState, PartialNone);
            if (!allowed || state < 0 || state >= pageCount) return -1;

            bool sameTarget = target is not null
                && ReferenceEquals(target, _partialTarget)
                && Math.Abs(_partialTargetTop - bandTopPx) < 0.01f
                && _partialTargetWidth == widthPx
                && _partialTargetHeight == heightPx
                && Math.Abs(_partialTargetZoom - zoom) < 1e-9;

            if (!sameTarget) return -1;

            int page = (int)state;
            if (page < _partialTargetFirstPage || page > _partialTargetLastPage) return -1;

            PerfCount("r.partial.frame");
            return page;
        }

        /// <summary>Битмап хранит полный кадр этой полосы.</summary>
        private void RememberPartialTarget(
            SKBitmap target, float bandTopPx, int widthPx, int heightPx, double zoom, int firstPage, int lastPage)
        {
            _partialTarget = target;
            _partialTargetTop = bandTopPx;
            _partialTargetWidth = widthPx;
            _partialTargetHeight = heightPx;
            _partialTargetZoom = zoom;
            _partialTargetFirstPage = firstPage;
            _partialTargetLastPage = lastPage;
        }

        /// <summary>
        /// Прямоугольник листа на холсте, пт, с запасом на тень и полосы карточек
        /// правок по бокам — всё, что проход содержимого рисует от этого листа.
        /// </summary>
        private SKRect PartialPageRectPt(int pageIndex, List<PageRect> pages, double canvasWidth)
        {
            const float Pad = 8f;

            var page = pages[pageIndex];
            var (dx, dy) = PageVisualDelta(pageIndex, pages);

            float x = page.PadLeftPt + dx;
            float y = page.Ypt + dy;

            // Один лист в ряду проход содержимого досдвигает к живому центру холста.
            if (_pagesPerRow <= 1)
            {
                float canvasWPt = (float)(canvasWidth * PxToPt);
                float curPageXPt = Math.Max((canvasWPt - GetPageWidthPt()) / 2f, 0f);
                x += curPageXPt - _layoutPageXPt;
            }

            float lane = RevisionLaneWidthPtNow;
            return new SKRect(
                x - lane - Pad,
                y - Pad,
                x + page.WidthPt + lane + Pad,
                y + page.HeightPt + Pad);
        }
    }
}
