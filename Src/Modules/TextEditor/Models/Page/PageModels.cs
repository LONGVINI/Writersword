using System.Text.Json.Serialization;

namespace Writersword.Modules.TextEditor.Models.Page
{
    /// <summary>
    /// Стандартный формат бумаги.
    /// </summary>
    public enum PaperSize
    {
        A4 = 0,
        A3 = 1,
        A5 = 2,
        Letter = 3,
        Custom = 4
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
    /// Настройки физической страницы (размер, поля, ориентация).
    /// Используется и на уровне документа, и переопределяется на уровне раздела.
    /// </summary>
    public sealed class PageSettings
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

        /// <summary>Расстояние от верхнего края до колонтитула в мм.</summary>
        public double HeaderDistanceMm { get; set; } = 12;

        /// <summary>Расстояние от нижнего края до колонтитула в мм.</summary>
        public double FooterDistanceMm { get; set; } = 12;

        /// <summary>
        /// Возвращает ширину текстовой области в мм с учётом полей и ориентации.
        /// </summary>
        public double GetTextWidthMm()
        {
            double pageWidth = Orientation == PageOrientation.Landscape ? HeightMm : WidthMm;
            return pageWidth - MarginLeftMm - MarginRightMm - MarginGutterMm;
        }

        /// <summary>
        /// Возвращает высоту текстовой области в мм.
        /// </summary>
        public double GetTextHeightMm()
        {
            double pageHeight = Orientation == PageOrientation.Landscape ? WidthMm : HeightMm;
            return pageHeight - MarginTopMm - MarginBottomMm;
        }

        /// <summary>Применяет предустановленный размер бумаги, обновляя WidthMm и HeightMm.</summary>
        public void ApplyPaperSize(PaperSize size)
        {
            PaperSize = size;
            switch (size)
            {
                case PaperSize.A3:
                    WidthMm = 297; HeightMm = 420; break;
                case PaperSize.A4:
                    WidthMm = 210; HeightMm = 297; break;
                case PaperSize.A5:
                    WidthMm = 148; HeightMm = 210; break;
                case PaperSize.Letter:
                    WidthMm = 215.9; HeightMm = 279.4; break;
            }
        }

        public PageSettings Clone() => (PageSettings)MemberwiseClone();
    }

    /// <summary>
    /// Предустановленная цветовая тема листа.
    /// </summary>
    public enum CanvasThemePreset
    {
        /// <summary>Чистый белый лист.</summary>
        White = 0,
        /// <summary>Состаренная бумага (кремовый фон).</summary>
        AgedPaper = 1,
        /// <summary>Тёмный режим для глаз.</summary>
        DarkMode = 2,
        /// <summary>Произвольные цвета заданные пользователем.</summary>
        Custom = 3
    }

    /// <summary>
    /// Визуальные настройки листа — цвет фона и текста.
    /// Полностью независимы от темы приложения и от настроек печати/экспорта.
    /// При экспорте в PDF/docx эти значения игнорируются.
    /// </summary>
    public sealed class CanvasSettings
    {
        /// <summary>Активный пресет.</summary>
        public CanvasThemePreset Preset { get; set; } = CanvasThemePreset.White;

        /// <summary>Цвет фона листа (#RRGGBB). Переопределяется пресетом при его выборе.</summary>
        public string PageBackgroundColor { get; set; } = "#FFFFFF";

        /// <summary>Цвет основного текста (#RRGGBB). Переопределяется пресетом.</summary>
        public string DefaultTextColor { get; set; } = "#1A1A1A";

        /// <summary>Цвет тени/фона вокруг листа.</summary>
        public string CanvasBackgroundColor { get; set; } = "#E8E8E8";

        /// <summary>Применяет предустановленную тему, обновляя все цвета.</summary>
        public void ApplyPreset(CanvasThemePreset preset)
        {
            Preset = preset;
            switch (preset)
            {
                case CanvasThemePreset.White:
                    PageBackgroundColor = "#FFFFFF";
                    DefaultTextColor = "#1A1A1A";
                    CanvasBackgroundColor = "#E8E8E8";
                    break;
                case CanvasThemePreset.AgedPaper:
                    PageBackgroundColor = "#F5EDD6";
                    DefaultTextColor = "#2C1A0E";
                    CanvasBackgroundColor = "#D4C4A0";
                    break;
                case CanvasThemePreset.DarkMode:
                    PageBackgroundColor = "#1E1E2E";
                    DefaultTextColor = "#CDD6F4";
                    CanvasBackgroundColor = "#11111B";
                    break;
            }
        }

        public CanvasSettings Clone() => (CanvasSettings)MemberwiseClone();
    }

    /// <summary>
    /// Настройки нумерации страниц в колонтитуле.
    /// </summary>
    public enum PageNumberFormat
    {
        /// <summary>1, 2, 3 ...</summary>
        Decimal = 0,
        /// <summary>i, ii, iii ...</summary>
        LowerRoman = 1,
        /// <summary>I, II, III ...</summary>
        UpperRoman = 2,
        /// <summary>a, b, c ...</summary>
        LowerLetter = 3,
        /// <summary>A, B, C ...</summary>
        UpperLetter = 4
    }

    /// <summary>
    /// Верхний или нижний колонтитул раздела.
    /// </summary>
    public sealed class HeaderFooterModel
    {
        /// <summary>Включён ли колонтитул.</summary>
        public bool IsEnabled { get; set; }

        /// <summary>Отдельный колонтитул для первой страницы раздела.</summary>
        public bool DifferentFirstPage { get; set; }

        /// <summary>Отдельные колонтитулы для чётных и нечётных страниц.</summary>
        public bool DifferentOddEven { get; set; }

        /// <summary>Текст колонтитула на нечётных (и всех обычных) страницах.</summary>
        public string OddPageContent { get; set; } = string.Empty;

        /// <summary>Текст колонтитула на чётных страницах.</summary>
        public string EvenPageContent { get; set; } = string.Empty;

        /// <summary>Текст колонтитула на первой странице раздела.</summary>
        public string FirstPageContent { get; set; } = string.Empty;

        /// <summary>Включить вывод номера страницы.</summary>
        public bool ShowPageNumber { get; set; }

        /// <summary>Формат нумерации страниц.</summary>
        public PageNumberFormat PageNumberFormat { get; set; } = PageNumberFormat.Decimal;

        /// <summary>Начальный номер страницы в разделе. -1 — продолжить от предыдущего.</summary>
        public int StartPageNumber { get; set; } = -1;

        public HeaderFooterModel Clone() => (HeaderFooterModel)MemberwiseClone();
    }

    /// <summary>
    /// Настройки колонок раздела.
    /// </summary>
    public sealed class ColumnSettings
    {
        /// <summary>Количество колонок.</summary>
        public int ColumnCount { get; set; } = 1;

        /// <summary>Одинаковая ширина колонок.</summary>
        public bool EqualWidth { get; set; } = true;

        /// <summary>
        /// Ширина каждой колонки в мм. Используется при EqualWidth = false.
        /// Длина массива должна равняться ColumnCount.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double[]? ColumnWidthsMm { get; set; }

        /// <summary>
        /// Отступ между колонками в мм. Используется при EqualWidth = false.
        /// Длина массива должна равняться ColumnCount - 1.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double[]? SpacingMm { get; set; }

        /// <summary>Отступ между колонками в мм при EqualWidth = true.</summary>
        public double EqualSpacingMm { get; set; } = 12.5;

        /// <summary>Показывать разделительную линию между колонками.</summary>
        public bool ShowSeparatorLine { get; set; }

        public ColumnSettings Clone() => (ColumnSettings)MemberwiseClone();
    }
}
