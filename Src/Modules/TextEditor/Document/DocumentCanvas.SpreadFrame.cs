using System;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Состояние книги, замороженное на время одного кадра.
    ///
    /// Кадр рисует поток отрисовки, а переворот ведёт поток интерфейса: такт таймера
    /// двигает угол листа, отпускание и конец переворота меняют разворот и летящие
    /// страницы. Полный кадр книги идёт десятки миллисекунд, и прежде он читал эти
    /// поля по ходу дела — каждый раз заново. Стоило углу перейти порог подъёма
    /// посреди кадра, как первая половина страниц рисовалась «лист лежит», а вторая
    /// «лист поднят»: страница под листом уводилась прочь или подменялась следующей,
    /// а сам лист в этот кадр не рисовался вовсе. На экране это и было моргание
    /// страницы при нажатии, в начале движения и в конце переворота.
    ///
    /// Теперь кадр в самом начале снимает копию этих полей, и всё, что он рисует,
    /// читает только её. Поток интерфейса по-прежнему пишет живые значения — копия
    /// принадлежит потоку отрисовки и кадру этого канваса и живёт до конца кадра.
    ///
    /// Поля, по которым несколько значений меняются заодно (начало, конец и сброс
    /// переворота), пишутся под тем же замком, под которым кадр снимает копию: иначе
    /// кадр мог бы застать переворот наполовину переписанным.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        private sealed class SpreadFrameState
        {
            public DocumentCanvas? Owner;
            public int FlipDir;
            public float FlipAngle;
            public int FlipTargetLeft;
            public int FlyFront;
            public int FlyBack;
            public int LeftPage;
            public float PanXPt;
            public float PanYPt;
        }

        // Копия текущего кадра на этом потоке. Потоковая: поток интерфейса её не видит
        // и читает живые поля.
        [ThreadStatic]
        private static SpreadFrameState? t_spreadFrame;

        // Буфер копии этого канваса. Кадры канваса идут строго по одному, поэтому
        // буфер переиспользуется и мусора на кадр не создаёт.
        private readonly SpreadFrameState _spreadFrameBuffer = new();

        // Замок согласованной записи: под ним кадр снимает копию, а поток интерфейса
        // меняет несколько полей переворота разом.
        private readonly object _spreadFrameLock = new();

        // Живые значения. Пишет их только поток интерфейса.
        private int _spreadFlipDirLive;
        private float _spreadFlipAngleLive;
        private int _spreadFlipTargetLeftLive = -1;
        private int _spreadFlyFrontLive = -1;
        private int _spreadFlyBackLive = -1;
        private int _spreadLeftPageLive;
        private float _readingPanXPtLive;
        private float _readingPanYPtLive;

        /// <summary>Копия кадра, если этот канвас сейчас рисует кадр на этом потоке.</summary>
        private SpreadFrameState? SpreadFrame
        {
            get
            {
                var f = t_spreadFrame;
                return f is not null && ReferenceEquals(f.Owner, this) ? f : null;
            }
        }

        // Направление идущего переворота: 0 — покоя нет, +1 — вперёд, -1 — назад.
        private int _spreadFlipDir
        {
            get => SpreadFrame is { } f ? f.FlipDir : _spreadFlipDirLive;
            set => _spreadFlipDirLive = value;
        }

        // Угол листа в градусах: 0 — лежит на своей стороне, 180 — лёг на другую.
        private float _spreadFlipAngle
        {
            get => SpreadFrame is { } f ? f.FlipAngle : _spreadFlipAngleLive;
            set => _spreadFlipAngleLive = value;
        }

        // На какой разворот лист ложится.
        private int _spreadFlipTargetLeft
        {
            get => SpreadFrame is { } f ? f.FlipTargetLeft : _spreadFlipTargetLeftLive;
            set => _spreadFlipTargetLeftLive = value;
        }

        // Страницы, которые обычный проход не рисует: они летят как отдельный лист.
        private int _spreadFlyFront
        {
            get => SpreadFrame is { } f ? f.FlyFront : _spreadFlyFrontLive;
            set => _spreadFlyFrontLive = value;
        }

        private int _spreadFlyBack
        {
            get => SpreadFrame is { } f ? f.FlyBack : _spreadFlyBackLive;
            set => _spreadFlyBackLive = value;
        }

        // Левая страница текущего разворота. Развороты идут парами: (0,1), (2,3)…
        private int _spreadLeftPage
        {
            get => SpreadFrame is { } f ? f.LeftPage : _spreadLeftPageLive;
            set => _spreadLeftPageLive = value;
        }

        // Сдвиг книги относительно центра видимой области, в пунктах. Пока книга
        // помещается в окно, он всегда нулевой.
        private float _readingPanXPt
        {
            get => SpreadFrame is { } f ? f.PanXPt : _readingPanXPtLive;
            set => _readingPanXPtLive = value;
        }

        private float _readingPanYPt
        {
            get => SpreadFrame is { } f ? f.PanYPt : _readingPanYPtLive;
            set => _readingPanYPtLive = value;
        }

        // Левая страница разворота, нарисованного в снимке кадра (_displayImage).
        // Быстрый путь кладёт этот снимок, не перерисовывая книгу, и после смены
        // разворота он показал бы на один кадр прежние страницы: конец переворота
        // меняет разворот раньше, чем помечает содержимое устаревшим. Расхождение
        // с текущим разворотом отправляет кадр в полный рендер. -1 — снимок не книжный.
        private int _displayImageSpreadLeft = -1;

        /// <summary>
        /// Начало кадра: снимает копию состояния книги для потока отрисовки.
        /// </summary>
        private void BeginSpreadFrame()
        {
            var f = _spreadFrameBuffer;

            lock (_spreadFrameLock)
            {
                f.FlipDir = _spreadFlipDirLive;
                f.FlipAngle = _spreadFlipAngleLive;
                f.FlipTargetLeft = _spreadFlipTargetLeftLive;
                f.FlyFront = _spreadFlyFrontLive;
                f.FlyBack = _spreadFlyBackLive;
                f.LeftPage = _spreadLeftPageLive;
                f.PanXPt = _readingPanXPtLive;
                f.PanYPt = _readingPanYPtLive;
            }

            f.Owner = this;
            t_spreadFrame = f;
        }

        /// <summary>Конец кадра: поток отрисовки снова читает живые значения.</summary>
        private void EndSpreadFrame()
        {
            if (ReferenceEquals(t_spreadFrame, _spreadFrameBuffer))
                t_spreadFrame = null;

            _spreadFrameBuffer.Owner = null;
        }
    }
}
