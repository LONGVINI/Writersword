using System;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace Writersword.Mobile.Views
{
    /// <summary>
    /// Журнал приложения.
    ///
    /// Нужен затем, что консоль на телефоне видна только через adb при
    /// подключённом проводе, а беда случается не у провода. Здесь журнал можно
    /// прочитать на месте и переслать — кнопкой в буфер обмена, откуда он
    /// вставляется в переписку.
    ///
    /// Показывается хвост, а не файл целиком: за день набегают тысячи строк, и
    /// интересны из них последние — те, что рядом с бедой.
    /// </summary>
    public partial class LogView : UserControl
    {
        /// <summary>Сколько последних строк показывать.</summary>
        private const int TailLines = 400;

        public LogView()
        {
            InitializeComponent();

            RefreshButton.Click += (_, _) => Reload();
            CopyButton.Click += OnCopyClicked;
            ClearButton.Click += OnClearClicked;

            PathBlock.Text = "Папка журналов: " + App.LogDirectory;
            Reload();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Reload();
        }

        /// <summary>Самый свежий файл журнала или null, если их ещё нет.</summary>
        private static string? LatestFile()
        {
            try
            {
                if (!Directory.Exists(App.LogDirectory))
                    return null;

                return Directory
                    .EnumerateFiles(App.LogDirectory, "*.log", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private void Reload()
        {
            var path = LatestFile();

            if (path is null)
            {
                LogBlock.Text = string.Empty;
                StatusBlock.Text = "Журнала ещё нет.";
                return;
            }

            try
            {
                // Читается с разрешением на чужую запись: приёмник Serilog держит
                // этот же файл открытым и дописывает в него прямо сейчас.
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                var tail = new string[TailLines];
                int count = 0;
                int total = 0;

                while (reader.ReadLine() is { } line)
                {
                    tail[total % TailLines] = line;
                    total++;
                    if (count < TailLines) count++;
                }

                var builder = new StringBuilder();
                int first = total > TailLines ? total % TailLines : 0;

                for (int i = 0; i < count; i++)
                    builder.AppendLine(tail[(first + i) % TailLines]);

                LogBlock.Text = builder.ToString();

                StatusBlock.Text = total > count
                    ? $"{Path.GetFileName(path)} — последние {count} строк из {total}"
                    : $"{Path.GetFileName(path)} — {total} строк";

                Scroll.ScrollToEnd();
            }
            catch (Exception ex)
            {
                LogBlock.Text = string.Empty;
                StatusBlock.Text = "Не удалось прочитать журнал: " + ex.Message;
            }
        }

        private async void OnCopyClicked(object? sender, RoutedEventArgs e)
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                StatusBlock.Text = "Буфер обмена недоступен.";
                return;
            }

            try
            {
                await clipboard.SetTextAsync(LogBlock.Text ?? string.Empty);
                StatusBlock.Text = "Журнал скопирован в буфер обмена.";
            }
            catch (Exception ex)
            {
                StatusBlock.Text = "Не удалось скопировать: " + ex.Message;
            }
        }

        /// <summary>
        /// Стирает журналы, кроме того, в который пишут прямо сейчас: он открыт
        /// приёмником, и удалить его нельзя, не уронив запись.
        /// </summary>
        private void OnClearClicked(object? sender, RoutedEventArgs e)
        {
            var current = LatestFile();
            int removed = 0;

            try
            {
                foreach (var path in Directory.EnumerateFiles(App.LogDirectory, "*.log"))
                {
                    if (string.Equals(path, current, StringComparison.OrdinalIgnoreCase)) continue;

                    File.Delete(path);
                    removed++;
                }
            }
            catch (Exception ex)
            {
                StatusBlock.Text = "Не удалось очистить: " + ex.Message;
                return;
            }

            StatusBlock.Text = removed > 0
                ? $"Удалено файлов: {removed}. Нынешний журнал остался — в него идёт запись."
                : "Удалять нечего: есть только нынешний журнал.";
        }
    }
}
