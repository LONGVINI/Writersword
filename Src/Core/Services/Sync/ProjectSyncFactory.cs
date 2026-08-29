using System;
using Serilog;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Models.Sync;

namespace Writersword.Core.Services.Sync
{
    /// <summary>
    /// Создаёт сервис синхронизации по текущим настройкам.
    ///
    /// Прямая регистрация IProjectSyncService в контейнере не годится: адрес
    /// сервера и учётные данные пользователь меняет во время работы, а внутри
    /// живёт HttpClient, настроенный один раз при создании. Поэтому смена
    /// настроек означает пересоздание сервиса, и фабрика — то место, где это
    /// происходит явно.
    ///
    /// Сама фабрика регистрируется синглтоном и владеет текущим экземпляром:
    /// пересоздание закрывает предыдущий, чтобы не оставлять открытых соединений
    /// и ключей в памяти.
    /// </summary>
    public sealed class ProjectSyncFactory : IDisposable
    {
        private const string SettingsKey = "Sync";

        private readonly ISettingsService _settings;
        private readonly ILogger _log;
        private readonly object _gate = new();

        private IProjectSyncService? _current;
        private bool _disposed;

        public ProjectSyncFactory(ISettingsService settings, ILogger logger)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _log = logger?.ForContext<ProjectSyncFactory>() ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>Текущие настройки синхронизации. Никогда не null.</summary>
        public SyncSettings LoadSettings()
            => _settings.GetModuleSettings<SyncSettings>(SettingsKey) ?? new SyncSettings();

        /// <summary>
        /// Сохранить настройки и пересоздать сервис под них.
        /// Возвращает новый экземпляр или null, если настройки неполны.
        /// </summary>
        public IProjectSyncService? ApplySettings(SyncSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            ThrowIfDisposed();

            _settings.SaveModuleSettings(SettingsKey, settings);
            _settings.Save();

            return Rebuild(settings);
        }

        /// <summary>
        /// Текущий сервис, созданный при первом обращении.
        /// Возвращает null, если синхронизация выключена или не настроена.
        /// </summary>
        public IProjectSyncService? Current
        {
            get
            {
                ThrowIfDisposed();

                lock (_gate)
                {
                    if (_current is not null)
                        return _current;
                }

                return Rebuild(LoadSettings());
            }
        }

        private IProjectSyncService? Rebuild(SyncSettings settings)
        {
            lock (_gate)
            {
                _current?.Dispose();
                _current = null;

                if (!settings.IsEnabled || !settings.IsConfigured)
                    return null;

                try
                {
                    var storage = new WebDavRemoteStorage(settings, _log);
                    var state = new SyncStateStore(_log);

                    _current = new ProjectSyncService(storage, state, _log);
                    return _current;
                }
                catch (ArgumentException ex)
                {
                    // Настройки заполнены, но негодны — например, адрес не разбирается
                    // в URI. Это ошибка пользователя, а не сбой: сервиса просто нет,
                    // пока адрес не поправят.
                    _log.Warning(ex, "Sync settings are invalid, service not created");
                    return null;
                }
            }
        }

        private void ThrowIfDisposed()
            => ObjectDisposedException.ThrowIf(_disposed, this);

        public void Dispose()
        {
            if (_disposed) return;

            lock (_gate)
            {
                _current?.Dispose();
                _current = null;
            }

            _disposed = true;
        }
    }
}
