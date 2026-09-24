using System;
using System.Collections.Generic;
using Avalonia.Threading;
using Writersword.Modules.TextEditor.Models.Document;

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
        public void GoToParagraph(int paragraphIndex) => GoToParagraph(paragraphIndex, true);

        /// <param name="rememberPlace">
        /// true — положить прежнее место в историю прыжков.
        ///
        /// Ложь нужна возвратам каретки, которые прыжком не являются: восстановление
        /// после пересборки оглавления, отмена, повтор. Они приводят каретку туда же,
        /// где человек и был, и запись такого «прыжка» в историю засорила бы её местами,
        /// возвращаться в которые незачем.
        /// </param>
        public void GoToParagraph(int paragraphIndex, bool rememberPlace)
        {
            if (paragraphIndex < 0) return;
            if (DocVm is null || paragraphIndex >= DocVm.Paragraphs.Count) return;

            // Место, откуда уходим, кладётся в историю прыжков: вернуться прокруткой из
            // чужой главы человек не сможет — он не знает, где был.
            if (rememberPlace) RememberPlaceBeforeJump();

            RestoreCaretState(paragraphIndex, 0);
        }

        /// <summary>
        /// Уводит рукопись к абзацу по его опознавателю.
        ///
        /// Место в потоке считается тем же правилом, что и в GetBlockPageNumbers и в
        /// TocService: только абзацы верхнего уровня первого раздела, по порядку блоков.
        /// Третьего счёта абзацев в модуле нет намеренно — разойдясь, они увели бы
        /// переход по оглавлению не в ту главу.
        /// </summary>
        /// <returns>false — абзаца с таким опознавателем в рукописи нет.</returns>
        public bool GoToBlock(Guid blockId)
        {
            if (DocVm is null) return false;

            var doc = DocVm.Document;
            if (doc is null || doc.Sections.Count == 0) return false;

            var blocks = doc.Sections[0].Blocks;
            int paragraphIndex = 0;

            for (int i = 0; i < blocks.Count; i++)
            {
                if (blocks[i] is not ParagraphBlock para) continue;

                if (para.Id == blockId)
                {
                    GoToParagraph(paragraphIndex);
                    return true;
                }

                paragraphIndex++;
            }

            return false;
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
            DocVm.PushUndoCommandDelegate = PushUndoCommand;
            DocVm.SetCaretToParagraphDelegate = SetCaretToParagraphNow;
            DocVm.FlushTocPageNumbersDelegate = FlushTocPageNumbersNow;

            DocVm.StylesChanged -= OnStylesChanged;
            DocVm.StylesChanged += OnStylesChanged;

            DocVm.TocPageNumbersStale -= OnTocPageNumbersStale;
            DocVm.TocPageNumbersStale += OnTocPageNumbersStale;
        }

        /// <summary>
        /// Ставит каретку на абзац сейчас же, ничего не прокручивая.
        ///
        /// Отличие от <see cref="GoToParagraph"/> в одном, но существенном: тот
        /// откладывает переход до конца кадра. Отмене и повтору откладывать нельзя —
        /// сразу после их возврата полотно прокручивает вид к каретке, и к этому мигу
        /// она обязана стоять на новом месте. Прокрутку здесь не делаем намеренно: её
        /// сделает тот самый проход, и делать её дважды значит дёрнуть лист.
        /// </summary>
        private void SetCaretToParagraphNow(int paragraphIndex)
        {
            if (DocVm is null) return;
            if (paragraphIndex < 0 || paragraphIndex >= DocVm.Paragraphs.Count) return;
            if (_layouts.Count == 0) return;

            // Раскладка обязана знать этот абзац: поиск слайса на ненайденный отвечает
            // нулём, и каретка уезжает в начало книги вместе с видом.
            if (!IsParagraphInLayouts(paragraphIndex))
            {
                RebuildLayouts();
                if (!IsParagraphInLayouts(paragraphIndex)) return;
            }

            _caretPara = FindFirstSliceForDocVmParagraph(paragraphIndex);
            _caretChar = 0;
            _caretLineHint = -1;

            SnapCaretToCorrectSlice();
            UpdatePreferredX();
            SyncSel();
        }

        /// <summary>
        /// Кладёт готовый шаг отмены в тот же стек, куда уходят снимки документа.
        ///
        /// Стеков два — снимочный и операционный, — и порядок отмены между ними ведётся
        /// отдельным списком. Своя команда обязана в него попасть, иначе Ctrl+Z пойдёт
        /// не по хронологии: сперва вычерпает один стек, потом другой, и человек увидит,
        /// как отменяется позавчерашнее вместо только что сделанного.
        /// </summary>
        private void PushUndoCommand(Writersword.Core.Interfaces.Modules.IUndoableCommand command)
        {
            if (command is null) return;

            if (UndoStack is null)
            {
                _logger.Warning("[UNDO] PushUndoCommand: UndoStack is null, '{D}'", command.Description);
                return;
            }

            UndoStack.Push(command);
            RecordSnapshotInOrder();
            DocVm?.RaiseContentModified();

            _logger.Debug("[UNDO] PushUndoCommand: pushed '{D}'", command.Description);
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

            // Оглавлений в рукописи не осталось — проставлять номера некому. Проверка
            // стоит здесь, а не только там, где проход назначают: назначить его могли
            // ДО того, как оглавление снесли, и тогда он приходил на пустое место и
            // всё равно гнал полный пересбор раскладки. На трёхсотстраничной книге это
            // полсотни миллисекунд впустую сразу после удаления и столько же после
            // отмены.
            if (DocVm.Document.TableOfContents is not { Count: > 0 }) return;

            // Раскладка обязана быть пересобрана по новому составу абзацев: номера
            // страниц спрашиваются у неё, а не выводятся из текста.
            //
            // Пересборка может и отказаться. На холодном кеше она уходит в порционный
            // прогрев и до его конца отдаёт прежние слайсы — построенные ДО вставки
            // оглавления. А оглавление сдвинуло вниз всю книгу.
            //
            // Проход, спросивший номера у такой раскладки, получает прежние числа,
            // видит, что менять нечего, и уходит — истратив свой ход и записав в
            // _tocKnownPageCount прежнее число страниц. Так первое в рукописи
            // оглавление и оставалось с номерами от книги без оглавления: стили
            // Toc1…Toc9 дописываются как раз при первой вставке, дописанные стили
            // чистят кеш раскладки целиком, и она уходит в прогрев ровно в ту минуту,
            // когда проход назначается.
            //
            // Поэтому ход не тратится: проход ждёт готовой раскладки, и назначит его
            // сам прогрев, когда закончит.
            bool warmingUp;

            _tocPassRunning = true;
            try
            {
                RebuildLayouts();
                warmingUp = _layoutWarmupActive;
            }
            finally
            {
                _tocPassRunning = false;
            }

            if (warmingUp)
            {
                _tocPassAwaitsWarmup = true;
                _tocPassAwaitsWarmupForce |= force;

                _logger.Debug("[TOC] Проход по номерам отложен до конца прогрева раскладки");
                return;
            }

            const int MaxPasses = 3;

            // Проход сам пересобирает раскладку, а пересборка сообщает о смене числа
            // страниц — и назначала бы следующий проход, тот третий, и так без конца.
            // На время работы такие сообщения не принимаются: круги здесь и без того
            // отсчитаны.
            var watch = System.Diagnostics.Stopwatch.StartNew();
            int passesRun = 0;
            int entriesTouched = 0;

            _tocPassRunning = true;
            try
            {
                for (int pass = 0; pass < MaxPasses; pass++)
                {
                    if (!DocVm.ApplyTocPageNumbers(force)) break;

                    passesRun++;

                    // Текст строк изменился: их раскладки в кэше считаны под прежние
                    // числа. Чистятся раскладки ровно этих строк, а не весь кэш: сброс
                    // целиком гнал через Skia каждый абзац книги ради полутора сотен
                    // строк оглавления — и делал это на каждом круге, до трёх раз.
                    var touched = DocVm.TakeTocTouched();
                    entriesTouched += touched.Count;

                    foreach (var pvm in touched)
                        _layoutCache.Remove(pvm);

                    RebuildLayouts();
                }
            }
            finally
            {
                _tocPassRunning = false;
                _tocKnownPageCount = _pages.Count;
            }

            watch.Stop();
            _logger.Debug(
                "[TOC] Проход по номерам: {Ms} мс, кругов {Passes}, строк {Entries}",
                watch.ElapsedMilliseconds, passesRun, entriesTouched);

            _caretLineHint = -1;
            SnapCaretToCorrectSlice();
            InvalidateMeasure();
            InvalidateFull();
        }

        // Проход по номерам страниц идёт прямо сейчас. Пока он идёт, сообщения о
        // смене числа страниц не назначают следующий: их порождает он сам.
        private bool _tocPassRunning;

        // Проход ждёт конца прогрева кеша раскладки. Перепланировать себя по таймеру он
        // не может: прогрев идёт порциями и длится столько, сколько длится, а проход,
        // пришедший раньше времени, молча получил бы прежние номера страниц.
        private bool _tocPassAwaitsWarmup;

        // Ждущему проходу нужно помнить, был ли он назначен явным действием человека:
        // такой проход обновляет и те оглавления, которым самообновление выключено.
        private bool _tocPassAwaitsWarmupForce;

        /// <summary>
        /// Прогрев кеша раскладки закончен — раскладка построена по настоящему составу
        /// абзацев. Если проход по номерам оглавления ждал этой минуты, назначаем его.
        ///
        /// Зовётся из <see cref="PumpLayoutWarmup"/> и только оттуда.
        /// </summary>
        private void OnLayoutWarmupFinished()
        {
            if (!_tocPassAwaitsWarmup) return;

            _tocPassAwaitsWarmup = false;

            bool force = _tocPassAwaitsWarmupForce;
            _tocPassAwaitsWarmupForce = false;

            OnTocPageNumbersStale(force);
        }

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

            // Пересборку раскладки порождает и сам проход по номерам. Её он не должен
            // принимать за правку человека — иначе каждый проход назначал бы следующий.
            if (_tocPassRunning) return;

            // Первая раскладка открытого документа правкой не является. Проход на ней
            // поправил бы числа, записанные в файл, и документ оказался бы изменённым
            // сразу после открытия — человек ещё ничего не тронул, а его уже спрашивают,
            // сохранить ли.
            bool firstLayout = _tocKnownPageCount < 0;
            _tocKnownPageCount = pageCount;
            if (firstLayout) return;

            if (DocVm.Document.TableOfContents is not { Count: > 0 }) return;

            // Автоматика ждёт паузы в наборе.
            //
            // Раньше проход назначался на смену числа страниц. Плохо это было дважды.
            // Во-первых, на границе страницы при наборе он срабатывал раз за разом: слово
            // перенеслось — страниц прибавилось, стёр — убавилось, и каждый раз по четыре
            // пересборки книги. Во-вторых, он пропускал настоящие сдвиги: дописал абзац
            // в первой главе и стёр такой же в пятой — число страниц прежнее, а главы
            // между ними съехали, и номера врали молча.
            //
            // Теперь каждая пересборка только перезапускает таймер. Сработает он, когда
            // человек остановится, и тогда дешёвая проверка скажет, врёт ли хоть одна
            // строка. Проход запускается только если да.
            EnsureTocIdleTimer();
            _tocIdleTimer!.Stop();
            _tocIdleTimer.Start();
        }

        // Таймер паузы в наборе. Каждая пересборка раскладки перезапускает его, и
        // срабатывает он только когда пересборки прекратились.
        private DispatcherTimer? _tocIdleTimer;

        /// <summary>
        /// Сколько ждать тишины перед проверкой номеров. Достаточно, чтобы не мешать
        /// набору, и мало, чтобы человек, поднявший глаза на оглавление, увидел в нём
        /// правду.
        /// </summary>
        private static readonly TimeSpan TocIdleDelay = TimeSpan.FromSeconds(1.5);

        private void EnsureTocIdleTimer()
        {
            if (_tocIdleTimer is not null) return;

            _tocIdleTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TocIdleDelay
            };

            _tocIdleTimer.Tick += OnTocIdle;
        }

        /// <summary>
        /// Человек остановился. Если хоть одна строка оглавления врёт о номере страницы —
        /// назначаем проход; если нет — не делаем ничего.
        /// </summary>
        private void OnTocIdle(object? sender, EventArgs e)
        {
            _tocIdleTimer?.Stop();

            if (DocVm is null) return;
            if (_tocPassRunning) return;

            if (!DocVm.AreTocPageNumbersStale()) return;

            OnTocPageNumbersStale(force: false);
        }

        /// <summary>
        /// Досчитать номера сейчас же, без паузы и без очереди. Зовётся перед печатью и
        /// выгрузкой: в готовый файл устаревшие числа уйдут навсегда.
        ///
        /// Работает, только если есть что править, и только синхронно — вызывающему
        /// нужен документ с правильными числами к той строке, что идёт следом.
        /// </summary>
        private void FlushTocPageNumbersNow()
        {
            _tocIdleTimer?.Stop();

            if (DocVm is null) return;
            if (DocVm.Document.TableOfContents is not { Count: > 0 }) return;

            // Отложенный проход, если он уже назначен, выполняем сейчас. Назначен он или
            // нет — решаем сами, по признаку устаревших чисел, плюс явное требование
            // обновить, пришедшее из ленты и ждущее своей очереди.
            bool pending = _tocPageNumbersPending;
            if (!pending && !DocVm.AreTocPageNumbersStale()) return;

            _tocPageNumbersPending = true;
            RunTocPageNumberPass();
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
            _tocPassAwaitsWarmup = false;
            _tocPassAwaitsWarmupForce = false;

            // Пауза, начатая над прежним документом, к новому отношения не имеет.
            _tocIdleTimer?.Stop();
        }
    }
}
