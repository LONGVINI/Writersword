using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Диагностика производительности выделения и отрисовки.
    ///
    /// Замеры копятся в корзины (число, сумма, максимум) и раз в секунду, если за
    /// секунду что-то происходило, уходят в лог одной строкой с префиксом [PERF].
    /// Пишут и UI-поток (ввод), и render-поток (кадры), поэтому корзины под локом.
    ///
    /// Счётчики без времени (причины полного рендера, кто позвал InvalidateFull)
    /// идут той же строкой: у них только число.
    ///
    /// Выключается одним полем PerfEnabled — тогда замеры не делаются вовсе.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        private static readonly bool PerfEnabled = true;

        private sealed class PerfBucket
        {
            public int Count;
            public double TotalMs;
            public double MaxMs;
            public bool Timed;
        }

        private readonly object _perfLock = new();
        private readonly Dictionary<string, PerfBucket> _perfBuckets = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _perfInfo = new(StringComparer.Ordinal);
        private long _perfWindowStartTs = Stopwatch.GetTimestamp();

        // Метка предыдущего движения мыши при протяжке выделения. По промежутку между
        // соседними событиями видно, успевает ли UI-поток: при перегрузке события
        // приходят реже, чем двигается рука. Сбрасывается на отпускание кнопки.
        private long _perfLastMoveTs;

        private static long PerfNow() => PerfEnabled ? Stopwatch.GetTimestamp() : 0L;

        private static double PerfMs(long startTs, long endTs)
            => (endTs - startTs) * 1000.0 / Stopwatch.Frequency;

        /// <summary>Замер интервала от startTs до текущего момента.</summary>
        private void PerfTime(string key, long startTs)
        {
            if (!PerfEnabled) return;

            long now = Stopwatch.GetTimestamp();
            double ms = PerfMs(startTs, now);

            lock (_perfLock)
            {
                if (!_perfBuckets.TryGetValue(key, out var b))
                {
                    b = new PerfBucket();
                    _perfBuckets[key] = b;
                }

                b.Timed = true;
                b.Count++;
                b.TotalMs += ms;
                if (ms > b.MaxMs) b.MaxMs = ms;
            }

            PerfFlushIfDue(now);
        }

        /// <summary>Счётчик события без времени.</summary>
        private void PerfCount(string key)
        {
            if (!PerfEnabled) return;

            lock (_perfLock)
            {
                if (!_perfBuckets.TryGetValue(key, out var b))
                {
                    b = new PerfBucket();
                    _perfBuckets[key] = b;
                }

                b.Count++;
            }

            PerfFlushIfDue(Stopwatch.GetTimestamp());
        }

        /// <summary>Последнее значение параметра кадра (размер битмапа, число листов и т.п.).</summary>
        private void PerfInfo(string key, string value)
        {
            if (!PerfEnabled) return;

            lock (_perfLock)
            {
                _perfInfo[key] = value;
            }
        }

        private void PerfFlushIfDue(long nowTs)
        {
            string? line = null;

            lock (_perfLock)
            {
                if (PerfMs(_perfWindowStartTs, nowTs) < 1000.0) return;

                _perfWindowStartTs = nowTs;
                if (_perfBuckets.Count == 0) return;

                // Окно без действий человека (только мигание каретки и прочие фоновые
                // кадры) в лог не идёт: иначе редактор в фокусе писал бы строку в секунду.
                if (!_perfBuckets.Keys.Any(k => k.StartsWith("ui.", StringComparison.Ordinal)))
                {
                    _perfBuckets.Clear();
                    return;
                }

                var sb = new StringBuilder("[PERF]");
                foreach (var kv in _perfBuckets.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    var b = kv.Value;
                    sb.Append(' ').Append(kv.Key).Append('=');
                    if (b.Timed)
                    {
                        sb.Append(b.Count.ToString(CultureInfo.InvariantCulture))
                          .Append("x avg ")
                          .Append((b.TotalMs / Math.Max(1, b.Count)).ToString("0.00", CultureInfo.InvariantCulture))
                          .Append(" max ")
                          .Append(b.MaxMs.ToString("0.00", CultureInfo.InvariantCulture))
                          .Append(" sum ")
                          .Append(b.TotalMs.ToString("0", CultureInfo.InvariantCulture))
                          .Append("ms;");
                    }
                    else
                    {
                        sb.Append(b.Count.ToString(CultureInfo.InvariantCulture)).Append(';');
                    }
                }

                foreach (var kv in _perfInfo.OrderBy(k => k.Key, StringComparer.Ordinal))
                    sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value).Append(';');

                _perfBuckets.Clear();
                line = sb.ToString();
            }

            _logger.Information("{PerfLine}", line);
        }
    }
}
