using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Serilog;
using Writersword.Core.Models.Sync;
using Writersword.Core.Services.Sync;

namespace Writersword.Mobile.Views
{
    public partial class SyncCheckView : UserControl
    {
        private readonly StringBuilder _log = new();
        private CancellationTokenSource? _cts;
        private bool _running;

        public SyncCheckView()
        {
            InitializeComponent();
            RunButton.Click += OnRunClicked;
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
                Append("Прервано пользователем.");
            }
            catch (Exception ex)
            {
                // Экран диагностический: показывается всё, включая тип
                // исключения, иначе по одному тексту причину не найти.
                Append($"Сбой: {ex.GetType().Name}");
                Append(ex.Message);
            }
            finally
            {
                _running = false;
                RunButton.Content = "Проверить";
            }
        }

        private async Task RunCheckAsync(CancellationToken ct)
        {
            var settings = new SyncSettings
            {
                ServerUrl = ServerBox.Text?.Trim() ?? string.Empty,
                Login = LoginBox.Text?.Trim() ?? string.Empty,
                Password = PasswordBox.Text ?? string.Empty,
                RemoteFolder = string.IsNullOrWhiteSpace(FolderBox.Text) ? "writersword" : FolderBox.Text.Trim(),
                IsEnabled = true
            };

            var master = MasterBox.Text ?? string.Empty;

            if (!settings.IsConfigured || master.Length == 0)
            {
                Append("Заполните адрес, логин и мастер-пароль.");
                return;
            }

            var logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Console().CreateLogger();

            using var storage = new WebDavRemoteStorage(settings, logger);
            var state = new SyncStateStore(logger);
            using var sync = new ProjectSyncService(storage, state, logger);

            Append("Подключение к хранилищу...");

            if (!await sync.ConnectAsync(master, ct).ConfigureAwait(true))
            {
                Append("Не удалось: сервер недоступен или мастер-пароль не подходит.");
                return;
            }

            Append("Подключено, описатель хранилища прочитан.");

            // Проверочный проект живёт в песочнице приложения: разрешений на
            // общее хранилище у приложения нет и не должно быть.
            var localPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "sync-check.writersword");

            var payload = Encoding.UTF8.GetBytes(
                $"Writersword sync check at {DateTimeOffset.Now:O}");

            await File.WriteAllBytesAsync(localPath, payload, ct).ConfigureAwait(true);
            Append($"Создан проверочный файл, {payload.Length} байт.");

            var push = await sync.PushAsync(localPath, force: true, ct).ConfigureAwait(true);
            if (!push.Success)
            {
                Append($"Отправка не удалась: {push.Error}");
                return;
            }

            Append($"Отправлено, версия на сервере {push.ETag}.");

            // Локальный файл убирается, чтобы загрузка была настоящей,
            // а не сверкой файла с самим собой.
            File.Delete(localPath);
            state.Remove(localPath);
            Append("Локальная копия удалена.");

            var pull = await sync.PullAsync(localPath, ct).ConfigureAwait(true);
            if (!pull.Success)
            {
                Append($"Загрузка не удалась: {pull.Error}");
                return;
            }

            var restored = await File.ReadAllBytesAsync(localPath, ct).ConfigureAwait(true);

            if (restored.Length == payload.Length && restored.AsSpan().SequenceEqual(payload))
                Append("Круг замкнулся: файл вернулся с сервера без изменений.");
            else
                Append($"Данные разошлись: было {payload.Length} байт, стало {restored.Length}.");

            Append("Готово.");
        }

        private void Append(string line)
        {
            _log.AppendLine(line);

            // Проверка выполняется из обработчика на UI-потоке, но продолжения
            // после await на Android могут прийти и с другого — Post снимает
            // вопрос целиком.
            Dispatcher.UIThread.Post(() => LogBlock.Text = _log.ToString());
        }
    }
}
