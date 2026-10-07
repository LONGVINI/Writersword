using System;
using Avalonia.Threading;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Перетаскивание плавающей картинки без пересборки всей раскладки на каждом шаге.
    ///
    /// Раньше каждое движение мыши пересобирало раскладку документа целиком: на
    /// рукописи в две-три тысячи абзацев это треть секунды на шаг, и картинка
    /// ползла за указателем рывками, а UI-поток стоял секундами. Так же уже
    /// двигаются фигуры (DocumentCanvas.Shapes): их запись на листе сдвигается на
    /// приращение, раскладка — только если фигура двигает текст.
    ///
    /// Картинка поверх текста и за текстом текст не двигает: её запись на листе
    /// сдвигается на приращение смещения, и этого достаточно. Картинка с
    /// обтеканием двигает текст — её запись тоже сдвигается сразу, чтобы картинка
    /// шла за указателем без задержки, а текст переверстывается не чаще, чем
    /// успевает раскладка: шаги, пришедшие за время пересборки, сливаются в одну.
    /// Отпускание кнопки, как и прежде, досчитывает раскладку до сходимости.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        /// <summary>Не чаще этого (мс) переверстывается текст вокруг перетаскиваемой картинки.</summary>
        private const int ImageDragRelayoutIntervalMs = 90;

        // Запись картинки на листе на момент нажатия: от неё отсчитывается сдвиг.
        private float _imgDragEntryX0;
        private float _imgDragEntryY0;
        private int _imgDragEntryPage0;

        private DispatcherTimer? _imageDragRelayoutTimer;
        private bool _imageDragRelayoutPending;

        /// <summary>Двигает ли картинка текст: у неё обтекание.</summary>
        private static bool ImageAffectsFlow(ImageBlock image)
            => image.WrapMode is WrapMode.Square or WrapMode.Tight;

        /// <summary>Запоминает запись картинки на листе в начале перетаскивания.</summary>
        private void CaptureImageDragEntry(float xPt, float yPt, int pageIndex)
        {
            _imgDragEntryX0 = xPt;
            _imgDragEntryY0 = yPt;
            _imgDragEntryPage0 = pageIndex;
            _imageDragRelayoutPending = false;
        }

        /// <summary>
        /// Сдвигает запись перетаскиваемой картинки на листе на приращение её
        /// смещения с начала жеста. Страница записи определяется заново: картинка,
        /// утащенная на соседний лист, рисуется и обрезается уже по нему. У
        /// привязанной к странице картинки страница остаётся её.
        /// </summary>
        private void MoveDraggedImageEntry()
        {
            var image = _selectedImage;
            if (image is null) return;

            float dx = ReadingOffsetXPt(image.OffsetXPt) - ReadingOffsetXPt(_imgDragStartOffX);
            float dy = ReadingOffsetYPt(image.OffsetYPt) - ReadingOffsetYPt(_imgDragStartOffY);

            float xPt = _imgDragEntryX0 + dx;
            float yPt = _imgDragEntryY0 + dy;

            lock (_renderLock)
            {
                for (int i = 0; i < _images.Count; i++)
                {
                    var entry = _images[i];
                    if (!ReferenceEquals(entry.Block, image) || entry.InLine) continue;

                    int pageIndex = image.PinnedPage > 0
                        ? entry.PageIndex
                        : ResolveFloatingObjectPage(xPt, yPt, entry.WidthPt, entry.HeightPt, _pages, _imgDragEntryPage0);

                    // Список заменяется копией: поток отрисовки может как раз обходить
                    // прежний, и правка на месте сломала бы ему перечисление.
                    var updated = new System.Collections.Generic.List<ImageEntry>(_images);
                    updated[i] = entry with
                    {
                        XPt = xPt,
                        Ypt = yPt,
                        PageIndex = pageIndex
                    };
                    _images = updated;
                    return;
                }
            }
        }

        /// <summary>
        /// Просит переверстать текст вокруг перетаскиваемой картинки. Пересборка
        /// идёт по таймеру: шаги мыши, пришедшие до неё, сливаются в одну.
        /// </summary>
        private void RequestImageDragRelayout()
        {
            _imageDragRelayoutPending = true;

            if (_imageDragRelayoutTimer is null)
            {
                _imageDragRelayoutTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(ImageDragRelayoutIntervalMs)
                };
                _imageDragRelayoutTimer.Tick += OnImageDragRelayoutTick;
            }

            if (!_imageDragRelayoutTimer.IsEnabled) _imageDragRelayoutTimer.Start();
        }

        private void OnImageDragRelayoutTick(object? sender, EventArgs e)
        {
            _imageDragRelayoutTimer?.Stop();

            if (!_imageDragRelayoutPending || !_imageDragging || _selectedImage is null) return;

            _imageDragRelayoutPending = false;
            RebuildLayouts();
            InvalidateFull();
        }

        /// <summary>Снимает отложенную пересборку: жест закончен, раскладку досчитает отпускание.</summary>
        private void StopImageDragRelayout()
        {
            _imageDragRelayoutPending = false;
            _imageDragRelayoutTimer?.Stop();
        }

        /// <summary>
        /// Шаг перетаскивания: запись картинки сдвигается сразу, текст
        /// переверстывается, только если картинка его обтекает.
        /// </summary>
        private void RefreshAfterImageDragStep()
        {
            MoveDraggedImageEntry();

            if (_selectedImage is not null && ImageAffectsFlow(_selectedImage))
                RequestImageDragRelayout();

            InvalidateFull();
        }
    }
}
