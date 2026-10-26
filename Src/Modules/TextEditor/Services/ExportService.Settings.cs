using System;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Writersword.Modules.TextEditor.Models.Document;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Настройки документа, от которых зависит вёрстка всех абзацев сразу: шаг
    /// табуляции по умолчанию и совместимость Word.
    ///
    /// Раньше выгрузка их не писала. Файл без режима совместимости Word открывает как
    /// документ Word 2007 — и обратный импорт тоже: выравнивание по ширине переставало
    /// сжимать пробелы, строки по ширине выходили длиннее, и документ вырастал на
    /// десяток страниц; абзац, кончающийся разрывом страницы, получал за разрывом
    /// лишний пустой абзац.
    /// </summary>
    public sealed partial class ExportService
    {
        /// <summary>
        /// Порядок элементов w:settings по схеме CT_Settings. Word читает настройки
        /// строго по схеме: элемент не на своём месте — файл повреждён.
        /// </summary>
        private static readonly string[] SettingsOrder =
        {
            "writeProtection", "view", "zoom", "removePersonalInformation", "removeDateAndTime",
            "doNotDisplayPageBoundaries", "displayBackgroundShape", "printPostScriptOverText",
            "printFractionalCharacterWidth", "printFormsData", "embedTrueTypeFonts", "embedSystemFonts",
            "saveSubsetFonts", "saveFormsData", "mirrorMargins", "alignBordersAndEdges",
            "bordersDoNotSurroundHeader", "bordersDoNotSurroundFooter", "gutterAtTop",
            "hideSpellingErrors", "hideGrammaticalErrors", "activeWritingStyle", "proofState",
            "formsDesign", "attachedTemplate", "linkStyles", "stylePaneFormatFilter",
            "stylePaneSortMethod", "documentType", "mailMerge", "revisionView", "trackRevisions",
            "doNotTrackMoves", "doNotTrackFormatting", "documentProtection", "autoFormatOverride",
            "styleLockTheme", "styleLockQFSet", "defaultTabStop", "autoHyphenation",
            "consecutiveHyphenLimit", "hyphenationZone", "doNotHyphenateCaps", "showEnvelope",
            "summaryLength", "clickAndTypeStyle", "defaultTableStyle", "evenAndOddHeaders",
            "bookFoldRevPrinting", "bookFoldPrinting", "bookFoldPrintingSheets",
            "drawingGridHorizontalSpacing", "drawingGridVerticalSpacing",
            "displayHorizontalDrawingGridEvery", "displayVerticalDrawingGridEvery",
            "doNotUseMarginsForDrawingGridOrigin", "drawingGridHorizontalOrigin",
            "drawingGridVerticalOrigin", "doNotShadeFormData", "noPunctuationKerning",
            "characterSpacingControl", "printTwoOnOne", "strictFirstAndLastChars",
            "noLineBreaksAfter", "noLineBreaksBefore", "savePreviewPicture",
            "doNotValidateAgainstSchema", "saveInvalidXml", "ignoreMixedContent",
            "alwaysShowPlaceholderText", "doNotDemarcateInvalidXml", "saveXmlDataOnly",
            "useXSLTWhenSaving", "saveThroughXslt", "showXMLTags", "alwaysMergeEmptyNamespace",
            "updateFields", "hdrShapeDefaults", "footnotePr", "endnotePr", "compat", "docVars",
            "rsids", "mathPr", "attachedSchema", "themeFontLang", "clrSchemeMapping",
            "doNotIncludeSubdocsInStats", "doNotAutoCompressPictures", "forceUpgrade", "captions",
            "readModeInkLockDown", "smartTagType", "schemaLibrary", "shapeDefaults",
            "doNotEmbedSmartTags", "decimalSymbol", "listSeparator"
        };

        /// <summary>
        /// Имя пометки wsx у w:compat: выравнивание по ширине в Writersword не сжимает
        /// пробелы. Word её не видит и раскладывает документ в режиме 15; Writersword
        /// при открытии файла возвращает документу его собственную вёрстку. Её читает
        /// импорт (ImportService).
        /// </summary>
        internal const string JustifyWithShrinkingAttribute = "justifyWithShrinking";

        /// <summary>
        /// Пишет шаг табуляции по умолчанию и совместимость Word.
        ///
        /// Режим совместимости — всегда 15 (Word 2013 и новее): по его правилам пишется
        /// и разрыв страницы в конце абзаца. Сжатие пробелов у документа, который его не
        /// просит, отмечено пометкой wsx. Интервалы между абзацами суммируются, если
        /// документ не схлопывает их, — w:doNotUseHTMLParagraphAutoSpacing.
        /// </summary>
        private static void WriteLayoutSettings(MainDocumentPart mainPart, DocumentModel document)
        {
            var part = mainPart.DocumentSettingsPart ?? mainPart.AddNewPart<DocumentSettingsPart>();
            part.Settings ??= new W.Settings();
            var settings = part.Settings;

            DocxTextEffects.DeclareNamespaces(settings);

            settings.RemoveAllChildren<W.DefaultTabStop>();
            settings.RemoveAllChildren<W.Compatibility>();

            double tabTwips = Math.Round(document.DefaultTabStopPt * TwipsPerPoint);
            if (tabTwips >= 1)
            {
                InsertSettingInOrder(settings, new W.DefaultTabStop
                {
                    Val = (Int16Value)(short)Math.Min(tabTwips, short.MaxValue)
                });
            }

            var compat = new W.Compatibility();

            if (!document.CollapseParagraphSpacing)
                compat.AppendChild(new W.DoNotUseHTMLParagraphAutoSpacing());

            compat.AppendChild(new W.CompatibilitySetting
            {
                Name = new EnumValue<W.CompatSettingNameValues>(W.CompatSettingNameValues.CompatibilityMode),
                Uri = "http://schemas.microsoft.com/office/word",
                Val = "15"
            });

            // Остальные настройки совместимости — те, что Word 2013 и новее пишет каждому
            // своему документу. Без них Word раскладывает файл по правилам старых версий:
            // иначе считает таблицы со стилем, переносы у нижнего края и отступы
            // зеркальных полей, и вёрстка расходится с исходным файлом.
            foreach (string setting in ModernCompatSettings)
                compat.AppendChild(CompatSetting(setting, "1"));

            if (!document.JustifyWithShrinking)
            {
                compat.SetAttribute(new OpenXmlAttribute(
                    "wsx", JustifyWithShrinkingAttribute, DocxTextEffects.WsxNamespace, "0"));
            }

            InsertSettingInOrder(settings, compat);

            settings.Save();
        }

        /// <summary>Настройки совместимости, которые Word 2013 и новее пишет каждому документу.</summary>
        private static readonly string[] ModernCompatSettings =
        {
            "overrideTableStyleFontSizeAndJustification",
            "enableOpenTypeFeatures",
            "doNotFlipMirrorIndents",
            "differentiateMultirowTableHeaders",
            "useWord2013TrackBottomHyphenation"
        };

        /// <summary>
        /// Настройка совместимости по имени. Имя пишется атрибутом, а не значением
        /// перечисления: в перечислении SDK есть не все имена, которые знает Word.
        /// </summary>
        private static W.CompatibilitySetting CompatSetting(string name, string value)
        {
            var setting = new W.CompatibilitySetting();
            const string wordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            setting.SetAttribute(new OpenXmlAttribute("w", "name", wordNamespace, name));
            setting.SetAttribute(new OpenXmlAttribute("w", "uri", wordNamespace, "http://schemas.microsoft.com/office/word"));
            setting.SetAttribute(new OpenXmlAttribute("w", "val", wordNamespace, value));
            return setting;
        }

        /// <summary>Ставит элемент настроек на его место по схеме.</summary>
        private static void InsertSettingInOrder(W.Settings settings, OpenXmlElement element)
        {
            int rank = Array.IndexOf(SettingsOrder, element.LocalName);

            foreach (var existing in settings.ChildElements)
            {
                int existingRank = Array.IndexOf(SettingsOrder, existing.LocalName);
                if (existingRank > rank)
                {
                    existing.InsertBeforeSelf(element);
                    return;
                }
            }

            settings.AppendChild(element);
        }
    }
}
