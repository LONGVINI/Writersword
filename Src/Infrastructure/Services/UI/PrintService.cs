using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Serilog;
using SkiaSharp;
using Writersword.Core.Interfaces.Print;
using Writersword.Core.Interfaces.Services.UI;
using Writersword.ViewModels.Print;
using Writersword.Views.Print;

namespace Writersword.Infrastructure.Services.UI
{
    /// <summary>
    /// Реализация сервиса печати.
    /// Рендерит документ через SkiaSharp в PDF, открывает окно PrintPreview,
    /// передаёт PDF операционной системе для вывода на принтер.
    /// Зависит только от SkiaSharp — сторонних PDF-библиотек не требует.
    /// </summary>
    public sealed class PrintService : IPrintService
    {
        private static readonly ILogger _logger = Log.ForContext<PrintService>();

        /// <summary>
        /// Конвертирует миллиметры в points (1 pt = 1/72 дюйма).
        /// </summary>
        private static float MmToPt(double mm) => (float)(mm * 72.0 / 25.4);

        // ── IPrintService ─────────────────────────────────────────────────

        /// <inheritdoc/>
        public async Task ShowPrintPreviewAsync(IPrintableDocument document, Window owner)
        {
            _logger.Debug("ShowPrintPreviewAsync: title={Title}, pages={Pages}",
                document.Title, document.PageCount);

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var vm = new PrintPreviewViewModel(document, this);
                var window = new PrintPreviewView { DataContext = vm };
                await window.ShowDialog(owner);
            });
        }

        /// <inheritdoc/>
        public Task SavePdfAsync(IPrintableDocument document, string outputPath)
        {
            _logger.Debug("SavePdfAsync: title={Title}, output={Path}",
                document.Title, outputPath);

            return Task.Run(() => RenderToPdfFile(document, outputPath));
        }

        /// <inheritdoc/>
        public async Task PrintAsync(IPrintableDocument document)
        {
            _logger.Debug("PrintAsync: title={Title}", document.Title);

            string tempPath = Path.Combine(
                Path.GetTempPath(),
                $"writersword_print_{Guid.NewGuid():N}.pdf");

            try
            {
                await Task.Run(() => RenderToPdfFile(document, tempPath));
                OpenWithSystemPrintDialog(tempPath);

                // Удаляем временный файл через 60 секунд —
                // достаточно для того чтобы ОС успела передать его спулеру.
                _ = Task.Delay(TimeSpan.FromSeconds(60)).ContinueWith(_ =>
                {
                    try
                    {
                        if (File.Exists(tempPath))
                            File.Delete(tempPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "Не удалось удалить временный PDF: {Path}", tempPath);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Ошибка при подготовке печати");

                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); }
                    catch { /* игнорируем — файл мог быть уже удалён */ }
                }

                throw;
            }
        }

        // ── Рендеринг в PDF ───────────────────────────────────────────────

        /// <summary>
        /// Рендерит все страницы документа в PDF-файл через SKDocument.
        /// Каждая страница рендерится в физических pt — точное соответствие бумаге.
        /// </summary>
        private static void RenderToPdfFile(IPrintableDocument document, string outputPath)
        {
            var settings = document.PageSettings;
            float pageWidthPt = MmToPt(settings.GetPhysicalWidthMm());
            float pageHeightPt = MmToPt(settings.GetPhysicalHeightMm());

            var metadata = new SKDocumentPdfMetadata
            {
                Title = document.Title,
                Creator = "Writersword",
                Creation = DateTime.Now,
                Modified = DateTime.Now
            };

            using var stream = new SKFileWStream(outputPath);
            using var pdf = SKDocument.CreatePdf(stream, metadata);

            for (int i = 0; i < document.PageCount; i++)
            {
                SKCanvas canvas = pdf.BeginPage(pageWidthPt, pageHeightPt);

                // Белый фон страницы — принтер не печатает «прозрачность».
                canvas.Clear(SKColors.White);

                document.RenderPage(i, canvas, pageWidthPt, pageHeightPt);

                pdf.EndPage();
            }

            pdf.Close();
        }

        // ── Платформо-специфичный запуск ──────────────────────────────────

        /// <summary>
        /// Открывает PDF через системный диалог печати.
        /// Windows: verb=print через ShellExecute.
        /// macOS: open -a Preview.
        /// Linux: xdg-open (открывает приложение по умолчанию для PDF).
        /// </summary>
        private static void OpenWithSystemPrintDialog(string pdfPath)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = pdfPath,
                    UseShellExecute = true,
                    Verb = "print"
                });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    Arguments = $"-a Preview \"{pdfPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            else
            {
                // Linux и прочие Unix-системы.
                Process.Start(new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    Arguments = $"\"{pdfPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
        }
    }
}