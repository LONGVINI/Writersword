using System;
using Avalonia.Threading;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Перерисовка после загрузки картинок — одна на пачку, а не на каждую.
    ///
    /// Картинка грузится в фоне, и до её прихода на листе пустое место; пришла —
    /// снимок листов пересобирается полностью, иначе дыра так и осталась бы. Раньше
    /// это делалось на каждую картинку: прокрутка к странице с полутора десятками
    /// картинок давала полтора десятка полных рендеров подряд, по кадру на каждый, и
    /// прокрутка у картинок проседала до рывков. Картинки одной страницы приходят
    /// почти разом, поэтому их приход собирается в одну пересборку: первая
    /// пришедшая заводит короткий таймер, остальные успевают к нему.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        /// <summary>Сколько (мс) ждать остальные картинки пачки перед пересборкой снимка.</summary>
        private const int ImageLoadRefreshDelayMs = 60;

        private DispatcherTimer? _imageLoadRefreshTimer;

        /// <summary>
        /// Картинка загружена. Вызывается на UI-потоке. Пересборка снимка — одна на
        /// все картинки, пришедшие за время ожидания.
        /// </summary>
        private void RequestImageLoadRefresh()
        {
            if (_imageLoadRefreshTimer is null)
            {
                _imageLoadRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(ImageLoadRefreshDelayMs)
                };
                _imageLoadRefreshTimer.Tick += OnImageLoadRefreshTick;
            }

            // Таймер не перезапускается: при непрерывной прокрутке картинки шли бы
            // одна за другой, и пересборка откладывалась бы без конца.
            if (!_imageLoadRefreshTimer.IsEnabled) _imageLoadRefreshTimer.Start();
        }

        private void OnImageLoadRefreshTick(object? sender, EventArgs e)
        {
            _imageLoadRefreshTimer?.Stop();

            // Сбрасываем снимок, чтобы полный рендер отрисовал только что загруженные
            // картинки, а не прежний снимок с пустыми местами.
            _contentDirty = true;

            // И снимки страниц книги: снятые до загрузки, они застыли бы с дырой на
            // месте картинки навсегда — снимок берётся один раз и переживает и
            // переворот, и возврат к той же странице.
            InvalidateSpreadSnapshots();

            InvalidateVisual();
        }
    }
}
