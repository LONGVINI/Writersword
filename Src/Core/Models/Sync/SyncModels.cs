using System;

namespace Writersword.Core.Models.Sync
{
    /// <summary>
    /// Сведения о файле на сервере. ETag — идентификатор версии, который
    /// выдаёт сам сервер; сравнение по нему надёжнее сравнения дат, потому
    /// что часы на устройствах расходятся, а ETag меняется ровно тогда,
    /// когда изменилось содержимое.
    /// </summary>
    public sealed class RemoteEntry
    {
        public required string ETag { get; init; }
        public long Length { get; init; }
        public DateTimeOffset LastModified { get; init; }
    }

    /// <summary>Результат сравнения локального файла с серверным.</summary>
    public enum SyncState
    {
        /// <summary>Синхронизация выключена или хранилище не подключено.</summary>
        Disabled,

        /// <summary>Сервер недоступен. Работа продолжается локально.</summary>
        Offline,

        /// <summary>На сервере файла нет — первая выгрузка.</summary>
        RemoteMissing,

        /// <summary>Локального файла нет — первая загрузка.</summary>
        LocalMissing,

        /// <summary>Версии совпадают, делать нечего.</summary>
        InSync,

        /// <summary>На сервере версия новее локальной.</summary>
        RemoteAhead,

        /// <summary>Локальная версия новее серверной.</summary>
        LocalAhead,

        /// <summary>
        /// Разошлись обе стороны: и локально правили, и на сервере появилась
        /// чужая версия. Разрешается только участием пользователя.
        /// </summary>
        Diverged
    }

    /// <summary>
    /// Состояние синхронизации конкретного проекта.
    ///
    /// KnownETag — ETag той серверной версии, от которой пляшет локальная копия.
    /// Именно он отличает «я обогнал сервер» от «мы разошлись»: если серверный
    /// ETag не совпадает с известным, значит там писал кто-то ещё.
    /// </summary>
    public sealed class SyncStatus
    {
        public required SyncState State { get; init; }
        public string? RemoteETag { get; init; }
        public string? KnownETag { get; init; }
        public DateTimeOffset? RemoteModified { get; init; }
        public long RemoteLength { get; init; }

        /// <summary>Текст ошибки, если состояние Offline. В остальных случаях null.</summary>
        public string? Error { get; init; }

        public static SyncStatus Simple(SyncState state) => new() { State = state };
    }

    /// <summary>Проект, лежащий в удалённом хранилище.</summary>
    public sealed class RemoteProjectInfo
    {
        public required string Name { get; init; }

        /// <summary>Когда проект был отправлен в хранилище в последний раз.</summary>
        public DateTimeOffset UpdatedAt { get; init; }

        /// <summary>Размер контейнера на сервере. Ноль, если сведений нет.</summary>
        public long Length { get; init; }
    }

    /// <summary>Что сделать при расхождении версий.</summary>
    public enum SyncResolution
    {
        /// <summary>Ничего не делать, оставить как есть.</summary>
        Skip,

        /// <summary>Взять серверную версию, локальная уходит в резервную копию.</summary>
        TakeRemote,

        /// <summary>Отправить локальную версию, серверная перезаписывается.</summary>
        TakeLocal
    }

    /// <summary>Исход операции синхронизации.</summary>
    public sealed class SyncResult
    {
        public required bool Success { get; init; }
        public required SyncState State { get; init; }
        public string? ETag { get; init; }

        /// <summary>Путь к резервной копии, если локальный файл был замещён серверным.</summary>
        public string? BackupPath { get; init; }

        public string? Error { get; init; }

        public static SyncResult Ok(SyncState state, string? etag = null, string? backupPath = null)
            => new() { Success = true, State = state, ETag = etag, BackupPath = backupPath };

        public static SyncResult Fail(SyncState state, string error)
            => new() { Success = false, State = state, Error = error };
    }
}
