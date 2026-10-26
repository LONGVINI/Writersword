using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Writersword.Modules.TextEditor.Models.Page
{
    /// <summary>Как раздел начинается относительно предыдущего (w:type у Word).</summary>
    public enum SectionStart
    {
        /// <summary>Со следующей страницы.</summary>
        NextPage = 0,

        /// <summary>На той же странице.</summary>
        Continuous = 1,

        /// <summary>Со следующей чётной страницы: при нечётной Word вставляет пустой лист.</summary>
        EvenPage = 2,

        /// <summary>Со следующей нечётной страницы: при чётной Word вставляет пустой лист.</summary>
        OddPage = 3,

        /// <summary>Со следующей колонки.</summary>
        NextColumn = 4
    }

    /// <summary>Выравнивание текста раздела по высоте листа (w:vAlign).</summary>
    public enum SectionVerticalAlignment
    {
        Top = 0,
        Center = 1,
        Bottom = 2,

        /// <summary>По высоте: промежутки между абзацами растягиваются до низа листа.</summary>
        Justify = 3
    }

    /// <summary>Колонка раздела с неравными колонками: ширина и промежуток после неё.</summary>
    public sealed class SectionColumn
    {
        public double WidthMm { get; set; }
        public double SpaceAfterMm { get; set; }

        public SectionColumn Clone() => (SectionColumn)MemberwiseClone();
    }

    /// <summary>Колонки раздела (w:cols).</summary>
    public sealed class SectionColumns
    {
        /// <summary>Число колонок.</summary>
        public int Count { get; set; } = 1;

        /// <summary>Промежуток между равными колонками.</summary>
        public double GapMm { get; set; } = 12.5;

        /// <summary>Черта между колонками.</summary>
        public bool Separator { get; set; }

        /// <summary>
        /// Цвет черты между колонками — код подборщика цвета: «#RRGGBB», «#AARRGGBB»
        /// (с прозрачностью) или градиент «grad|…». Null — авто: чёрная, как у Word.
        /// У Word черта всегда одна — цвета у неё нет; в .docx цвет уходит пропускаемым
        /// атрибутом wsx (ExportService.SeparatorColorAttribute), Word его не видит.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SeparatorColor { get; set; }

        /// <summary>
        /// Колонки разной ширины: каждая со своей шириной и промежутком. Null — все
        /// колонки равны и стоят через <see cref="GapMm"/>.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<SectionColumn>? Columns { get; set; }

        public SectionColumns Clone()
        {
            var copy = (SectionColumns)MemberwiseClone();
            if (Columns is not null)
            {
                copy.Columns = new List<SectionColumn>(Columns.Count);
                foreach (var column in Columns) copy.Columns.Add(column.Clone());
            }
            return copy;
        }
    }

    /// <summary>Одна сторона рамки страницы (w:pgBorders).</summary>
    public sealed class PageBorderSide
    {
        /// <summary>Вид линии Word: single, double, dotted, dashed, thick и другие (ST_Border).</summary>
        public string Style { get; set; } = "single";

        /// <summary>Толщина линии, пт.</summary>
        public double WidthPt { get; set; } = 0.5;

        /// <summary>Отступ рамки: от края листа или от текста — по <see cref="SectionPageBorders.FromPageEdge"/>, пт.</summary>
        public double SpacePt { get; set; }

        /// <summary>Цвет «#RRGGBB»; null — авто (чёрный).</summary>
        public string? Color { get; set; }

        /// <summary>Рамка с тенью (w:shadow).</summary>
        public bool Shadow { get; set; }

        public PageBorderSide Clone() => (PageBorderSide)MemberwiseClone();
    }

    /// <summary>На каких листах раздела рисуется рамка страницы (w:display).</summary>
    public enum PageBorderDisplay
    {
        AllPages = 0,
        FirstPage = 1,
        NotFirstPage = 2
    }

    /// <summary>Рамка страницы раздела.</summary>
    public sealed class SectionPageBorders
    {
        /// <summary>Отступ рамки отсчитывается от края листа (иначе — от текста).</summary>
        public bool FromPageEdge { get; set; }

        public PageBorderDisplay Display { get; set; }

        /// <summary>Рамка под текстом (w:zOrder="back"); иначе поверх.</summary>
        public bool Behind { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public PageBorderSide? Top { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public PageBorderSide? Left { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public PageBorderSide? Bottom { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public PageBorderSide? Right { get; set; }

        public SectionPageBorders Clone() => new()
        {
            FromPageEdge = FromPageEdge,
            Display = Display,
            Behind = Behind,
            Top = Top?.Clone(),
            Left = Left?.Clone(),
            Bottom = Bottom?.Clone(),
            Right = Right?.Clone()
        };
    }

    /// <summary>Когда счёт номеров строк начинается заново (w:lnNumType/@w:restart).</summary>
    public enum LineNumberRestart
    {
        NewPage = 0,
        NewSection = 1,
        Continuous = 2
    }

    /// <summary>Номера строк раздела (w:lnNumType).</summary>
    public sealed class SectionLineNumbering
    {
        /// <summary>Показывается каждый n-й номер.</summary>
        public int CountBy { get; set; } = 1;

        /// <summary>Номер первой строки.</summary>
        public int Start { get; set; } = 1;

        /// <summary>Расстояние от номера до текста, мм; 0 — как у Word по умолчанию.</summary>
        public double DistanceMm { get; set; }

        public LineNumberRestart Restart { get; set; }

        public SectionLineNumbering Clone() => (SectionLineNumbering)MemberwiseClone();
    }

    /// <summary>
    /// Раздел документа Word: лист, поля, колонки, начало, рамка, номера строк и счёт
    /// страниц. Хранится у разрыва, которым раздел кончается
    /// (<see cref="Document.BreakBlock.Section"/>), — так же, как Word хранит w:sectPr у
    /// последнего абзаца раздела. Последний раздел описывают параметры страницы и колонки
    /// документа и <see cref="Document.DocumentModel.FinalSection"/>.
    /// </summary>
    public sealed class SectionLayout
    {
        /// <summary>Как раздел начинается относительно предыдущего.</summary>
        public SectionStart Start { get; set; }

        /// <summary>
        /// Лист и поля раздела. Null — у последнего раздела: его лист — параметры
        /// страницы документа.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public TextEditorPageSettings? Page { get; set; }

        /// <summary>Колонки раздела. Null — у последнего раздела: колонки документа.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SectionColumns? Columns { get; set; }

        /// <summary>Особая первая страница раздела (w:titlePg).</summary>
        public bool TitlePage { get; set; }

        public SectionVerticalAlignment VerticalAlignment { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SectionPageBorders? PageBorders { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SectionLineNumbering? LineNumbering { get; set; }

        /// <summary>Счёт страниц раздела начинается с этого номера; null — продолжается.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? PageNumberStart { get; set; }

        /// <summary>Вид номера страницы раздела (w:pgNumType/@w:fmt); null — арабские цифры.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public PageNumberFormat? PageNumberFormat { get; set; }

        /// <summary>
        /// Смена параметров раздела правкой: лист, поля, колонки, выравнивание по
        /// высоте, рамка, номера строк (w:sectPrChange у Word). Прежний вид — запись
        /// Services.SectionFrame. Null — параметры правкой не менялись.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Document.ObjectChange? FormatChange { get; set; }

        public SectionLayout Clone() => new()
        {
            FormatChange = FormatChange?.Clone(),
            Start = Start,
            Page = Page?.Clone(),
            Columns = Columns?.Clone(),
            TitlePage = TitlePage,
            VerticalAlignment = VerticalAlignment,
            PageBorders = PageBorders?.Clone(),
            LineNumbering = LineNumbering?.Clone(),
            PageNumberStart = PageNumberStart,
            PageNumberFormat = PageNumberFormat
        };
    }
}
