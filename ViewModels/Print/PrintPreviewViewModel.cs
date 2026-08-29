using Avalonia.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using Serilog;
using SkiaSharp;
using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Writersword.Core.Interfaces.Print;
using Writersword.Core.Interfaces.Services.UI;

namespace Writersword.ViewModels.Print
{
    /// <summary>
    /// ViewModel окна предпросмотра печати.
    /// Управляет навигацией по страницам, масштабированием,
    /// рендерингом текущей страницы в Avalonia Bitmap для отображения в Image.
    /// Размеры листа вычисляются из реальных физических размеров PageSettings —
    /// корректно работает для любого формата бумаги включая кастомные.
    /// </summary>
    public sealed class PrintPreviewViewModel : ReactiveObject, IDisposable
    {
        private static readonly ILogger _logger = Log.ForContext<PrintPreviewViewModel>();

        /// <summary>
        /// Разрешение рендеринга превью в DPI.
        /// 150 — баланс качества и скорости для экранного просмотра.
        /// </summary>
        private const float PreviewDpi = 150f;

        private readonly IPrintableDocument _document;
        private readonly IPrintService _printService;

        private int _currentPageIndex;
        private double _zoomLevel = 1.0;
        private Bitmap? _previewBitmap;
        private bool _isRendering;
        private bool _disposed;

        // ── Базовые размеры листа в пикселях при ZoomLevel = 1.0 ──────────

        /// <summary>
        /// Базовая ширина страницы в пикселях при ZoomLevel = 1.0.
        /// Вычисляется из физической ширины страницы и PreviewDpi.
        /// Формула: physicalWidthMm * PreviewDpi / 25.4
        /// </summary>
        private readonly double _basePreviewWidthPx;

        /// <summary>
        /// Базовая высота страницы в пикселях при ZoomLevel = 1.0.
        /// Вычисляется из физической высоты страницы и PreviewDpi.
        /// Формула: physicalHeightMm * PreviewDpi / 25.4
        /// </summary>
        private readonly double _basePreviewHeightPx;

        // ── Свойства ──────────────────────────────────────────────────────

        /// <summary>Индекс текущей страницы (0-based).</summary>
        public int CurrentPageIndex
        {
            get => _currentPageIndex;
            private set
            {
                this.RaiseAndSetIfChanged(ref _currentPageIndex, value);
                this.RaisePropertyChanged(nameof(CurrentPageDisplay));
                this.RaisePropertyChanged(nameof(CanNavigatePrev));
                this.RaisePropertyChanged(nameof(CanNavigateNext));
            }
        }

        /// <summary>Номер страницы для отображения в UI (1-based).</summary>
        public int CurrentPageDisplay => _currentPageIndex + 1;

        /// <summary>Общее количество страниц.</summary>
        public int PageCount => _document.PageCount;

        /// <summary>Заголовок документа для заголовка окна.</summary>
        public string DocumentTitle => _document.Title;

        /// <summary>
        /// Текущий масштаб превью. 1.0 = 100%.
        /// При изменении пересчитываются ScaledPreviewWidthPx и ScaledPreviewHeightPx.
        /// </summary>
        public double ZoomLevel
        {
            get => _zoomLevel;
            private set
            {
                this.RaiseAndSetIfChanged(ref _zoomLevel, Math.Clamp(value, 0.25, 4.0));
                this.RaisePropertyChanged(nameof(ZoomPercent));
                this.RaisePropertyChanged(nameof(ScaledPreviewWidthPx));
                this.RaisePropertyChanged(nameof(ScaledPreviewHeightPx));
            }
        }

        /// <summary>Масштаб в процентах для отображения в UI.</summary>
        public int ZoomPercent => (int)Math.Round(_zoomLevel * 100);

        /// <summary>
        /// Ширина Image в пикселях с учётом ZoomLevel.
        /// Биндится напрямую к Width Image в XAML.
        /// Корректна для любого формата бумаги — A4, A3, книжный, кастомный.
        /// </summary>
        public double ScaledPreviewWidthPx => _basePreviewWidthPx * _zoomLevel;

        /// <summary>
        /// Высота Image в пикселях с учётом ZoomLevel.
        /// Биндится напрямую к Height Image в XAML.
        /// </summary>
        public double ScaledPreviewHeightPx => _basePreviewHeightPx * _zoomLevel;

        /// <summary>Bitmap текущей страницы для привязки к Image в XAML.</summary>
        public Bitmap? PreviewBitmap
        {
            get => _previewBitmap;
            private set => this.RaiseAndSetIfChanged(ref _previewBitmap, value);
        }

        /// <summary>True пока идёт рендеринг страницы.</summary>
        public bool IsRendering
        {
            get => _isRendering;
            private set
            {
                this.RaiseAndSetIfChanged(ref _isRendering, value);
                this.RaisePropertyChanged(nameof(CanNavigatePrev));
                this.RaisePropertyChanged(nameof(CanNavigateNext));
            }
        }

        public bool CanNavigatePrev => _currentPageIndex > 0 && !_isRendering;
        public bool CanNavigateNext => _currentPageIndex < _document.PageCount - 1 && !_isRendering;

        // ── Команды ───────────────────────────────────────────────────────

        /// <summary>Перейти к предыдущей странице.</summary>
        public ICommand NavigatePrevCommand { get; }

        /// <summary>Перейти к следующей странице.</summary>
        public ICommand NavigateNextCommand { get; }

        /// <summary>Перейти к первой странице.</summary>
        public ICommand NavigateFirstCommand { get; }

        /// <summary>Перейти к последней странице.</summary>
        public ICommand NavigateLastCommand { get; }

        /// <summary>Увеличить масштаб на 25%.</summary>
        public ICommand ZoomInCommand { get; }

        /// <summary>Уменьшить масштаб на 25%.</summary>
        public ICommand ZoomOutCommand { get; }

        /// <summary>Сбросить масштаб к 100%.</summary>
        public ICommand ZoomResetCommand { get; }

        /// <summary>
        /// Отправить на печать через системный диалог ОС.
        /// Рендерит PDF во временный файл и передаёт его ОС.
        /// </summary>
        public ICommand PrintCommand { get; }

        /// <summary>Сохранить как PDF — открывает диалог выбора пути.</summary>
        public ICommand SavePdfCommand { get; }

        /// <summary>Закрыть окно превью.</summary>
        public ICommand CloseCommand { get; }

        // ── Constructor ───────────────────────────────────────────────────

        public PrintPreviewViewModel(IPrintableDocument document, IPrintService printService)
        {
            _document = document ?? throw new ArgumentNullException(nameof(document));
            _printService = printService ?? throw new ArgumentNullException(nameof(printService));

            // Вычисляем базовые размеры листа из реальных физических размеров.
            // Формула: physicalMm * PreviewDpi / 25.4
            // Работает для любого формата: A4, A3, A5, Letter, книжный, кастомный.
            var ps = document.PageSettings;
            _basePreviewWidthPx = ps.GetPhysicalWidthMm() * PreviewDpi / 25.4;
            _basePreviewHeightPx = ps.GetPhysicalHeightMm() * PreviewDpi / 25.4;

            NavigatePrevCommand = ReactiveCommand.CreateFromTask(
                NavigatePrevAsync,
                this.WhenAnyValue(x => x.CanNavigatePrev));
            NavigateNextCommand = ReactiveCommand.CreateFromTask(
                NavigateNextAsync,
                this.WhenAnyValue(x => x.CanNavigateNext));
            NavigateFirstCommand = ReactiveCommand.CreateFromTask(NavigateFirstAsync);
            NavigateLastCommand = ReactiveCommand.CreateFromTask(NavigateLastAsync);

            ZoomInCommand = ReactiveCommand.Create(() => ZoomLevel += 0.25);
            ZoomOutCommand = ReactiveCommand.Create(() => ZoomLevel -= 0.25);
            ZoomResetCommand = ReactiveCommand.Create(() => ZoomLevel = 1.0);

            PrintCommand = ReactiveCommand.CreateFromTask(ExecutePrintAsync);
            SavePdfCommand = ReactiveCommand.CreateFromTask(ExecuteSavePdfAsync);
            CloseCommand = ReactiveCommand.Create(() => { });

            _ = RenderCurrentPageAsync();
        }

        // ── Навигация ─────────────────────────────────────────────────────

        private async Task NavigatePrevAsync()
        {
            if (_currentPageIndex <= 0) return;
            CurrentPageIndex--;
            await RenderCurrentPageAsync();
        }

        private async Task NavigateNextAsync()
        {
            if (_currentPageIndex >= _document.PageCount - 1) return;
            CurrentPageIndex++;
            await RenderCurrentPageAsync();
        }

        private async Task NavigateFirstAsync()
        {
            if (_currentPageIndex == 0) return;
            CurrentPageIndex = 0;
            await RenderCurrentPageAsync();
        }

        private async Task NavigateLastAsync()
        {
            int last = _document.PageCount - 1;
            if (_currentPageIndex == last) return;
            CurrentPageIndex = last;
            await RenderCurrentPageAsync();
        }

        // ── Рендеринг превью ──────────────────────────────────────────────

        /// <summary>
        /// Рендерит текущую страницу в Avalonia Bitmap.
        /// Выполняется в фоновом потоке, результат передаётся в UI-поток.
        /// Размер SKBitmap вычисляется из реальных физических размеров страницы —
        /// точен для любого формата бумаги.
        /// </summary>
        private async Task RenderCurrentPageAsync()
        {
            if (_disposed) return;

            IsRendering = true;

            try
            {
                int pageIndex = _currentPageIndex;

                Bitmap? bitmap = await Task.Run(() => RenderPageToBitmap(pageIndex));

                if (!_disposed)
                {
                    var old = PreviewBitmap;
                    PreviewBitmap = bitmap;
                    old?.Dispose();
                }
                else
                {
                    bitmap?.Dispose();
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to render page {Index}", _currentPageIndex);
            }
            finally
            {
                IsRendering = false;
            }
        }

        /// <summary>
        /// Рендерит одну страницу в SKBitmap и конвертирует в Avalonia Bitmap.
        /// Размер в пикселях вычисляется из физических размеров страницы и PreviewDpi.
        /// Формула: physicalMm * PreviewDpi / 25.4
        /// </summary>
        private Bitmap RenderPageToBitmap(int pageIndex)
        {
            var ps = _document.PageSettings;

            float pageWidthPt = (float)(ps.GetPhysicalWidthMm() * 72.0 / 25.4);
            float pageHeightPt = (float)(ps.GetPhysicalHeightMm() * 72.0 / 25.4);

            int pixelWidth = (int)Math.Round(_basePreviewWidthPx);
            int pixelHeight = (int)Math.Round(_basePreviewHeightPx);

            float scale = PreviewDpi / 72f;

            using var skBitmap = new SKBitmap(
                pixelWidth, pixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(skBitmap);

            canvas.Clear(SKColors.White);
            canvas.Scale(scale, scale);

            _document.RenderPage(pageIndex, canvas, pageWidthPt, pageHeightPt);

            canvas.Flush();

            using var skImage = SKImage.FromBitmap(skBitmap);
            using var data = skImage.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = new System.IO.MemoryStream(data.ToArray());
            return new Bitmap(stream);
        }

        // ── Печать и сохранение ───────────────────────────────────────────

        private async Task ExecutePrintAsync()
        {
            try
            {
                _logger.Debug("ExecutePrintAsync: {Title}", _document.Title);
                await _printService.PrintAsync(_document);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to send the document to the printer");
            }
        }

        private async Task ExecuteSavePdfAsync()
        {
            try
            {
                _logger.Debug("ExecuteSavePdfAsync: {Title}", _document.Title);

                var dialog = App.Services
                    .GetRequiredService<IDialogService>();

                string? path = await dialog.SaveFileAsync(
                    defaultFileName: _document.Title + ".pdf");

                if (string.IsNullOrWhiteSpace(path)) return;

                if (!path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    path += ".pdf";

                await _printService.SavePdfAsync(_document, path);

                _logger.Debug("PDF saved: {Path}", path);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to save the PDF");
            }
        }

        /// <summary>
        /// Вычисляет и устанавливает ZoomLevel так чтобы лист вписывался
        /// в доступную ширину области просмотра с отступами.
        /// Вызывается из code-behind при загрузке окна.
        /// </summary>
        public void FitToWidth(double availableWidth)
        {
            if (_basePreviewWidthPx <= 0) return;
            double padding = 48;
            double fitZoom = (availableWidth - padding) / _basePreviewWidthPx;
            ZoomLevel = Math.Clamp(fitZoom, 0.25, 4.0);
        }

        // ── IDisposable ───────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            PreviewBitmap?.Dispose();
            PreviewBitmap = null;
        }
    }
}