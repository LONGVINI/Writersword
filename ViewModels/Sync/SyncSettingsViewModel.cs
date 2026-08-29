using System;
using System.IO;
using System.Reactive;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using Serilog;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Models.Sync;
using Writersword.Core.Services.Sync;

namespace Writersword.ViewModels.Sync
{
    /// <summary>
    /// Настройки синхронизации проекта с удалённым хранилищем.
    ///
    /// Мастер-пароль здесь только вводится и в настройки не попадает: из него
    /// выводятся ключи шифрования, и на диске ему делать нечего. Пароль от
    /// самого хранилища сохраняется — он охраняет доступ к контейнеру, а не
    /// его содержимое.
    /// </summary>
    public class SyncSettingsViewModel : ReactiveObject
    {
        private readonly ProjectSyncFactory _factory;
        private readonly ISecretStore _secrets;
        private readonly SyncCoordinator _coordinator;
        private readonly ILogger _log;
        private readonly StringBuilder _status = new();

        private CancellationTokenSource? _cts;

        private string _serverUrl = string.Empty;
        private string _login = string.Empty;
        private string _password = string.Empty;
        private string _remoteFolder = "writersword";
        private string _masterPassword = string.Empty;
        private bool _isEnabled;
        private bool _isBusy;
        private string _statusText = string.Empty;

        public SyncSettingsViewModel()
            : this(App.Services.GetRequiredService<ProjectSyncFactory>(),
                   App.Services.GetRequiredService<ISecretStore>(),
                   App.Services.GetRequiredService<SyncCoordinator>(),
                   Log.Logger)
        {
        }

        public SyncSettingsViewModel(
            ProjectSyncFactory factory,
            ISecretStore secrets,
            SyncCoordinator coordinator,
            ILogger logger)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            _log = logger?.ForContext<SyncSettingsViewModel>() ?? throw new ArgumentNullException(nameof(logger));

            var stored = _factory.LoadSettings();
            _serverUrl = stored.ServerUrl;
            _login = stored.Login;
            _password = stored.Password;
            _remoteFolder = string.IsNullOrWhiteSpace(stored.RemoteFolder) ? "writersword" : stored.RemoteFolder;
            _isEnabled = stored.IsEnabled;

            // Сохранённый мастер-пароль подставляется в поле: иначе автор,
            // зашедший поправить адрес, стёр бы его пустым сохранением и
            // выключил автоматику, ничего не заметив.
            _masterPassword = _secrets.Read(SyncCoordinator.MasterPasswordKey) ?? string.Empty;

            TestCommand = ReactiveCommand.CreateFromTask(TestAsync);
            SaveCommand = ReactiveCommand.Create(Save);
        }

        public string ServerUrl
        {
            get => _serverUrl;
            set => this.RaiseAndSetIfChanged(ref _serverUrl, value);
        }

        public string Login
        {
            get => _login;
            set => this.RaiseAndSetIfChanged(ref _login, value);
        }

        public string Password
        {
            get => _password;
            set => this.RaiseAndSetIfChanged(ref _password, value);
        }

        public string RemoteFolder
        {
            get => _remoteFolder;
            set => this.RaiseAndSetIfChanged(ref _remoteFolder, value);
        }

        public string MasterPassword
        {
            get => _masterPassword;
            set => this.RaiseAndSetIfChanged(ref _masterPassword, value);
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set => this.RaiseAndSetIfChanged(ref _isEnabled, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set => this.RaiseAndSetIfChanged(ref _isBusy, value);
        }

        public string StatusText
        {
            get => _statusText;
            private set => this.RaiseAndSetIfChanged(ref _statusText, value);
        }

        public ReactiveCommand<Unit, Unit> TestCommand { get; }
        public ReactiveCommand<Unit, Unit> SaveCommand { get; }

        /// <summary>Закрытие окна. Проставляется представлением.</summary>
        public Action? CloseRequested { get; set; }

        public void Close() => CloseRequested?.Invoke();

        /// <summary>Собрать настройки из полей формы.</summary>
        private SyncSettings BuildSettings() => new()
        {
            ServerUrl = ServerUrl?.Trim() ?? string.Empty,
            Login = Login?.Trim() ?? string.Empty,
            Password = Password ?? string.Empty,
            RemoteFolder = string.IsNullOrWhiteSpace(RemoteFolder) ? "writersword" : RemoteFolder.Trim(),
            IsEnabled = IsEnabled
        };

        private void Save()
        {
            var settings = BuildSettings();
            _factory.ApplySettings(settings);

            // Мастер-пароль уходит в хранилище системы, а не в настройки:
            // без него координатор не сможет подключиться сам, а значит
            // автоматической отправки не будет вовсе.
            if (settings.IsEnabled && MasterPassword.Length > 0)
            {
                if (_secrets.IsAvailable && _secrets.Write(SyncCoordinator.MasterPasswordKey, MasterPassword))
                    Append("Settings saved, master password stored in the system credential store.");
                else
                    Append("Settings saved, but the master password could not be stored - automatic sync will stay off.");
            }
            else
            {
                // Синхронизация выключена — пароль не должен пережить это
                // действие: хранить секрет от отключённой возможности незачем.
                _secrets.Delete(SyncCoordinator.MasterPasswordKey);
                Append("Settings saved.");
            }

            _coordinator.Restart();
            _log.Information("Sync settings updated, coordinator restarted");
        }

        /// <summary>
        /// Полная проверка круга: подключение, отправка, загрузка, сверка.
        ///
        /// Проверяется именно круг, а не только доступность сервера: WebDAV
        /// может ответить на PROPFIND и при этом не принять PUT, а шифрование
        /// может оказаться обратимым не полностью — обе поломки видны только
        /// при возврате файла обратно.
        /// </summary>
        private async Task TestAsync()
        {
            if (IsBusy)
            {
                _cts?.Cancel();
                return;
            }

            _status.Clear();
            StatusText = string.Empty;
            IsBusy = true;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            string? tempPath = null;

            try
            {
                var settings = BuildSettings();

                if (!settings.IsConfigured || MasterPassword.Length == 0)
                {
                    Append("Fill in the server address, login and master password.");
                    return;
                }

                // Проверка идёт на временном сервисе, а не на текущем: настройки
                // могли быть изменены в полях и ещё не сохранены, и проверять
                // надо именно их.
                using var storage = new WebDavRemoteStorage(settings, _log);
                var state = new SyncStateStore(_log);
                using var sync = new ProjectSyncService(storage, state, _log);

                Append("Connecting to the remote vault...");

                if (!await sync.ConnectAsync(MasterPassword, ct).ConfigureAwait(true))
                {
                    Append("Failed: the server is unreachable or the master password does not match.");
                    return;
                }

                Append("Connected, vault descriptor read.");

                tempPath = Path.Combine(Path.GetTempPath(), "writersword-sync-check.writersword");
                var payload = Encoding.UTF8.GetBytes($"Writersword sync check at {DateTimeOffset.Now:O}");

                await File.WriteAllBytesAsync(tempPath, payload, ct).ConfigureAwait(true);
                Append($"Check file created, {payload.Length} bytes.");

                var push = await sync.PushAsync(tempPath, force: true, ct).ConfigureAwait(true);
                if (!push.Success)
                {
                    Append($"Upload failed: {push.Error}");
                    return;
                }

                Append($"Uploaded, remote version {push.ETag}.");

                File.Delete(tempPath);
                state.Remove(tempPath);
                Append("Local copy removed.");

                var pull = await sync.PullAsync(tempPath, ct).ConfigureAwait(true);
                if (!pull.Success)
                {
                    Append($"Download failed: {pull.Error}");
                    return;
                }

                var restored = await File.ReadAllBytesAsync(tempPath, ct).ConfigureAwait(true);

                if (restored.Length == payload.Length && restored.AsSpan().SequenceEqual(payload))
                    Append("Round trip complete: the file came back unchanged.");
                else
                    Append($"Data mismatch: sent {payload.Length} bytes, got {restored.Length}.");
            }
            catch (OperationCanceledException)
            {
                Append("Cancelled.");
            }
            catch (Exception ex)
            {
                // Окно диагностическое: показывается и тип исключения, иначе по
                // одному тексту причину не найти.
                Append($"Failure: {ex.GetType().Name}");
                Append(ex.Message);
                _log.Warning(ex, "Sync check failed");
            }
            finally
            {
                if (tempPath is not null && File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); }
                    catch (IOException) { }
                }

                IsBusy = false;
            }
        }

        private void Append(string line)
        {
            _status.AppendLine(line);
            StatusText = _status.ToString();
        }
    }
}
