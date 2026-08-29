using System;

namespace Writersword.Core.Models.Sync
{
    /// <summary>
    /// Настройки подключения к удалённому хранилищу.
    /// Хранятся через ISettingsService.SaveModuleSettings("Sync", ...) —
    /// отдельного механизма для них не заводится.
    ///
    /// Мастер-пароль здесь не лежит и на диск не пишется никогда: он живёт
    /// только в памяти сессии. Пароль от самого WebDAV — лежит, потому что
    /// иначе его пришлось бы вводить при каждом запуске, а он защищает
    /// доступ к контейнеру, а не его содержимое: без мастер-пароля скачанные
    /// файлы бесполезны.
    /// </summary>
    public sealed class SyncSettings
    {
        /// <summary>
        /// Базовый адрес WebDAV, например
        /// https://cloud.disroot.org/remote.php/dav/files/username
        /// </summary>
        public string ServerUrl { get; set; } = string.Empty;

        /// <summary>Логин WebDAV.</summary>
        public string Login { get; set; } = string.Empty;

        /// <summary>Пароль WebDAV — пароль приложения, а не основной пароль аккаунта.</summary>
        public string Password { get; set; } = string.Empty;

        /// <summary>Папка внутри хранилища, куда складываются контейнеры.</summary>
        public string RemoteFolder { get; set; } = "writersword";

        /// <summary>Включена ли синхронизация.</summary>
        public bool IsEnabled { get; set; }

        /// <summary>
        /// Как часто опрашивать сервер на предмет изменений при открытом проекте.
        /// Ноль отключает опрос — синхронизация тогда только при открытии и сохранении.
        /// </summary>
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(2);

        /// <summary>Таймаут сетевых операций. Сеть может быть медленной или отсутствовать.</summary>
        public TimeSpan NetworkTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Заполнены ли поля, без которых подключение невозможно.</summary>
        public bool IsConfigured
            => !string.IsNullOrWhiteSpace(ServerUrl)
               && !string.IsNullOrWhiteSpace(Login);
    }
}
