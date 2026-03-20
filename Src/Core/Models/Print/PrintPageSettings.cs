using System.Text.Json.Serialization;

namespace Writersword.Core.Models.Print
{
    /// <summary>
    /// Стандартный формат бумаги.
    /// Используется во всех модулях поддерживающих печать.
    /// </summary>
    public enum PaperSize
    {
        A3 = 0,
        A4 = 1,
        A5 = 2,
        Letter = 3,
        Legal = 4,
        B5 = 5,
        Custom = 6
    }

    /// <summary>
    /// Ориентация страницы.
    /// </summary>
    public enum PageOrientation
    {
        Portrait = 0,
        Landscape = 1
    }

    /// <summary>
    /// Физические настройки страницы — размер бумаги, поля, ориентация.
    /// Общая модель уровня приложения: используется IPrintableDocument,
    /// PrintService и PrintPreviewViewModel.
    /// Не привязана ни к одному конкретному модулю.
    /// Модули могут расширять её через собственные классы-наследники
    /// или хранить ссылку на неё внутри своих моделей.
    /// </summary>
    public sealed class PrintPageSettings
    {
        /// <summary>Предустановленный формат бумаги.</summary>
        public PaperSize PaperSize { get; set; } = PaperSize.A4;

        /// <summary>Ширина страницы в миллиметрах. Используется при PaperSize.Custom.</summary>
        public double WidthMm { get; set; } = 210;

        /// <summary>Высота страницы в миллиметрах. Используется при PaperSize.Custom.</summary>
        public double HeightMm { get; set; } = 297;

        /// <summary>Ориентация страницы.</summary>
        public PageOrientation Orientation { get; set; } = PageOrientation.Portrait;

        /// <summary>Верхнее поле в мм.</summary>
        public double MarginTopMm { get; set; } = 25;

        /// <summary>Нижнее поле в мм.</summary>
        public double MarginBottomMm { get; set; } = 25;

        /// <summary>Левое поле в мм.</summary>
        public double MarginLeftMm { get; set; } = 30;

        /// <summary>Правое поле в мм.</summary>
        public double MarginRightMm { get; set; } = 15;

        /// <summary>Поле переплёта в мм (добавляется к левому полю).</summary>
        public double MarginGutterMm { get; set; } = 0;

        /// <summary>Расстояние от верхнего края до верхнего колонтитула в мм.</summary>
        public double HeaderDistanceMm { get; set; } = 12;

        /// <summary>Расстояние от нижнего края до нижнего колонтитула в мм.</summary>
        public double FooterDistanceMm { get; set; } = 12;

        /// <summary>
        /// Возвращает физическую ширину страницы в мм с учётом ориентации.
        /// При Landscape ширина и высота меняются местами.
        /// </summary>
        public double GetPhysicalWidthMm()
            => Orientation == PageOrientation.Landscape ? HeightMm : WidthMm;

        /// <summary>
        /// Возвращает физическую высоту страницы в мм с учётом ориентации.
        /// </summary>
        public double GetPhysicalHeightMm()
            => Orientation == PageOrientation.Landscape ? WidthMm : HeightMm;

        /// <summary>
        /// Возвращает ширину текстовой области в мм —
        /// ширина страницы минус левое, правое поля и переплёт.
        /// </summary>
        public double GetTextWidthMm()
            => GetPhysicalWidthMm() - MarginLeftMm - MarginRightMm - MarginGutterMm;

        /// <summary>
        /// Возвращает высоту текстовой области в мм —
        /// высота страницы минус верхнее и нижнее поля.
        /// </summary>
        public double GetTextHeightMm()
            => GetPhysicalHeightMm() - MarginTopMm - MarginBottomMm;

        /// <summary>
        /// Фабричный метод: стандартные настройки A4 Portrait с полями как в Word по умолчанию.
        /// </summary>
        public static PrintPageSettings DefaultA4() => new();

        /// <summary>
        /// Фабричный метод: книжный формат 145x205 мм (распространённый формат романов).
        /// Поля: внутреннее 20 мм, внешнее 15 мм, верх/низ 20 мм, переплёт 5 мм.
        /// </summary>
        public static PrintPageSettings BookFormat() => new()
        {
            PaperSize = PaperSize.Custom,
            WidthMm = 145,
            HeightMm = 205,
            Orientation = PageOrientation.Portrait,
            MarginTopMm = 20,
            MarginBottomMm = 20,
            MarginLeftMm = 20,
            MarginRightMm = 15,
            MarginGutterMm = 5
        };

        /// <summary>
        /// Фабричный метод: A3 Landscape — для схем, таблиц, раскладок.
        /// </summary>
        public static PrintPageSettings A3Landscape() => new()
        {
            PaperSize = PaperSize.A3,
            WidthMm = 297,
            HeightMm = 420,
            Orientation = PageOrientation.Landscape,
            MarginTopMm = 20,
            MarginBottomMm = 20,
            MarginLeftMm = 20,
            MarginRightMm = 20
        };
    }
}