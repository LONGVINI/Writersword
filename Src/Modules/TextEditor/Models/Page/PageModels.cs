using System.Collections.Generic;
using Writersword.Core.Models.Print;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Models.Page
{
    /// <summary>
    /// Настройки физической страницы документа TextEditor.
    /// PaperSize и PageOrientation вынесены в Core.Models.Print —
    /// они общие для всех модулей поддерживающих печать.
    /// BreakType определён в Models.Document — не дублируется здесь.
    /// </summary>
    public sealed class TextEditorPageSettings
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
        /// Возвращает ширину текстовой области в мм с учётом полей и переплёта.
        /// </summary>
        public double GetTextWidthMm()
            => GetPhysicalWidthMm() - MarginLeftMm - MarginRightMm - MarginGutterMm;

        /// <summary>
        /// Возвращает высоту текстовой области в мм.
        /// </summary>
        public double GetTextHeightMm()
            => GetPhysicalHeightMm() - MarginTopMm - MarginBottomMm;

        /// <summary>
        /// Применяет предустановленный формат бумаги — выставляет WidthMm и HeightMm.
        /// При Custom размеры не меняются.
        /// </summary>
        public void ApplyPaperSize(PaperSize size)
        {
            PaperSize = size;
            switch (size)
            {
                case PaperSize.A3:
                    WidthMm = 297; HeightMm = 420;
                    break;
                case PaperSize.A4:
                    WidthMm = 210; HeightMm = 297;
                    break;
                case PaperSize.A5:
                    WidthMm = 148; HeightMm = 210;
                    break;
                case PaperSize.Letter:
                    WidthMm = 215.9; HeightMm = 279.4;
                    break;
                case PaperSize.Legal:
                    WidthMm = 215.9; HeightMm = 355.6;
                    break;
                case PaperSize.B5:
                    WidthMm = 176; HeightMm = 250;
                    break;
                    // Custom — размеры задаются пользователем вручную, не меняем.
            }
        }
    }

    /// <summary>
    /// Настройки колонок текстовой области.
    /// </summary>
    public sealed class ColumnSettings
    {
        /// <summary>Количество колонок. 1 — одна колонка (нет разбивки).</summary>
        public int ColumnCount { get; set; } = 1;

        /// <summary>Расстояние между колонками в мм.</summary>
        public double GapMm { get; set; } = 12.5;

        /// <summary>Показывать разделительную линию между колонками.</summary>
        public bool ShowSeparator { get; set; } = false;
    }

    /// <summary>
    /// Пресет цветовой темы канваса редактора.
    /// Влияет только на отображение в редакторе — не на печать.
    /// </summary>
    public enum CanvasThemePreset
    {
        Default = 0,
        Sepia = 1,
        Dark = 2,
        HighContrast = 3,
        Custom = 4
    }

    /// <summary>
    /// Настройки визуального отображения канваса.
    /// Не влияют на печать и экспорт.
    /// </summary>
    public sealed class CanvasSettings
    {
        /// <summary>Активный пресет темы канваса.</summary>
        public CanvasThemePreset Preset { get; set; } = CanvasThemePreset.Default;

        /// <summary>Цвет фона страницы в редакторе (HEX). Не влияет на печать.</summary>
        public string PageBackgroundColor { get; set; } = "#FFFFFF";

        /// <summary>Цвет текста по умолчанию в редакторе (HEX). Не влияет на печать.</summary>
        public string DefaultTextColor { get; set; } = "#1A1A1A";

        /// <summary>
        /// Применяет предустановленную тему — выставляет цвета канваса по пресету.
        /// </summary>
        public void ApplyPreset(CanvasThemePreset preset)
        {
            Preset = preset;
            switch (preset)
            {
                case CanvasThemePreset.Default:
                    PageBackgroundColor = "#FFFFFF";
                    DefaultTextColor = "#1A1A1A";
                    break;
                case CanvasThemePreset.Sepia:
                    PageBackgroundColor = "#F5F0E8";
                    DefaultTextColor = "#3B2F2F";
                    break;
                case CanvasThemePreset.Dark:
                    PageBackgroundColor = "#1E1E1E";
                    DefaultTextColor = "#D4D4D4";
                    break;
                case CanvasThemePreset.HighContrast:
                    PageBackgroundColor = "#000000";
                    DefaultTextColor = "#FFFFFF";
                    break;
                    // Custom — цвета задаются пользователем вручную, не меняем.
            }
        }
    }

    /// <summary>
    /// Колонтитул раздела — верхний или нижний.
    /// Содержит параграфы которые повторяются на каждой странице раздела.
    /// </summary>
    public sealed class HeaderFooterModel
    {
        /// <summary>Колонтитул активен — отображается при печати и в режиме Page.</summary>
        public bool IsEnabled { get; set; } = false;

        /// <summary>
        /// Отличается ли колонтитул первой страницы от остальных.
        /// Используется для титульной страницы.
        /// </summary>
        public bool DifferentFirstPage { get; set; } = false;

        /// <summary>Параграфы колонтитула в порядке следования.</summary>
        public List<ParagraphBlock> Paragraphs { get; set; } = new();
    }
}