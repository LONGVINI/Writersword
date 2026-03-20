using SkiaSharp;
using Writersword.Core.Models.Print;

namespace Writersword.Core.Interfaces.Print
{
    /// <summary>
    /// Контракт для любого модуля который поддерживает печать.
    /// Реализующий класс отвечает за рендеринг содержимого на SKCanvas
    /// в физических единицах (points, 1 pt = 1/72 дюйма).
    /// Окно PrintPreview и PrintService работают исключительно через этот интерфейс
    /// и не знают ничего о внутреннем устройстве модуля.
    /// </summary>
    public interface IPrintableDocument
    {
        /// <summary>
        /// Заголовок документа — отображается в заголовке окна PrintPreview
        /// и передаётся в метаданные PDF.
        /// </summary>
        string Title { get; }

        /// <summary>
        /// Количество страниц в документе.
        /// Вычисляется до открытия окна превью на основе текущего содержимого.
        /// </summary>
        int PageCount { get; }

        /// <summary>
        /// Физические настройки страницы: размер бумаги, поля, ориентация.
        /// PrintService использует их для задания размеров PDF-страницы.
        /// PrintPreview использует их для отображения пропорций листа.
        /// </summary>
        PrintPageSettings PageSettings { get; }

        /// <summary>
        /// Рендерит одну страницу на переданный SKCanvas.
        /// Координата (0, 0) — левый верхний угол страницы включая поля.
        /// Единица измерения — points (pt), 1 pt = 1/72 дюйма.
        /// Метод вызывается как для Preview (рендер в Bitmap), так и для PDF.
        /// Реализация должна сама соблюдать отступы из PageSettings.
        /// </summary>
        /// <param name="pageIndex">Индекс страницы, начиная с 0.</param>
        /// <param name="canvas">Целевой canvas для рисования.</param>
        /// <param name="pageWidthPt">Полная ширина страницы в pt включая поля.</param>
        /// <param name="pageHeightPt">Полная высота страницы в pt включая поля.</param>
        void RenderPage(int pageIndex, SKCanvas canvas, float pageWidthPt, float pageHeightPt);
    }
}