using System;
using System.Threading;
using System.Threading.Tasks;
using Writersword.Core.Models.Sync;

namespace Writersword.Core.Interfaces.Services.Storage
{
    /// <summary>
    /// Транспорт удалённого хранилища. Оперирует непрозрачными байтами и
    /// ключами — ни о проектах, ни о шифровании не знает.
    ///
    /// Смысл этого интерфейса в том, что переезд с чужого WebDAV на
    /// собственный сервер не должен трогать ничего, кроме одной реализации.
    /// </summary>
    public interface IRemoteStorage : IDisposable
    {
        /// <summary>Сведения о файле или null, если его нет.</summary>
        Task<RemoteEntry?> GetInfoAsync(string key, CancellationToken ct = default);

        /// <summary>
        /// Скачать файл. Если передан ifNoneMatch и версия не изменилась,
        /// вернётся null — трафик не тратится впустую.
        /// </summary>
        Task<RemoteContent?> DownloadAsync(
            string key, string? ifNoneMatch = null, CancellationToken ct = default);

        /// <summary>
        /// Загрузить файл и получить новый ETag.
        ///
        /// ifMatch — защита от затирания: если на сервере версия отличается
        /// от ожидаемой, запись не произойдёт и вернётся null. Значение "*"
        /// в ifNoneMatch означает «только если файла ещё нет».
        /// </summary>
        Task<string?> UploadAsync(
            string key,
            byte[] data,
            string? ifMatch = null,
            string? ifNoneMatch = null,
            CancellationToken ct = default);

        /// <summary>Удалить файл. Отсутствие файла ошибкой не считается.</summary>
        Task DeleteAsync(string key, CancellationToken ct = default);

        /// <summary>
        /// Создать подпапку внутри хранилища, если её ещё нет.
        ///
        /// Нужна, чтобы история версий не сваливалась в одну кучу с проектами:
        /// точка состоит из десятков объектов, и за неделю работы корень
        /// превращается в тысячу неразличимых файлов.
        /// </summary>
        Task EnsureFolderAsync(string relativeFolder, CancellationToken ct = default);

        /// <summary>
        /// Проверить доступность хранилища и создать корневую папку, если её нет.
        /// Вызывается при подключении и при первом обращении после разрыва связи.
        /// </summary>
        Task<bool> EnsureAvailableAsync(CancellationToken ct = default);
    }

    /// <summary>Скачанное содержимое вместе с версией, которой оно соответствует.</summary>
    public sealed class RemoteContent
    {
        public required byte[] Data { get; init; }
        public required string ETag { get; init; }
    }
}
