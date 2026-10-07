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

        /// <summary>
        /// Свойства рисунка Word, которых нет в собственной модели объекта: они нужны,
        /// чтобы объект из .docx стоял на листе как у Word и уходил обратно в .docx
        /// без потерь. Null — объект создан в редакторе.
        /// </summary>
        WordDrawingInfo? WordDrawing { get; set; }
    }

    /// <summary>
    /// Точка контура обтекания Word (wp:wrapPolygon) в его собственных единицах:
    /// доли габарита объекта, где 21600 — вся его сторона.
    /// </summary>
    public sealed class WordWrapPoint
    {
        public long X { get; set; }
        public long Y { get; set; }
    }

    /// <summary>
    /// Свойства рисунка Word (wp:inline, wp:anchor), которые объект Writersword сам
    /// не описывает, но без которых его место на листе и обратный перенос в .docx
    /// расходятся с Word.
    ///
    /// Поля поля обрамления (effectExtent) действуют, пока объект не поворачивали и
    /// не меняли в размере: Word пересчитывает их при каждой такой правке, а
    /// редактор вместо этого переходит на свой собственный габарит — габарит
    /// повёрнутого прямоугольника. Остальное переносится как есть.
    /// </summary>
    public sealed class WordDrawingInfo
    {
        /// <summary>Есть ли у объекта поля обрамления из Word.</summary>
        public bool HasEffectExtent { get; set; }

        /// <summary>Поле обрамления слева, пт (wp:effectExtent l).</summary>
        public double EffectLeftPt { get; set; }

        /// <summary>Поле обрамления сверху, пт (wp:effectExtent t).</summary>
        public double EffectTopPt { get; set; }

        /// <summary>Поле обрамления справа, пт (wp:effectExtent r).</summary>
        public double EffectRightPt { get; set; }

        /// <summary>Поле обрамления снизу, пт (wp:effectExtent b).</summary>
        public double EffectBottomPt { get; set; }

        /// <summary>Ширина объекта, для которой записаны поля обрамления, пт.</summary>
        public double EffectForWidthPt { get; set; }

        /// <summary>Высота объекта, для которой записаны поля обрамления, пт.</summary>
        public double EffectForHeightPt { get; set; }

        /// <summary>Угол объекта, для которого записаны поля обрамления, градусы.</summary>
        public double EffectForRotationDeg { get; set; }

        /// <summary>Объект привязан к ячейке таблицы (layoutInCell).</summary>
        public bool LayoutInCell { get; set; } = true;

        /// <summary>Объекту разрешено перекрывать другие объекты (allowOverlap).</summary>
        public bool AllowOverlap { get; set; } = true;

        /// <summary>Якорь объекта закреплён (locked).</summary>
        public bool Locked { get; set; }

        /// <summary>Объект скрыт (docPr hidden).</summary>
        public bool Hidden { get; set; }

        /// <summary>
        /// Объект лежит за текстом (behindDoc). У объекта без обтекания это режим
        /// «за текстом», у обтекаемого — только слой рисования.
        /// </summary>
        public bool BehindDoc { get; set; }

        /// <summary>Заголовок объекта (docPr title).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Title { get; set; }

        /// <summary>Имя объекта (docPr name).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; set; }

        /// <summary>
        /// Обтекание «сквозное» (wp:wrapThrough), а не «по контуру» (wp:wrapTight).
        /// Раскладка у них одна; различие нужно для обратного переноса.
        /// </summary>
        public bool WrapThrough { get; set; }

        /// <summary>
        /// Обтекание «сверху и снизу» (wp:wrapTopAndBottom). Объект стоит в потоке
        /// своей полосой, а в .docx уходит якорем с исходным положением.
        /// </summary>
        public bool WrapTopAndBottom { get; set; }

        /// <summary>Положение объекта «сверху и снизу» относительно опоры, как в Word.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public TableFloatPosition? TopAndBottomPosition { get; set; }

        /// <summary>Контур обтекания был изменён вручную (wrapPolygon edited).</summary>
        public bool WrapPolygonEdited { get; set; }

        /// <summary>Контур обтекания Word. Null — контур совпадает с рамкой объекта.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public System.Collections.Generic.List<WordWrapPoint>? WrapPolygon { get; set; }

        /// <summary>
        /// Порядок наложения Word как он записан (relativeHeight). Уходит обратно без
        /// изменений, пока порядок объекта не меняли в редакторе.
        /// </summary>
        public long RelativeHeight { get; set; }

        /// <summary>Порядок наложения объекта, при котором записан RelativeHeight.</summary>
        public int RelativeHeightForZOrder { get; set; }

        /// <summary>
        /// Опора по горизонтали, как она названа у Word (relativeFrom), когда она
        /// уже, чем различает модель: «character», «insideMargin» и т. п.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? HorizontalRelativeFrom { get; set; }

        /// <summary>Опора по вертикали, как она названа у Word (relativeFrom).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? VerticalRelativeFrom { get; set; }

        /// <summary>
        /// Размер самой картинки (a:xfrm a:ext), когда он отличается от габарита
        /// рисунка (wp:extent): Word показывает рисунок по габариту, а размер картинки
        /// хранит отдельно. 0 — совпадает с габаритом.
        /// </summary>
        public double PictureWidthPt { get; set; }

        /// <summary>Высота самой картинки (a:xfrm a:ext). 0 — совпадает с габаритом.</summary>
        public double PictureHeightPt { get; set; }

        /// <summary>
        /// Контур объекта Word (a:prstGeom) как он записан, когда модель различает его
        /// грубее: шестиугольник, звезда, стрелка-блок и прочие заготовки Word. Уходит
        /// обратно без изменений, пока контур объекта не меняли в редакторе.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PresetGeometryXml { get; set; }

        /// <summary>Контур объекта в модели, при котором записан PresetGeometryXml.</summary>
        public ShapeType PresetGeometryShapeType { get; set; }

        /// <summary>Скругление углов в модели, при котором записан PresetGeometryXml.</summary>
        public double PresetGeometryCornerPt { get; set; }

        /// <summary>Контур Word ещё описывает объект: его не меняли в редакторе.</summary>
        public bool PresetGeometryValidFor(IFloatingObject obj) =>
            PresetGeometryXml is not null
            && obj.ShapeType == PresetGeometryShapeType
            && System.Math.Abs(obj.CornerRadiusPt - PresetGeometryCornerPt) < 0.01;

        /// <summary>
        /// Действуют ли поля обрамления для объекта в его нынешнем виде: после
        /// поворота или изменения размера в редакторе Word пересчитал бы их, и
        /// раскладка переходит на габарит повёрнутого прямоугольника.
        /// </summary>
        public bool EffectExtentValidFor(IFloatingObject obj)
        {
            if (!HasEffectExtent) return false;

            return System.Math.Abs(obj.WidthPt - EffectForWidthPt) < 0.01
                && System.Math.Abs(obj.HeightPt - EffectForHeightPt) < 0.01
                && System.Math.Abs(obj.RotationDeg - EffectForRotationDeg) < 0.01;
        }

        /// <summary>Глубокая копия.</summary>
        public WordDrawingInfo Clone()
        {
            var copy = (WordDrawingInfo)MemberwiseClone();
            copy.TopAndBottomPosition = TopAndBottomPosition?.Clone();

            if (WrapPolygon is not null)
            {
                copy.WrapPolygon = new System.Collections.Generic.List<WordWrapPoint>(WrapPolygon.Count);
                foreach (var point in WrapPolygon)
                    copy.WrapPolygon.Add(new WordWrapPoint { X = point.X, Y = point.Y });
            }

            return copy;
        }
    }

    /// <summary>
    /// Габарит, который объект занимает в строке и в зоне обтекания.
    ///
    /// У Word это рамка объекта плюс поля обрамления (effectExtent): повёрнутая без
    /// полей картинка занимает в строке свой неповёрнутый размер и выходит за него
    /// при рисовании. У объекта без полей из Word — габарит повёрнутого
    /// прямоугольника, как всегда было в редакторе.
    /// </summary>
    public static class FloatingObjectBox
    {
        /// <summary>
        /// Габарит объекта шириной widthPt и высотой heightPt. Размер может быть
        /// приведён к листу чтения; поля обрамления масштабируются тем же множителем.
        /// </summary>
        public static (float WidthPt, float HeightPt) Of(IFloatingObject obj, float widthPt, float heightPt)
        {
            if (obj.WordDrawing is { } word && word.EffectExtentValidFor(obj))
            {
                float scaleX = obj.WidthPt > 0.0 ? widthPt / (float)obj.WidthPt : 1f;
                float scaleY = obj.HeightPt > 0.0 ? heightPt / (float)obj.HeightPt : 1f;

                return (
                    widthPt + (float)(word.EffectLeftPt + word.EffectRightPt) * scaleX,
                    heightPt + (float)(word.EffectTopPt + word.EffectBottomPt) * scaleY);
            }

            double rad = obj.RotationDeg * System.Math.PI / 180.0;
            float absCos = (float)System.Math.Abs(System.Math.Cos(rad));
            float absSin = (float)System.Math.Abs(System.Math.Sin(rad));

            return (widthPt * absCos + heightPt * absSin, widthPt * absSin + heightPt * absCos);
        }

        /// <summary>
        /// Поля обрамления для записи в .docx: из Word — как пришли, пока действуют;
        /// иначе — столько, сколько повёрнутый прямоугольник выходит за свою рамку.
        /// </summary>
        public static (double Left, double Top, double Right, double Bottom) EffectExtentPt(IFloatingObject obj)
        {
            if (obj.WordDrawing is { } word && word.EffectExtentValidFor(obj))
                return (word.EffectLeftPt, word.EffectTopPt, word.EffectRightPt, word.EffectBottomPt);

            double rad = obj.RotationDeg * System.Math.PI / 180.0;
            double absCos = System.Math.Abs(System.Math.Cos(rad));
            double absSin = System.Math.Abs(System.Math.Sin(rad));
            double boxW = obj.WidthPt * absCos + obj.HeightPt * absSin;
            double boxH = obj.WidthPt * absSin + obj.HeightPt * absCos;

            double dx = System.Math.Max(0.0, (boxW - obj.WidthPt) / 2.0);
            double dy = System.Math.Max(0.0, (boxH - obj.HeightPt) / 2.0);

            return (dx, dy, dx, dy);
        }
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
        /// Опора плавающей картинки, пришедшей из Word: от листа, от полей или от своего
        /// абзаца, по смещению или по стороне. Null — опоры нет, и картинка отсчитывается
        /// от начала текстовой области своей страницы, как любая вставленная в редакторе.
        ///
        /// С опорой точка отсчёта другая, а смещения (<see cref="OffsetXPt"/>,
        /// <see cref="OffsetYPt"/>) прибавляются к ней так же: перетаскивание сдвигает
        /// картинку от её места у абзаца, и она продолжает ходить за ним. Опора от
        /// абзаца — это место блока картинки в потоке: он стоит прямо перед абзацем.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public TableFloatPosition? AnchorPosition { get; set; }

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

        /// <summary>
        /// Файл картинки в том виде, в каком он пришёл из .docx, когда лист показывает
        /// его перекодированным: TIFF, EMF и WMF лист не читает и рисует их копию в
        /// PNG (<see cref="ImageFileName"/>), а в .docx уходит исходный файл — тот же
        /// вектор и тот же формат, что был у Word. Null — показывается сам исходник.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SourceImageFileName { get; set; }

        /// <summary>Свойства рисунка Word, которых нет в модели картинки.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public WordDrawingInfo? WordDrawing { get; set; }
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
        /// Опора плавающей фигуры, пришедшей из Word: от листа, от полей или от своего
        /// абзаца. Null — опоры нет, и фигура отсчитывается от начала текстовой области
        /// своей страницы. Устроена так же, как у картинки
        /// (<see cref="ImageBlock.AnchorPosition"/>): смещения прибавляются к опоре.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public TableFloatPosition? AnchorPosition { get; set; }

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

        /// <summary>
        /// Текст внутри фигуры — надпись. Абзацы разделены переводом строки; строки
        /// переносятся по ширине фигуры сами. Есть только у замкнутой фигуры: у линии
        /// и стрелки писать негде.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? InnerText { get; set; }

        /// <summary>Шрифт текста фигуры. Null — шрифт по умолчанию.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? TextFontFamily { get; set; }

        /// <summary>Кегль текста фигуры, пт.</summary>
        public double TextSizePt { get; set; } = DefaultTextSizePt;

        /// <summary>Цвет текста фигуры в hex. Null — чёрный.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? TextColor { get; set; }

        /// <summary>Полужирный текст фигуры.</summary>
        public bool TextBold { get; set; }

        /// <summary>Курсивный текст фигуры.</summary>
        public bool TextItalic { get; set; }

        /// <summary>Выравнивание строк текста по ширине фигуры.</summary>
        public TextAlignment TextAlign { get; set; } = TextAlignment.Center;

        /// <summary>Положение текста по высоте фигуры.</summary>
        public VerticalAlignment TextVerticalAlign { get; set; } = VerticalAlignment.Middle;

        /// <summary>Отступ текста от левого и правого края фигуры, пт (как у надписи Word).</summary>
        public double TextInsetHorizontalPt { get; set; } = DefaultTextInsetHorizontalPt;

        /// <summary>Отступ текста от верхнего и нижнего края фигуры, пт (как у надписи Word).</summary>
        public double TextInsetVerticalPt { get; set; } = DefaultTextInsetVerticalPt;

        public const double DefaultTextSizePt = 11.0;
        public const double DefaultTextInsetHorizontalPt = 7.2;
        public const double DefaultTextInsetVerticalPt = 3.6;

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

        /// <summary>Свойства рисунка Word, которых нет в модели фигуры.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public WordDrawingInfo? WordDrawing { get; set; }
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

    /// <summary>
    /// Текст фигуры и его оформление одним значением: так лента читает надпись
    /// выделенной фигуры и так же записывает её обратно.
    /// </summary>
    public sealed record ShapeTextInfo(
        string Text,
        string? FontFamily,
        double SizePt,
        string? Color,
        bool Bold,
        bool Italic,
        TextAlignment Align,
        VerticalAlignment VerticalAlign);
}
