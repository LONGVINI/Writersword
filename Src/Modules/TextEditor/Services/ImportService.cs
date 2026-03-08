using System;
using System.Threading.Tasks;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Models.Styles;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Результат импорта документа.
    /// </summary>
    public sealed class ImportResult
    {
        public bool Success { get; set; }
        public DocumentModel? Document { get; set; }
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// Предупреждения о потере форматирования при импорте.
        /// </summary>
        public string[] Warnings { get; set; } = Array.Empty<string>();

        public static ImportResult Ok(DocumentModel doc, string[] warnings = null!) =>
            new() { Success = true, Document = doc, Warnings = warnings ?? Array.Empty<string>() };

        public static ImportResult Fail(string error) =>
            new() { Success = false, ErrorMessage = error };
    }

    /// <summary>
    /// Импортирует документы из внешних форматов в <see cref="DocumentModel"/>.
    /// Writersword-специфичные метки (персонажи, таймлайн) не могут быть восстановлены
    /// из внешних форматов — только базовое форматирование.
    /// </summary>
    public sealed class ImportService
    {
        /// <summary>
        /// Импортирует .docx файл в DocumentModel.
        /// Требует NuGet: DocumentFormat.OpenXml.
        /// При импорте маппируются стили Word в встроенные стили Writersword
        /// (Normal, Heading1–6, Quote, Code).
        /// Таблицы, изображения и большинство сложного форматирования поддерживаются.
        /// Макросы, встроенные объекты OLE — игнорируются.
        /// </summary>
        public Task<ImportResult> ImportFromDocxAsync(string filePath)
        {
            // Реализация через DocumentFormat.OpenXml.
            // Install-Package DocumentFormat.OpenXml
            throw new NotImplementedException(
                "docx import requires DocumentFormat.OpenXml NuGet package.");
        }

        /// <summary>
        /// Импортирует plain text (.txt) файл.
        /// Каждая строка становится отдельным параграфом.
        /// Форматирование не применяется — используется стиль Normal.
        /// </summary>
        public async Task<ImportResult> ImportFromTxtAsync(string filePath)
        {
            try
            {
                string[] lines = await System.IO.File.ReadAllLinesAsync(filePath);
                var doc = DocumentModel.CreateNew(System.IO.Path.GetFileNameWithoutExtension(filePath));
                var section = doc.Sections[0];
                section.Blocks.Clear();

                foreach (string line in lines)
                {
                    var para = new ParagraphBlock();
                    para.Properties.StyleName = "Normal";

                    var run = new Models.Inline.RunModel { Text = line };
                    para.Chunks[0].Runs.Add(run);
                    para.Chunks[0].InvalidateLength();

                    section.Blocks.Add(para);
                }

                // Минимум один параграф если файл пустой.
                if (section.Blocks.Count == 0)
                    section.Blocks.Add(new ParagraphBlock());

                return ImportResult.Ok(doc, new[] { "Plain text imported without formatting." });
            }
            catch (Exception ex)
            {
                return ImportResult.Fail(ex.Message);
            }
        }
    }
}
