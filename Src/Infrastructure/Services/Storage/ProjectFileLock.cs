using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Writersword.Infrastructure.Services.Storage
{
    /// <summary>
    /// Глобальный шлюз доступа к файлу проекта (.writersword).
    ///
    /// Файл открывается из нескольких независимых мест: запись через
    /// ZipFileStorageService (workspace.json, конфиги), хеширование в
    /// ZipCacheService и ProjectWorkflow, чтение/сохранение в ZipProjectService.
    /// Каждое место раньше открывало файл со своими режимами FileShare,
    /// из-за чего параллельные операции падали с IOException
    /// "file is being used by another process".
    ///
    /// Шлюз выстраивает все операции над одним файлом в очередь:
    /// на каждый нормализованный путь — один SemaphoreSlim(1,1).
    /// SemaphoreSlim не привязан к потоку, поэтому его можно удерживать
    /// через await (в отличие от Monitor/lock).
    ///
    /// Использование:
    ///     using (ProjectFileLock.Acquire(path)) { ... }
    ///     using (await ProjectFileLock.AcquireAsync(path)) { ... }
    /// </summary>
    public static class ProjectFileLock
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _gates =
            new(StringComparer.OrdinalIgnoreCase);

        private static SemaphoreSlim GetGate(string filePath)
        {
            string key;
            try { key = Path.GetFullPath(filePath); }
            catch { key = filePath; }
            return _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        }

        /// <summary>
        /// Синхронно занять шлюз файла. Освобождение — через Dispose результата.
        /// </summary>
        public static IDisposable Acquire(string filePath)
        {
            var gate = GetGate(filePath);
            gate.Wait();
            return new Releaser(gate);
        }

        /// <summary>
        /// Асинхронно занять шлюз файла. Освобождение — через Dispose результата.
        /// Держать можно через await — семафор не привязан к потоку.
        /// </summary>
        public static async Task<IDisposable> AcquireAsync(string filePath)
        {
            var gate = GetGate(filePath);
            await gate.WaitAsync().ConfigureAwait(false);
            return new Releaser(gate);
        }

        private sealed class Releaser : IDisposable
        {
            private SemaphoreSlim? _gate;

            public Releaser(SemaphoreSlim gate) => _gate = gate;

            public void Dispose()
            {
                // Interlocked гарантирует однократный Release даже при
                // повторном вызове Dispose.
                var gate = Interlocked.Exchange(ref _gate, null);
                gate?.Release();
            }
        }
    }
}
