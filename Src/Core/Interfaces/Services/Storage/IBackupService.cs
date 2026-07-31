using System.Collections.Generic;
using System.Threading.Tasks;
using Writersword.Core.Models.Backup;

namespace Writersword.Core.Interfaces.Services.Storage
{
    /// <summary>
    /// История версий проекта.
    ///
    /// Хранилище устроено как у Git: содержимое каждой записи архива кладётся
    /// в общий склад объектов под именем своего SHA-256, а точка восстановления —
    /// это манифест «путь → хеш». Запись, которая не менялась между точками,
    /// физически хранится один раз, поэтому аватарки и картинки не копируются
    /// заново при каждом сохранении.
    /// </summary>
    public interface IBackupService
    {
        /// <summary>
        /// Путь к хранилищу .wsbk для указанного проекта с учётом настроек.
        /// </summary>
        string GetStoragePath(string projectPath);

        /// <summary>
        /// Путь хранилища, прописанный внутри самого проекта.
        /// Пусто — переопределения нет, действует общая настройка.
        /// </summary>
        string? GetProjectStorageOverride(string projectPath);

        /// <summary>
        /// Задать или снять путь хранилища для конкретного проекта.
        /// Значение хранится внутри .writersword и переезжает вместе с файлом;
        /// если на другом компьютере такой папки нет, используется настройка
        /// того, кто открыл проект.
        /// </summary>
        /// <param name="path">Путь; пусто или null — убрать переопределение.</param>
        Task<bool> SetProjectStorageOverrideAsync(string projectPath, string? path);

        /// <summary>
        /// Снять точку восстановления с текущего содержимого файла проекта.
        /// Вызывается до перезаписи проекта, поэтому в точку попадает состояние
        /// «как было до сохранения». Возвращает false, если история выключена
        /// в настройках или снимать нечего.
        /// </summary>
        Task<bool> CreateSnapshotAsync(string projectPath, BackupTrigger trigger);

        /// <summary>
        /// Список точек восстановления проекта, новые первыми.
        /// </summary>
        Task<IReadOnlyList<BackupSnapshotInfo>> GetSnapshotsAsync(string projectPath);

        /// <summary>
        /// Собрать точку восстановления обратно в файл .writersword.
        /// targetPath может отличаться от исходного пути — тогда точка
        /// разворачивается как отдельная копия и текущий проект не трогается.
        /// </summary>
        Task<bool> RestoreSnapshotAsync(string projectPath, string snapshotId, string targetPath);

        /// <summary>
        /// Развернуть точку во временный файл и вернуть путь к нему.
        /// Нужно для режима сравнения: данные точки читаются обычным
        /// загрузчиком проекта, а файл удаляется при выходе из режима.
        /// Возвращает null, если точку собрать не удалось.
        /// </summary>
        Task<string?> ExtractSnapshotToTempAsync(string projectPath, string snapshotId);

        /// <summary>
        /// Удалить точку и объекты, на которые больше никто не ссылается.
        /// </summary>
        Task<bool> DeleteSnapshotAsync(string projectPath, string snapshotId);

        /// <summary>
        /// Размеры данных модулей в текущем файле проекта: имя модуля → байты.
        /// Нужно, чтобы перед откатом показать, что именно изменится.
        /// </summary>
        Task<Dictionary<string, long>> GetCurrentModuleSizesAsync(string projectPath);

        /// <summary>
        /// Удалить временные файлы сравнения, оставшиеся от прошлых сессий.
        /// Вызывается при запуске: в этой папке лежат полные копии проектов,
        /// и после падения они остаются на диске в общем временном каталоге.
        /// </summary>
        void CleanupTempFiles();

        /// <summary>
        /// Перенести историю на новый путь проекта. Имя хранилища завязано на
        /// отпечаток пути файла, поэтому после «Сохранить как» история осталась
        /// бы висеть за старым путём, а у нового проекта была бы пустой.
        /// </summary>
        Task<bool> MoveStoreAsync(string oldProjectPath, string newProjectPath);

        /// <summary>
        /// Все хранилища в служебной папке и в папке из настроек.
        /// Хранилища скрыты от глаз, поэтому список — единственный способ
        /// найти историю проектов, которых давно нет.
        /// </summary>
        Task<IReadOnlyList<BackupStoreInfo>> ListStoresAsync();

        /// <summary>
        /// Удалить хранилище целиком вместе со всеми точками.
        /// Работает только внутри известных корней — чужие папки не трогает.
        /// </summary>
        Task<bool> DeleteStoreAsync(string storePath);

        /// <summary>
        /// Суммарный размер хранилища на диске в байтах.
        /// </summary>
        Task<long> GetStorageSizeAsync(string projectPath);
    }
}
