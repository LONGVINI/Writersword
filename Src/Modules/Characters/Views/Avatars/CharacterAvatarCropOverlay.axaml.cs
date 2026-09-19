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

        // Потолок ползунка: во сколько раз кадр может стать мельче того
        // размера, при котором картинка ровно закрывает рамку. Потолок самой
        // правки это больше не задаёт — числом в поле берут и выше.
        private const double MaxZoomFactor = 6.0;

        // Дальше приближать нечего: в кадр попадает меньше восьми точек
        // исходника по стороне, и на карточке от картинки остаётся ровное
        // пятно. Этим и ограничено поле, а не привычным потолком ползунка.
        private const double MinCropSide = 8.0;

        private Bitmap? _source;

        // Повёрнутая копия исходника. Принадлежит окну — в отличие от самого
        // исходника, который приходит снаружи и там же освобождается.
        private Bitmap? _rotated;

        // Поворот картинки в градусах: 0, 90, 180 или 270. Принадлежит
        // картинке целиком, а не отдельному кадру: оба кадра снимаются уже с
        // повёрнутой, и разный поворот у кружка и полоски означал бы две
        // разные картинки на одной карточке.
        private int _rotation;

        // Стороны показываемой картинки — то есть повёрнутой, если поворот
        // задан. Вся геометрия кадра считается по ним.
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

        // Какие направляющие показаны: 0 — никаких, 1 — центральные оси,
        // 2 — сетка по третям. Не поле ссылки и не часть кадра: линии видит
        // только тот, кто кадрирует, и в картинку они не попадают.
        //
        // Выбор переживает закрытие окна: окно живёт всё время работы модуля,
        // и человек, который кадрирует по третям, кадрирует так и следующую
        // картинку.
        private int _guides;

        // Рамка, по которой построена нынешняя фигура затемнения. Хранится,
        // чтобы не пересобирать геометрию на каждом шаге перетаскивания.
        private double _shadeX = double.NaN;
        private double _shadeY = double.NaN;
        private double _shadeWidth = double.NaN;
        private double _shadeHeight = double.NaN;

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

        // Бегунок выбранного сегмента уже вставал на место. До первой
        // постановки он переезжает без перехода: иначе при открытии окна
        // подложка прилетала бы из левого края дорожки.
        private bool _segThumbPlaced;

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

            // Бегунок сегментов встаёт по границам самой кнопки, а они
            // известны только после прохода раскладки — и меняются, когда
            // окно ужимается по ширине модуля.
            foreach (var name in new[] { "ModeCircleButton", "ModeStripButton" })
            {
                var segButton = this.FindControl<Button>(name);
                segButton?.GetObservable(BoundsProperty).Subscribe(_ => UpdateSegThumb());
            }

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
        /// Повёрнутую копию окно делает себе само и само же освобождает.
        ///
        /// initialRotation — поворот, с которым картинку уже показывают. Окно
        /// открывается на нём, а не на нуле: иначе повторный заход в обрезку
        /// показывал бы лежащую на боку фотографию, однажды уже поставленную
        /// как надо.
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
            bool openOnStrip = false,
            int initialRotation = 0)
        {
            if (_tcs != null) return _tcs.Task;
            if (source == null) return Task.FromResult<CharacterAvatarCropPair?>(null);

            _tcs = new TaskCompletionSource<CharacterAvatarCropPair?>();
            _source = source;
            _rotation = CharacterAvatarRef.NormalizeRotation(initialRotation);

            var titleText = this.FindControl<TextBlock>("TitleText");
            if (titleText != null && !string.IsNullOrWhiteSpace(title))
                titleText.Text = title;

            ApplyCardContext(cardContext);

            // Картинка ставится уже повёрнутой: кадры из ссылки сняты с
            // повёрнутой, и раскладывать их на исходную было бы не на что.
            ApplyRotationToImages();

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
            UpdateGuidesButton();

            LayoutFrame();
            ApplyCropToState(_stripMode ? _stripCrop : _circleCrop);
            SyncZoomControls();
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

        // ── Поворот ───────────────────────────────────────────────────────

        /// <summary>
        /// Пересобрать показываемую картинку под текущий поворот и раздать её
        /// полотну и всем трём превью.
        ///
        /// Поворот делается один раз в новый битмап, а не преобразованием при
        /// отрисовке: рамка, затемнение и превью считаются по сторонам
        /// картинки, и поворот на лету пришлось бы учитывать в каждом из этих
        /// расчётов.
        ///
        /// Не удался — окно остаётся на нулевом повороте. Показать одно, а
        /// запомнить другое хуже, чем не повернуть вовсе.
        /// </summary>
        private void ApplyRotationToImages()
        {
            _rotated?.Dispose();
            _rotated = null;

            if (_source == null) return;

            if (_rotation != 0)
            {
                _rotated = Services.AvatarImageRotation.Rotate(_source, _rotation);
                if (_rotated == null) _rotation = 0;
            }

            ShowImage(_rotated ?? _source);
        }

        /// <summary>Поставить картинку полотну и превью, запомнив её стороны.</summary>
        private void ShowImage(Bitmap display)
        {
            _imageWidth = Math.Max(1, display.PixelSize.Width);
            _imageHeight = Math.Max(1, display.PixelSize.Height);

            SetImageSource(this.FindControl<Image>("SourceImage"), display);
            SetImageSource(this.FindControl<Image>("PreviewCircleImage"), display);
            SetImageSource(this.FindControl<Image>("PreviewStripImage"), display);
            SetImageSource(this.FindControl<Image>("PreviewTinyImage"), display);
        }

        /// <summary>
        /// Повернуть картинку на четверть по часовой стрелке.
        ///
        /// Вместе с картинкой поворачиваются оба кадра: человек выбрал место на
        /// фотографии, и поворот листа не должен уводить кадр на чужой угол.
        /// После поворота каждый кадр подгоняется под форму своей рамки — доли
        /// ширины и высоты меняются местами, и квадратный на вид кадр иначе
        /// стал бы прямоугольным.
        ///
        /// Повёрнутая копия строится до правки состояния: не построится — в
        /// окне не меняется ничего, и показанное по-прежнему совпадает с тем,
        /// что уедет в ссылку.
        /// </summary>
        private void OnRotateClick(object? sender, RoutedEventArgs e)
        {
            if (_source == null) return;

            var next = CharacterAvatarRef.NormalizeRotation(_rotation + 90);

            Bitmap? rotated = null;
            if (next != 0)
            {
                rotated = Services.AvatarImageRotation.Rotate(_source, next);
                if (rotated == null) return;
            }

            StoreCurrentCrop();

            _rotated?.Dispose();
            _rotated = rotated;
            _rotation = next;
            ShowImage(_rotated ?? _source);

            _circleCrop = FitCropToFrame(_circleCrop.RotateCw(), 1.0);
            _stripCrop = FitCropToFrame(_stripCrop.RotateCw(), StripAspect);

            LayoutFrame();
            ApplyCropToState(_stripMode ? _stripCrop : _circleCrop);
            SyncZoomControls();
            Redraw();
        }

        /// <summary>
        /// Подогнать кадр под форму рамки, оставив его середину на месте.
        ///
        /// Берётся наибольший прямоугольник нужной формы, вписанный в прежний
        /// кадр: так кадр после поворота показывает то же место картинки и не
        /// вылезает за её край.
        ///
        /// frameAspect задан в точках экрана, а доли кадра считаются от разных
        /// сторон картинки, поэтому пропорция переводится через её стороны.
        /// </summary>
        private CharacterAvatarCrop FitCropToFrame(CharacterAvatarCrop crop, double frameAspect)
        {
            if (_imageWidth <= 0 || _imageHeight <= 0) return crop;

            var ratio = frameAspect * _imageHeight / _imageWidth;
            if (double.IsNaN(ratio) || double.IsInfinity(ratio) || ratio <= 0) return crop;

            var width = crop.Width;
            var height = width / ratio;
            if (height > crop.Height)
            {
                height = crop.Height;
                width = height * ratio;
            }

            if (width > 1.0) { width = 1.0; height = width / ratio; }
            if (height > 1.0) { height = 1.0; width = height * ratio; }

            var centerX = crop.X + crop.Width / 2.0;
            var centerY = crop.Y + crop.Height / 2.0;

            return new CharacterAvatarCrop(
                centerX - width / 2.0,
                centerY - height / 2.0,
                width,
                height);
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
            SyncZoomControls();
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
            UpdateSegThumb();
        }

        /// <summary>
        /// Поставить бегунок под выбранный сегмент.
        ///
        /// Ширина и сдвиг берутся у самой кнопки, а не считаются делением
        /// дорожки пополам: надписи разной длины, и половина промахнулась бы
        /// мимо той, что длиннее.
        ///
        /// Сдвиг задан преобразованием, а не отступом: отступ пересчитывает
        /// раскладку на каждом кадре переезда, а преобразование меняет только
        /// то, как уже размеренный прямоугольник ложится на экран.
        /// </summary>
        private void UpdateSegThumb()
        {
            var thumb = this.FindControl<Border>("SegThumb");
            var target = this.FindControl<Button>(
                _stripMode ? "ModeStripButton" : "ModeCircleButton");
            if (thumb == null || target == null) return;

            var bounds = target.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                thumb.IsVisible = false;
                return;
            }

            var shift = Avalonia.Media.Transformation.TransformOperations.Parse(
                string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "translateX({0:0.##}px)", bounds.X));

            if (_segThumbPlaced)
            {
                thumb.IsVisible = true;
                thumb.Width = bounds.Width;
                thumb.RenderTransform = shift;
                return;
            }

            // Первая постановка — без перехода: переходы на время снимаются,
            // иначе бегунок приехал бы к своему сегменту прямо при открытии
            // окна, хотя переключать никто ничего не просил.
            var transitions = thumb.Transitions;
            thumb.Transitions = null;
            thumb.IsVisible = true;
            thumb.Width = bounds.Width;
            thumb.RenderTransform = shift;
            thumb.Transitions = transitions;

            _segThumbPlaced = true;
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
        /// Наибольший масштаб. Считается от кадра, а не от MinScale: предел,
        /// привязанный к вписанному размеру, менялся при каждом повороте и
        /// смене вида — после поворота одна и та же картинка приближалась то
        /// ближе, то дальше. Здесь же предел говорит ровно одно: мельче
        /// MinCropSide точек исходника кадр не режет.
        ///
        /// MinScale снизу — на случай картинки мельче самой рамки: у неё
        /// вписанный масштаб и так выше предела.
        /// </summary>
        private double MaxScale => Math.Max(MinScale, _frameWidth / MinCropSide);

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
            LayoutGuides();
            RedrawPreviews();
        }

        /// <summary>
        /// Затемнение вне кадра — одна фигура с дыркой.
        ///
        /// Раньше это были четыре полосы вокруг рамки. Стороны кадра нацело не
        /// делятся — у полоски высота выходит дробной, — и полосы стыковались
        /// по дробной координате. Растеризация клала полупрозрачный чёрный на
        /// стыковую строку дважды, и вдоль верхнего и нижнего края кадра, по
        /// бокам от него, оставались тёмные чёрточки. Одна фигура так не умеет
        /// по построению: дырка вырезается правилом чёт-нечет, и ни одна точка
        /// не закрашивается второй раз.
        ///
        /// Геометрия пересобирается только при смене самой рамки: перетаскивание
        /// картинки её не двигает, а строить фигуру на каждый шаг мыши значило
        /// бы выбрасывать её шестьдесят раз в секунду.
        /// </summary>
        private void LayoutShade()
        {
            var path = this.FindControl<Avalonia.Controls.Shapes.Path>("ShadePath");
            if (path == null) return;

            if (path.Data != null
                && Math.Abs(_shadeX - _frameX) < 0.001
                && Math.Abs(_shadeY - _frameY) < 0.001
                && Math.Abs(_shadeWidth - _frameWidth) < 0.001
                && Math.Abs(_shadeHeight - _frameHeight) < 0.001)
                return;

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.SetFillRule(FillRule.EvenOdd);
                AddRectangle(ctx, 0, 0, ViewportSide, ViewportSide);
                AddRectangle(ctx, _frameX, _frameY, _frameWidth, _frameHeight);
            }

            path.Data = geometry;

            _shadeX = _frameX;
            _shadeY = _frameY;
            _shadeWidth = _frameWidth;
            _shadeHeight = _frameHeight;
        }

        private static void AddRectangle(
            StreamGeometryContext ctx, double x, double y, double width, double height)
        {
            ctx.BeginFigure(new Point(x, y), true);
            ctx.LineTo(new Point(x + width, y));
            ctx.LineTo(new Point(x + width, y + height));
            ctx.LineTo(new Point(x, y + height));
            ctx.EndFigure(true);
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

        /// <summary>
        /// Переключить направляющие по кругу: оси, сетка, ничего.
        ///
        /// По кругу, а не двумя кнопками: вместе эти два вида не нужны — оси
        /// ставят лицо по середине кружка, сетка кладёт его по третям, и
        /// выбирают из них, а не складывают.
        /// </summary>
        private void OnGuidesClick(object? sender, RoutedEventArgs e)
        {
            _guides = (_guides + 1) % 3;
            UpdateGuidesButton();
            LayoutGuides();
        }

        private void UpdateGuidesButton()
        {
            var button = this.FindControl<Button>("GuidesButton");
            if (button == null) return;

            if (_guides != 0)
            {
                if (!button.Classes.Contains("on")) button.Classes.Add("on");
            }
            else
            {
                button.Classes.Remove("on");
            }
        }

        /// <summary>
        /// Разложить направляющие по кадру.
        ///
        /// Линии идут внутри рамки, а не через всё полотно: за рамкой лежит
        /// затемнение, и продолжение линий по нему показывало бы деления там,
        /// где кадра уже нет.
        ///
        /// Толщина в одну точку, а половина её вычитается из координаты:
        /// иначе линия встаёт не по самой середине, а рядом с ней.
        /// </summary>
        private void LayoutGuides()
        {
            var guideCanvas = this.FindControl<Canvas>("GuideCanvas");
            if (guideCanvas != null)
            {
                guideCanvas.Width = ViewportSide;
                guideCanvas.Height = ViewportSide;
            }

            if (_guides == 0)
            {
                PlaceLine("GuideV1", 0, 0, 0, 0, false);
                PlaceLine("GuideV2", 0, 0, 0, 0, false);
                PlaceLine("GuideH1", 0, 0, 0, 0, false);
                PlaceLine("GuideH2", 0, 0, 0, 0, false);
                return;
            }

            if (_guides == 1)
            {
                var centerX = _frameX + _frameWidth / 2.0 - 0.5;
                var centerY = _frameY + _frameHeight / 2.0 - 0.5;

                PlaceLine("GuideV1", centerX, _frameY, 1, _frameHeight, true);
                PlaceLine("GuideH1", _frameX, centerY, _frameWidth, 1, true);
                PlaceLine("GuideV2", 0, 0, 0, 0, false);
                PlaceLine("GuideH2", 0, 0, 0, 0, false);
                return;
            }

            var thirdX = _frameWidth / 3.0;
            var thirdY = _frameHeight / 3.0;

            PlaceLine("GuideV1", _frameX + thirdX - 0.5, _frameY, 1, _frameHeight, true);
            PlaceLine("GuideV2", _frameX + thirdX * 2.0 - 0.5, _frameY, 1, _frameHeight, true);
            PlaceLine("GuideH1", _frameX, _frameY + thirdY - 0.5, _frameWidth, 1, true);
            PlaceLine("GuideH2", _frameX, _frameY + thirdY * 2.0 - 0.5, _frameWidth, 1, true);
        }

        private void PlaceLine(
            string name, double x, double y, double width, double height, bool visible)
        {
            var line = this.FindControl<Border>(name);
            if (line == null) return;

            line.IsVisible = visible;
            if (!visible) return;

            PlaceBox(name, x, y, width, height);
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

        /// <summary>
        /// Привести ползунок и поле к нынешнему масштабу.
        ///
        /// Масштаб считается в процентах от натуральной величины: сто
        /// процентов — точка картинки на точку экрана. Мера не зависит ни от
        /// рамки, ни от поворота, поэтому число на экране означает одно и то
        /// же до и после любой правки; прежняя шкала «во столько-то раз от
        /// вписанного» после поворота меняла смысл вместе с MinScale.
        /// </summary>
        private void SyncZoomControls()
        {
            var minPercent = MinScale * 100.0;
            var maxPercent = MaxScale * 100.0;
            var percent = _scale * 100.0;

            _suppressZoomEvent = true;
            try
            {
                var slider = this.FindControl<Slider>("ZoomSlider");
                if (slider != null)
                {
                    slider.Minimum = minPercent;

                    // Потолок ползунка привычный — вшестеро от вписанного, —
                    // но если числом задали больше, он раздвигается до этого
                    // значения: иначе ползунок молча вернул бы масштаб к своему
                    // потолку на первом же прикосновении.
                    slider.Maximum = Math.Max(minPercent * MaxZoomFactor, percent);
                    slider.Value = percent;
                }

                var box = this.FindControl<NumericUpDown>("ZoomBox");
                if (box != null)
                {
                    box.Minimum = (decimal)Math.Floor(minPercent);
                    box.Maximum = (decimal)Math.Ceiling(maxPercent);
                    box.Value = (decimal)Math.Round(percent);
                }
            }
            finally
            {
                _suppressZoomEvent = false;
            }
        }

        private void OnZoomSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (_suppressZoomEvent || _source == null) return;
            ZoomTo(e.NewValue / 100.0, new Point(ViewportSide / 2.0, ViewportSide / 2.0));
            SyncZoomControls();
            Redraw();
        }

        private void OnZoomBoxChanged(object? sender, NumericUpDownValueChangedEventArgs e)
        {
            if (_suppressZoomEvent || _source == null) return;
            if (e.NewValue is not decimal percent) return;

            ZoomTo((double)percent / 100.0, new Point(ViewportSide / 2.0, ViewportSide / 2.0));
            SyncZoomControls();
            Redraw();
        }

        /// <summary>
        /// Приблизить или отдалить, оставив точку под курсором на месте: иначе
        /// колесо уводит кадр в сторону от того места, куда смотрят.
        /// </summary>
        private void ZoomTo(double targetScale, Point anchor)
        {
            var minScale = MinScale;
            var maxScale = MaxScale;

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
            SyncZoomControls();
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
            SyncZoomControls();
            Redraw();
        }

        private void OnApplyClick(object? sender, RoutedEventArgs e)
        {
            StoreCurrentCrop();
            Close(new CharacterAvatarCropPair(_circleCrop, _stripCrop, _rotation));
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

            // Следующий показ поставит бегунок сегментов сразу на место:
            // переезд уместен, когда вид переключают, а не когда открывают
            // окно уже на нужном.
            _segThumbPlaced = false;

            // Повёрнутая копия своя, её и освобождаем.
            _rotated?.Dispose();
            _rotated = null;
            _rotation = 0;

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
