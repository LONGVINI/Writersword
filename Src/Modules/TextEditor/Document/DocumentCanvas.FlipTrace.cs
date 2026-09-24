using System;
using System.Diagnostics;
using System.Threading;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Трассировка переворота листа в книге: временная диагностика моргания при
    /// нажатии на страницу.
    ///
    /// Пишет в лог строки с префиксом [FLIP]: события руки и переворота (нажатие,
    /// движение, отпускание, начало и конец переворота, снимки, уголок, полная
    /// инвалидация) и каждый кадр — каким путём он нарисован, сколько шёл, сколько
    /// прошло с предыдущего и в каком состоянии был переворот.
    ///
    /// Кадры пишутся только в окне трассировки: оно открывается нажатием, началом
    /// или концом переворота и держится ещё две секунды после последнего такого
    /// события. В покое лог не растёт.
    ///
    /// Выключается одним полем FlipTraceEnabled.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        private static readonly bool FlipTraceEnabled = true;

        // Сколько секунд после последнего события руки или переворота ещё пишутся кадры.
        private const double FlipTraceTailSeconds = 2.0;

        // Отметка Stopwatch, до которой окно трассировки открыто.
        private long _flipTraceUntilTs;

        // Отметка начала предыдущего записанного кадра — для промежутка между кадрами.
        private long _flipTraceLastFrameTs;

        // Номер кадра в окне трассировки: по нему удобно сопоставлять события и кадры.
        private int _flipTraceFrameNo;

        // Левая страница разворота, для которого был снят последний полный кадр.
        // Кадр из кэша кладёт именно его: если он расходится с текущим разворотом,
        // на экран попадает прежняя картинка.
        private int _flipTraceDisplayLeft = -1;

        private static long FlipTraceNow() => Stopwatch.GetTimestamp();

        private static double FlipTraceMs(long fromTs, long toTs)
            => (toTs - fromTs) * 1000.0 / Stopwatch.Frequency;

        /// <summary>Открыто ли окно трассировки.</summary>
        private bool FlipTraceActive
        {
            get
            {
                if (!FlipTraceEnabled || !SpreadMode) return false;
                if (_spreadFlipDir != 0 || _singleSlideDir != 0) return true;
                return Stopwatch.GetTimestamp() < Interlocked.Read(ref _flipTraceUntilTs);
            }
        }

        /// <summary>Открывает или продлевает окно трассировки.</summary>
        private void FlipTraceArm()
        {
            long until = Stopwatch.GetTimestamp() + (long)(FlipTraceTailSeconds * Stopwatch.Frequency);
            Interlocked.Exchange(ref _flipTraceUntilTs, until);
        }

        /// <summary>
        /// Событие, открывающее окно трассировки: нажатие, начало, отпускание и конец
        /// переворота, листание клавишами и колесом.
        /// </summary>
        private void FlipTraceStart(string what)
        {
            if (!FlipTraceEnabled || !SpreadMode) return;

            if (!FlipTraceActive) Interlocked.Exchange(ref _flipTraceFrameNo, 0);
            FlipTraceArm();

            _logger.Debug("[FLIP] ev {What} | {State}", what, FlipTraceState());
        }

        /// <summary>
        /// Попутное событие: пишется, только если окно уже открыто, и окно не продлевает.
        /// </summary>
        private void FlipTraceEvent(string what)
        {
            if (!FlipTraceActive) return;

            _logger.Debug("[FLIP] ev {What} | {State}", what, FlipTraceState());
        }

        /// <summary>
        /// Кадр: каким путём нарисован и сколько шёл. Вызывается в конце каждого пути
        /// RenderWithSKCanvas, на потоке отрисовки.
        /// </summary>
        private void FlipTraceFrame(string path, long startTs)
        {
            bool full = path == "full" || path == "fallback";

            // Разворот полного кадра запоминается всегда, а не только в окне: кадр из
            // кэша, пришедший в окне, может класть снимок, сделанный задолго до него.
            if (full && SpreadMode) _flipTraceDisplayLeft = _spreadLeftPage;

            if (!FlipTraceActive) return;

            long now = Stopwatch.GetTimestamp();
            double frameMs = FlipTraceMs(startTs, now);
            double gapMs = _flipTraceLastFrameTs == 0 ? -1.0 : FlipTraceMs(_flipTraceLastFrameTs, startTs);
            _flipTraceLastFrameTs = startTs;

            int no = Interlocked.Increment(ref _flipTraceFrameNo);

            string line = FormattableString.Invariant(
                $"frame #{no} {path} {frameMs:0.0}ms gap={gapMs:0.0}ms disp={_flipTraceDisplayLeft}");

            _logger.Debug("[FLIP] {Line} | {State}", line, FlipTraceState());
        }

        /// <summary>Снимок состояния переворота одной строкой.</summary>
        private string FlipTraceState()
        {
            int ul = -1, ur = -1;
            try
            {
                (ul, ur) = SpreadUnderPages();
            }
            catch (Exception)
            {
                // Состояние читается с чужого потока и может быть на полпути смены.
            }

            // Замок снимков берётся без ожидания: трассировка не должна вставать в очередь
            // к потоку, который прямо сейчас снимает страницу.
            string snap = "?/?";
            if (Monitor.TryEnter(_spreadCacheLock))
            {
                try
                {
                    bool hasFront = _spreadFlyFront >= 0 && _spreadPageCache.ContainsKey(_spreadFlyFront);
                    bool hasBack = _spreadFlyBack >= 0 && _spreadPageCache.ContainsKey(_spreadFlyBack);
                    snap = (hasFront ? "F" : "-") + "/" + (hasBack ? "B" : "-");
                }
                finally
                {
                    Monitor.Exit(_spreadCacheLock);
                }
            }

            return string.Concat(
                FormattableString.Invariant($"dir={_spreadFlipDir} ang={_spreadFlipAngle:0.00} aim={_spreadDragTargetAngle:0.00} "),
                FormattableString.Invariant($"lifted={SpreadLeafLifted} drag={_spreadDragging} moved={_spreadDragMoved} rel={_spreadReleasing} "),
                FormattableString.Invariant($"left={_spreadLeftPage} target={_spreadFlipTargetLeft} fly={_spreadFlyFront}/{_spreadFlyBack} "),
                FormattableString.Invariant($"snap={snap} under={ul}/{ur} hint={_spreadCornerHint:0.00} "),
                FormattableString.Invariant($"caretOnly={_caretOnlyRedraw} dirty={_contentDirty} trans={_isTransitioning} "),
                FormattableString.Invariant($"th={Environment.CurrentManagedThreadId}"));
        }
    }
}
