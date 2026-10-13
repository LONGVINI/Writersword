using System;
using Avalonia.Threading;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Страж прогрева раскладки.
    ///
    /// Новый документ (открытие, переключение версии в режиме сравнения) сначала
    /// показывает предварительную раскладку первых листов, а полная приходит по
    /// окончании порционного прогрева. Прогрев может оборваться, не дойдя до конца:
    /// полотно сняли с экрана, документ на миг пропал из контекста. Повторный запуск
    /// держится на проходе measure, а measure во время жеста масштаба пересборку
    /// пропускает — и на экране навсегда оставалась предварительная раскладка:
    /// пустые листы, высота холста под один лист в ряду, прокрутка не туда. Так
    /// «слетал вид» после нескольких переключений версий, и чинил его только клик.
    ///
    /// Страж раз в секунду, пока раскладка не догнала документ, проверяет, жив ли
    /// прогрев. Нет прогрева, нет жеста, а раскладка не та — пересобирает сам. Прогрев,
    /// идущий подозрительно долго, пишет в журнал, в каком он состоянии.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        private static readonly TimeSpan WarmupGuardInterval = TimeSpan.FromSeconds(1);

        // Прогрев дольше этого — повод записать его состояние в журнал.
        private const double WarmupSlowLogMs = 15000;

        private DispatcherTimer? _warmupGuardTimer;
        private bool _attachedToTree;
        private bool _warmupSlowLogged;

        // Прогрев оборван не своим окончанием (снятие с экрана, пропавший документ).
        private bool _warmupInterrupted;

        // Когда прогрев начал ждать показа полотна (Stopwatch). 0 — не ждёт.
        private long _warmupHiddenSinceTs;

        /// <summary>Прогрев запущен: страж следит, пока раскладка не догонит документ.</summary>
        private void ArmWarmupGuard()
        {
            _warmupSlowLogged = false;
            _warmupHiddenSinceTs = 0;

            _warmupGuardTimer ??= CreateWarmupGuardTimer();
            if (!_warmupGuardTimer.IsEnabled)
                _warmupGuardTimer.Start();
        }

        private DispatcherTimer CreateWarmupGuardTimer()
        {
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = WarmupGuardInterval };
            timer.Tick += (_, _) => CheckWarmupGuard();
            return timer;
        }

        /// <summary>Прогрев оборван не своим окончанием.</summary>
        private void NoteWarmupInterrupted(string reason)
        {
            if (!_layoutWarmupActive) return;

            _warmupInterrupted = true;
            _logger.Debug("[WARMUP] Прогрев оборван: {Reason}; абзацев в кеше {Cache}", reason, _layoutCache.Count);
        }

        /// <summary>
        /// Полотно снова на экране: оборванный прогрев продолжается сразу, не дожидаясь
        /// прохода measure, который во время жеста масштаба пересборку пропускает.
        /// </summary>
        private void ResumeInterruptedWarmup()
        {
            if (!_warmupInterrupted) return;

            Dispatcher.UIThread.Post(() =>
            {
                if (!_warmupInterrupted || !_attachedToTree) return;
                _warmupInterrupted = false;

                if (DocVm is null || _layoutWarmupActive || LayoutsMatchCurrentState()) return;

                _logger.Debug("[WARMUP] Оборванный прогрев продолжается");
                RebuildLayouts();
                InvalidateMeasure();
                InvalidateFull();
            }, DispatcherPriority.Background);
        }

        private void CheckWarmupGuard()
        {
            if (DocVm is null || !_attachedToTree || DocVm.Paragraphs.Count == 0)
            {
                _warmupGuardTimer?.Stop();
                return;
            }

            if (_layoutWarmupActive)
            {
                LogSlowWarmup();
                return;
            }

            // Жест масштаба сам пересоберёт раскладку, когда закончится.
            if (_zooming) return;

            if (LayoutsMatchCurrentState())
            {
                _warmupGuardTimer?.Stop();
                return;
            }

            _logger.Information(
                "[WARMUP] Раскладка отстала от документа, а прогрева нет — пересборка: листов {Pages}, абзацев в кеше {Cache}",
                _pages.Count, _layoutCache.Count);

            RebuildLayouts();
            InvalidateMeasure();
            InvalidateFull();
        }

        private void LogSlowWarmup()
        {
            if (_warmupSlowLogged || _warmupStartedTs == 0) return;

            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - _warmupStartedTs) * 1000.0
                        / System.Diagnostics.Stopwatch.Frequency;
            if (ms < WarmupSlowLogMs) return;

            _warmupSlowLogged = true;
            _logger.Warning(
                "[WARMUP] Прогрев идёт {Ms:F0} мс: виден {Visible}, ждёт показа {Hidden}, абзацев {Count}, в кеше {Cache}, жест масштаба {Zooming}",
                ms, IsEffectivelyVisible, _warmupHiddenSinceTs != 0,
                DocVm?.Paragraphs.Count ?? 0, _layoutCache.Count, _zooming);
        }

        /// <summary>Проход прогрева застал полотно скрытым.</summary>
        private void NoteWarmupHidden()
        {
            if (_warmupHiddenSinceTs == 0)
            {
                _warmupHiddenSinceTs = System.Diagnostics.Stopwatch.GetTimestamp();
                _logger.Debug("[WARMUP] Прогрев ждёт показа полотна");
            }
        }

        /// <summary>Проход прогрева застал полотно видимым.</summary>
        private void NoteWarmupVisible()
        {
            if (_warmupHiddenSinceTs == 0) return;

            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - _warmupHiddenSinceTs) * 1000.0
                        / System.Diagnostics.Stopwatch.Frequency;
            _warmupHiddenSinceTs = 0;
            _logger.Debug("[WARMUP] Полотно показано, прогрев продолжается (ждал {Ms:F0} мс)", ms);
        }
    }
}
