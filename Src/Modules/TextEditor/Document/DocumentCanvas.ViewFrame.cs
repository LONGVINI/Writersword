using Avalonia;
using System;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Состояние вида, замороженное на кадр: масштаб, ширина холста, прокрутка и
    /// размер полотна в ту минуту, когда кадр записан.
    ///
    /// Кадр полотна записывается в потоке интерфейса (Render), а рисуется позже, в
    /// потоке отрисовки (CanvasSKDrawOperation). Раньше отрисовка читала масштаб,
    /// ширину холста и прокрутку живыми — такими, какими они стали к моменту, когда
    /// до кадра дошла очередь. Анимация масштаба меняет их каждые 14 мс, и кадр часто
    /// рисовался уже по следующему масштабу, а окно сдвигало его ещё по прежней
    /// прокрутке: весь текст отскакивал на несколько пикселей туда-обратно, кадр за
    /// кадром.
    ///
    /// Теперь кадр несёт снимок этих величин, сделанный вместе с записью, — тот же,
    /// что окно использует для сдвига полотна в этом кадре. Пока кадр рисуется, поток
    /// отрисовки видит именно его. Поток интерфейса и фоновая дорисовка снимков, как
    /// и раньше, видят живые значения.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        private sealed class ViewFrame
        {
            public required DocumentCanvas Owner { get; init; }
            public required double Zoom { get; init; }
            public required double CanvasWidth { get; init; }
            public required double ScrollOffsetY { get; init; }
            public required double ViewportHeight { get; init; }
            public required Rect Bounds { get; init; }
        }

        // Кадр, который сейчас рисует этот поток. Только поток отрисовки и только на
        // время кадра; у остальных потоков его нет.
        [ThreadStatic] private static ViewFrame? t_viewFrame;

        /// <summary>Замороженный кадр этого полотна, если поток сейчас его рисует.</summary>
        private ViewFrame? CurrentViewFrame
            => t_viewFrame is { } frame && ReferenceEquals(frame.Owner, this) ? frame : null;

        /// <summary>Снимок вида для записываемого кадра. Поток интерфейса.</summary>
        private ViewFrame CaptureViewFrame() => new()
        {
            Owner = this,
            Zoom = Zoom,
            CanvasWidth = _canvasWidthLive,
            ScrollOffsetY = _parentScrollViewer?.Offset.Y ?? _scrollOffsetYLive,
            ViewportHeight = _viewportHeightLive,
            Bounds = base.Bounds
        };

        /// <summary>Ширина холста: в кадре — замороженная, иначе живая.</summary>
        private double _canvasWidth
        {
            get => CurrentViewFrame?.CanvasWidth ?? _canvasWidthLive;
            set => _canvasWidthLive = value;
        }

        /// <summary>Прокрутка по вертикали: в кадре — замороженная, иначе живая.</summary>
        private double _scrollOffsetY
        {
            get => CurrentViewFrame?.ScrollOffsetY ?? _scrollOffsetYLive;
            set => _scrollOffsetYLive = value;
        }

        /// <summary>Высота окна: в кадре — замороженная, иначе живая.</summary>
        private double _viewportHeight
        {
            get => CurrentViewFrame?.ViewportHeight ?? _viewportHeightLive;
            set => _viewportHeightLive = value;
        }

        /// <summary>
        /// Размер полотна: в кадре — тот, с которым кадр записан. Высота полотна
        /// меняется вместе с масштабом, и по ней отрисовка выбирает полосу снимка.
        /// </summary>
        private new Rect Bounds => CurrentViewFrame?.Bounds ?? base.Bounds;
    }
}
