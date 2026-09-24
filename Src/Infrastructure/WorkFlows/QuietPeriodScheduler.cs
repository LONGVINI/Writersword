using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Writersword.Infrastructure.WorkFlows
{
    /// <summary>
    /// Откладывает тяжёлую работу, которую порождает переключение вкладок и воркмодов
    /// (сохранение кеша и workspace.json ушедшей вкладки), до момента, когда
    /// пользователь перестал переключаться.
    /// <para>
    /// Каждое переключение сдвигает момент выполнения на QuietPeriod вперёд. Пока
    /// пользователь кликает по вкладкам, ничего тяжёлого не запускается вообще, а
    /// задания с одним ключом схлопываются в одно: десять уходов с вкладки дают одно
    /// сохранение, а не десять.
    /// </para>
    /// <para>
    /// Зачем это нужно. Сбор данных модулей снимает снимок модели на UI-потоке (у
    /// TextEditor — поиск изменений и глубокий клон всего документа). При быстрых
    /// переключениях такие снимки выстраивались в очередь десятками и замораживали
    /// интерфейс на десятки секунд. Прервать уже идущий снимок нельзя: поток не
    /// останавливается посреди метода, отмена в .NET только добровольная. Поэтому
    /// работа не запускается, пока есть шанс, что она окажется лишней.
    /// </para>
    /// <para>
    /// Если пользователь снова начал переключаться, пока очередь выполняется,
    /// оставшиеся задания ждут следующей паузы. Уже начатое задание доводится до конца.
    /// </para>
    /// Все вызовы — только с UI-потока. IsBusy можно читать с любого потока.
    /// </summary>
    public static class QuietPeriodScheduler
    {
        /// <summary>Сколько должно пройти без переключений, чтобы запустить отложенное.</summary>
        public static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(1500);

        private static readonly Serilog.ILogger Log =
            Serilog.Log.ForContext("SourceContext", "QuietPeriodScheduler");

        private static readonly Dictionary<string, Func<Task>> _pending = new(StringComparer.Ordinal);
        private static readonly List<string> _order = new();

        private static DispatcherTimer? _timer;
        private static long _lastActivityTicks = DateTime.MinValue.Ticks;
        private static bool _running;

        /// <summary>
        /// Пользователь переключался меньше QuietPeriod назад: тяжёлую работу стоит отложить.
        /// Читается и с фоновых потоков (периодический кеш).
        /// </summary>
        public static bool IsBusy =>
            DateTime.UtcNow.Ticks - System.Threading.Interlocked.Read(ref _lastActivityTicks)
            < QuietPeriod.Ticks;

        /// <summary>Отметить переключение: всё отложенное сдвигается на QuietPeriod.</summary>
        public static void NotifyActivity()
        {
            System.Threading.Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
            RestartTimer();
        }

        /// <summary>
        /// Поставить задание. Задание с тем же ключом заменяет прежнее, ещё не выполненное:
        /// сохраняется самое свежее состояние, промежуточные не нужны.
        /// </summary>
        public static void Schedule(string key, Func<Task> work)
        {
            if (!_pending.ContainsKey(key))
                _order.Add(key);

            _pending[key] = work;
            NotifyActivity();
        }

        /// <summary>
        /// Снять невыполненное задание. Нужно при закрытии вкладки: отложенное сохранение
        /// кеша после закрытия проекта воскресило бы уже удалённую точку восстановления.
        /// </summary>
        public static void Cancel(string key)
        {
            if (_pending.Remove(key))
                _order.Remove(key);
        }

        /// <summary>Есть ли невыполненное задание с этим ключом.</summary>
        public static bool HasPending(string key) => _pending.ContainsKey(key);

        /// <summary>Выполнить всё отложенное сразу, не дожидаясь паузы.</summary>
        public static async Task FlushAsync()
        {
            _timer?.Stop();

            while (_order.Count > 0)
            {
                var key = _order[0];
                _order.RemoveAt(0);

                if (!_pending.Remove(key, out var work))
                    continue;

                await RunOneAsync(key, work);
            }
        }

        private static void RestartTimer()
        {
            if (_timer == null)
            {
                _timer = new DispatcherTimer { Interval = QuietPeriod };
                _timer.Tick += async (_, _) =>
                {
                    _timer!.Stop();
                    await RunPendingAsync();
                };
            }

            _timer.Stop();

            if (_order.Count > 0)
                _timer.Start();
        }

        private static async Task RunPendingAsync()
        {
            if (_running)
                return;

            _running = true;
            try
            {
                while (_order.Count > 0)
                {
                    // Пользователь снова переключается — остаток ждёт следующей паузы.
                    if (IsBusy)
                    {
                        RestartTimer();
                        return;
                    }

                    var key = _order[0];
                    _order.RemoveAt(0);

                    if (!_pending.Remove(key, out var work))
                        continue;

                    await RunOneAsync(key, work);
                }
            }
            finally
            {
                _running = false;
            }
        }

        private static async Task RunOneAsync(string key, Func<Task> work)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            Writersword.Infrastructure.Diagnostics.SwitchProfiler.SetStage("отложенное задание " + key);

            try
            {
                await work();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Deferred work failed: {Key}", key);
            }
            finally
            {
                stopwatch.Stop();
                Writersword.Infrastructure.Diagnostics.SwitchProfiler.SetStage(
                    "после отложенного задания " + key);

                // Диагностика: сколько заняло отложенное задание целиком (не только
                // UI-поток). Долгие задания видны рядом с сообщениями сторожа UI.
                if (stopwatch.ElapsedMilliseconds >= 100)
                    Log.Warning("Deferred work {Key} took {ElapsedMs}ms", key, stopwatch.ElapsedMilliseconds);
            }
        }
    }
}
