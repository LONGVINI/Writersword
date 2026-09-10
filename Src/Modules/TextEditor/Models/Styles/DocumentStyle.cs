using System.Text.Json.Serialization;
using Writersword.Modules.TextEditor.Models.Inline;

namespace Writersword.Modules.TextEditor.Models.Styles
{
    /// <summary>
    /// Тип именованного стиля.
    /// </summary>
    public enum DocumentStyleType
    {
        /// <summary>Стиль абзаца (применяется к целому абзацу).</summary>
        Paragraph = 0,
        /// <summary>Символьный стиль (применяется к выделенному фрагменту).</summary>
        Character = 1
    }

    /// <summary>
    /// Именованный стиль документа.
    /// Может быть встроенным (IsBuiltIn = true) или пользовательским.
    /// Встроенные стили не удаляются пользователем.
    /// </summary>
    public sealed class DocumentStyle
    {
        /// <summary>Уникальное строковое имя стиля (например "Normal", "Heading1", "Quote").</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Отображаемое имя в UI (локализованное).</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>Тип стиля.</summary>
        public DocumentStyleType StyleType { get; set; } = DocumentStyleType.Paragraph;

        /// <summary>Встроенный стиль — не может быть удалён пользователем.</summary>
        public bool IsBuiltIn { get; set; }

        /// <summary>
        /// Имя базового стиля из которого наследуются незаданные свойства.
        /// Null — нет базового стиля.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? BasedOn { get; set; }

        /// <summary>Свойства абзаца. Null для символьных стилей.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ParagraphProperties? ParagraphProperties { get; set; }

        /// <summary>Свойства шрифта/текста применяемые этим стилем.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public RunProperties? RunProperties { get; set; }

        /// <summary>Порядок отображения в выпадающем списке стилей (меньше = выше).</summary>
        public int SortOrder { get; set; }

        /// <summary>Создаёт набор встроенных стилей по умолчанию.</summary>
        public static DocumentStyle[] CreateBuiltInStyles()
        {
            return new[]
            {
                new DocumentStyle
                {
                    Name = "Normal",
                    DisplayName = "Normal",
                    IsBuiltIn = true,
                    SortOrder = 0,
                    ParagraphProperties = new ParagraphProperties
                    {
                        Alignment = TextAlignment.Left,
                        LineSpacingRule = Styles.LineSpacingRule.Auto,
                        LineSpacingValue = 1.0,
                        SpaceAfter = 8
                    },
                    RunProperties = new RunProperties
                    {
                        FontFamily = "Times New Roman",
                        FontSize = 14
                    }
                },
                new DocumentStyle
                {
                    Name = "Heading1",
                    DisplayName = "Heading 1",
                    IsBuiltIn = true,
                    BasedOn = "Normal",
                    SortOrder = 1,
                    ParagraphProperties = new ParagraphProperties
                    {
                        SpaceBefore = 12,
                        SpaceAfter = 6,
                        KeepWithNext = true,
                        PageBreakBefore = false
                    },
                    RunProperties = new RunProperties
                    {
                        FontSize = 24,
                        IsBold = true
                    }
                },
                new DocumentStyle
                {
                    Name = "Heading2",
                    DisplayName = "Heading 2",
                    IsBuiltIn = true,
                    BasedOn = "Normal",
                    SortOrder = 2,
                    ParagraphProperties = new ParagraphProperties
                    {
                        SpaceBefore = 10,
                        SpaceAfter = 4,
                        KeepWithNext = true
                    },
                    RunProperties = new RunProperties
                    {
                        FontSize = 20,
                        IsBold = true
                    }
                },
                new DocumentStyle
                {
                    Name = "Heading3",
                    DisplayName = "Heading 3",
                    IsBuiltIn = true,
                    BasedOn = "Normal",
                    SortOrder = 3,
                    ParagraphProperties = new ParagraphProperties
                    {
                        SpaceBefore = 8,
                        SpaceAfter = 4,
                        KeepWithNext = true
                    },
                    RunProperties = new RunProperties
                    {
                        FontSize = 16,
                        IsBold = true
                    }
                },
                new DocumentStyle
                {
                    Name = "Heading4",
                    DisplayName = "Heading 4",
                    IsBuiltIn = true,
                    BasedOn = "Normal",
                    SortOrder = 4,
                    ParagraphProperties = new ParagraphProperties { SpaceBefore = 6, SpaceAfter = 2, KeepWithNext = true },
                    RunProperties = new RunProperties { FontSize = 14, IsBold = true, IsItalic = true }
                },
                new DocumentStyle
                {
                    Name = "Heading5",
                    DisplayName = "Heading 5",
                    IsBuiltIn = true,
                    BasedOn = "Normal",
                    SortOrder = 5,
                    ParagraphProperties = new ParagraphProperties { SpaceBefore = 4, SpaceAfter = 2, KeepWithNext = true },
                    RunProperties = new RunProperties { FontSize = 12, IsBold = true }
                },
                new DocumentStyle
                {
                    Name = "Heading6",
                    DisplayName = "Heading 6",
                    IsBuiltIn = true,
                    BasedOn = "Normal",
                    SortOrder = 6,
                    ParagraphProperties = new ParagraphProperties { SpaceBefore = 4, SpaceAfter = 2, KeepWithNext = true },
                    RunProperties = new RunProperties { FontSize = 11, IsBold = true, IsSmallCaps = true }
                },
                new DocumentStyle
                {
                    Name = "Quote",
                    DisplayName = "Quote",
                    IsBuiltIn = true,
                    BasedOn = "Normal",
                    SortOrder = 10,
                    ParagraphProperties = new ParagraphProperties
                    {
                        LeftIndent = 36,
                        RightIndent = 36,
                        SpaceBefore = 6,
                        SpaceAfter = 6,
                        Alignment = TextAlignment.Justify
                    },
                    RunProperties = new RunProperties { IsItalic = true }
                },
                new DocumentStyle
                {
                    Name = "Code",
                    DisplayName = "Code",
                    IsBuiltIn = true,
                    BasedOn = "Normal",
                    SortOrder = 11,
                    ParagraphProperties = new ParagraphProperties
                    {
                        LeftIndent = 18,
                        SpaceBefore = 4,
                        SpaceAfter = 4,
                        LineSpacingRule = Styles.LineSpacingRule.Auto,
                        LineSpacingValue = 1.0
                    },
                    RunProperties = new RunProperties { FontFamily = "Consolas", FontSize = 12 }
                },
                // ── Оглавление ────────────────────────────────────────────
                //
                // Строки оглавления носят свои стили, а не «Обычный». Иначе оглавление
                // ничем не отличается от текста книги, и человек не видит, где оно
                // кончается. Со своими стилями его вид правится один раз на весь
                // документ — как в Word.
                //
                // Крупнее и заметнее верхний уровень, глубокие идут мельче и спокойнее:
                // так столбик названий читается как структура, а не как список.
                new DocumentStyle
                {
                    Name = "TocTitle",
                    DisplayName = "Оглавление — название",
                    IsBuiltIn = true,
                    BasedOn = "Normal",
                    SortOrder = 20,
                    ParagraphProperties = new ParagraphProperties
                    {
                        Alignment = TextAlignment.Center,
                        SpaceBefore = 0,
                        SpaceAfter = 14,
                        KeepWithNext = true
                    },
                    RunProperties = new RunProperties { FontSize = 20, IsBold = true }
                },
                new DocumentStyle
                {
                    Name = "Toc1",
                    DisplayName = "Оглавление 1",
                    IsBuiltIn = true,
                    BasedOn = "Normal",
                    SortOrder = 21,
                    ParagraphProperties = new ParagraphProperties
                    {
                        SpaceBefore = 4,
                        SpaceAfter = 2,
                        FirstLineIndent = 0,
                        LineSpacingRule = Styles.LineSpacingRule.Auto,
                        LineSpacingValue = 1.0
                    },
                    RunProperties = new RunProperties { FontSize = 13, IsBold = true }
                },
                new DocumentStyle
                {
                    Name = "Toc2",
                    DisplayName = "Оглавление 2",
                    IsBuiltIn = true,
                    BasedOn = "Toc1",
                    SortOrder = 22,
                    ParagraphProperties = new ParagraphProperties { SpaceBefore = 0, SpaceAfter = 2 },
                    RunProperties = new RunProperties { FontSize = 12, IsBold = false }
                },
                new DocumentStyle
                {
                    Name = "Toc3",
                    DisplayName = "Оглавление 3",
                    IsBuiltIn = true,
                    BasedOn = "Toc2",
                    SortOrder = 23,
                    RunProperties = new RunProperties { FontSize = 12, IsItalic = true }
                },
                new DocumentStyle
                {
                    Name = "Toc4",
                    DisplayName = "Оглавление 4",
                    IsBuiltIn = true,
                    BasedOn = "Toc3",
                    SortOrder = 24,
                    RunProperties = new RunProperties { FontSize = 11 }
                },
                new DocumentStyle
                {
                    Name = "Toc5",
                    DisplayName = "Оглавление 5",
                    IsBuiltIn = true,
                    BasedOn = "Toc4",
                    SortOrder = 25
                },
                new DocumentStyle
                {
                    Name = "Toc6",
                    DisplayName = "Оглавление 6",
                    IsBuiltIn = true,
                    BasedOn = "Toc5",
                    SortOrder = 26
                },
                new DocumentStyle
                {
                    Name = "Toc7",
                    DisplayName = "Оглавление 7",
                    IsBuiltIn = true,
                    BasedOn = "Toc6",
                    SortOrder = 27
                },
                new DocumentStyle
                {
                    Name = "Toc8",
                    DisplayName = "Оглавление 8",
                    IsBuiltIn = true,
                    BasedOn = "Toc7",
                    SortOrder = 28
                },
                new DocumentStyle
                {
                    Name = "Toc9",
                    DisplayName = "Оглавление 9",
                    IsBuiltIn = true,
                    BasedOn = "Toc8",
                    SortOrder = 29
                },
                new DocumentStyle
                {
                    Name = "NoSpacing",
                    DisplayName = "No Spacing",
                    IsBuiltIn = true,
                    BasedOn = "Normal",
                    SortOrder = 12,
                    ParagraphProperties = new ParagraphProperties
                    {
                        SpaceBefore = 0,
                        SpaceAfter = 0,
                        LineSpacingRule = Styles.LineSpacingRule.Auto,
                        LineSpacingValue = 1.0
                    }
                }
            };
        }
    }
}
