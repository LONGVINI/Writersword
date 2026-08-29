using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Writersword.Mobile.Services;

namespace Writersword.Mobile.Views
{
    /// <summary>
    /// Настройки подключения и проверка круга.
    ///
    /// Настройки хранятся в песочнице приложения и подставляются при открытии:
    /// вводить пять полей при каждом запуске — верный способ перестать
    /// пользоваться синхронизацией вообще.
    /// </summary>
    public partial class SyncCheckView : UserControl
    {
        private readonly StringBuilder _log = new();
        private CancellationTokenSource? _cts;
        private bool _running;

        public SyncCheckView()
        {
            InitializeComponent();

            ConnectButton.Click += OnConnectClicked;
            RunButton.Click += OnRunClicked;

            LoadSettings();
        }

        private void LoadSettings()
        {
            var stored = MobileSyncSession.Instance.LoadSettings();

            ServerBox.Text = stored.ServerUrl;
            LoginBox.Text = stored.Login;
            PasswordBox.Text = stored.Password;
            FolderBox.Text = string.IsNullOrWhiteSpace(stored.RemoteFolder) ? "writersword" : stored.RemoteFolder;
            MasterBox.Text = stored.MasterPassword;
        }

        private MobileSyncSession.StoredSettings CollectSettings() => new()
        {
            ServerUrl = ServerBox.Text?.Trim() ?? string.Empty,
            Login = LoginBox.Text?.Trim() ?? string.Empty,
            Password = PasswordBox.Text ?? string.Empty,
            RemoteFolder = string.IsNullOrWhiteSpace(FolderBox.Text) ? "writersword" : FolderBox.Text.Trim(),
            MasterPassword = MasterBox.Text ?? string.Empty
        };

        private async void OnConnectClicked(object? sender, RoutedEventArgs e)
        {
            if (_running) return;

            _running = true;
            ConnectButton.IsEnabled = false;
            _log.Clear();

            try
            {
                var settings = CollectSettings();
                MobileSyncSession.Instance.SaveSettings(settings);
                Append("Settings saved.");

                Append("Connecting...");

                if (await MobileSyncSession.Instance.ConnectAsync(settings).ConfigureAwait(true))
                    Append("Connected. Open the Storage tab to see the projects.");
                else
                    Append("Failed: server unreachable, credentials rejected or master password does not match.");
            }
            catch (Exception ex)
            {
                Append($"Failure: {ex.GetType().Name}");
                Append(ex.Message);
            }
            finally
            {
                _running = false;
                ConnectButton.IsEnabled = true;
            }
        }

        private async void OnRunClicked(object? sender, RoutedEventArgs e)
        {
            if (_running)
            {
                _cts?.Cancel();
                return;
            }

            _running = true;
            RunButton.Content = "Прервать";
            _log.Clear();

            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            try
            {
                await RunCheckAsync(_cts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                Append("Cancelled.");
            }
            catch (Exception ex)
            {
                Append($"Failure: {ex.GetType().Name}");
                Append(ex.Message);
            }
            finally
            {
                _running = false;
                RunButton.Content = "Проверить круг";
            }
        }

        /// <summary>
        /// Полный круг: отправка, удаление локальной копии, загрузка, сверка.
        ///
        /// Проверяется именно круг, а не доступность сервера: WebDAV может
        /// ответить на PROPFIND и при этом не принять PUT, а шифрование —
        /// оказаться обратимым не полностью. Обе поломки видны только при
        /// возврате файла обратно.
        /// </summary>
        private async Task RunCheckAsync(CancellationToken ct)
        {
            var session = MobileSyncSession.Instance;
            var settings = CollectSettings();
            session.SaveSettings(settings);

            if (!session.IsConnected)
            {
                Append("Connecting...");

                if (!await session.ConnectAsync(settings, ct).ConfigureAwait(true))
                {
                    Append("Failed: server unreachable or master password does not match.");
                    return;
                }
            }

            Append("Connected, vault descriptor read.");

            var localPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "sync-check.writersword");

            var payload = Encoding.UTF8.GetBytes($"Writersword sync check at {DateTimeOffset.Now:O}");

            await File.WriteAllBytesAsync(localPath, payload, ct).ConfigureAwait(true);
            Append($"Check file created, {payload.Length} bytes.");

            var push = await session.Service!.PushAsync(localPath, force: true, ct).ConfigureAwait(true);
            if (!push.Success)
            {
                Append($"Upload failed: {push.Error}");
                return;
            }

            Append($"Uploaded, remote version {push.ETag}.");

            File.Delete(localPath);
            Append("Local copy removed.");

            var pull = await session.Service.PullAsync(localPath, ct).ConfigureAwait(true);
            if (!pull.Success)
            {
                Append($"Download failed: {pull.Error}");
                return;
            }

            var restored = await File.ReadAllBytesAsync(localPath, ct).ConfigureAwait(true);

            if (restored.Length == payload.Length && restored.AsSpan().SequenceEqual(payload))
                Append("Round trip complete: the file came back unchanged.");
            else
                Append($"Data mismatch: sent {payload.Length} bytes, got {restored.Length}.");

            // Проверочный файл в списке проектов не нужен: он засоряет хранилище
            // и попадает в указатель наравне с настоящими книгами.
            try
            {
                File.Delete(localPath);
            }
            catch (IOException)
            {
            }

            Append("Done.");
        }

        private void Append(string line)
        {
            _log.AppendLine(line);
            Dispatcher.UIThread.Post(() => LogBlock.Text = _log.ToString());
        }
    }
}
