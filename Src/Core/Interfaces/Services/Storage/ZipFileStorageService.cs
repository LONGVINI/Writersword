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
    /// Держит ZIP открытым в режиме Update для быстрой записи
    /// </summary>
    public class ZipFileStorageService : IProjectFileStorage, IDisposable
    {
        private readonly string _zipFilePath;
        private ZipArchive? _archive;
        private readonly Dictionary<string, byte[]> _pendingWrites = new();
        private bool _isDisposed = false;

        public ZipFileStorageService(string zipFilePath)
        {
            _zipFilePath = zipFilePath;

            // Открываем ZIP в режиме Update
            if (File.Exists(zipFilePath))
            {
                var fileStream = new FileStream(zipFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                _archive = new ZipArchive(fileStream, ZipArchiveMode.Update, leaveOpen: false);
                Console.WriteLine($"[ZipFileStorage] Opened ZIP: {zipFilePath}");
            }
            else
            {
                Console.WriteLine($"[ZipFileStorage] WARNING: ZIP file not found: {zipFilePath}");
            }
        }

        /// <summary>
        /// Записать файл в ZIP
        /// ВАЖНО: Запись происходит сразу в ZIP (не в память)
        /// </summary>
        public void WriteFile(string relativePath, byte[] data)
        {
            if (_isDisposed)
            {
                Console.WriteLine("[ZipFileStorage] ERROR: Cannot write, storage is disposed");
                return;
            }

            if (_archive == null)
            {
                Console.WriteLine("[ZipFileStorage] ERROR: Archive is null");
                return;
            }

            try
            {
                // Нормализуем путь (заменяем \ на /)
                relativePath = relativePath.Replace("\\", "/");

                // Удаляем старую запись если существует
                var existingEntry = _archive.GetEntry(relativePath);
                if (existingEntry != null)
                {
                    existingEntry.Delete();
                    Console.WriteLine($"[ZipFileStorage] Deleted old entry: {relativePath}");
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
        }

        /// <summary>
        /// Прочитать файл из ZIP
        /// </summary>
        public byte[]? ReadFile(string relativePath)
        {
            if (_isDisposed || _archive == null)
            {
                Console.WriteLine("[ZipFileStorage] ERROR: Cannot read, storage is disposed or null");
                return null;
            }

            try
            {
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
        }

        /// <summary>
        /// Проверить существует ли файл
        /// </summary>
        public bool FileExists(string relativePath)
        {
            if (_isDisposed || _archive == null)
                return false;

            relativePath = relativePath.Replace("\\", "/");
            return _archive.GetEntry(relativePath) != null;
        }

        /// <summary>
        /// Удалить файл из ZIP
        /// </summary>
        public void DeleteFile(string relativePath)
        {
            if (_isDisposed || _archive == null)
            {
                Console.WriteLine("[ZipFileStorage] ERROR: Cannot delete, storage is disposed or null");
                return;
            }

            try
            {
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
        }

        /// <summary>
        /// Получить список файлов в папке
        /// </summary>
        public IEnumerable<string> GetFiles(string relativePath)
        {
            if (_isDisposed || _archive == null)
                return Enumerable.Empty<string>();

            relativePath = relativePath.Replace("\\", "/").TrimEnd('/') + "/";

            return _archive.Entries
                .Where(e => e.FullName.StartsWith(relativePath) && !e.FullName.EndsWith("/"))
                .Select(e => e.FullName);
        }

        /// <summary>
        /// Закрыть ZIP архив
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