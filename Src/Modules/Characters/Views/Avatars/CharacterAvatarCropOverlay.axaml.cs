using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Serilog;
using System;
using System.Threading.Tasks;
using Writersword.Modules.Characters.Models;

namespace Writersword.Modules.Characters.Views.Avatars
{
    /// <summary>
    /// Обрезка аватарки по центру модуля, со скримом — как выбор аватарки и
    /// настройки карточки. Результат отдаётся через ShowAsync: кадр в долях
    /// исходной картинки или null при отмене.
    ///
    /// Картинка не принадлежит окну: битмап приходит снаружи и снаружи же
    /// освобождается. Окно только показывает его и считает кадр.
    ///
    /// Карточка справа тоже не принадлежит окну: контекстом данных ставится
    /// вью-модель настоящей карточки, и правки цвета и кольца уходят персонажу
    /// напрямую, минуя результат обрезки. Отмена откатывает кадр, но не цвет —
    /// ровно так же, как если бы цвет меняли на самой карточке.
    /// </summary>
    public partial class CharacterAvatarCropOverlay : UserControl
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterAvatarCropOverlay>();

        // Сторона полотна кадра. Задана и здесь, и в разметке: разметке нужен
        // размер до первого прохода раскладки, расчётам — то же число сразу.
        private const double ViewportSide = 420.0;

        // Отступ рамки кадра от края полотна. Без него рамка доходила до
        // самого края, попадала в скругление полотна и обрывалась в углах.
        private const double FrameInset = 14.0;

        // Насколько тёмная линия отстоит от белой рамки наружу. Меньше
        // FrameInset, иначе линия вышла бы за полотно и попала в скругление.
        private const double FrameOutlineGap = 3.0;

        // Размеры коробок превью — те же, что и у настоящей карточки при её
        // размере по умолчанию: верхняя зона 60, аватарка 60-12=48. Ширина
        // полосы зависит ещё и от толщины рамки карточки, поэтому здесь она
        // только запасная — рабочую берёт код по границам самой зоны.
        private const double PreviewCircleSide = 48.0;
        private const double PreviewStripWidth = 148.0;
        private const double PreviewStripHeight = 60.0;
        private const double PreviewTinySide = 44.0;

        // Предел приближения: во сколько раз кадр может стать мельче того
        // размера, при котором картинка ровно закрывает рамку.
        private const double MaxZoomFactor = 6.0;

        private Bitmap? _source;
        private double _imageWidth;
        private double _imageHeight;

        // Масштаб показа: точек экрана на точку картинки.
        private double _scale = 1.0;

        // Левый верхний угол картинки в координатах полотна.
        private double _offsetX;
        private double _offsetY;

        // Рамка кадра в координатах полотна. Всегда квадратная: и кружок
        // карточки, и мелкая плитка квадратные, а полоска в любом случае
        // режет кадр по-своему.
        private double _frameX = FrameInset;
        private double _frameY = FrameInset;
        private double _frameWidth = ViewportSide - FrameInset * 2;
        private double _frameHeight = ViewportSide - FrameInset * 2;

        private bool _dragging;
        private Point _dragOrigin;
        private double _dragOffsetX;
        private double _dragOffsetY;

        // Ползунок масштаба двигает и сам код (сброс, смена пропорций).
        // Флаг отделяет такие правки от движения рукой, иначе обработчик
        // ползунка пересчитал бы масштаб поверх только что заданного.
        private bool _suppressZoomEvent;

        private TaskCompletionSource<CharacterAvatarCropPair?>? _tcs;

        // Какой из двух кадров правят сейчас. Кадра два, потому что карточка
        // показывает аватарку двумя способами: кружку нужен квадрат вокруг
        // лица, полоске — широкая полоса. Один кадр на оба вида означал бы,
        // что один из них всегда обрезан не туда.
        private bool _stripMode;

        // Отложенные кадры: тот, который сейчас не правят, ждёт здесь.
        private CharacterAvatarCrop _circleCrop = CharacterAvatarCrop.Full;
        private CharacterAvatarCrop _stripCrop = CharacterAvatarCrop.Full;

        // Пропорции рамки в режиме полоски — те же, что у цветной зоны
        // карточки: рамка обязана показывать ровно тот прямоугольник, который
        // потом и будет виден.
        private static double StripAspect => PreviewStripWidth / PreviewStripHeight;

        public CharacterAvatarCropOverlay()
        {
            InitializeComponent();

            // Панель не должна вылезать за модуль: при сжатом окне её края
            // вместе с нижними кнопками иначе обрезаются. Тот же приём, что у
            // редактора цвета и у выбора аватарки.
            this.GetObservable(BoundsProperty).Subscribe(b =>
            {
                if (b.Width <= 0) return;
                ApplyPanelMetrics(b.Width, b.Height);
            });

            // Тень лежит на отдельной подложке под панелью и повторяет её
            // размер. Эффект на самой панели заставлял бы перерисовывать её
            // содержимое целиком при каждом движении кадра.
            var shadowSource = this.FindControl<Border>("CropPanel");
            shadowSource?.GetObservable(BoundsProperty).Subscribe(b =>
            {
                var shadow = this.FindControl<Border>("PanelShadow");
                if (shadow == null) return;
                shadow.Width = Math.Max(0, b.Width);
                shadow.Height = Math.Max(0, b.Height);
            });

            // Зона полоски меряется раскладкой: до первого прохода её границы
            // нулевые, и картинка встала бы в неё по запасным числам.
            var stripBox = this.FindControl<Border>("PreviewStripBox");
            stripBox?.GetObservable(BoundsProperty).Subscribe(_ =>
            {
                if (_source != null) RedrawPreviews();
            });
        }

        private void ApplyPanelMetrics(double width, double height)
        {
            var panel = this.FindControl<Border>("CropPanel");
            if (panel is null) return;

            panel.MaxHeight = Math.Max(320, height - 48);
            panel.MaxWidth = Math.Max(320, width - 48);
        }

        /// <summary>
        /// Открыть обрезку. Возвращает кадр или null при отмене. Повторный
        /// вызов при уже открытом окне возвращает задачу текущего показа.
        ///
        /// Битмап остаётся за вызывающей стороной: окно его не освобождает.
        ///
        /// cardContext — вью-модель карточки, для которой выбирают кадр. Если
        /// она передана, справа показывается сама карточка с её цветом,
        /// кольцом и именем, и цвет можно менять прямо отсюда. Если её нет
        /// (например, картинку кладут в папку, а не персонажу), превью
        /// остаётся без цветов, а цветопикер прячется — менять было бы нечего.
        /// </summary>
        public Task<CharacterAvatarCropPair?> ShowAsync(
            Bitmap source,
            CharacterAvatarCrop? initialCrop = null,
            string? title = null,
            object? cardContext = null,
            CharacterAvatarCrop? initialStripCrop = null,
            bool openOnStrip = false)
        {
            if (_tcs != null) return _tcs.Task;
            if (source == null) return Task.FromResult<CharacterAvatarCropPair?>(null);

            _tcs = new TaskCompletionSource<CharacterAvatarCropPair?>();
            _source = source;
            _imageWidth = Math.Max(1, source.PixelSize.Width);
            _imageHeight = Math.Max(1, source.PixelSize.Height);

            var titleText = this.FindControl<TextBlock>("TitleText");
            if (titleText != null && !string.IsNullOrWhiteSpace(title))
                titleText.Text = title;

            ApplyCardContext(cardContext);

            SetImageSource(this.FindControl<Image>("SourceImage"), source);
            SetImageSource(this.FindControl<Image>("PreviewCircleImage"), source);
            SetImageSource(this.FindControl<Image>("PreviewStripImage"), source);
            SetImageSource(this.FindControl<Image>("PreviewTinyImage"), source);

            // Повторный заход в обрезку должен показать ровно то, что было
            // выбрано в прошлый раз: положение и масштаб разворачиваются из
            // сохранённого кадра. Форма рамки при этом не восстанавливается —
            // она всегда квадратная, — поэтому кадр, снятый когда-то рамкой
            // другой формы, подтянется к квадрату вокруг того же центра.
            _circleCrop = initialCrop ?? CharacterAvatarCrop.Full;

            // Полоска без своего кадра начинает с кружкового — там уже выбрано
            // нужное место картинки, и заставлять выбирать его второй раз с
            // нуля незачем.
            _stripCrop = initialStripCrop ?? _circleCrop;

            _stripMode = openOnStrip;
            UpdateModeButtons();

            LayoutFrame();
            ApplyCropToState(_stripMode ? _stripCrop : _circleCrop);
            SyncZoomSliderFromScale();
            Redraw();

            IsVisible = true;
            return _tcs.Task;
        }

        /// <summary>
        /// Подключить карточку к превью. Цветопикер показывается только вместе
        /// с карточкой: без персонажа менять цвет не у чего.
        /// </summary>
        private void ApplyCardContext(object? cardContext)
        {
            var host = this.FindControl<StackPanel>("CardPreviewHost");
            if (host != null) host.DataContext = cardContext;

            var colorRow = this.FindControl<Grid>("CardColorRow");
            if (colorRow != null) colorRow.IsVisible = cardContext != null;
        }

        private static void SetImageSource(Image? image, Bitmap source)
        {
            if (image == null) return;
            image.Source = source;
            image.Stretch = Stretch.Fill;
        }

        // ── Геометрия рамки ───────────────────────────────────────────────

        /// <summary>
        /// Поставить квадратную рамку по центру полотна, отступив от края.
        ///
        /// Отступ обязателен: рамка, доходящая до края, попадает в скругление
        /// полотна и обрывается в углах.
        /// </summary>
        private void LayoutFrame()
        {
            var side = ViewportSide - FrameInset * 2.0;

            if (_stripMode)
            {
                _frameWidth = side;
                _frameHeight = side / StripAspect;
            }
            else
            {
                _frameWidth = side;
                _frameHeight = side;
            }

            _frameX = (ViewportSide - _frameWidth) / 2.0;
            _frameY = (ViewportSide - _frameHeight) / 2.0;
        }

        // ── Переключение вида ─────────────────────────────────────────────

        private void OnModeCircleClick(object? sender, RoutedEventArgs e) => SetMode(false);
        private void OnModeStripClick(object? sender, RoutedEventArgs e) => SetMode(true);

        /// <summary>
        /// Перейти к другому кадру. Текущий кадр сохраняется как есть, рамка
        /// принимает форму нового вида, и на неё раскладывается тот кадр,
        /// который для этого вида уже был выбран.
        /// </summary>
        private void SetMode(bool strip)
        {
            if (_source == null) return;
            if (_stripMode == strip) return;

            StoreCurrentCrop();
            _stripMode = strip;

            LayoutFrame();
            ApplyCropToState(_stripMode ? _stripCrop : _circleCrop);
            SyncZoomSliderFromScale();
            UpdateModeButtons();
            Redraw();
        }

        /// <summary>Записать правимый сейчас кадр в его ячейку.</summary>
        private void StoreCurrentCrop()
        {
            if (_source == null) return;
            var crop = BuildCrop();
            if (_stripMode) _stripCrop = crop; else _circleCrop = crop;
        }

        private void UpdateModeButtons()
        {
            MarkModeButton("ModeCircleButton", !_stripMode);
            MarkModeButton("ModeStripButton", _stripMode);
        }

        private void MarkModeButton(string name, bool active)
        {
            var button = this.FindControl<Button>(name);
            if (button == null) return;

            if (active)
            {
                if (!button.Classes.Contains("active")) button.Classes.Add("active");
            }
            else
            {
                button.Classes.Remove("active");
            }
        }

        /// <summary>Масштаб, при котором картинка ровно закрывает рамку.</summary>
        private double MinScale => Math.Max(
            _frameWidth / _imageWidth,
            _frameHeight / _imageHeight);

        /// <summary>
        /// Развернуть кадр в положение и масштаб картинки. Обратная операция к
        /// BuildCrop.
        /// </summary>
        private void ApplyCropToState(CharacterAvatarCrop crop)
        {
            var displayWidth = _frameWidth / Math.Max(crop.Width, 0.0001);
            _scale = displayWidth / _imageWidth;

            var minScale = MinScale;
            if (_scale < minScale) _scale = minScale;

            _offsetX = _frameX - crop.X * _imageWidth * _scale;
            _offsetY = _frameY - crop.Y * _imageHeight * _scale;
            ClampOffsets();
        }

        /// <summary>
        /// Картинка обязана закрывать рамку целиком: за край её не выпускаем,
        /// иначе в кадр попала бы пустота, которой нет в исходнике.
        /// </summary>
        private void ClampOffsets()
        {
            var displayWidth = _imageWidth * _scale;
            var displayHeight = _imageHeight * _scale;

            var minX = _frameX + _frameWidth - displayWidth;
            var maxX = _frameX;
            var minY = _frameY + _frameHeight - displayHeight;
            var maxY = _frameY;

            if (minX > maxX) minX = maxX;
            if (minY > maxY) minY = maxY;

            if (_offsetX < minX) _offsetX = minX;
            if (_offsetX > maxX) _offsetX = maxX;
            if (_offsetY < minY) _offsetY = minY;
            if (_offsetY > maxY) _offsetY = maxY;
        }

        /// <summary>Свернуть текущее положение и масштаб в доли исходника.</summary>
        private CharacterAvatarCrop BuildCrop()
        {
            var displayWidth = _imageWidth * _scale;
            var displayHeight = _imageHeight * _scale;
            if (displayWidth <= 0 || displayHeight <= 0) return CharacterAvatarCrop.Full;

            var x = (_frameX - _offsetX) / displayWidth;
            var y = (_frameY - _offsetY) / displayHeight;
            var w = _frameWidth / displayWidth;
            var h = _frameHeight / displayHeight;

            return new CharacterAvatarCrop(x, y, w, h);
        }

        // ── Отрисовка ─────────────────────────────────────────────────────

        private void Redraw()
        {
            if (_source == null) return;

            var canvas = this.FindControl<Canvas>("ImageCanvas");
            var image = this.FindControl<Image>("SourceImage");
            if (canvas != null)
            {
                canvas.Width = ViewportSide;
                canvas.Height = ViewportSide;
            }
            if (image != null)
            {
                image.Width = _imageWidth * _scale;
                image.Height = _imageHeight * _scale;
                Canvas.SetLeft(image, _offsetX);
                Canvas.SetTop(image, _offsetY);
            }

            LayoutShade();
            LayoutFrameVisuals();
            RedrawPreviews();
        }

        private void LayoutShade()
        {
            var shadeCanvas = this.FindControl<Canvas>("ShadeCanvas");
            if (shadeCanvas != null)
            {
                shadeCanvas.Width = ViewportSide;
                shadeCanvas.Height = ViewportSide;
            }

            PlaceBox("ShadeTop", 0, 0, ViewportSide, _frameY);
            PlaceBox("ShadeBottom", 0, _frameY + _frameHeight,
                ViewportSide, Math.Max(0, ViewportSide - (_frameY + _frameHeight)));
            PlaceBox("ShadeLeft", 0, _frameY, _frameX, _frameHeight);
            PlaceBox("ShadeRight", _frameX + _frameWidth, _frameY,
                Math.Max(0, ViewportSide - (_frameX + _frameWidth)), _frameHeight);
        }

        private void LayoutFrameVisuals()
        {
            var frameCanvas = this.FindControl<Canvas>("FrameCanvas");
            if (frameCanvas != null)
            {
                frameCanvas.Width = ViewportSide;
                frameCanvas.Height = ViewportSide;
            }

            // Вторая линия отстоит от белой рамки наружу на FrameOutlineGap:
            // рамка толщиной 2 рисуется внутрь своего прямоугольника, значит
            // между двумя линиями остаётся видимый промежуток.
            PlaceBox("FrameOuter",
                _frameX - FrameOutlineGap, _frameY - FrameOutlineGap,
                _frameWidth + FrameOutlineGap * 2.0, _frameHeight + FrameOutlineGap * 2.0);
            PlaceBox("FrameBorder", _frameX, _frameY, _frameWidth, _frameHeight);

            // Круговая подсказка нужна только на квадратном кадре: на нём
            // кружок карточки берёт вписанную окружность, и видно, что из
            // углов в него не попадёт.
            var hint = this.FindControl<Border>("FrameCircleHint");
            if (hint != null)
            {
                hint.IsVisible = !_stripMode;
                if (!_stripMode)
                    PlaceBox("FrameCircleHint", _frameX, _frameY, _frameWidth, _frameHeight);
            }
        }

        private void PlaceBox(string name, double x, double y, double width, double height)
        {
            var box = this.FindControl<Border>(name);
            if (box == null) return;

            box.Width = Math.Max(0, width);
            box.Height = Math.Max(0, height);
            Canvas.SetLeft(box, x);
            Canvas.SetTop(box, y);
        }

        private void RedrawPreviews()
        {
            // Правится один кадр, а показываются оба: видно, что делает
            // текущая правка и что при этом остаётся у другого вида.
            StoreCurrentCrop();

            var crop = _circleCrop;
            var stripCrop = _stripCrop;
            DrawPreview("PreviewCircleCanvas", "PreviewCircleImage", crop, PreviewCircleSide, PreviewCircleSide);

            // Ширина цветной зоны у полоски зависит от толщины рамки карточки,
            // а она у каждого персонажа своя. Числа из разметки идут в дело
            // только до первого прохода раскладки.
            var stripBox = this.FindControl<Border>("PreviewStripBox");
            var stripWidth = stripBox != null && stripBox.Bounds.Width > 1
                ? stripBox.Bounds.Width : PreviewStripWidth;
            var stripHeight = stripBox != null && stripBox.Bounds.Height > 1
                ? stripBox.Bounds.Height : PreviewStripHeight;
            DrawPreview("PreviewStripCanvas", "PreviewStripImage", stripCrop, stripWidth, stripHeight);

            DrawPreview("PreviewTinyCanvas", "PreviewTinyImage", crop, PreviewTinySide, PreviewTinySide);
        }

        /// <summary>
        /// Показать вырезанный кусок в коробке превью так, как его покажет
        /// карточка: кусок растягивается до полного закрытия коробки и режется
        /// по её краям — то же, что UniformToFill у самой карточки.
        /// </summary>
        private void DrawPreview(
            string canvasName, string imageName,
            CharacterAvatarCrop crop, double boxWidth, double boxHeight)
        {
            var canvas = this.FindControl<Canvas>(canvasName);
            var image = this.FindControl<Image>(imageName);
            if (canvas == null || image == null || _source == null) return;

            canvas.Width = boxWidth;
            canvas.Height = boxHeight;

            var cropWidth = Math.Max(1.0, crop.Width * _imageWidth);
            var cropHeight = Math.Max(1.0, crop.Height * _imageHeight);
            var cropCenterX = (crop.X + crop.Width / 2.0) * _imageWidth;
            var cropCenterY = (crop.Y + crop.Height / 2.0) * _imageHeight;

            var scale = Math.Max(boxWidth / cropWidth, boxHeight / cropHeight);

            image.Width = _imageWidth * scale;
            image.Height = _imageHeight * scale;
            Canvas.SetLeft(image, boxWidth / 2.0 - cropCenterX * scale);
            Canvas.SetTop(image, boxHeight / 2.0 - cropCenterY * scale);
        }

        // ── Ползунок масштаба ─────────────────────────────────────────────

        private void SyncZoomSliderFromScale()
        {
            var slider = this.FindControl<Slider>("ZoomSlider");
            if (slider == null) return;

            var minScale = MinScale;
            var factor = minScale <= 0 ? 1.0 : _scale / minScale;
            if (factor < 1.0) factor = 1.0;
            if (factor > MaxZoomFactor) factor = MaxZoomFactor;

            _suppressZoomEvent = true;
            slider.Minimum = 1.0;
            slider.Maximum = MaxZoomFactor;
            slider.Value = factor;
            _suppressZoomEvent = false;
        }

        private void OnZoomSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (_suppressZoomEvent || _source == null) return;
            ZoomTo(MinScale * e.NewValue, new Point(ViewportSide / 2.0, ViewportSide / 2.0));
            Redraw();
        }

        /// <summary>
        /// Приблизить или отдалить, оставив точку под курсором на месте: иначе
        /// колесо уводит кадр в сторону от того места, куда смотрят.
        /// </summary>
        private void ZoomTo(double targetScale, Point anchor)
        {
            var minScale = MinScale;
            var maxScale = minScale * MaxZoomFactor;

            if (targetScale < minScale) targetScale = minScale;
            if (targetScale > maxScale) targetScale = maxScale;
            if (Math.Abs(targetScale - _scale) < 0.000001) return;

            // Точка картинки под якорем до смены масштаба.
            var imagePointX = (anchor.X - _offsetX) / _scale;
            var imagePointY = (anchor.Y - _offsetY) / _scale;

            _scale = targetScale;
            _offsetX = anchor.X - imagePointX * _scale;
            _offsetY = anchor.Y - imagePointY * _scale;
            ClampOffsets();
        }

        // ── Перетаскивание ────────────────────────────────────────────────

        private void OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (_source == null) return;
            var point = e.GetCurrentPoint(this.FindControl<Panel>("Viewport"));
            if (!point.Properties.IsLeftButtonPressed) return;

            _dragging = true;
            _dragOrigin = point.Position;
            _dragOffsetX = _offsetX;
            _dragOffsetY = _offsetY;
            e.Pointer.Capture(this.FindControl<Panel>("Viewport"));
            e.Handled = true;
        }

        private void OnViewportPointerMoved(object? sender, PointerEventArgs e)
        {
            if (!_dragging || _source == null) return;

            var position = e.GetPosition(this.FindControl<Panel>("Viewport"));
            _offsetX = _dragOffsetX + (position.X - _dragOrigin.X);
            _offsetY = _dragOffsetY + (position.Y - _dragOrigin.Y);
            ClampOffsets();
            Redraw();
            e.Handled = true;
        }

        private void OnViewportPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }

        private void OnViewportWheel(object? sender, PointerWheelEventArgs e)
        {
            if (_source == null) return;

            // Шаг мелкий и пропорциональный самому повороту: четыре процента
            // на щелчок вместо прежних двенадцати с половиной. Знак и величина
            // берутся прямо из Delta, а не сводятся к «вверх или вниз» — у
            // точных тачпадов Delta дробная, и округление её до целого щелчка
            // превращало плавное движение пальцем в рывки.
            var notches = e.Delta.Y;
            if (Math.Abs(notches) < 0.0001) return;

            var step = Math.Pow(1.04, notches);
            ZoomTo(_scale * step, e.GetPosition(this.FindControl<Panel>("Viewport")));
            SyncZoomSliderFromScale();
            Redraw();
            e.Handled = true;
        }

        // ── Кнопки ────────────────────────────────────────────────────────

        private void OnResetClick(object? sender, RoutedEventArgs e)
        {
            if (_source == null) return;

            _scale = MinScale;
            _offsetX = _frameX + _frameWidth / 2.0 - _imageWidth * _scale / 2.0;
            _offsetY = _frameY + _frameHeight / 2.0 - _imageHeight * _scale / 2.0;
            ClampOffsets();
            SyncZoomSliderFromScale();
            Redraw();
        }

        private void OnApplyClick(object? sender, RoutedEventArgs e)
        {
            StoreCurrentCrop();
            Close(new CharacterAvatarCropPair(_circleCrop, _stripCrop));
        }

        private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

        // Скрим блокирует модуль, но окно не закрывает — как в редакторе цвета.
        private void OnScrimPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

        private void Close(CharacterAvatarCropPair? result)
        {
            IsVisible = false;
            _dragging = false;

            // Ссылки на битмап снимаются, но сам он не освобождается: его
            // владелец — вызывающая сторона, и он может понадобиться ей дальше.
            ClearImage("SourceImage");
            ClearImage("PreviewCircleImage");
            ClearImage("PreviewStripImage");
            ClearImage("PreviewTinyImage");
            _source = null;

            // Карточка отпускается вместе с картинкой: окно живёт всё время
            // работы модуля, и держать за собой вью-модель закрытого выбора
            // ему незачем.
            ApplyCardContext(null);

            var tcs = _tcs;
            _tcs = null;

            try { tcs?.TrySetResult(result); }
            catch (Exception ex) { _logger.Error(ex, "Crop overlay close failed"); }
        }

        private void ClearImage(string name)
        {
            var image = this.FindControl<Image>(name);
            if (image != null) image.Source = null;
        }
    }
}
