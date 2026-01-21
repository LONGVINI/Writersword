using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Writersword.Core.Interfaces.Services;

namespace Writersword.Src.Infrastructure.Services.Storage
{
    /// <summary>
    /// Реализация работы с файлами внутри ZIP архива проекта
    /// DEBUG: Открывает/закрывает ZIP при каждой операции (не блокирует файл)
    /// RELEASE: Держит ZIP открытым в режиме Update для быстрой записи (оптимизация)
    /// </summary>
    public class ZipFileStorageService : IProjectFileStorage, IDisposable
    {
        private readonly string _zipFilePath;
        private ZipArchive? _archive;
        private bool _isDisposed = false;

#if DEBUG
        // DEBUG режим: не держим архив открытым
        private const bool KeepArchiveOpen = false;
#else
        // RELEASE режим: держим архив открытым для оптимизации
        private const bool KeepArchiveOpen = true;
#endif

#pragma warning disable CS0162 // Недостижимый код (ожидается в DEBUG/RELEASE режимах)
        public ZipFileStorageService(string zipFilePath)
        {
            _zipFilePath = zipFilePath;

            if (!File.Exists(zipFilePath))
            {
                Console.WriteLine($"[ZipFileStorage] WARNING: ZIP file not found: {zipFilePath}");
                return;
            }

            // В RELEASE режиме открываем архив сразу и держим открытым
            if (KeepArchiveOpen)
            {
                OpenArchive();
                Console.WriteLine($"[ZipFileStorage] Opened ZIP (RELEASE mode): {zipFilePath}");
            }
            else
            {
                Console.WriteLine($"[ZipFileStorage] Initialized (DEBUG mode): {zipFilePath}");
            }
        }
#pragma warning restore CS0162

        /// <summary>
        /// Открыть ZIP архив для работы
        /// </summary>
        private void OpenArchive()
        {
            if (_archive != null)
                return;

            var fileStream = new FileStream(_zipFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            _archive = new ZipArchive(fileStream, ZipArchiveMode.Update, leaveOpen: false);
        }

        /// <summary>
        /// Закрыть ZIP архив (только в DEBUG режиме)
        /// </summary>
        private void CloseArchive()
        {
            if (_archive != null && !KeepArchiveOpen)
            {
                _archive.Dispose();
                _archive = null;
            }
        }

        /// <summary>
        /// Записать файл в ZIP
        /// DEBUG: Открывает ZIP → пишет → закрывает (не блокирует файл)
        /// RELEASE: Пишет в уже открытый ZIP (быстро)
        /// </summary>
        public void WriteFile(string relativePath, byte[] data)
        {
            if (_isDisposed)
            {
                Console.WriteLine("[ZipFileStorage] ERROR: Cannot write, storage is disposed");
                return;
            }

            try
            {
                // В DEBUG режиме открываем архив перед записью
                if (!KeepArchiveOpen)
                {
                    OpenArchive();
                }

                if (_archive == null)
                {
                    Console.WriteLine("[ZipFileStorage] ERROR: Archive is null");
                    return;
                }

                // Нормализуем путь (заменяем \ на /)
                relativePath = relativePath.Replace("\\", "/");

                // Удаляем старую запись если существует
                var existingEntry = _archive.GetEntry(relativePath);
                if (existingEntry != null)
                {
                    existingEntry.Delete();
                }

                // Создаём новую запись
                var entry = _archive.CreateEntry(relativePath, CompressionLevel.Optimal);

                using (var stream = entry.Open())
                {
                    stream.Write(data, 0, data.Length);
                }

                Console.WriteLine($"[ZipFileStorage] Written: {relativePath} ({data.Length} bytes)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ZipFileStorage] Write error: {ex.Message}");
                throw;
            }
            finally
            {
                // В DEBUG режиме закрываем архив после записи
                CloseArchive();
            }
        }

        /// <summary>
        /// Прочитать файл из ZIP
        /// DEBUG: Открывает ZIP → читает → закрывает
        /// RELEASE: Читает из уже открытого ZIP
        /// </summary>
        public byte[]? ReadFile(string relativePath)
        {
            if (_isDisposed)
            {
                Console.WriteLine("[ZipFileStorage] ERROR: Cannot read, storage is disposed");
                return null;
            }

            try
            {
                // В DEBUG режиме открываем архив перед чтением
                if (!KeepArchiveOpen)
                {
                    OpenArchive();
                }

                if (_archive == null)
                {
                    Console.WriteLine("[ZipFileStorage] ERROR: Archive is null");
                    return null;
                }

                // Нормализуем путь
                relativePath = relativePath.Replace("\\", "/");

                var entry = _archive.GetEntry(relativePath);
                if (entry == null)
                {
                    Console.WriteLine($"[ZipFileStorage] File not found: {relativePath}");
                    return null;
                }

                using (var stream = entry.Open())
                using (var memoryStream = new MemoryStream())
                {
                    stream.CopyTo(memoryStream);
                    var data = memoryStream.ToArray();
                    Console.WriteLine($"[ZipFileStorage] Read: {relativePath} ({data.Length} bytes)");
                    return data;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ZipFileStorage] Read error: {ex.Message}");
                return null;
            }
            finally
            {
                // В DEBUG режиме закрываем архив после чтения
                CloseArchive();
            }
        }

        /// <summary>
        /// Проверить существует ли файл
        /// DEBUG: Открывает ZIP → проверяет → закрывает
        /// RELEASE: Проверяет в уже открытом ZIP
        /// </summary>
        public bool FileExists(string relativePath)
        {
            if (_isDisposed)
                return false;

            try
            {
                // В DEBUG режиме открываем архив
                if (!KeepArchiveOpen)
                {
                    OpenArchive();
                }

                if (_archive == null)
                    return false;

                relativePath = relativePath.Replace("\\", "/");
                return _archive.GetEntry(relativePath) != null;
            }
            finally
            {
                // В DEBUG режиме закрываем архив
                CloseArchive();
            }
        }

        /// <summary>
        /// Удалить файл из ZIP
        /// DEBUG: Открывает ZIP → удаляет → закрывает
        /// RELEASE: Удаляет из уже открытого ZIP
        /// </summary>
        public void DeleteFile(string relativePath)
        {
            if (_isDisposed)
            {
                Console.WriteLine("[ZipFileStorage] ERROR: Cannot delete, storage is disposed");
                return;
            }

            try
            {
                // В DEBUG режиме открываем архив
                if (!KeepArchiveOpen)
                {
                    OpenArchive();
                }

                if (_archive == null)
                {
                    Console.WriteLine("[ZipFileStorage] ERROR: Archive is null");
                    return;
                }

                relativePath = relativePath.Replace("\\", "/");
                var entry = _archive.GetEntry(relativePath);

                if (entry != null)
                {
                    entry.Delete();
                    Console.WriteLine($"[ZipFileStorage] Deleted: {relativePath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ZipFileStorage] Delete error: {ex.Message}");
                throw;
            }
            finally
            {
                // В DEBUG режиме закрываем архив
                CloseArchive();
            }
        }

        /// <summary>
        /// Получить список файлов в папке
        /// DEBUG: Открывает ZIP → читает список → закрывает
        /// RELEASE: Читает из уже открытого ZIP
        /// </summary>
        public IEnumerable<string> GetFiles(string relativePath)
        {
            if (_isDisposed)
                return Enumerable.Empty<string>();

            try
            {
                // В DEBUG режиме открываем архив
                if (!KeepArchiveOpen)
                {
                    OpenArchive();
                }

                if (_archive == null)
                    return Enumerable.Empty<string>();

                relativePath = relativePath.Replace("\\", "/").TrimEnd('/') + "/";

                return _archive.Entries
                    .Where(e => e.FullName.StartsWith(relativePath) && !e.FullName.EndsWith("/"))
                    .Select(e => e.FullName)
                    .ToList(); // Материализуем список до закрытия архива
            }
            finally
            {
                // В DEBUG режиме закрываем архив
                CloseArchive();
            }
        }

        /// <summary>
        /// Закрыть ZIP архив и освободить ресурсы
        /// ВАЖНО: Все изменения сохраняются при вызове Dispose()
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;

            if (_archive != null)
            {
                _archive.Dispose();
                _archive = null;
                Console.WriteLine($"[ZipFileStorage] Closed ZIP: {_zipFilePath}");
            }
        }
    }
}