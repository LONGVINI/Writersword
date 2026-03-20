using System.Threading.Tasks;
using Avalonia.Controls;
using Writersword.Core.Interfaces.Print;

namespace Writersword.Core.Interfaces.Services.UI
{
    /// <summary>
    /// Сервис печати уровня приложения.
    /// Регистрируется в DI-контейнере один раз — доступен всем модулям.
    /// Модуль получает сервис через App.Services.GetRequiredService<IPrintService>()
    /// в своём классе Module (не в ViewModel).
    /// </summary>
    public interface IPrintService
    {
        /// <summary>
        /// Открывает окно предпросмотра печати для переданного документа.
        /// Окно модальное — ожидает закрытия пользователем.
        /// Из окна доступны: постраничная навигация, масштабирование,
        /// кнопка Print (→ системный диалог принтера), кнопка Save as PDF.
        /// </summary>
        /// <param name="document">
        /// Документ реализующий IPrintableDocument.
        /// Предоставляется модулем — TextEditor, или любым другим в будущем.
        /// </param>
        /// <param name="owner">
        /// Родительское окно — нужно для корректного позиционирования
        /// модального окна превью и блокировки родителя на время просмотра.
        /// </param>
        Task ShowPrintPreviewAsync(IPrintableDocument document, Window owner);

        /// <summary>
        /// Рендерит документ в PDF-файл по указанному пути без открытия окна превью.
        /// Используется кнопкой Save as PDF внутри PrintPreviewViewModel,
        /// а также может вызываться напрямую из модуля если превью не нужно.
        /// </summary>
        /// <param name="document">Документ для рендеринга.</param>
        /// <param name="outputPath">Полный путь к выходному .pdf файлу.</param>
        Task SavePdfAsync(IPrintableDocument document, string outputPath);

        /// <summary>
        /// Рендерит документ во временный PDF и передаёт его операционной системе
        /// для вывода на принтер через системный диалог.
        /// Windows: verb=print через Process.Start.
        /// macOS: open -a Preview.
        /// Linux: xdg-open.
        /// Временный файл удаляется после закрытия процесса ОС.
        /// </summary>
        /// <param name="document">Документ для печати.</param>
        Task PrintAsync(IPrintableDocument document);
    }
}