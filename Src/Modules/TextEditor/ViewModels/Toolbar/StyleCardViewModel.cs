using System;
using System.Collections.Generic;
using Avalonia.Media;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Styles;

namespace Writersword.Modules.TextEditor.ViewModels.Toolbar
{
    /// <summary>
    /// Одна карточка в галерее стилей.
    ///
    /// Карточка держит и внутреннее имя стиля, и то, которое видит человек. Раньше
    /// галерея работала с одними строками, и строки эти были отображаемыми: в списке
    /// стояло «Heading 1», а стиль в рукописи зовётся «Heading1». Выбор карточки писал
    /// абзацу имя, которого в документе нет, — абзац верстался «Обычным», а оглавление
    /// не считало его заголовком, потому что и поиск стиля, и разбор встроенного имени
    /// идут по Name.
    ///
    /// Вид карточки тоже берётся у самого стиля, а не угадывается по его имени. Иначе
    /// пользовательский стиль показывался бы в галерее не тем, чем он является.
    /// </summary>
    public sealed class StyleCardViewModel
    {
        /// <summary>Наибольший кегль, который помещается в карточку.</summary>
        private const double PreviewMaxPt = 17;

        /// <summary>Наименьший — ниже надпись не читается.</summary>
        private const double PreviewMinPt = 9;

        /// <summary>Глубина подъёма по цепочке BasedOn.</summary>
        private const int MaxChainDepth = 16;

        public StyleCardViewModel(DocumentStyle style, DocumentModel? document)
        {
            Name = style.Name;

            DisplayName = string.IsNullOrWhiteSpace(style.DisplayName)
                ? style.Name
                : style.DisplayName;

            IsBuiltIn = style.IsBuiltIn;
            SortOrderKey = style.SortOrder;
            IsCharacterStyle = style.StyleType == DocumentStyleType.Character;

            var (size, bold, italic) = ResolveLook(style, document);

            PreviewFontSize = size;
            PreviewFontWeight = bold ? FontWeight.Bold : FontWeight.Normal;
            PreviewFontStyle = italic ? FontStyle.Italic : FontStyle.Normal;
        }

        /// <summary>Внутреннее имя стиля. Именно оно ложится абзацу.</summary>
        public string Name { get; }

        /// <summary>Имя для человека. Его видно на карточке и его можно менять.</summary>
        public string DisplayName { get; }

        /// <summary>Встроенный стиль — его нельзя удалить.</summary>
        public bool IsBuiltIn { get; }

        /// <summary>
        /// Символьный стиль — ложится на выделенные буквы, а не на абзац целиком.
        ///
        /// По этому признаку галерея и решает, что делать на щелчке. Человеку выбирать
        /// не приходится: он выбирает стиль, а куда тот ложится — свойство самого стиля.
        /// </summary>
        public bool IsCharacterStyle { get; }

        /// <summary>
        /// Порядок в галерее. Встроенные стили расставлены им осмысленно — обычный,
        /// заголовки по возрастанию, потом всё прочее, — и человек привык к этому ряду.
        /// </summary>
        public int SortOrderKey { get; }

        public double PreviewFontSize { get; }

        public FontWeight PreviewFontWeight { get; }

        public FontStyle PreviewFontStyle { get; }

        /// <summary>
        /// Кегль, жирность и курсив стиля с учётом цепочки BasedOn.
        ///
        /// Подъём по цепочке обязателен: у большинства стилей своего кегля нет, он
        /// приходит от основы. Взяв только собственные свойства, галерея показывала бы
        /// половину карточек одинаковыми.
        ///
        /// Кегль для карточки ужимается в её размер: карточка шириной в шесть десятков
        /// точек не покажет заголовок в тридцать два пункта, а показывать его обрезанным
        /// незачем — человек и так знает, что заголовок крупный.
        /// </summary>
        private static (double Size, bool Bold, bool Italic) ResolveLook(
            DocumentStyle style, DocumentModel? document)
        {
            double size = 0;
            bool bold = false;
            bool italic = false;

            bool sizeFound = false;
            bool lookFound = false;

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            DocumentStyle? current = style;

            for (int depth = 0; current is not null && depth < MaxChainDepth; depth++)
            {
                if (!visited.Add(current.Name)) break;

                var run = current.RunProperties;
                if (run is not null)
                {
                    if (!sizeFound && run.FontSize.HasValue)
                    {
                        size = run.FontSize.Value;
                        sizeFound = true;
                    }

                    if (!lookFound && (run.IsBold == true || run.IsItalic == true))
                    {
                        bold = run.IsBold == true;
                        italic = run.IsItalic == true;
                        lookFound = true;
                    }
                }

                if (string.IsNullOrEmpty(current.BasedOn)) break;
                current = document?.FindStyle(current.BasedOn!);
            }

            if (!sizeFound || size <= 0) size = 12;

            if (size > PreviewMaxPt) size = PreviewMaxPt;
            if (size < PreviewMinPt) size = PreviewMinPt;

            return (size, bold, italic);
        }
    }
}
