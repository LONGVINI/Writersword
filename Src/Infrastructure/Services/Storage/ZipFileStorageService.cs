using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Writersword.Core.Interfaces.Services;

namespace Writersword.Infrastructure.Services.Storage
{
    /// <summary>
    /// Реализация работы с файлами внутри ZIP архива проекта
    /// DEBUG: Открывает/закрывает ZIP при каждой операции (не блокирует файл)
    /// RELEASE: Держит ZIP открытым в режиме Update для быстрой записи (оптимизация)
    /// </summary>
    public class ZipFileStorageService : IProjectFileStorage, IDisposable
    {
        private readonly ILogger<ZipFileStorageService> _logger;
        private readonly string _zipFilePath;
        private readonly object _sync = new object();
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
            _logger = App.Services.GetService<ILogger<ZipFileStorageService>>()!;
            _zipFilePath = zipFilePath;

            if (!File.Exists(zipFilePath))
            {
                _logger.LogWarning("ZIP file not found: {FilePath}", zipFilePath);
                return;
            }

            // В RELEASE режиме открываем архив сразу и держим открытым
            if (KeepArchiveOpen)
            {
                OpenArchive(ZipArchiveMode.Update);
                _logger.LogDebug("Opened ZIP (RELEASE mode): {FilePath}", zipFilePath);
            }
            else
            {
                _logger.LogDebug("Initialized (DEBUG mode): {FilePath}", zipFilePath);
            }
        }
#pragma warning restore CS0162

        /// <summary>
        /// Открыть ZIP архив для работы.
        /// Режим Read используется для операций чтения (не переписывает архив при закрытии),
        /// Update — для записи и удаления.
        /// </summary>
        private void OpenArchive(ZipArchiveMode mode)
        {
            if (_archive != null)
                return;

            var access = mode == ZipArchiveMode.Read
                ? FileAccess.Read
                : FileAccess.ReadWrite;

            var fileStream = new FileStream(_zipFilePath, FileMode.Open, access, FileShare.ReadWrite);
            _archive = new ZipArchive(fileStream, mode, leaveOpen: false);
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
                _logger.LogError("Cannot write, storage is disposed");
                return;
            }

            lock (_sync)
            {
                // Глобальный шлюз файла: запись не пересекается с хешированием
                // и чтением проекта из других сервисов.
                using var fileGate = ProjectFileLock.Acquire(_zipFilePath);
                try
                {
                    // В DEBUG режиме открываем архив перед записью
                    if (!KeepArchiveOpen)
                    {
                        OpenArchive(ZipArchiveMode.Update);
                    }

                    if (_archive == null)
                    {
                        _logger.LogError("Archive is null");
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

                    _logger.LogDebug("Written: {RelativePath} ({Size} bytes)", relativePath, data.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Write error");
                    throw;
                }
                finally
                {
                    // В DEBUG режиме закрываем архив после записи
                    CloseArchive();
                }
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
                _logger.LogError("Cannot read, storage is disposed");
                return null;
            }

            lock (_sync)
            {
                // Глобальный шлюз файла: чтение не пересекается с параллельной
                // записью или заменой файла (SaveToZipAsync → File.Move).
                using var fileGate = ProjectFileLock.Acquire(_zipFilePath);
                try
                {
                    // В DEBUG режиме открываем архив перед чтением
                    if (!KeepArchiveOpen)
                    {
                        OpenArchive(ZipArchiveMode.Read);
                    }

                    if (_archive == null)
                    {
                        _logger.LogError("Archive is null");
                        return null;
                    }

                    // Нормализуем путь
                    relativePath = relativePath.Replace("\\", "/");

                    var entry = _archive.GetEntry(relativePath);
                    if (entry == null)
                    {
                        _logger.LogDebug("File not found: {RelativePath}", relativePath);
                        return null;
                    }

                    using (var stream = entry.Open())
                    using (var memoryStream = new MemoryStream())
                    {
                        stream.CopyTo(memoryStream);
                        var data = memoryStream.ToArray();
                        _logger.LogDebug("Read: {RelativePath} ({Size} bytes)", relativePath, data.Length);
                        return data;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Read error");
                    return null;
                }
                finally
                {
                    // В DEBUG режиме закрываем архив после чтения
                    CloseArchive();
                }
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

            lock (_sync)
            {
                using var fileGate = ProjectFileLock.Acquire(_zipFilePath);
                try
                {
                    // В DEBUG режиме открываем архив
                    if (!KeepArchiveOpen)
                    {
                        OpenArchive(ZipArchiveMode.Read);
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
                _logger.LogError("Cannot delete, storage is disposed");
                return;
            }

            lock (_sync)
            {
                using var fileGate = ProjectFileLock.Acquire(_zipFilePath);
                try
                {
                    // В DEBUG режиме открываем архив
                    if (!KeepArchiveOpen)
                    {
                        OpenArchive(ZipArchiveMode.Update);
                    }

                    if (_archive == null)
                    {
                        _logger.LogError("Archive is null");
                        return;
                    }

                    relativePath = relativePath.Replace("\\", "/");
                    var entry = _archive.GetEntry(relativePath);

                    if (entry != null)
                    {
                        entry.Delete();
                        _logger.LogDebug("Deleted: {RelativePath}", relativePath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Delete error");
                    throw;
                }
                finally
                {
                    // В DEBUG режиме закрываем архив
                    CloseArchive();
                }
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

            lock (_sync)
            {
                using var fileGate = ProjectFileLock.Acquire(_zipFilePath);
                try
                {
                    // В DEBUG режиме открываем архив
                    if (!KeepArchiveOpen)
                    {
                        OpenArchive(ZipArchiveMode.Read);
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
        }

        /// <summary>
        /// Закрыть ZIP архив и освободить ресурсы
        /// Все изменения сохраняются при вызове Dispose()
        /// </summary>
        public void Dispose()
        {
            lock (_sync)
            {
                if (_isDisposed)
                    return;

                _isDisposed = true;

                if (_archive != null)
                {
                    // В режиме Update закрытие архива переписывает весь ZIP —
                    // выполняем под глобальным шлюзом файла.
                    using var fileGate = ProjectFileLock.Acquire(_zipFilePath);
                    _archive.Dispose();
                    _archive = null;
                    _logger.LogDebug("Closed ZIP: {FilePath}", _zipFilePath);
                }
            }
        }
    }
}