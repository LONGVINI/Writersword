using System;
using System.Diagnostics;
using System.Threading;
using Avalonia.Threading;

namespace Writersword.Infrastructure.Diagnostics
{
    /// <summary>
    /// ВРЕМЕННАЯ диагностика отклика. Снимает две вещи:
    /// <para>
    /// 1. Разбивку по этапам одного переключения вкладки или воркмода: сколько
    /// миллисекунд съел каждый шаг и когда UI-поток освободился.
    /// </para>
    /// <para>
    /// 2. Сторож UI-потока: таймер тикает каждые 50 мс, и если между тиками прошло
    /// заметно больше, значит UI-поток всё это время был занят и не обрабатывал ввод.
    /// В сообщение попадает последний отмеченный этап — по нему видно, на чём именно
    /// приложение встало, даже если код этого места ничего не логирует.
    /// </para>
    /// <para>
    /// Всё пишется уровнем Warning, чтобы попадать в лог при MinimumLevel = Warning
    /// из appsettings.json. Источник в логе — SwitchProfiler. Убрать вместе со всеми
    /// вызовами, когда причина задержек будет найдена.
    /// </para>
    /// </summary>
    public static class SwitchProfiler
    {
        private static readonly Serilog.ILogger Log =
            Serilog.Log.ForContext("SourceContext", "SwitchProfiler");

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly Stopwatch StallClock = Stopwatch.StartNew();

        private static string _scenario = "idle";
        private static long _scenarioStartMs;
        private static long _lastMarkMs;
        private static long _lastMarkAllocated;
        private static string _lastStage = "idle";

        private static DispatcherTimer? _stallTimer;
        private static long _lastTickMs;
        private static int _lastGen2;
        private static TimeSpan _lastGcPause;
        private static long _lastTickAllocated;

        private static Timer? _resourceTimer;

        // Последние записанные показатели памяти: строка пишется, только когда
        // они заметно изменились или давно не писались.
        private static double _loggedWorkingSetMb = -1;
        private static double _loggedManagedMb = -1;
        private static int _loggedGen2 = -1;
        private static long _loggedAtMs;
        private static long _loggedAllocated = -1;

        private const double MemoryLogStepMb = 300;
        private const long MemoryLogEveryMs = 5 * 60 * 1000;

        /// <summary>Последний отмеченный этап — попадает в сообщения сторожа.</summary>
        public static string LastStage => _lastStage;

        /// <summary>Начало сценария (клик по вкладке, клик по воркмоду).</summary>
        public static void Begin(string scenario)
        {
            _scenario = scenario;
            _scenarioStartMs = Clock.ElapsedMilliseconds;
            _lastMarkMs = _scenarioStartMs;
            _lastMarkAllocated = GC.GetTotalAllocatedBytes(false);
            _lastStage = scenario + ": начало";

            Log.Warning("=== {Scenario}: НАЧАЛО", scenario);
        }

        /// <summary>Этап внутри сценария: сколько прошло с прошлой отметки и с начала.</summary>
        public static void Mark(string stage)
        {
            long now = Clock.ElapsedMilliseconds;
            long delta = now - _lastMarkMs;
            long total = now - _scenarioStartMs;
            _lastMarkMs = now;
            _lastStage = _scenario + ": " + stage;

            // Сколько памяти выделено с прошлой отметки — по всему процессу. Этап,
            // который создаёт сотни мегабайт мусора, виден здесь, даже если сам он
            // быстрый: платить за этот мусор потом будет сборщик.
            long allocated = GC.GetTotalAllocatedBytes(false);
            long allocatedMb = (allocated - _lastMarkAllocated) / (1024 * 1024);
            _lastMarkAllocated = allocated;

            Log.Warning("    [{Scenario}] {Stage}: +{Delta}ms (всего {Total}ms), выделено {AllocatedMb} МБ",
                _scenario, stage, delta, total, allocatedMb);
        }

        /// <summary>
        /// Отметка «UI-поток освободился»: задание ставится в очередь диспетчера с
        /// фоновым приоритетом и выполняется, только когда обработаны раскладка,
        /// отрисовка и ввод. Разница со временем постановки — это и есть то, сколько
        /// пользователь ждал реального кадра.
        /// </summary>
        public static void MarkWhenIdle(string stage)
        {
            string scenario = _scenario;
            long postedAt = Clock.ElapsedMilliseconds;
            long scenarioStart = _scenarioStartMs;

            Dispatcher.UIThread.Post(() =>
            {
                long now = Clock.ElapsedMilliseconds;
                Log.Warning(
                    "    [{Scenario}] {Stage}: UI освободился через {Wait}ms после постановки (всего {Total}ms)",
                    scenario, stage, now - postedAt, now - scenarioStart);
            }, DispatcherPriority.Background);
        }

        /// <summary>
        /// Отметить этап без записи в журнал: только чтобы сторож UI-потока назвал его,
        /// если UI встанет. Для частых операций вне сценариев (отложенные сохранения,
        /// тики кеша), которые иначе засоряли бы журнал.
        /// </summary>
        public static void SetStage(string stage) => _lastStage = stage;

        /// <summary>Конец сценария.</summary>
        public static void End()
        {
            Log.Warning("=== {Scenario}: КОНЕЦ — {Total}ms",
                _scenario, Clock.ElapsedMilliseconds - _scenarioStartMs);
        }

        /// <summary>
        /// Сторож UI-потока. Таймер с фоновым приоритетом: тик выполняется только
        /// когда UI-поток свободен, поэтому увеличенный промежуток между тиками
        /// прямо означает заморозку интерфейса на это время.
        /// </summary>
        public static void StartStallWatchdog(int thresholdMs = 120)
        {
            if (_stallTimer != null) return;

            _lastTickMs = StallClock.ElapsedMilliseconds;
            _lastGen2 = GC.CollectionCount(2);
            _lastGcPause = GC.GetTotalPauseDuration();
            _lastTickAllocated = GC.GetTotalAllocatedBytes(false);

            _stallTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(50),
                DispatcherPriority.Background,
                (_, _) =>
                {
                    long now = StallClock.ElapsedMilliseconds;
                    long gap = now - _lastTickMs;
                    _lastTickMs = now;

                    // Паузы сборщика мусора за этот промежуток: если заморозка почти
                    // целиком из них — причина в памяти, а не в коде этапа.
                    int gen2 = GC.CollectionCount(2);
                    var pause = GC.GetTotalPauseDuration();
                    int gen2Delta = gen2 - _lastGen2;
                    double pauseMs = (pause - _lastGcPause).TotalMilliseconds;
                    _lastGen2 = gen2;
                    _lastGcPause = pause;

                    long allocated = GC.GetTotalAllocatedBytes(false);
                    long allocatedMb = (allocated - _lastTickAllocated) / (1024 * 1024);
                    _lastTickAllocated = allocated;

                    if (gap >= thresholdMs)
                    {
                        Log.Warning(
                            "UI-ПОТОК БЫЛ ЗАНЯТ {Gap}ms подряд. Последний этап: {Stage}. "
                            + "Из них паузы GC {GcPause:F0}ms, полных сборок {Gen2}, выделено за это время {AllocatedMb} МБ",
                            gap, _lastStage, pauseMs, gen2Delta, allocatedMb);
                    }
                });

            _stallTimer.Start();
            Log.Warning("Сторож UI-потока запущен, порог {Threshold}ms", thresholdMs);
        }

        /// <summary>
        /// Раз в 10 секунд снимает память процесса и состояние сборщика мусора, а
        /// пишет в журнал, только когда память сдвинулась на MemoryLogStepMb и
        /// больше, прошла полная сборка — или раз в пять минут. Прежде строка
        /// писалась каждые 10 секунд и забивала журнал одинаковыми цифрами.
        /// Считается на фоновом потоке, UI не трогает.
        /// </summary>
        public static void StartResourceMonitor()
        {
            if (_resourceTimer != null) return;

            _resourceTimer = new Timer(
                _ =>
                {
                    try
                    {
                        using var process = Process.GetCurrentProcess();
                        double workingSetMb = process.WorkingSet64 / 1024.0 / 1024.0;
                        double managedMb = GC.GetTotalMemory(false) / 1024.0 / 1024.0;
                        int gen2 = GC.CollectionCount(2);
                        long nowMs = Clock.ElapsedMilliseconds;

                        bool changed = _loggedWorkingSetMb < 0
                                       || Math.Abs(workingSetMb - _loggedWorkingSetMb) >= MemoryLogStepMb
                                       || Math.Abs(managedMb - _loggedManagedMb) >= MemoryLogStepMb
                                       || gen2 != _loggedGen2
                                       || nowMs - _loggedAtMs >= MemoryLogEveryMs;
                        if (!changed) return;

                        // Выделено с прошлой строки: куча — это живые данные плюс ещё не
                        // собранный мусор, и большая куча при большом выделении значит
                        // мусор, а не утечку.
                        long allocated = GC.GetTotalAllocatedBytes(false);
                        long allocatedMb = _loggedAllocated < 0 ? 0 : (allocated - _loggedAllocated) / (1024 * 1024);

                        _loggedWorkingSetMb = workingSetMb;
                        _loggedManagedMb = managedMb;
                        _loggedGen2 = gen2;
                        _loggedAtMs = nowMs;
                        _loggedAllocated = allocated;

                        // Живые данные после последней сборки — то, что действительно занято.
                        double liveAfterGcMb = GC.GetGCMemoryInfo(GCKind.Any).HeapSizeBytes / 1024.0 / 1024.0;

                        Log.Warning(
                            "ПАМЯТЬ: процесс {WorkingSet:F0} МБ, управляемая куча {Managed:F0} МБ "
                            + "(после последней сборки {LiveAfterGc:F0} МБ), выделено с прошлой строки {AllocatedMb} МБ, "
                            + "сборки gen0={Gen0} gen1={Gen1} gen2={Gen2}, потоков {Threads}",
                            workingSetMb, managedMb, liveAfterGcMb, allocatedMb,
                            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
                            process.Threads.Count);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Не удалось снять показатели памяти");
                    }
                },
                null,
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(10));
        }
    }
}
