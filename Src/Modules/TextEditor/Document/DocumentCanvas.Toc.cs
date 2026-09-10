using System;
using System.Collections.Generic;
using Avalonia.Threading;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Что канвас рассказывает о структуре книги: на какой странице лежит абзац и как
    /// к нему уйти.
    ///
    /// Номер страницы известен только раскладке — по тексту документа он не выводится,
    /// потому что зависит от полей, шрифта, картинок и разрывов. Поэтому и навигатор, и
    /// оглавление спрашивают его здесь, а не считают сами.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        /// <summary>
        /// Карта «абзац — номер страницы» (1-based) по текущей раскладке.
        ///
        /// Абзац, разрезанный между страницами, лежит в раскладке несколькими кусками;
        /// в карту идёт первый — заголовок числится на той странице, где он начинается.
        /// Абзацы ячеек таблиц и надписей пропускаются: заголовками они не бывают, а
        /// перекрыть собой одноимённый абзац потока могли бы.
        /// </summary>
        public Dictionary<Guid, int> GetBlockPageNumbers()
        {
            List<ParaLayout> layouts;
            lock (_renderLock) { layouts = _layouts; }

            var map = new Dictionary<Guid, int>(layouts.Count);

            foreach (var pl in layouts)
            {
                if (pl.Cell is not null) continue;

                int page = pl.PageIndex + 1;
                if (page < 1) continue;

                Guid id = pl.Vm.BlockId;
                if (map.TryGetValue(id, out int known) && known <= page) continue;

                map[id] = page;
            }

            return map;
        }

        /// <summary>
        /// Уводит рукопись к абзацу по его месту в потоке документа.
        ///
        /// Переход делается кареткой, а не прокруткой: человек, ушедший в главу из
        /// навигатора, почти всегда собирается там писать, и ставить курсор ему вторым
        /// движением незачем. Заодно даром достаётся всё, что уже умеет каретка —
        /// плавная прокрутка, разворот страниц рядом и подсветка в линейке.
        /// </summary>
        public void GoToParagraph(int paragraphIndex)
        {
            if (paragraphIndex < 0) return;
            if (DocVm is null || paragraphIndex >= DocVm.Paragraphs.Count) return;

            RestoreCaretState(paragraphIndex, 0);
        }

        /// <summary>
        /// Отдаёт вью-модели документа доступ к раскладке. Вызывается вместе с прочими
        /// подписками канваса.
        /// </summary>
        private void WireTocDelegates()
        {
            if (DocVm is null) return;

            DocVm.GetBlockPageNumbersDelegate = GetBlockPageNumbers;
            DocVm.GoToParagraphDelegate = GoToParagraph;

            DocVm.StylesChanged -= OnStylesChanged;
            DocVm.StylesChanged += OnStylesChanged;

            DocVm.TocPageNumbersStale -= OnTocPageNumbersStale;
            DocVm.TocPageNumbersStale += OnTocPageNumbersStale;
        }

        /// <summary>
        /// Список стилей документа пополнился — резолвер собирается заново.
        ///
        /// Он строит указатель имён один раз, в конструкторе, и о стиле, дописанном
        /// позже, сам не узнаёт: абзац, носящий такое имя, раскладывается как «Обычный».
        /// Кэш раскладки чистится целиком: под новый стиль попадают абзацы, о которых
        /// вызывающий ничего не знает.
        /// </summary>
        private void OnStylesChanged()
        {
            if (DocVm is null) return;

            _styleResolver = CreateStyleResolver();
            _layoutCache.Clear();
            InvalidateCellLayoutCaches();
            RebuildLayouts();
            InvalidateMeasure();
            InvalidateFull();
        }

        // Проход по номерам страниц уже назначен. Без этого правка нескольких настроек
        // подряд назначала бы по проходу на каждую, а считают они одно и то же.
        private bool _tocPageNumbersPending;

        // Проход, назначенный явным действием человека, обновляет и те оглавления,
        // которым самообновление выключено. Признак копится до самого прохода: пока он
        // ждёт, могло прийти и обычное уведомление о сдвиге страниц.
        private bool _tocPageNumbersForce;

        /// <summary>
        /// Номера страниц в оглавлениях устарели. Проход откладывается до конца текущего
        /// кадра: раскладка под новое содержимое ещё не построена, и спрашивать её о
        /// страницах сейчас значит получить те же старые числа.
        /// </summary>
        private void OnTocPageNumbersStale(bool force)
        {
            if (DocVm is null) return;

            _tocPageNumbersForce |= force;

            if (_tocPageNumbersPending) return;
            _tocPageNumbersPending = true;

            Dispatcher.UIThread.Post(RunTocPageNumberPass, DispatcherPriority.Background);
        }

        /// <summary>
        /// Второй проход оглавления: по готовой раскладке проставляет строкам настоящие
        /// номера страниц.
        ///
        /// Проход повторяется, потому что сам себя может опровергнуть: номер шире
        /// прежнего удлиняет строку, длинная строка переносится, перенос сдвигает
        /// страницу — и число, только что записанное, снова становится неверным.
        /// Сходится это за один-два круга; предел в три круга стоит на случай, когда
        /// раскладка качается между двумя состояниями и сходиться ей не с чем.
        /// </summary>
        private void RunTocPageNumberPass()
        {
            _tocPageNumbersPending = false;

            bool force = _tocPageNumbersForce;
            _tocPageNumbersForce = false;

            if (DocVm is null) return;
            if (_tocPassRunning) return;

            const int MaxPasses = 3;

            // Проход сам пересобирает раскладку, а пересборка сообщает о смене числа
            // страниц — и назначала бы следующий проход, тот третий, и так без конца.
            // На время работы такие сообщения не принимаются: круги здесь и без того
            // отсчитаны.
            _tocPassRunning = true;
            try
            {
                RebuildLayouts();

                for (int pass = 0; pass < MaxPasses; pass++)
                {
                    if (!DocVm.ApplyTocPageNumbers(force)) break;

                    // Текст строк изменился: их раскладки в кэше считаны под прежние числа.
                    _layoutCache.Clear();
                    RebuildLayouts();
                }
            }
            finally
            {
                _tocPassRunning = false;
                _tocKnownPageCount = _pages.Count;
            }

            _caretLineHint = -1;
            SnapCaretToCorrectSlice();
            InvalidateMeasure();
            InvalidateFull();
        }

        // Проход по номерам страниц идёт прямо сейчас. Пока он идёт, сообщения о
        // смене числа страниц не назначают следующий: их порождает он сам.
        private bool _tocPassRunning;

        // Число страниц на прошлой раскладке. По его изменению оглавления с включённым
        // самообновлением получают новые номера: страницы поехали — числа устарели.
        private int _tocKnownPageCount = -1;

        /// <summary>
        /// Раскладка пересобрана. Если книга сменила число страниц, оглавлениям с
        /// включённым самообновлением назначается проход по номерам.
        ///
        /// Сравнивается именно число страниц, а не содержимое: набор внутри страницы
        /// номеров не меняет, и гонять по оглавлению на каждый нажатый символ незачем.
        /// </summary>
        private void NotifyTocPageCount(int pageCount)
        {
            if (DocVm is null) return;
            if (_tocPassRunning) return;

            if (_tocKnownPageCount == pageCount) return;

            bool firstLayout = _tocKnownPageCount < 0;
            _tocKnownPageCount = pageCount;

            if (firstLayout) return;
            if (DocVm.Document.TableOfContents is not { Count: > 0 }) return;

            OnTocPageNumbersStale(force: false);
        }

        /// <summary>
        /// Забывает известное число страниц. Зовётся при смене документа: у новой
        /// рукописи своё число страниц, и первая её раскладка сдвигом не считается.
        /// </summary>
        private void ResetTocPageCount()
        {
            _tocKnownPageCount = -1;
            _tocPageNumbersPending = false;
            _tocPageNumbersForce = false;
            _tocPassRunning = false;
        }
    }
}
