using System.Text.Json.Serialization;
using Writersword.Modules.TextEditor.Models.Styles;

namespace Writersword.Modules.TextEditor.Models.Document
{
    /// <summary>
    /// Режим обтекания плавающего объекта текстом.
    /// </summary>
    public enum WrapMode
    {
        /// <summary>Объект встроен в строку как символ.</summary>
        Inline = 0,
        /// <summary>Текст обтекает объект со всех сторон.</summary>
        Square = 1,
        /// <summary>Текст обтекает по контуру объекта.</summary>
        Tight = 2,
        /// <summary>Объект поверх текста.</summary>
        InFront = 3,
        /// <summary>Объект за текстом.</summary>
        Behind = 4
    }

    /// <summary>
    /// С какой стороны от обтекаемого объекта разрешено идти тексту.
    /// </summary>
    public enum WrapSide
    {
        /// <summary>Только по той стороне, где больше свободного места (как было всегда).</summary>
        LargestOnly = 0,
        /// <summary>С обеих сторон: строка идёт слева от объекта и продолжается справа.</summary>
        BothSides = 1,
        /// <summary>Только слева от объекта.</summary>
        LeftOnly = 2,
        /// <summary>Только справа от объекта.</summary>
        RightOnly = 3
    }

    /// <summary>
    /// Как линия по контуру ложится относительно границы объекта: рамка картинки
    /// и обводка фигуры — одно и то же, поэтому и правило у них одно. Имя
    /// оставлено прежним, чтобы не ломать уже сохранённые документы.
    /// </summary>
    public enum ImageBorderAlign
    {
        /// <summary>Линия целиком внутри объекта: наружу габарита не выступает.</summary>
        Inside = 0,
        /// <summary>Линия по границе: половина толщины внутрь, половина наружу.</summary>
        Center = 1,
        /// <summary>Линия целиком снаружи: габарит растёт на её толщину со всех сторон.</summary>
        Outside = 2
    }

    /// <summary>
    /// Штрих линии: рамки картинки и обводки фигуры.
    /// Тип общий на оба объекта — рисуются они одной и той же кистью.
    /// </summary>
    public enum ShapeDashStyle
    {
        Solid = 0,
        Dash = 1,
        Dot = 2,
        DashDot = 3
    }

    /// <summary>
    /// Якорь привязки плавающего объекта.
    /// </summary>
    public enum FloatAnchor
    {
        /// <summary>Позиция относительно страницы.</summary>
        Page = 0,
        /// <summary>Позиция относительно абзаца-якоря.</summary>
        Paragraph = 1,
        /// <summary>Позиция относительно символа-якоря.</summary>
        Character = 2
    }

    /// <summary>
    /// Общая часть плавающего объекта: всё, что нужно раскладке, чтобы построить
    /// вокруг него зону обтекания и понять, на какой странице он живёт.
    ///
    /// Введён ради фигур: обтекание, вытеснение таблиц и переброс строк написаны
    /// один раз и работают и с картинкой, и с фигурой. Раскладка ходит только
    /// через эти члены, поэтому следующий плавающий объект (надпись) включается
    /// в обтекание реализацией интерфейса, без правок в самой раскладке.
    /// </summary>
    public interface IFloatingObject
    {
        /// <summary>Режим обтекания текстом.</summary>
        WrapMode WrapMode { get; set; }

        /// <summary>С какой стороны от объекта разрешено идти тексту.</summary>
        WrapSide WrapSide { get; set; }

        /// <summary>Угол поворота в градусах по часовой стрелке, вокруг центра габарита.</summary>
        double RotationDeg { get; set; }

        /// <summary>Ширина габарита объекта, пт.</summary>
        double WidthPt { get; set; }

        /// <summary>Высота габарита объекта, пт.</summary>
        double HeightPt { get; set; }

        /// <summary>Блокировка пропорций при изменении размера.</summary>
        bool LockAspectRatio { get; set; }

        /// <summary>Непрозрачность объекта: 1 — полностью видим, 0 — невидим.</summary>
        double Opacity { get; set; }

        /// <summary>
        /// Контур объекта: у фигуры это её геометрия, у картинки — форма, по которой
        /// она обрезается и по которой идёт её рамка. Поле одного смысла и одного
        /// типа у обоих, поэтому команды выбора формы пишутся один раз.
        /// </summary>
        ShapeType ShapeType { get; set; }

        /// <summary>Скругление углов контура, пт. 0 — прямые углы.</summary>
        double CornerRadiusPt { get; set; }

        /// <summary>
        /// Есть ли у объекта замкнутый контур, который можно залить. У линии и
        /// стрелки его нет: заполнять там нечего, и обрезать по ним тоже.
        /// </summary>
        bool IsClosedShape { get; }

        /// <summary>
        /// Цвет линии по контуру в общих терминах: рамка картинки и обводка фигуры —
        /// одно и то же, просто исторически названы по-разному. null или пусто —
        /// линии нет.
        /// </summary>
        string? OutlineColor { get; set; }

        /// <summary>Толщина линии по контуру, пт. 0 — линии нет.</summary>
        double OutlineThicknessPt { get; set; }

        /// <summary>Штрих линии по контуру.</summary>
        ShapeDashStyle OutlineDash { get; set; }

        /// <summary>
        /// Как линия по контуру ложится относительно границы объекта: внутрь,
        /// по границе или наружу. От этого зависит и габарит пятна на листе,
        /// и зона обтекания.
        /// </summary>
        ImageBorderAlign OutlineAlign { get; set; }

        /// <summary>Зеркальное отражение по горизонтали.</summary>
        bool FlipHorizontal { get; set; }

        /// <summary>Зеркальное отражение по вертикали.</summary>
        bool FlipVertical { get; set; }

        /// <summary>
        /// Обрезка слева, доля исходной ширины (0..1). У картинки кадрируется она
        /// сама, у фигуры — её картинка-заливка: действие одно и то же.
        /// </summary>
        double CropLeftFrac { get; set; }

        /// <summary>Обрезка сверху, доля исходной высоты (0..1).</summary>
        double CropTopFrac { get; set; }

        /// <summary>Обрезка справа, доля исходной ширины (0..1).</summary>
        double CropRightFrac { get; set; }

        /// <summary>Обрезка снизу, доля исходной высоты (0..1).</summary>
        double CropBottomFrac { get; set; }

        /// <summary>Альтернативный текст для доступности.</summary>
        string? AltText { get; set; }

        /// <summary>Горизонтальное выравнивание объекта-блока (Inline) в колонке.</summary>
        TextAlignment Alignment { get; set; }

        /// <summary>Якорь привязки при WrapMode != Inline.</summary>
        FloatAnchor Anchor { get; set; }

        /// <summary>Отступ текста от объекта сверху при обтекании, пт.</summary>
        double WrapPadTopPt { get; set; }

        /// <summary>Отступ текста от объекта снизу при обтекании, пт.</summary>
        double WrapPadBottomPt { get; set; }

        /// <summary>Отступ текста от объекта слева при обтекании, пт.</summary>
        double WrapPadLeftPt { get; set; }

        /// <summary>Отступ текста от объекта справа при обтекании, пт.</summary>
        double WrapPadRightPt { get; set; }

        /// <summary>
        /// Насколько оформление объекта выходит за его габарит: у картинки это
        /// наружная часть рамки, у фигуры — наружная половина обводки. Эта часть
        /// занимает место на листе так же, как сам объект, и входит в зону обтекания.
        /// </summary>
        double WrapOutsetPt { get; }

        /// <summary>Жёсткая привязка к номеру страницы (1-based). 0 — привязки нет.</summary>
        int PinnedPage { get; set; }

        /// <summary>Горизонтальное смещение от начала текстовой области страницы, пт.</summary>
        double OffsetXPt { get; set; }

        /// <summary>Вертикальное смещение от начала текстовой области страницы, пт.</summary>
        double OffsetYPt { get; set; }

        /// <summary>Z-порядок среди плавающих объектов (больше = поверх).</summary>
        int ZOrder { get; set; }
    }

    /// <summary>
    /// Изображение в документе.
    /// Файл изображения хранится в ZIP по пути TextEditor/Images/{ImageFileName}.
    /// </summary>
    public sealed class ImageBlock : BlockModel, IFloatingObject
    {
        public override BlockType BlockType => BlockType.Image;

        /// <summary>
        /// Имя файла изображения внутри ZIP (например "img_abc123.png").
        /// Полный путь в ZIP: TextEditor/Images/{ImageFileName}.
        /// </summary>
        public string ImageFileName { get; set; } = string.Empty;

        /// <summary>Ширина изображения в пунктах (пользовательски заданная).</summary>
        public double WidthPt { get; set; }

        /// <summary>Высота изображения в пунктах (пользовательски заданная).</summary>
        public double HeightPt { get; set; }

        /// <summary>Блокировка пропорций при изменении размера.</summary>
        public bool LockAspectRatio { get; set; } = true;

        /// <summary>Угол поворота изображения в градусах по часовой стрелке, вокруг центра.</summary>
        public double RotationDeg { get; set; }

        /// <summary>Непрозрачность изображения: 1 — полностью видимо, 0 — невидимо.</summary>
        public double Opacity { get; set; } = 1.0;

        /// <summary>Цвет рамки изображения в hex (#RRGGBB). null или прозрачный — рамки нет.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? BorderColor { get; set; }

        /// <summary>Толщина рамки изображения в пунктах.</summary>
        public double BorderThicknessPt { get; set; }

        /// <summary>Штрих рамки изображения.</summary>
        public ShapeDashStyle BorderDashStyle { get; set; } = ShapeDashStyle.Solid;

        /// <summary>
        /// Форма, по контуру которой обрезается картинка и идёт её рамка.
        /// Rectangle — обычная прямоугольная картинка.
        ///
        /// Раньше здесь было скругление ОДНОЙ ЛИШЬ рамки: линия шла по дуге, а углы
        /// картинки торчали из-под неё. Скругление без обрезки самой картинки
        /// бессмысленно, поэтому теперь контур один на оба — как «обрезка по фигуре»
        /// в Word.
        /// </summary>
        public ShapeType ShapeType { get; set; } = ShapeType.Rectangle;

        /// <summary>
        /// Скругление углов формы в пунктах. 0 — прямые углы. Значимо для
        /// прямоугольника и выноски; на эллипс не влияет.
        /// </summary>
        public double CornerRadiusPt { get; set; }

        /// <summary>Обрезается ли картинка по контуру формы, а не по прямоугольнику.</summary>
        [JsonIgnore]
        public bool HasShapeClip =>
            ShapeType != ShapeType.Rectangle || CornerRadiusPt > 0.0;

        /// <summary>
        /// Есть ли замкнутый контур. Линия и стрелка картинке не назначаются —
        /// у них нет площади, и обрезать по ним нечего, — но проверка нужна общая:
        /// через неё лента решает, что показывать активным.
        /// </summary>
        [JsonIgnore]
        public bool IsClosedShape =>
            ShapeType is ShapeType.Rectangle or ShapeType.Ellipse or ShapeType.Callout;

        /// <summary>
        /// Линия по контуру в общих терминах плавающего объекта: у картинки это её
        /// рамка. Переходник на BorderColor — в файл уходит по-прежнему BorderColor,
        /// формат документа не меняется.
        /// </summary>
        [JsonIgnore]
        public string? OutlineColor
        {
            get => BorderColor;
            set => BorderColor = value;
        }

        /// <summary>Толщина линии по контуру — переходник на BorderThicknessPt.</summary>
        [JsonIgnore]
        public double OutlineThicknessPt
        {
            get => BorderThicknessPt;
            set => BorderThicknessPt = value;
        }

        /// <summary>Штрих линии по контуру — переходник на BorderDashStyle.</summary>
        [JsonIgnore]
        public ShapeDashStyle OutlineDash
        {
            get => BorderDashStyle;
            set => BorderDashStyle = value;
        }

        /// <summary>Положение линии по контуру — переходник на BorderAlign.</summary>
        [JsonIgnore]
        public ImageBorderAlign OutlineAlign
        {
            get => BorderAlign;
            set => BorderAlign = value;
        }

        /// <summary>
        /// Как рамка ложится относительно границы картинки.
        /// По умолчанию — по центру границы: так рамка рисовалась всегда,
        /// и старые документы выглядят так же.
        /// </summary>
        public ImageBorderAlign BorderAlign { get; set; } = ImageBorderAlign.Center;

        /// <summary>
        /// Насколько рамка выступает наружу габарита картинки, пунктов.
        /// Ровно на столько шире пятно картинки на листе, поэтому на столько же
        /// расширяется её зона обтекания.
        /// </summary>
        [JsonIgnore]
        public double BorderOutsetPt
        {
            get
            {
                if (BorderThicknessPt <= 0.0 || string.IsNullOrEmpty(BorderColor)) return 0.0;
                return BorderAlign switch
                {
                    ImageBorderAlign.Inside => 0.0,
                    ImageBorderAlign.Outside => BorderThicknessPt,
                    _ => BorderThicknessPt / 2.0
                };
            }
        }

        /// <summary>Зеркальное отражение по горизонтали.</summary>
        public bool FlipHorizontal { get; set; }

        /// <summary>Зеркальное отражение по вертикали.</summary>
        public bool FlipVertical { get; set; }

        /// <summary>Обрезка слева, доля исходной ширины (0..1).</summary>
        public double CropLeftFrac { get; set; }

        /// <summary>Обрезка сверху, доля исходной высоты (0..1).</summary>
        public double CropTopFrac { get; set; }

        /// <summary>Обрезка справа, доля исходной ширины (0..1).</summary>
        public double CropRightFrac { get; set; }

        /// <summary>Обрезка снизу, доля исходной высоты (0..1).</summary>
        public double CropBottomFrac { get; set; }

        /// <summary>Режим обтекания текстом.</summary>
        public WrapMode WrapMode { get; set; } = WrapMode.Inline;

        /// <summary>
        /// С какой стороны обтекать. Значимо при WrapMode Square/Tight.
        /// По умолчанию — по большей стороне: так вёл себя редактор до появления
        /// двустороннего обтекания, и старые документы не меняются.
        /// </summary>
        public WrapSide WrapSide { get; set; } = WrapSide.LargestOnly;

        /// <summary>Отступ по умолчанию от обтекающей картинки, пт (~0.21 см). Совпадает
        /// с прежним единым зазором зоны — старые документы выглядят так же.</summary>
        public const double WrapPadDefaultPt = 6.0;

        /// <summary>Отступ текста от картинки сверху при обтекании, пт.</summary>
        public double WrapPadTopPt { get; set; } = WrapPadDefaultPt;

        /// <summary>Отступ текста от картинки снизу при обтекании, пт.</summary>
        public double WrapPadBottomPt { get; set; } = WrapPadDefaultPt;

        /// <summary>Отступ текста от картинки слева при обтекании, пт.</summary>
        public double WrapPadLeftPt { get; set; } = WrapPadDefaultPt;

        /// <summary>Отступ текста от картинки справа при обтекании, пт.</summary>
        public double WrapPadRightPt { get; set; } = WrapPadDefaultPt;

        /// <summary>Горизонтальное выравнивание блок-картинки (Inline) в текстовой колонке.</summary>
        public TextAlignment Alignment { get; set; } = TextAlignment.Left;

        /// <summary>Якорь привязки при WrapMode != Inline.</summary>
        public FloatAnchor Anchor { get; set; } = FloatAnchor.Paragraph;

        /// <summary>
        /// Жёсткая привязка к номеру страницы (1-based). 0 — привязки нет, картинка
        /// переезжает между страницами сама, следуя за своим местом в потоке.
        ///
        /// При включённой привязке картинка принадлежит ровно этой странице и никуда
        /// не переезжает: её смещения отсчитываются от краёв этой страницы, а документ
        /// держит столько страниц, чтобы она существовала — удаление текста не утащит
        /// картинку выше, страницы до неё останутся пустыми.
        /// </summary>
        public int PinnedPage { get; set; }

        /// <summary>Горизонтальное смещение от якоря в пунктах.</summary>
        public double OffsetXPt { get; set; }

        /// <summary>Вертикальное смещение от якоря в пунктах.</summary>
        public double OffsetYPt { get; set; }

        /// <summary>Z-порядок среди плавающих объектов (больше = поверх).</summary>
        public int ZOrder { get; set; }

        /// <summary>Альтернативный текст для доступности.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? AltText { get; set; }

        /// <summary>Вылет оформления за габарит для обтекания — наружная часть рамки.</summary>
        [JsonIgnore]
        public double WrapOutsetPt => BorderOutsetPt;
    }

    /// <summary>
    /// Тип фигуры.
    /// </summary>
    public enum ShapeType
    {
        Rectangle = 0,
        Ellipse = 1,
        Line = 2,
        Arrow = 3,
        Callout = 4
    }

    /// <summary>
    /// Наконечник на конце линии.
    /// </summary>
    public enum ShapeArrowHead
    {
        /// <summary>Конец линии без наконечника.</summary>
        None = 0,
        /// <summary>Сплошной треугольник.</summary>
        Triangle = 1,
        /// <summary>Открытая «птичка» из двух отрезков.</summary>
        Open = 2,
        /// <summary>Кружок.</summary>
        Circle = 3
    }

    /// <summary>
    /// Геометрическая фигура или стрелка.
    ///
    /// Плавающий объект наравне с картинкой: те же режимы обтекания, те же отступы,
    /// та же привязка к странице и тот же отсчёт смещений — от начала текстовой
    /// области своей страницы. Отличие только в содержимом: вместо файла картинки
    /// у неё геометрия, заливка и обводка. Заливка при этом может быть и картинкой.
    /// </summary>
    public sealed class ShapeBlock : BlockModel, IFloatingObject
    {
        public override BlockType BlockType => BlockType.Shape;

        /// <summary>Вид фигуры. Меняется на лету: геометрия строится по нему при отрисовке.</summary>
        public ShapeType ShapeType { get; set; }

        /// <summary>Ширина габарита фигуры в пунктах.</summary>
        public double WidthPt { get; set; }

        /// <summary>Высота габарита фигуры в пунктах.</summary>
        public double HeightPt { get; set; }

        /// <summary>Блокировка пропорций при изменении размера.</summary>
        public bool LockAspectRatio { get; set; }

        /// <summary>Угол поворота в градусах по часовой стрелке, вокруг центра габарита.</summary>
        public double RotationDeg { get; set; }

        /// <summary>Непрозрачность фигуры: 1 — полностью видима, 0 — невидима.</summary>
        public double Opacity { get; set; } = 1.0;

        /// <summary>Цвет заливки в hex (#RRGGBB). null — заливки нет.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? FillColor { get; set; }

        /// <summary>
        /// Имя файла картинки-заливки внутри ZIP, там же, где картинки документа:
        /// TextEditor/Images/{ImageFileName}. Пусто — заливка одноцветная.
        /// Картинка рисуется по контуру фигуры и обрезается им.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? FillImageFileName { get; set; }

        /// <summary>
        /// Растягивать картинку-заливку на весь габарит фигуры. false — картинка
        /// вписывается целиком, сохраняя пропорции, и по краям остаётся фон заливки.
        /// </summary>
        public bool FillImageStretch { get; set; } = true;

        /// <summary>Цвет обводки в hex (#RRGGBB). null — обводки нет.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? StrokeColor { get; set; }

        /// <summary>Толщина обводки в пунктах. 0 — обводки нет.</summary>
        public double StrokeThicknessPt { get; set; } = 1.0;

        /// <summary>Штрих обводки.</summary>
        public ShapeDashStyle DashStyle { get; set; } = ShapeDashStyle.Solid;

        /// <summary>
        /// Как обводка ложится относительно контура фигуры. По центру границы —
        /// так она рисовалась всегда, и старые документы выглядят так же.
        /// </summary>
        public ImageBorderAlign StrokeAlign { get; set; } = ImageBorderAlign.Center;

        /// <summary>Зеркальное отражение по горизонтали.</summary>
        public bool FlipHorizontal { get; set; }

        /// <summary>Зеркальное отражение по вертикали.</summary>
        public bool FlipVertical { get; set; }

        /// <summary>Обрезка картинки-заливки слева, доля исходной ширины (0..1).</summary>
        public double CropLeftFrac { get; set; }

        /// <summary>Обрезка картинки-заливки сверху, доля исходной высоты (0..1).</summary>
        public double CropTopFrac { get; set; }

        /// <summary>Обрезка картинки-заливки справа, доля исходной ширины (0..1).</summary>
        public double CropRightFrac { get; set; }

        /// <summary>Обрезка картинки-заливки снизу, доля исходной высоты (0..1).</summary>
        public double CropBottomFrac { get; set; }

        /// <summary>Альтернативный текст для доступности.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? AltText { get; set; }

        /// <summary>
        /// Скругление углов прямоугольника и выноски в пунктах. 0 — прямые углы.
        /// На эллипс, линию и стрелку не влияет.
        /// </summary>
        public double CornerRadiusPt { get; set; }

        /// <summary>Наконечник в начале линии (слева).</summary>
        public ShapeArrowHead StartArrow { get; set; } = ShapeArrowHead.None;

        /// <summary>Наконечник в конце линии (справа).</summary>
        public ShapeArrowHead EndArrow { get; set; } = ShapeArrowHead.None;

        /// <summary>Режим обтекания текстом.</summary>
        public WrapMode WrapMode { get; set; } = WrapMode.InFront;

        /// <summary>С какой стороны обтекать. Значимо при WrapMode Square/Tight.</summary>
        public WrapSide WrapSide { get; set; } = WrapSide.LargestOnly;

        /// <summary>Отступ текста от фигуры сверху при обтекании, пт.</summary>
        public double WrapPadTopPt { get; set; } = ImageBlock.WrapPadDefaultPt;

        /// <summary>Отступ текста от фигуры снизу при обтекании, пт.</summary>
        public double WrapPadBottomPt { get; set; } = ImageBlock.WrapPadDefaultPt;

        /// <summary>Отступ текста от фигуры слева при обтекании, пт.</summary>
        public double WrapPadLeftPt { get; set; } = ImageBlock.WrapPadDefaultPt;

        /// <summary>Отступ текста от фигуры справа при обтекании, пт.</summary>
        public double WrapPadRightPt { get; set; } = ImageBlock.WrapPadDefaultPt;

        /// <summary>Горизонтальное выравнивание фигуры-блока (Inline) в текстовой колонке.</summary>
        public TextAlignment Alignment { get; set; } = TextAlignment.Left;

        /// <summary>Якорь привязки при WrapMode != Inline.</summary>
        public FloatAnchor Anchor { get; set; } = FloatAnchor.Page;

        /// <summary>
        /// Жёсткая привязка к номеру страницы (1-based). 0 — привязки нет, фигура
        /// переезжает между страницами сама, следуя за своим местом в потоке.
        /// Работает так же, как привязка картинки.
        /// </summary>
        public int PinnedPage { get; set; }

        /// <summary>Горизонтальное смещение от начала текстовой области страницы, пт.</summary>
        public double OffsetXPt { get; set; }

        /// <summary>Вертикальное смещение от начала текстовой области страницы, пт.</summary>
        public double OffsetYPt { get; set; }

        /// <summary>Z-порядок среди плавающих объектов (больше = поверх).</summary>
        public int ZOrder { get; set; }

        /// <summary>Текст внутри фигуры (для прямоугольников, выносок).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? InnerText { get; set; }

        public bool IsGrouped { get; set; }

        /// <summary>Id группы если объект входит в группу.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? GroupId { get; set; }

        /// <summary>
        /// Вылет оформления за габарит для обтекания. Считается по положению
        /// обводки так же, как у рамки картинки: внутрь — не выходит вовсе,
        /// по границе — половиной толщины, наружу — всей толщиной. Прежде тут
        /// всегда была половина, и обводка наружу ложилась поверх текста,
        /// который считал себя обтекающим.
        /// </summary>
        [JsonIgnore]
        public double WrapOutsetPt
        {
            get
            {
                if (StrokeThicknessPt <= 0.0 || string.IsNullOrEmpty(StrokeColor)) return 0.0;
                return StrokeAlign switch
                {
                    ImageBorderAlign.Inside => 0.0,
                    ImageBorderAlign.Outside => StrokeThicknessPt,
                    _ => StrokeThicknessPt / 2.0
                };
            }
        }

        /// <summary>Есть ли у фигуры замкнутый контур, который можно залить.</summary>
        [JsonIgnore]
        public bool IsClosedShape =>
            ShapeType is ShapeType.Rectangle or ShapeType.Ellipse or ShapeType.Callout;

        /// <summary>
        /// Линия по контуру в общих терминах плавающего объекта: у фигуры это её
        /// обводка. Переходник на StrokeColor — в файл уходит по-прежнему
        /// StrokeColor, формат документа не меняется.
        /// </summary>
        [JsonIgnore]
        public string? OutlineColor
        {
            get => StrokeColor;
            set => StrokeColor = value;
        }

        /// <summary>Толщина линии по контуру — переходник на StrokeThicknessPt.</summary>
        [JsonIgnore]
        public double OutlineThicknessPt
        {
            get => StrokeThicknessPt;
            set => StrokeThicknessPt = value;
        }

        /// <summary>Штрих линии по контуру — переходник на DashStyle.</summary>
        [JsonIgnore]
        public ShapeDashStyle OutlineDash
        {
            get => DashStyle;
            set => DashStyle = value;
        }

        /// <summary>Положение линии по контуру — переходник на StrokeAlign.</summary>
        [JsonIgnore]
        public ImageBorderAlign OutlineAlign
        {
            get => StrokeAlign;
            set => StrokeAlign = value;
        }
    }

    /// <summary>
    /// Плавающая надпись — текстовый блок в произвольном месте страницы.
    /// Содержит параграфы как обычный поток документа.
    /// </summary>
    public sealed class FloatingTextBlock : BlockModel
    {
        public override BlockType BlockType => BlockType.FloatingText;

        public double XPt { get; set; }
        public double YPt { get; set; }
        public double WidthPt { get; set; }
        public double HeightPt { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? BackgroundColor { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? BorderColor { get; set; }

        public double BorderThicknessPt { get; set; }

        public FloatAnchor Anchor { get; set; } = FloatAnchor.Page;

        public int ZOrder { get; set; }

        public System.Collections.Generic.List<ParagraphBlock> Paragraphs { get; set; } = new()
        {
            new ParagraphBlock()
        };

        public bool IsGrouped { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? GroupId { get; set; }
    }
}
