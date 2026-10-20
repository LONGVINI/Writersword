using Serilog;
using System;
using System.Threading.Tasks;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Views.Dialogs
{
    /// <summary>Итог окна диаграммы.</summary>
    /// <param name="Chart">Новая модель диаграммы; null при удалении.</param>
    /// <param name="Delete">Нажата кнопка удаления диаграммы.</param>
    public sealed record ChartEditResult(ChartModel? Chart, bool Delete);

    /// <summary>
    /// Вход в окно диаграммы. Само окно — оверлей модуля
    /// (<see cref="ChartEditorOverlay"/>): стоит в редакторе под редактором цвета, и
    /// кнопки цвета в нём открывают полный подборщик.
    /// </summary>
    public static class ChartEditorDialog
    {
        private static readonly ILogger _log = Log.ForContext(typeof(ChartEditorDialog));

        /// <summary>
        /// Открывает окно диаграммы в активном редакторе. null — окно закрыто без
        /// изменений (Отмена, Esc) или показать его не на чем. storeImage кладёт файл
        /// картинки в хранилище проекта и даёт его имя, readImage читает картинку
        /// хранилища — для картинки фона диаграммы; без них картинку не выбрать.
        /// </summary>
        public static Task<ChartEditResult?> ShowAsync(ChartModel chart, double widthPt, double heightPt, bool canDelete,
            Func<string, string?>? storeImage = null, Func<string, byte[]?>? readImage = null)
        {
            if (chart is null) return Task.FromResult<ChartEditResult?>(null);

            var host = ChartEditorOverlay.FindHost();
            if (host is null)
            {
                _log.Warning("[CHART] Окно диаграммы не найдено в редакторе: показать правку не на чем");
                return Task.FromResult<ChartEditResult?>(null);
            }

            return host.ShowAsync(chart, widthPt, heightPt, canDelete, storeImage, readImage);
        }
    }
}
