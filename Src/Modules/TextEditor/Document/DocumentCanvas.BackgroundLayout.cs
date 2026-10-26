using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia.Threading;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Фоновая пересборка листов — как у Word.
    ///
    /// Полный проход раскладки идёт по всей книге и на трёхстах страницах занимает
    /// секунду и больше, а с картинками в обтекании и колонками проходов бывает
    /// несколько. Раньше он шёл на потоке интерфейса одним куском: после смены колонок
    /// или полей, после паузы в наборе окно замирало на секунды. Word так не делает:
    /// видимые листы у него встают сразу, а остальные досчитываются в фоне — счётчик
    /// страниц внизу «дотикивает» уже после правки.
    ///
    /// Здесь так же. Проход режется на порции между блоками документа
    /// (RebuildLayoutsCoreSteps, RebuildPageModeSteps): порция — около двадцати
    /// миллисекунд, затем ход отдаётся диспетчеру с приоритетом ниже ввода, и
    /// между порциями окно отвечает — щелчки, клавиши, прокрутка, кадры. Всё время,
    /// пока идёт пересборка, на листах прежняя раскладка (а видимые листы уже
    /// переложены частичным проходом, DocumentCanvas.MarginPreview); новая
    /// показывается целиком, когда проход закончен.
    ///
    /// Работа целиком на потоке интерфейса — гонок с моделью нет по построению. Но
    /// между порциями модель может поменяться: человек набрал букву, отменил правку,
    /// вставил таблицу. Недоделанный проход тогда бросается (без публикации — флаг
    /// _abandonPass), а раскладку соберёт тот, кто правку сделал: быстрый путь набора
    /// и его отложенная пересборка, полная пересборка после структурной правки. Так же
    /// бросается проход, если кто-то пересобирает раскладку сразу (RebuildLayouts):
    /// его результат свежее.
    ///
    /// Холодный кеш строк пересборка не трогает: она уходит в порционный прогрев
    /// (PumpLayoutWarmup), а прогрев по окончании запускает её снова.
    ///
    /// Пересборка идёт по частям только в режиме листов. В чтении, книге и ленте лист
    /// считается по окну, и там всё по-прежнему сразу.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        // Порция фоновой пересборки, мс: меньше кадра при 60 Гц с запасом на отрисовку.
        private const int BackgroundLayoutSliceMs = 20;

        // Идущая фоновая пересборка; null — её нет.
        private IEnumerator<bool>? _backgroundLayout;

        // Что сделать, когда пересборка закончится (прокрутка к каретке после набора).
        private readonly List<Action> _backgroundLayoutDone = new();

        private readonly Stopwatch _backgroundLayoutSlice = new();
        private long _backgroundLayoutStartTs;
        private int _backgroundLayoutTicks;
        private bool _backgroundLayoutPosted;

        // Ширина холста, под которую пересборка начата: окно поменяли — начинается заново.
        private double _backgroundLayoutWidth = double.NaN;

        // Порция исполняется прямо сейчас: бросить проход изнутри него нельзя — только
        // после того, как порция вернёт ход.
        private bool _backgroundLayoutStepping;
        private bool _backgroundLayoutAbandonAfterStep;

        // Проход бросается: его finally не публикует недоделанную раскладку.
        private bool _abandonPass;

        /// <summary>Идёт фоновая пересборка.</summary>
        private bool BackgroundLayoutRunning => _backgroundLayout is not null;

        /// <summary>Пересборка по частям возможна: режим листов (не чтение, не книга, не лента).</summary>
        private bool CanLayoutInBackground => CanRelayoutVisiblePages;

        /// <summary>
        /// Пересобирает раскладку по частям, не держа интерфейс. Идущая пересборка
        /// начинается заново — по нынешнему документу. <paramref name="done"/> — после
        /// того, как новая раскладка показана.
        /// </summary>
        private void RebuildLayoutsInBackground(Action? done = null)
        {
            if (!CanLayoutInBackground)
            {
                RebuildLayouts();
                _caretLineHint = -1;
                SnapCaretToCorrectSlice();
                InvalidateMeasure();
                InvalidateFull();
                done?.Invoke();
                return;
            }

            if (done is not null) _backgroundLayoutDone.Add(done);

            AbandonBackgroundLayout(keepDone: true);
            if (DocVm is null) return;

            _backgroundLayout = RebuildLayoutsCoreSteps(BackgroundSliceSpent).GetEnumerator();
            _backgroundLayoutStartTs = Stopwatch.GetTimestamp();
            _backgroundLayoutTicks = 0;
            _backgroundLayoutWidth = _canvasWidth;
            PostBackgroundLayoutTick();
        }

        /// <summary>Порция исчерпана — пора отдать ход диспетчеру.</summary>
        private bool BackgroundSliceSpent()
            => _backgroundLayoutSlice.ElapsedMilliseconds >= BackgroundLayoutSliceMs;

        private void PostBackgroundLayoutTick()
        {
            if (_backgroundLayoutPosted) return;
            _backgroundLayoutPosted = true;
            Dispatcher.UIThread.Post(BackgroundLayoutTick, DispatcherPriority.Background);
        }

        /// <summary>Одна порция фоновой пересборки.</summary>
        private void BackgroundLayoutTick()
        {
            _backgroundLayoutPosted = false;

            var run = _backgroundLayout;
            if (run is null) return;

            if (DocVm is null)
            {
                AbandonBackgroundLayout(keepDone: false);
                return;
            }

            // Скрытый холст (другая вкладка, другой воркмод) не раскладывается: окно
            // занято другим делом. Пересборка ждёт и продолжает с того же места.
            if (!IsEffectivelyVisible)
            {
                _backgroundLayoutPosted = true;
                DispatcherTimer.RunOnce(BackgroundLayoutTick, HiddenWarmupPollInterval, DispatcherPriority.Background);
                return;
            }

            // Подмены чтения статические: между порциями их мог поставить другой проход
            // отрисовки, а вёрстка обязана идти с теми же, с которыми началась.
            PushReadingTextOverrides();

            // Каретка и выделение хранятся номерами слайсов раскладки, а новая раскладка
            // эти номера сдвигает. Пока шла пересборка, человек мог двигать каретку по
            // прежней раскладке — её место запоминается абзацем и символом перед каждой
            // порцией и ставится обратно, если порция показала новую раскладку. Без этого
            // каретка после публикации оказывалась в чужом абзаце — на другом листе или
            // в другом разделе. Это же перебивает место, запомненное в начале пересборки
            // после сдвига поля (FinishMarginSettlePass): оно к концу уже устарело.
            var caretAnchor = _layouts.Count > 0 ? CaptureCaretAnchor() : null;
            var layoutsBefore = _layouts;

            _backgroundLayoutSlice.Restart();
            _backgroundLayoutTicks++;
            long perfTs = PerfNow();

            bool more;
            _backgroundLayoutStepping = true;
            try
            {
                more = run.MoveNext();
            }
            catch (Exception ex)
            {
                // Проход упал. Его finally уже показал то, что успело сложиться, — как
                // и при пересборке сразу; дальше идти некуда.
                _logger.Error(ex, "[LAYOUT] Фоновая пересборка раскладки упала");
                _backgroundLayout = null;
                _backgroundLayoutAbandonAfterStep = false;
                _backgroundLayoutDone.Clear();
                try { run.Dispose(); } catch (Exception) { }
                InvalidateMeasure();
                InvalidateFull();
                return;
            }
            finally
            {
                _backgroundLayoutStepping = false;
                PerfTime("ui.rebuild.bg", perfTs);
            }

            if (caretAnchor is not null && !ReferenceEquals(_layouts, layoutsBefore))
                ApplyCaretAnchor(caretAnchor);

            // Пока шла порция, проход попросили бросить (правка изнутри неё).
            if (_backgroundLayoutAbandonAfterStep)
            {
                _backgroundLayoutAbandonAfterStep = false;
                if (ReferenceEquals(_backgroundLayout, run)) _backgroundLayout = null;
                AbandonRun(run);
                return;
            }

            if (more)
            {
                PostBackgroundLayoutTick();
                return;
            }

            _backgroundLayout = null;
            run.Dispose();

            double ms = (Stopwatch.GetTimestamp() - _backgroundLayoutStartTs) * 1000.0 / Stopwatch.Frequency;
            if (ms > 250)
            {
                _logger.Debug(
                    "[LAYOUT] Фоновая пересборка: {Ms:F0} мс, порций {Ticks}, страниц {Pages}",
                    ms, _backgroundLayoutTicks, _pages.Count);
            }

            FinishBackgroundLayout();
        }

        /// <summary>Новая раскладка показана: каретка на место, холст под неё, отложенные дела.</summary>
        private void FinishBackgroundLayout()
        {
            // Пересборка ушла в прогрев холодного кеша: прогрев сам запустит её снова,
            // и отложенные дела дождутся той, настоящей.
            if (_layoutWarmupActive) return;

            _caretLineHint = -1;
            SnapCaretToCorrectSlice();
            InvalidateMeasure();
            InvalidateFull();

            if (_backgroundLayoutDone.Count == 0) return;

            var done = _backgroundLayoutDone.ToArray();
            _backgroundLayoutDone.Clear();
            foreach (var action in done) action();
        }

        /// <summary>
        /// Бросает идущую фоновую пересборку, ничего не публикуя. <paramref name="keepDone"/> —
        /// отложенные дела ждут следующей. true — пересборка шла.
        /// </summary>
        private bool AbandonBackgroundLayout(bool keepDone)
        {
            if (!keepDone) _backgroundLayoutDone.Clear();

            var run = _backgroundLayout;
            if (run is null) return false;

            if (_backgroundLayoutStepping)
            {
                // Изнутри порции итератор не освободить: он исполняется. Бросается
                // сразу, как порция вернёт ход.
                _backgroundLayoutAbandonAfterStep = true;
                _backgroundLayout = null;
                return true;
            }

            _backgroundLayout = null;
            AbandonRun(run);
            return true;
        }

        private void AbandonRun(IEnumerator<bool> run)
        {
            _abandonPass = true;
            try
            {
                run.Dispose();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[LAYOUT] Брошенная фоновая пересборка не освободилась чисто");
            }
            finally
            {
                _abandonPass = false;
            }
        }

        /// <summary>Холст сменил ширину, пока шла пересборка: она начинается заново под новую.</summary>
        private void RestartBackgroundLayoutIfWidthChanged()
        {
            if (!BackgroundLayoutRunning) return;
            if (Math.Abs(_backgroundLayoutWidth - _canvasWidth) < 0.5) return;
            RebuildLayoutsInBackground();
        }

        /// <summary>После фоновой пересборки, начатой паузой в наборе: каретка в окне.</summary>
        private void AfterTypingBackgroundRebuild() => ScrollToCaret();
    }
}
