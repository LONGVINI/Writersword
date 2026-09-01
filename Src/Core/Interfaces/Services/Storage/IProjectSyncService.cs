using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Writersword.Core.Models.Sync;

namespace Writersword.Core.Interfaces.Services.Storage
{
    /// <summary>
    /// Синхронизация файла проекта с удалённым хранилищем.
    ///
    /// Источник правды — локальный файл. Сеть может отсутствовать сколь угодно
    /// долго, и ни одна операция редактора не имеет права этого ждать: всё,
    /// что делает этот сервис, происходит по краям работы — при открытии,
    /// после сохранения и по таймеру, — и любой сбой сети заканчивается
    /// состоянием Offline, а не исключением наверх.
    /// </summary>
    public interface IProjectSyncService : IDisposable
    {
        /// <summary>Подключено ли хранилище в этой сессии.</summary>
        bool IsConnected { get; }

        /// <summary>
        /// Подключить хранилище мастер-паролем.
        ///
        /// Если на сервере уже есть описатель хранилища, пароль сверяется с ним.
        /// Если описателя нет, хранилище создаётся с этим паролем.
        /// Возвращает false при неверном пароле, недоступном сервере или
        /// незаполненных настройках.
        /// </summary>
        Task<bool> ConnectAsync(string masterPassword, CancellationToken ct = default);

        /// <summary>Отключить хранилище и стереть ключи из памяти.</summary>
        void Disconnect();

        /// <summary>
        /// Сравнить локальный файл с серверным, ничего не изменяя.
        /// Безопасно вызывать по таймеру при открытом проекте.
        /// </summary>
        Task<SyncStatus> GetStatusAsync(string localPath, CancellationToken ct = default);

        /// <summary>
        /// Отправить локальную версию на сервер.
        ///
        /// force = false защищает чужую правку: если на сервере появилась
        /// версия новее известной, отправка не состоится и вернётся состояние
        /// Diverged. force = true перезаписывает серверную версию безусловно.
        /// </summary>
        Task<SyncResult> PushAsync(string localPath, bool force = false, CancellationToken ct = default);

        /// <summary>
        /// Забрать серверную версию.
        ///
        /// Локальный файл, если он отличается от последнего синхронизированного,
        /// сначала уходит в резервную копию рядом с оригиналом — путь к ней
        /// возвращается в результате.
        /// </summary>
        Task<SyncResult> PullAsync(string localPath, CancellationToken ct = default);

        /// <summary>
        /// Перечислить проекты, лежащие в хранилище.
        ///
        /// Отдельный указатель нужен потому, что имена файлов на сервере
        /// выведены через HMAC и необратимы: по содержимому папки узнать,
        /// какие там книги, нельзя — в этом и был смысл.
        /// </summary>
        Task<IReadOnlyList<RemoteProjectInfo>> ListProjectsAsync(CancellationToken ct = default);

        /// <summary>
        /// Забрать проект по имени из указателя в указанный локальный файл.
        /// Используется на устройстве, где этого проекта ещё нет.
        /// </summary>
        Task<SyncResult> FetchProjectAsync(
            string projectName, string localPath, CancellationToken ct = default);

        /// <summary>
        /// Отправить историю версий проекта в хранилище.
        ///
        /// Склад дедуплицирован и содержательно адресуем: имя записи равно хешу
        /// её содержимого. Поэтому отправляются только недостающие записи, а
        /// повторная отправка ничего не стоит.
        ///
        /// Возвращает число отправленных записей.
        /// </summary>
        Task<int> PushBackupStoreAsync(
            string storePath, string projectName, CancellationToken ct = default);

        /// <summary>
        /// Восстановить историю версий из хранилища.
        ///
        /// Забираются записи, которых здесь нет и которые здесь не удаляли:
        /// прореженное отмечено надгробиями и обратно не тянется, а всё
        /// остальное считается потерей. За один заход берётся ограниченное
        /// число записей, остаток догонит следующий.
        ///
        /// Возвращает число восстановленных записей.
        /// </summary>
        Task<int> PullBackupStoreAsync(
            string storePath, string projectName, CancellationToken ct = default);

        /// <summary>
        /// Состояние синхронизации изменилось по итогам фоновой проверки.
        /// Подписчик в интерфейсе решает, спрашивать ли пользователя.
        /// </summary>
        event EventHandler<SyncStatus>? StatusChanged;
    }
}
