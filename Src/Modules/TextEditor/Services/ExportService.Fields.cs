using System;
using System.Collections.Generic;
using DocumentFormat.OpenXml;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Живые поля в выгрузке. В .docx поле уходит сложным полем Word — begin, код,
    /// separate, значение, end, — и Word пересчитает его сам. Значение в файле — то,
    /// что поле показало бы сейчас: дата на сегодня; номер страницы и число страниц —
    /// последние известные. В текстовые форматы идёт одно значение.
    /// </summary>
    public sealed partial class ExportService
    {
        private static Dictionary<Guid, FieldBlock> BuildInlineFieldMap(DocumentModel document)
        {
            var map = new Dictionary<Guid, FieldBlock>();
            foreach (var section in document.Sections)
                foreach (var block in section.InlineObjects)
                    if (block is FieldBlock field)
                        map[field.Id] = field;
            return map;
        }

        /// <summary>Значение живого поля для выгрузки.</summary>
        private static string LiveFieldValue(FieldBlock field, RunProperties? properties)
        {
            return FieldCodes.KindOf(field.Instruction) switch
            {
                LiveFieldKind.Date or LiveFieldKind.Time
                    => FieldCodes.FormatDateTime(field.Instruction, DateTime.Now, properties?.Language),
                _ => field.Result
            };
        }

        /// <summary>Раны сложного поля Word с оформлением символа-заполнителя.</summary>
        private List<OpenXmlElement> BuildFieldRuns(RunModel run, FieldBlock field, DocxWriteContext ctx)
        {
            var runs = new List<OpenXmlElement>(5);

            W.Run NewRun()
            {
                var wordRun = new W.Run();
                var properties = BuildRunPropertyElements(run.Properties, writeExplicitToggles: true);
                if (properties.Count > 0 || run.Properties?.FormatChange is not null)
                {
                    var rPr = new W.RunProperties();
                    foreach (var element in properties) rPr.AppendChild(element);
                    if (run.Properties?.FormatChange is { } formatChange)
                        AppendRunFormatChange(rPr, formatChange, ctx);
                    wordRun.AppendChild(rPr);
                }
                return wordRun;
            }

            var begin = NewRun();
            begin.AppendChild(new W.FieldChar { FieldCharType = W.FieldCharValues.Begin });
            runs.Add(begin);

            var code = NewRun();
            code.AppendChild(new W.FieldCode(" " + field.Instruction.Trim() + " ")
            {
                Space = new EnumValue<SpaceProcessingModeValues>(SpaceProcessingModeValues.Preserve)
            });
            runs.Add(code);

            var separate = NewRun();
            separate.AppendChild(new W.FieldChar { FieldCharType = W.FieldCharValues.Separate });
            runs.Add(separate);

            string value = LiveFieldValue(field, run.Properties);
            if (value.Length > 0)
            {
                var result = NewRun();
                result.AppendChild(new W.Text(value)
                {
                    Space = new EnumValue<SpaceProcessingModeValues>(SpaceProcessingModeValues.Preserve)
                });
                runs.Add(result);
            }

            var end = NewRun();
            end.AppendChild(new W.FieldChar { FieldCharType = W.FieldCharValues.End });
            runs.Add(end);

            return runs;
        }
    }
}
