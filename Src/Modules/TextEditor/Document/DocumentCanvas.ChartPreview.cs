using System;
using Avalonia.Threading;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Предпросмотр стиля или цвета на выделенной диаграмме, как в Word: пока указатель на
    /// образце галереи (лента «Диаграмма», кисть у диаграммы), лист рисует диаграмму с
    /// моделью образца. Документ при этом не меняется и в историю ничего не попадает —
    /// подменяется только картинка на экране; печать и выгрузка берут настоящую модель.
    ///
    /// Снятие предпросмотра откладывается на мгновение: при переходе с образца на соседний
    /// указатель сначала уходит с одного и лишь потом входит в другой, и без задержки
    /// диаграмма мигала бы настоящим видом между ними.
    /// </summary>
    public partial class DocumentCanvas
    {
        private ImageBlock? _chartPreviewBlock;
        private ChartModel? _chartPreviewModel;
        private DispatcherTimer? _chartPreviewClear;

        private void SetChartPreview(ChartModel? preview)
        {
            if (preview is null)
            {
                if (_chartPreviewBlock is null) return;
                _chartPreviewClear ??= CreatePreviewClearTimer();
                _chartPreviewClear.Stop();
                _chartPreviewClear.Start();
                return;
            }

            _chartPreviewClear?.Stop();
            if (_selectedImage is not { IsChart: true } image) return;
            _chartPreviewBlock = image;
            _chartPreviewModel = preview;
            InvalidateFull();
        }

        private DispatcherTimer CreatePreviewClearTimer()
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                ClearChartPreview();
            };
            return timer;
        }

        /// <summary>Сразу возвращает настоящий вид диаграммы — перед применением образца.</summary>
        private void ClearChartPreview()
        {
            _chartPreviewClear?.Stop();
            if (_chartPreviewBlock is null) return;
            _chartPreviewBlock = null;
            _chartPreviewModel = null;
            InvalidateFull();
        }
    }
}
