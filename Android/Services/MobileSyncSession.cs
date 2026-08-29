using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Serilog;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Models.Sync;
using Writersword.Core.Services.Sync;

namespace Writersword.Mobile.Services
{
    /// <summary>
    /// Подключение к хранилищу на время работы приложения.
    ///
    /// На телефоне нет ни контейнера служб, ни настроек программы — они живут
    /// в десктопной сборке. Поэтому здесь простой держатель: одно подключение
    /// на процесс, доступное обоим экранам.
    ///
    /// Настройки лежат в песочнице приложения. Разрешений это не требует, и
    /// при удалении программы они исчезают вместе с ней — что для мастер-пароля
    /// скорее хорошо.
    /// </summary>
    public sealed class MobileSyncSession
    {
        private static readonly Lazy<MobileSyncSession> _instance = new(() => new MobileSyncSession());

        private readonly ILogger _log;
        private readonly SemaphoreSlim _gate = new(1, 1);

        private IProjectSyncService? _service;

        private MobileSyncSession()
        {
            _log = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console()
                .CreateLogger()
                .ForContext<MobileSyncSession>();
        }

        public static MobileSyncSession Instance => _instance.Value;

        /// <summary>Папка, в которой лежат скачанные проекты.</summary>
        public static string ProjectsDirectory
        {
            get
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "projects");

                Directory.CreateDirectory(path);
                return path;
            }
        }

        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "sync-settings.json");

        /// <summary>Подключён ли сеанс к хранилищу.</summary>
        public bool IsConnected => _service?.IsConnected == true;

        /// <summary>Текущее подключение или null.</summary>
        public IProjectSyncService? Service => _service;

        /// <summary>Настройки вместе с мастер-паролем, как их ввёл автор.</summary>
        public sealed class StoredSettings
        {
            public string ServerUrl { get; set; } = string.Empty;
            public string Login { get; set; } = string.Empty;
            public string Password { get; set; } = string.Empty;
            public string RemoteFolder { get; set; } = "writersword";
            public string MasterPassword { get; set; } = string.Empty;
        }

        public StoredSettings LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                    return new StoredSettings();

                var json = File.ReadAllText(SettingsPath);
                return JsonConvert.DeserializeObject<StoredSettings>(json) ?? new StoredSettings();
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to read mobile sync settings");
                return new StoredSettings();
            }
        }

        public void SaveSettings(StoredSettings settings)
        {
            try
            {
                File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(settings, Formatting.Indented));
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to save mobile sync settings");
            }
        }

        /// <summary>
        /// Подключиться сохранёнными настройками.
        ///
        /// Прежнее подключение закрывается: у него свой HttpClient, настроенный
        /// под прежний адрес, и оставлять его висеть незачем.
        /// </summary>
        public async Task<bool> ConnectAsync(StoredSettings stored, CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _service?.Dispose();
                _service = null;

                var settings = new SyncSettings
                {
                    ServerUrl = stored.ServerUrl?.Trim() ?? string.Empty,
                    Login = stored.Login?.Trim() ?? string.Empty,
                    Password = stored.Password ?? string.Empty,
                    RemoteFolder = string.IsNullOrWhiteSpace(stored.RemoteFolder)
                        ? "writersword"
                        : stored.RemoteFolder.Trim(),
                    IsEnabled = true
                };

                if (!settings.IsConfigured || string.IsNullOrEmpty(stored.MasterPassword))
                    return false;

                var storage = new WebDavRemoteStorage(settings, _log);
                var state = new SyncStateStore(_log);
                var service = new ProjectSyncService(storage, state, _log);

                if (!await service.ConnectAsync(stored.MasterPassword, ct).ConfigureAwait(false))
                {
                    service.Dispose();
                    return false;
                }

                _service = service;
                return true;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Локальный путь, куда кладётся скачанный проект.</summary>
        public static string LocalPathFor(string projectName)
            => Path.Combine(ProjectsDirectory, projectName + ".writersword");
    }
}
