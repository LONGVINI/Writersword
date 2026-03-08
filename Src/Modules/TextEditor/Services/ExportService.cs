using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Результат операции экспорта.
    /// </summary>
    public sealed class ExportResult
    {
        public bool Success { get; set; }

        /// <summary>Сообщение об ошибке. Null при успехе.</summary>
        public string? ErrorMessage { get; set; }

        /// <summary>Путь к сохранённому файлу.</summary>
        public string? OutputPath { get; set; }

        public static ExportResult Ok(string path) => new() { Success = true, OutputPath = path };
        public static ExportResult Fail(string error) => new() { Success = false, ErrorMessage = error };
    }

    /// <summary>
    /// Экспортирует документ в различные форматы.
    /// PDF и docx требуют дополнительных NuGet-пакетов (указаны в комментариях к методам).
    /// При экспорте настройки <see cref="Models.Page.CanvasSettings"/> игнорируются —
    /// используются только физические свойства страницы.
    /// </summary>
    public sealed class ExportService
    {
        /// <summary>
        /// Экспортирует документ в .txt (plain text, без форматирования).
        /// </summary>
        public async Task<ExportResult> ExportToTxtAsync(DocumentModel document, string outputPath)
        {
            try
            {
                using var writer = new StreamWriter(outputPath, false, Encoding.UTF8);

                foreach (var section in document.Sections)
                {
                    foreach (var block in section.Blocks)
                    {
                        if (block is ParagraphBlock paragraph)
                        {
                            await writer.WriteLineAsync(paragraph.GetPlainText());
                        }
                        else if (block is BreakBlock breakBlock)
                        {
                            if (breakBlock.BreakType == BreakType.Page)
                                await writer.WriteLineAsync("\f"); // form feed
                        }
                    }
                }

                return ExportResult.Ok(outputPath);
            }
            catch (Exception ex)
            {
                return ExportResult.Fail(ex.Message);
            }
        }

        /// <summary>
        /// Экспортирует документ в Markdown (.md).
        /// Заголовки маппируются по стилю абзаца (Heading1 → #, Heading2 → ## и т.д.).
        /// Форматирование символов: жирный → **text**, курсив → *text*.
        /// Writersword-специфичные метки (персонажи, таймлайн) теряются.
        /// </summary>
        public async Task<ExportResult> ExportToMarkdownAsync(DocumentModel document, string outputPath)
        {
            try
            {
                var sb = new StringBuilder();

                foreach (var section in document.Sections)
                {
                    foreach (var block in section.Blocks)
                    {
                        if (block is ParagraphBlock paragraph)
                        {
                            string mdLine = ConvertParagraphToMarkdown(paragraph);
                            sb.AppendLine(mdLine);
                        }
                        else if (block is BreakBlock b && b.BreakType == BreakType.Page)
                        {
                            sb.AppendLine();
                            sb.AppendLine("---");
                            sb.AppendLine();
                        }
                    }
                }

                await File.WriteAllTextAsync(outputPath, sb.ToString(), Encoding.UTF8);
                return ExportResult.Ok(outputPath);
            }
            catch (Exception ex)
            {
                return ExportResult.Fail(ex.Message);
            }
        }

        /// <summary>
        /// Экспортирует документ в .docx через DocumentFormat.OpenXml.
        /// Требует NuGet: DocumentFormat.OpenXml.
        /// Writersword-специфичные метки (персонажи, таймлайн, ключевые слова) теряются.
        /// CanvasSettings (цвет листа) игнорируется.
        /// </summary>
        public Task<ExportResult> ExportToDocxAsync(DocumentModel document, string outputPath)
        {
            // Реализация через DocumentFormat.OpenXml будет подключена отдельно.
            // Пакет: Install-Package DocumentFormat.OpenXml
            throw new NotImplementedException(
                "docx export requires DocumentFormat.OpenXml NuGet package. " +
                "Install it and implement OpenXmlDocxWriter.");
        }

        /// <summary>
        /// Экспортирует документ в .pdf.
        /// Требует NuGet: PdfSharpCore или SkiaSharp-based renderer.
        /// </summary>
        public Task<ExportResult> ExportToPdfAsync(DocumentModel document, string outputPath)
        {
            throw new NotImplementedException(
                "PDF export requires a PDF rendering library. " +
                "Recommended: PdfSharpCore or QuestPDF.");
        }

        // --- Вспомогательные методы ---

        private static string ConvertParagraphToMarkdown(ParagraphBlock paragraph)
        {
            string? styleName = paragraph.Properties.StyleName;
            string plainText = paragraph.GetPlainText();

            string prefix = styleName switch
            {
                "Heading1" => "# ",
                "Heading2" => "## ",
                "Heading3" => "### ",
                "Heading4" => "#### ",
                "Heading5" => "##### ",
                "Heading6" => "###### ",
                "Quote" => "> ",
                "Code" => "    ",
                _ => string.Empty
            };

            if (string.IsNullOrEmpty(prefix))
            {
                // Применяем inline-форматирование для обычных абзацев.
                return ConvertRunsToMarkdown(paragraph);
            }

            return prefix + plainText;
        }

        private static string ConvertRunsToMarkdown(ParagraphBlock paragraph)
        {
            var sb = new StringBuilder();

            foreach (var chunk in paragraph.Chunks)
            {
                foreach (var run in chunk.Runs)
                {
                    string text = run.Text;
                    if (run.Properties is null) { sb.Append(text); continue; }

                    bool bold = run.Properties.IsBold;
                    bool italic = run.Properties.IsItalic;
                    bool code = run.Properties.FontFamily == "Consolas"
                        || run.Properties.FontFamily == "Courier New";

                    if (code) { sb.Append('`').Append(text).Append('`'); continue; }
                    if (bold && italic) { sb.Append("***").Append(text).Append("***"); continue; }
                    if (bold) { sb.Append("**").Append(text).Append("**"); continue; }
                    if (italic) { sb.Append('*').Append(text).Append('*'); continue; }

                    sb.Append(text);
                }
            }

            return sb.ToString();
        }
    }
}
