using System;
using System.Collections.Generic;
using Avalonia.Threading;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Живой сдвиг поля страницы — как в Word: пока край тянут на линейке, текст на
    /// видимых листах перекладывается под новое поле на каждом движении мыши.
    ///
    /// Полный проход пагинации на каждое движение здесь не годится: на большом
    /// документе он шейпит тысячи абзацев и стоит секунды. Поэтому во время жеста
    /// перекладываются только видимые листы. Проход начинается с первого видимого
    /// листа — точнее, с листа, на котором начинается его первый блок, — и
    /// кончается сразу за нижним краем окна. Листы выше остаются как были: их не
    /// видно, а после отпускания полный проход поправит и их.
    ///
    /// После отпускания видимые листы уже верны, а остальной документ догоняет
    /// порционным прогревом кеша (PumpLayoutWarmup): интерфейс в это время отвечает,
    /// окно можно листать — открывшиеся листы верстаются тем же частичным проходом.
    /// Когда прогрев закончен, один полный проход ставит всё на места.
    ///
    /// Каретка и выделение хранятся номерами слайсов раскладки, а частичный проход
    /// эти номера сдвигает. Поэтому до прохода они запоминаются абзацем и позицией
    /// символа, а после — ставятся обратно по абзацу.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        // ── Частичный проход ───────────────────────────────────────────────
        // Первый блок, лист, с которого он верстается, и последний нужный лист.
        // _partialFromBlock < 0 — обычный полный проход.
        private int _partialFromBlock = -1;
        private int _partialFromPage;
        private int _partialToPage;

        // Сколько листов назад можно искать начало блока, первого на видимом листе.
        // Блок длиннее (таблица на десятки страниц) живым проходом не верстается:
        // проход от его начала был бы уже не частичным.
        private const int PartialStartSearchPages = 12;

        // Сколько листов верстать за нижним краем окна: хватает, чтобы короткая
        // прокрутка не открыла пустой лист.
        private const int PartialPagesAfterView = 1;

        // ── Состояние жеста ────────────────────────────────────────────────
        // Край поля тянут прямо сейчас.
        private bool _marginLiveActive;

        // Жест закончился, и следующее уведомление о параметрах страницы — его итог.
        private bool _marginGestureEnding;

        // Итог жеста применён, но полный проход ещё впереди (идёт прогрев кеша).
        private bool _marginSettlePending;

        // Частичный проход уже поставлен в очередь диспетчера.
        private bool _marginLiveScheduled;

        // Листы, которые последний частичный проход переложил под нынешние поля.
        // Пока прокрутка не выходит за них, перекладывать нечего.
        private int _partialCoveredFromPage = -1;
        private int _partialCoveredToPage = -1;

        // ── Каретка и выделение по абзацу ──────────────────────────────────
        private readonly record struct LayoutPoint(ParagraphBlock Block, int Char);

        private sealed record CaretAnchor(
            LayoutPoint? Caret,
            LayoutPoint? SelStart,
            LayoutPoint? SelEnd,
            bool HadSelection,
            bool CaretIsSelEnd);

        private readonly record struct CaretState(
            int CaretPara, int CaretChar,
            int SelStartPara, int SelStartChar,
            int SelEndPara, int SelEndChar);

        // Где стояли каретка и выделение до жеста. Держится, пока не пройдёт полный
        // проход: каретка может оказаться за пределами частичного, и тогда поставить
        // её обратно можно только полной раскладкой.
        private CaretAnchor? _marginAnchor;

        // Номера, которые выставила последняя расстановка по _marginAnchor. Если
        // человек с тех пор сам переставил каретку, номера разойдутся, и тогда главнее
        // его выбор, а не запомненное до жеста место.
        private CaretState _marginAnchorApplied;

        /// <summary>
        /// Край поля сдвинулся (поля в документе уже новые) или жест закончился.
        /// </summary>
        private void OnMarginPreviewChanged()
        {
            if (DocVm?.MarginPreview is null)
            {
                if (_marginLiveActive)
                {
                    _marginLiveActive = false;
                    _marginGestureEnding = true;
                }
                return;
            }

            _marginLiveActive = true;
            ScheduleMarginLiveRelayout();
        }

        /// <summary>Сброс состояния жеста — при смене документа.</summary>
        private void ResetMarginLiveState()
        {
            _marginLiveActive = false;
            _marginGestureEnding = false;
            _marginSettlePending = false;
            _partialFromBlock = -1;
            _partialCoveredFromPage = -1;
            _partialCoveredToPage = -1;
            _marginAnchor = null;
        }

        /// <summary>
        /// Частичный проход ставится в очередь с приоритетом ниже ввода: движения
        /// мыши, пришедшие за время прохода, схлопываются, и следующий проход
        /// верстает уже последнее положение края, а не каждое промежуточное.
        /// </summary>
        private void ScheduleMarginLiveRelayout()
        {
            if (_marginLiveScheduled) return;

            _marginLiveScheduled = true;
            Dispatcher.UIThread.Post(RunMarginLiveRelayout, DispatcherPriority.Background);
        }

        private void RunMarginLiveRelayout()
        {
            _marginLiveScheduled = false;

            if (!_marginLiveActive && !_marginSettlePending) return;

            // После отпускания поля уже не меняются: пока окно стоит на переложенных
            // листах, повторный проход дал бы то же самое и только сбил бы прокрутке
            // быстрый путь отрисовки.
            if (!_marginLiveActive && VisiblePagesCovered()) return;

            if (!RelayoutVisiblePages()) return;

            InvalidateFull();
        }

        /// <summary>
        /// Итог жеста: поля записаны в документ. Видимые листы перекладываются сразу,
        /// остальное — через обычную пересборку: при холодном кеше она уходит в
        /// порционный прогрев и не держит интерфейс.
        ///
        /// Кеш раскладок здесь не чистится: записи абзацев и таблиц проверяются по
        /// ширине, и те, что частичный проход уже построил под новое поле, остаются
        /// верными. Чистка заставила бы шейпить заново как раз видимые абзацы.
        /// </summary>
        private void SettleMarginChange()
        {
            _styleResolver = CreateStyleResolver();
            ResetSpreadState();

            _marginSettlePending = true;

            if (ShouldWarmupBeforeRebuild())
                RelayoutVisiblePages();

            RebuildLayouts();

            _lastZoom = Zoom;
            InvalidateMeasure();
            InvalidateFull();
        }

        /// <summary>Живой проход возможен только в листах: у потоковых режимов полей нет.</summary>
        private bool CanRelayoutVisiblePages =>
            DocVm is not null
            && DocVm.ViewMode == EditorViewMode.Page
            && !SpreadMode
            && !ReadingRibbon;

        /// <summary>
        /// Перекладывает видимые листы под текущие поля документа.
        /// false — проход не нужен или не может быть частичным.
        /// </summary>
        private bool RelayoutVisiblePages()
        {
            if (!CanRelayoutVisiblePages) return false;

            if (_styleResolver is null)
                _styleResolver = CreateStyleResolver();

            if (!TryFindPartialRange(out int fromBlock, out int fromPage, out int toPage))
                return false;

            var anchor = TakeMarginCaretAnchor();

            PushReadingTextOverrides();

            _partialFromBlock = fromBlock;
            _partialFromPage = fromPage;
            _partialToPage = toPage;
            try
            {
                RebuildPageMode();
            }
            finally
            {
                _partialFromBlock = -1;
            }

            ApplyCaretAnchor(anchor);

            _partialCoveredFromPage = fromPage;
            _partialCoveredToPage = toPage;
            return true;
        }

        /// <summary>Видимые листы уже переложены последним частичным проходом.</summary>
        private bool VisiblePagesCovered()
        {
            if (_partialCoveredFromPage < 0) return false;

            List<PageRect> pages;
            lock (_renderLock) pages = _pages;
            if (pages.Count == 0) return false;

            var (firstVisible, lastVisible) = GetVisiblePageRange(pages);
            return firstVisible >= _partialCoveredFromPage
                && lastVisible <= _partialCoveredToPage;
        }

        /// <summary>
        /// Полный проход после жеста — зовётся из RebuildLayoutsCore перед проходом.
        /// Отдаёт то место каретки, которое надо восстановить после него.
        /// </summary>
        private CaretAnchor? BeginMarginSettlePass()
            => _marginSettlePending ? TakeMarginCaretAnchor() : null;

        /// <summary>Полный проход после жеста выполнен: каретка на место, жест закрыт.</summary>
        private void FinishMarginSettlePass(CaretAnchor? anchor)
        {
            if (!_marginSettlePending) return;

            _marginSettlePending = false;
            if (anchor is not null) ApplyCaretAnchor(anchor);
            _marginAnchor = null;
            _partialCoveredFromPage = -1;
            _partialCoveredToPage = -1;
        }

        // ── Диапазон частичного прохода ────────────────────────────────────

        /// <summary>
        /// С какого блока и листа начинать и на каком листе закончить.
        ///
        /// Начинать можно только с блока, который начинается с верха листа, — иначе
        /// поток сверху этого листа неизвестен. Первый блок видимого листа может
        /// оказаться продолжением абзаца или таблицы с прошлого листа: тогда поиск
        /// уходит на лист, где этот блок начался, и повторяется там.
        /// </summary>
        private bool TryFindPartialRange(out int fromBlock, out int fromPage, out int toPage)
        {
            fromBlock = -1;
            fromPage = 0;
            toPage = 0;

            if (DocVm is null) return false;

            List<ParaLayout> layouts;
            List<PageRect> pages;
            List<TableEntry> tables;
            List<ImageEntry> images;
            lock (_renderLock)
            {
                layouts = _layouts;
                pages = _pages;
                tables = _tables;
                images = _images;
            }

            if (pages.Count == 0) return false;

            var blocks = DocVm.Document.Sections[0].Blocks;
            var blockIndex = new Dictionary<BlockModel, int>(blocks.Count, ReferenceEqualityComparer.Instance);
            for (int i = 0; i < blocks.Count; i++)
                blockIndex[blocks[i]] = i;

            var (firstVisible, lastVisible) = GetVisiblePageRange(pages);
            toPage = Math.Max(lastVisible, firstVisible) + PartialPagesAfterView;

            int page = Math.Clamp(firstVisible, 0, pages.Count - 1);
            for (int step = 0; step < PartialStartSearchPages; step++)
            {
                if (!TryFirstBlockOnPage(page, layouts, tables, images, blockIndex,
                        out int firstBlock, out int startPage))
                {
                    // Лист без содержимого: пустые листы в конце частичного прохода
                    // или лист под привязанную картинку. Начало ищется выше.
                    if (page == 0)
                    {
                        fromBlock = 0;
                        fromPage = 0;
                        return true;
                    }

                    page--;
                    continue;
                }

                if (startPage >= page)
                {
                    fromBlock = firstBlock;
                    fromPage = page;
                    return true;
                }

                page = startPage;
            }

            return false;
        }

        /// <summary>
        /// Первый блок потока на листе и лист, где этот блок начинается.
        /// Плавающие картинки и фигуры в расчёт не идут: места в потоке они не
        /// занимают, и начинать с них значило бы сдвинуть весь текст листа.
        /// </summary>
        private static bool TryFirstBlockOnPage(
            int page,
            List<ParaLayout> layouts,
            List<TableEntry> tables,
            List<ImageEntry> images,
            Dictionary<BlockModel, int> blockIndex,
            out int firstBlock,
            out int startPage)
        {
            firstBlock = int.MaxValue;
            startPage = page;

            foreach (var pl in layouts)
            {
                if (pl.PageIndex != page || pl.Cell is not null) continue;
                if (pl.Vm?.Model is not ParagraphBlock para) continue;
                if (!blockIndex.TryGetValue(para, out int bi)) continue;

                if (bi < firstBlock)
                    firstBlock = bi;

                // Слайсы идут по порядку документа: первый на листе и есть самый ранний
                // абзац листа.
                break;
            }

            foreach (var te in tables)
            {
                if (te.PageIndex != page) continue;
                if (!blockIndex.TryGetValue(te.Table, out int bi)) continue;

                if (bi < firstBlock)
                    firstBlock = bi;
                break;
            }

            foreach (var ie in images)
            {
                if (ie.PageIndex != page || ie.InLine) continue;
                if (ie.Block.WrapMode != WrapMode.Inline) continue;
                if (!blockIndex.TryGetValue(ie.Block, out int bi)) continue;

                if (bi < firstBlock)
                    firstBlock = bi;
            }

            if (firstBlock == int.MaxValue) return false;

            // Лист, где начинается найденный блок: самый ранний из его кусков.
            foreach (var pl in layouts)
            {
                if (pl.Cell is not null) continue;
                if (pl.Vm?.Model is not ParagraphBlock para) continue;
                if (!blockIndex.TryGetValue(para, out int bi) || bi != firstBlock) continue;

                if (pl.PageIndex < startPage)
                    startPage = pl.PageIndex;
            }

            foreach (var te in tables)
            {
                if (!blockIndex.TryGetValue(te.Table, out int bi) || bi != firstBlock) continue;

                if (te.PageIndex < startPage)
                    startPage = te.PageIndex;
            }

            return true;
        }

        /// <summary>
        /// Начало частичного прохода: листы до стартового и всё, что на них лежит,
        /// переносится из прошлой раскладки как есть, стартовый лист добавляется
        /// пустым — с него пойдёт поток.
        ///
        /// Таблицы переносятся только непрерывным началом списка: абзацы ячеек
        /// ссылаются на свою таблицу номером в этом списке, и номера у перенесённых
        /// записей обязаны остаться прежними.
        /// </summary>
        private void SeedPartialPass(
            List<PageRect> newPages,
            List<ParaLayout> newLayouts,
            List<TableEntry> newTables,
            List<ImageEntry> newImages,
            List<ShapeEntry> newShapes,
            HashSet<ImageBlock> newInlineTransferred,
            List<PageBreakMark> newBreakMarks,
            float pageWidthPt, float pageHeightPt, float pageXPt,
            float mt, float ml, float mb)
        {
            List<ParaLayout> layouts;
            List<PageRect> pages;
            List<TableEntry> tables;
            List<ImageEntry> images;
            List<ShapeEntry> shapes;
            HashSet<ImageBlock> inlineTransferred;
            List<PageBreakMark> breakMarks;
            lock (_renderLock)
            {
                layouts = _layouts;
                pages = _pages;
                tables = _tables;
                images = _images;
                shapes = _shapes;
                inlineTransferred = _inlineTransferredImages;
                breakMarks = _breakMarks;
            }

            int fromPage = _partialFromPage;

            for (int i = 0; i <= fromPage; i++)
            {
                float y = i < pages.Count
                    ? pages[i].Ypt
                    : PageGapPt + i * (pageHeightPt + PageGapPt);
                newPages.Add(new PageRect(y, pageWidthPt, pageHeightPt, pageXPt, mt, ml, mb));
            }

            foreach (var te in tables)
            {
                if (te.PageIndex >= fromPage) break;
                newTables.Add(te);
            }

            int keptTables = newTables.Count;

            foreach (var pl in layouts)
            {
                if (pl.PageIndex >= fromPage) continue;
                if (pl.Cell is not null && pl.Cell.TableEntryIdx >= keptTables) continue;
                newLayouts.Add(pl);
            }

            // Картинки в строке заново собирает сам проход — по перенесённым абзацам тоже.
            foreach (var ie in images)
            {
                if (ie.InLine || ie.PageIndex >= fromPage) continue;
                newImages.Add(ie);
            }

            foreach (var se in shapes)
            {
                if (se.PageIndex >= fromPage) continue;
                newShapes.Add(se);
            }

            foreach (var transferred in inlineTransferred)
                newInlineTransferred.Add(transferred);

            foreach (var mark in breakMarks)
            {
                if (mark.PageIndex >= fromPage) continue;
                newBreakMarks.Add(mark);
            }
        }

        // ── Каретка и выделение по абзацу ──────────────────────────────────

        private CaretState CurrentCaretState() => new(
            _caretPara, _caretChar,
            _selStartPara, _selStartChar,
            _selEndPara, _selEndChar);

        /// <summary>
        /// Место каретки для очередного прохода: запомненное до жеста, если человек
        /// с тех пор каретку не трогал, иначе — нынешнее.
        /// </summary>
        private CaretAnchor TakeMarginCaretAnchor()
        {
            if (_marginAnchor is not null && CurrentCaretState() == _marginAnchorApplied)
                return _marginAnchor;

            _marginAnchor = CaptureCaretAnchor();
            return _marginAnchor;
        }

        private CaretAnchor CaptureCaretAnchor() => new(
            PointAt(_caretPara, _caretChar),
            PointAt(_selStartPara, _selStartChar),
            PointAt(_selEndPara, _selEndChar),
            HasSel(),
            _selEndPara == _caretPara && _selEndChar == _caretChar);

        private LayoutPoint? PointAt(int layoutIdx, int charIdx)
        {
            if (layoutIdx < 0 || layoutIdx >= _layouts.Count) return null;

            var pl = _layouts[layoutIdx];
            var block = pl.Cell?.ParaBlock ?? pl.Vm?.Model;
            return block is null ? null : new LayoutPoint(block, charIdx);
        }

        /// <summary>
        /// Ставит каретку и выделение по абзацам. Абзаца может не оказаться в
        /// раскладке — он за пределами частичного прохода. Тогда каретка остаётся
        /// в допустимых границах, выделение на время сворачивается, а точное место
        /// вернёт полный проход.
        /// </summary>
        private void ApplyCaretAnchor(CaretAnchor anchor)
        {
            if (_layouts.Count == 0) return;

            bool caretFound = false;
            if (anchor.Caret is { } caret && FindSliceFor(caret) is int caretIdx)
            {
                _caretPara = caretIdx;
                _caretChar = caret.Char;
                _caretLineHint = -1;
                caretFound = true;
            }
            else
            {
                _caretPara = Clamp(_caretPara, 0, _layouts.Count - 1);
            }

            if (caretFound)
                SnapCaretToCorrectSlice();

            bool selectionRestored = false;
            if (anchor.HadSelection
                && anchor.SelStart is { } selStart && FindSliceFor(selStart) is int startIdx)
            {
                if (anchor.CaretIsSelEnd && caretFound)
                {
                    _selStartPara = startIdx;
                    _selStartChar = selStart.Char;
                    _selEndPara = _caretPara;
                    _selEndChar = _caretChar;
                    selectionRestored = true;
                }
                else if (anchor.SelEnd is { } selEnd && FindSliceFor(selEnd) is int endIdx)
                {
                    _selStartPara = startIdx;
                    _selStartChar = selStart.Char;
                    _selEndPara = endIdx;
                    _selEndChar = selEnd.Char;
                    selectionRestored = true;
                }
            }

            if (!selectionRestored)
                SyncSel();

            _marginAnchorApplied = CurrentCaretState();
        }

        /// <summary>
        /// Слайс абзаца, в котором лежит символ. Абзац, разрезанный листом, лежит в
        /// раскладке несколькими слайсами, и символ принадлежит одному из них.
        /// </summary>
        private int? FindSliceFor(LayoutPoint point)
        {
            int? first = null;
            float widthPt = GetCurrentTextWidthPt();

            for (int i = 0; i < _layouts.Count; i++)
            {
                var pl = _layouts[i];
                var block = pl.Cell?.ParaBlock ?? pl.Vm?.Model;
                if (!ReferenceEquals(block, point.Block)) continue;

                first ??= i;

                // Раскладку ячейки кеш абзацев не строит — у неё своя ширина.
                var layout = pl.Layout ?? (pl.Cell is null && pl.Vm is not null
                    ? GetOrBuildLayout(pl.Vm, widthPt)
                    : null);
                if (layout is null) continue;

                int line = layout.GetLineIndexForChar(Math.Max(0, point.Char));
                if (line >= pl.LineFrom && line < Math.Max(pl.LineTo, pl.LineFrom + 1))
                    return i;
            }

            return first;
        }
    }
}
