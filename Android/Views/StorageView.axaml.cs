using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Writersword.Mobile.Services;

namespace Writersword.Mobile.Views
{
    /// <summary>
    /// Что лежит в хранилище и что уже есть на телефоне.
    ///
    /// Полноценного открытия книги здесь пока нет: чтобы показать текст, нужен
    /// модуль редактора, и это следующий шаг. Сейчас экран отвечает на вопрос
    /// «доехало ли» — и этого достаточно, чтобы убедиться, что связь работает
    /// в обе стороны.
    /// </summary>
    public partial class StorageView : UserControl
    {
        private readonly ObservableCollection<Row> _remote = new();
        private readonly ObservableCollection<Row> _local = new();
        private bool _busy;

        public StorageView()
        {
            InitializeComponent();

            RemoteList.ItemsSource = _remote;
            LocalList.ItemsSource = _local;

            RefreshButton.Click += OnRefreshClicked;
            DownloadButton.Click += OnDownloadClicked;
            RemoteList.SelectionChanged += (_, _) =>
                DownloadButton.IsEnabled = RemoteList.SelectedItem is Row && !_busy;

            PathBlock.Text = "Папка проектов: " + MobileSyncSession.ProjectsDirectory;
            RefreshLocal();
        }

        /// <summary>Строка списка. Своей модели ради двух полей заводить незачем.</summary>
        public sealed class Row
        {
            public required string Name { get; init; }
            public required string Details { get; init; }
        }

        private async void OnRefreshClicked(object? sender, RoutedEventArgs e)
        {
            if (_busy) return;

            _busy = true;
            RefreshButton.IsEnabled = false;

            try
            {
                RefreshLocal();
                await RefreshRemoteAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                StatusBlock.Text = $"Сбой: {ex.GetType().Name} — {ex.Message}";
            }
            finally
            {
                _busy = false;
                RefreshButton.IsEnabled = true;
            }
        }

        private async Task RefreshRemoteAsync()
        {
            var session = MobileSyncSession.Instance;

            if (!session.IsConnected)
            {
                StatusBlock.Text = "Подключаюсь...";

                if (!await session.ConnectAsync(session.LoadSettings()).ConfigureAwait(true))
                {
                    StatusBlock.Text = "Не подключено. Проверьте вкладку «Подключение».";
                    _remote.Clear();
                    return;
                }
            }

            var projects = await session.Service!.ListProjectsAsync().ConfigureAwait(true);

            _remote.Clear();
            foreach (var project in projects.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                _remote.Add(new Row
                {
                    Name = project.Name,
                    Details = $"{FormatSize(project.Length)}, изменён {project.UpdatedAt.ToLocalTime():dd.MM.yyyy HH:mm}"
                });
            }

            StatusBlock.Text = _remote.Count == 0
                ? "В хранилище пока нет проектов. Сохраните книгу на компьютере — она появится здесь."
                : $"В хранилище проектов: {_remote.Count}";
        }

        private void RefreshLocal()
        {
            _local.Clear();

            var dir = MobileSyncSession.ProjectsDirectory;
            if (!Directory.Exists(dir))
                return;

            foreach (var file in Directory.EnumerateFiles(dir).OrderBy(f => f))
            {
                var info = new FileInfo(file);
                _local.Add(new Row
                {
                    Name = Path.GetFileName(file),
                    Details = $"{FormatSize(info.Length)}, {info.LastWriteTime:dd.MM.yyyy HH:mm}"
                });
            }
        }

        private async void OnDownloadClicked(object? sender, RoutedEventArgs e)
        {
            if (_busy || RemoteList.SelectedItem is not Row row) return;

            var session = MobileSyncSession.Instance;
            if (!session.IsConnected)
            {
                StatusBlock.Text = "Сначала подключитесь";
                return;
            }

            _busy = true;
            DownloadButton.IsEnabled = false;
            StatusBlock.Text = $"Скачиваю «{row.Name}»...";

            try
            {
                var path = MobileSyncSession.LocalPathFor(row.Name);
                var result = await session.Service!.FetchProjectAsync(row.Name, path).ConfigureAwait(true);

                StatusBlock.Text = result.Success
                    ? $"«{row.Name}» скачан"
                    : $"Не удалось скачать: {result.Error}";

                RefreshLocal();
            }
            catch (Exception ex)
            {
                StatusBlock.Text = $"Сбой: {ex.GetType().Name} — {ex.Message}";
            }
            finally
            {
                _busy = false;
                DownloadButton.IsEnabled = RemoteList.SelectedItem is Row;
            }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "размер неизвестен";
            if (bytes < 1024) return bytes + " Б";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " КБ";
            return (bytes / (1024.0 * 1024)).ToString("0.#") + " МБ";
        }
    }
}
