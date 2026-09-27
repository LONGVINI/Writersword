using Avalonia;
using Avalonia.Threading;
using System;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Возврат на место в документе после переключения вкладки или воркмода.
    ///
    /// Вью редактора при переключении создаётся заново, и документ раскладывается ещё
    /// секунду-две. Раньше каретка и прокрутка ставились сразу, на пустую раскладку:
    /// каретке некуда было встать, а прокрутка упиралась в холст нулевой высоты и
    /// прижималась к началу — человек каждый раз оказывался в самом начале книги.
    ///
    /// Теперь место запоминается и ставится, когда вёрстка документа закончена
    /// целиком: каретка — в свой абзац, прокрутка — туда, где лист стоял.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        /// <summary>Место, которое ждёт готовой раскладки. null — ждать нечего.</summary>
        private (int Para, int Char, double ScrollY)? _pendingViewRestore;

        // ── Сообщение о смене места ────────────────────────────────────────
        //
        // Модуль хранит место в документе (абзац, символ, прокрутку) в сессионном
        // кэше. Освежить этот кэш можно только на UI-потоке, а сохраняют его фоном:
        // при смене воркмода, по таймеру кэша, при закрытии. Фоновый сбор получал то
        // место, которое модуль запомнил когда-то раньше, — чаще всего начало
        // документа, — и при возврате вид уезжал туда.
        //
        // Поэтому полотно само сообщает, что место сменилось: после прокрутки и
        // движения каретки (с задержкой, чтобы не дёргать модуль на каждом кадре
        // прокрутки) и сразу — перед тем как уйти с экрана.

        /// <summary>Место в документе сменилось — пора освежить сессионный кэш.</summary>
        public event EventHandler? ViewStateChanged;

        // Задержка сообщения после последней прокрутки или движения каретки.
        private static readonly TimeSpan ViewStateNotifyDelay = TimeSpan.FromMilliseconds(300);

        private DispatcherTimer? _viewStateTimer;

        /// <summary>Назначает сообщение о смене места после паузы.</summary>
        private void ScheduleViewStateChanged()
        {
            if (ViewStateChanged is null) return;

            if (_viewStateTimer is null)
            {
                _viewStateTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = ViewStateNotifyDelay
                };
                _viewStateTimer.Tick += (_, _) =>
                {
                    _viewStateTimer!.Stop();
                    ViewStateChanged?.Invoke(this, EventArgs.Empty);
                };
            }

            _viewStateTimer.Stop();
            _viewStateTimer.Start();
        }

        /// <summary>
        /// Сообщает о месте сейчас же, не дожидаясь паузы. Зовётся, когда полотно
        /// уходит с экрана: после этого прокрутка ему уже не принадлежит.
        /// </summary>
        private void FlushViewStateChanged()
        {
            _viewStateTimer?.Stop();
            ViewStateChanged?.Invoke(this, EventArgs.Empty);
        }

        // ── Та же вью, снова на экране ────────────────────────────────────
        //
        // Вью не всегда пересоздаётся: раскладка воркмода может вернуться из запаса,
        // и прежнее полотно просто снова прикрепляется. Модуль в этом случае место
        // не восстанавливает — вью он не создавал, — а ScrollViewer, побыв вне
        // экрана, мог сброситься к началу. Место, где полотно ушло с экрана, оно
        // возвращает само.

        /// <summary>Место на момент ухода с экрана. null — полотно с экрана не уходило.</summary>
        private (int Para, int Char, double ScrollY)? _detachedViewState;

        /// <summary>Запоминает место, уходя с экрана.</summary>
        private void RememberDetachedViewState()
        {
            var (para, ch, scrollY) = GetCaretState();
            _detachedViewState = (para, ch, scrollY);
        }

        /// <summary>
        /// Возвращает место после повторного прикрепления, если модуль не поставил
        /// своё: его место свежее — оно пришло из сессии.
        /// </summary>
        private void RestoreDetachedViewState()
        {
            if (_detachedViewState is not { } state) return;
            _detachedViewState = null;

            if (_pendingViewRestore is not null) return;

            _logger.Debug(
                "[VIEW] Полотно снова на экране: возврат на абзац {Para}, прокрутка {Scroll:F0}",
                state.Para, state.ScrollY);

            RestoreViewState(state.Para, state.Char, state.ScrollY);
        }

        /// <summary>
        /// Вернуть каретку в абзац и символ, а лист — на прежнюю прокрутку. Если
        /// раскладка ещё не готова, место ставится по её готовности.
        /// </summary>
        public void RestoreViewState(int docParaIdx, int charIdx, double scrollY)
        {
            _pendingViewRestore = (docParaIdx, charIdx, scrollY);

            _logger.Debug(
                "[VIEW] Возврат на место: абзац {Para}, символ {Char}, прокрутка {Scroll:F0}; раскладка готова {Ready}",
                docParaIdx, charIdx, scrollY, _layouts.Count > 0 && !_layoutWarmupActive);

            // Вью не пересоздавалась и документ уже разложен — ждать нечего.
            if (_layouts.Count > 0 && !_layoutWarmupActive)
                Dispatcher.UIThread.Post(ApplyPendingViewRestore, DispatcherPriority.Loaded);
        }

        private void ApplyPendingViewRestore()
        {
            if (_pendingViewRestore is not { } restore) return;
            if (DocVm is null || DocVm.Paragraphs.Count == 0) return;
            if (_layouts.Count == 0 || _layoutWarmupActive) return;

            _pendingViewRestore = null;

            int para = Clamp(restore.Para, 0, DocVm.Paragraphs.Count - 1);
            if (IsParagraphInLayouts(para))
            {
                _caretPara = FindFirstSliceForDocVmParagraph(para);
                _caretChar = Clamp(restore.Char, 0, GetVmAt(_caretPara)?.PlainText?.Length ?? 0);
                SnapCaretToCorrectSlice();
                UpdatePreferredX();
                SyncSel();
                ResetCaret();

                var pvm = GetVmAt(_caretPara);
                if (pvm is not null && DocVm.Paragraphs.Contains(pvm))
                    DocVm.SetActiveParagraph(pvm);

                UpdateSelectionContext();
            }

            Focus();

            // Высота холста выросла вместе с раскладкой, но прокрутка узнает о ней только
            // после прохода измерения. Поэтому прокрутка ставится следующим шагом, уже
            // по готовому холсту, — иначе она снова упёрлась бы в прежнюю высоту.
            InvalidateMeasure();
            InvalidateFull();

            double targetY = restore.ScrollY;
            Dispatcher.UIThread.Post(() =>
            {
                var sv = _parentScrollViewer;
                if (sv is null) return;

                if (targetY > 0)
                    sv.Offset = new Vector(sv.Offset.X, targetY);
                else
                    ScrollToCaret();

                _logger.Debug(
                    "[VIEW] Место восстановлено: абзац {Para}, прокрутка {Target:F0} → {Actual:F0} (высота холста {Extent:F0})",
                    para, targetY, sv.Offset.Y, sv.Extent.Height);
            }, DispatcherPriority.Background);
        }
    }
}
