using System;
using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using Writersword.Modules.TextEditor.Models.Document;
using W = DocumentFormat.OpenXml.Wordprocessing;
using Dr = DocumentFormat.OpenXml.Drawing;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Диаграммы в .docx: рисунок с графикой c:chart и своя часть диаграммы со ссылкой
    /// на неё. Место рисунка — в строке или плавающее — пишется тем же кодом, что у
    /// картинки (<see cref="BuildObjectDrawing"/>).
    /// </summary>
    public sealed partial class ExportService
    {
        private const string ChartGraphicUri = "http://schemas.openxmlformats.org/drawingml/2006/chart";

        /// <summary>
        /// Рисунок Word с диаграммой. anchorPosition — опора плавающей диаграммы; у
        /// диаграммы в строке — null.
        /// </summary>
        private W.Drawing? BuildChartDrawing(
            ImageBlock image, TableFloatPosition? anchorPosition, bool topAndBottomAnchor, DocxWriteContext ctx)
        {
            if (image.Chart is not { } chart) return null;

            ChartPart chartPart;
            try
            {
                chartPart = ctx.MainPart.AddNewPart<ChartPart>();
                DocxChartWriter.WriteChartPart(chartPart, chart, ctx.ResolveImage);
            }
            catch (Exception)
            {
                ctx.Warnings.Add("Диаграмма не записалась и в .docx не перенесена.");
                return null;
            }

            string relationshipId = ctx.MainPart.GetIdOfPart(chartPart);

            long cx = (long)Math.Round(Math.Max(image.WidthPt, 1) * EmuPerPoint);
            long cy = (long)Math.Round(Math.Max(image.HeightPt, 1) * EmuPerPoint);

            uint drawingId = ctx.NextDrawingId++;
            string name = "Диаграмма " + drawingId.ToString(CultureInfo.InvariantCulture);

            var graphic = new Dr.Graphic(
                new Dr.GraphicData(XmlElement(
                    $"<c:chart xmlns:c=\"{ChartGraphicUri}\" xmlns:r=\"{RelationshipsNamespace}\" r:id=\"{relationshipId}\"/>"))
                {
                    Uri = ChartGraphicUri
                });

            return BuildObjectDrawing(
                image, anchorPosition, cx, cy, drawingId, name, image.AltText, graphic, topAndBottomAnchor);
        }
    }
}
