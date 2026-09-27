using System;
using System.Diagnostics;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Первые листы документа — сразу, не дожидаясь вёрстки всего документа.
    ///
    /// Большой документ (от двухсот абзацев) на холодном кеше верстается порционным
    /// прогревом: сначала строки считаются у всех абзацев, от первого до последнего,
    /// и только потом раскладываются листы. Пока прогрев идёт, на экране прежняя
    /// раскладка — а у только что импортированного или только что открытого
    /// документа прежней нет: лист пуст, пока не посчитан последний абзац книги.
    ///
    /// Первые листы от прогрева не зависят: вёрстка листа определяется только тем,
    /// что стоит выше него, а выше первого листа нет ничего. Поэтому они
    /// раскладываются сразу, частичным проходом от начала документа — тем же, что
    /// перекладывает видимые листы при сдвиге полей на линейке. Строки считаются
    /// только у абзацев этих листов. Когда прогрев закончится, полный проход
    /// заменит частичную раскладку, и первые листы при этом не сдвинутся.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        // Сколько листов раскладывать до прогрева: окно редактора обычно показывает
        // один-два листа, третий — запас на первую прокрутку.
        private const int FirstPagesBeforeWarmup = 3;

        // Когда начался текущий прогрев (Stopwatch). 0 — прогрева нет.
        private long _warmupStartedTs;

        /// <summary>
        /// Раскладывает первые листы, если прежней раскладки у документа нет.
        /// Зовётся в начале прогрева.
        /// </summary>
        private void ShowFirstPagesBeforeWarmup()
        {
            if (!CanRelayoutVisiblePages) return;
            if (DocVm is null || DocVm.Paragraphs.Count == 0) return;

            // Раскладка есть и она этого документа — на время прогрева её и показываем:
            // смена масштаба или стилей тоже уходит в прогрев, и перекладывать первые
            // листы, когда человек смотрит на сотый, незачем.
            if (!LayoutsBelongToOtherDocument()) return;

            long ts = Stopwatch.GetTimestamp();

            if (_styleResolver is null)
                _styleResolver = CreateStyleResolver();

            PushReadingTextOverrides();
            RefreshCollapsedBlocks();

            _partialFromBlock = 0;
            _partialFromPage = 0;
            _partialToPage = FirstPagesBeforeWarmup - 1;
            try
            {
                RebuildPageMode();
            }
            finally
            {
                _partialFromBlock = -1;
            }

            // Каретка стоит номером слайса прежней раскладки — у новой их меньше.
            if (_layouts.Count > 0)
                _caretPara = Clamp(_caretPara, 0, _layouts.Count - 1);

            InvalidateMeasure();
            InvalidateFull();

            double ms = (Stopwatch.GetTimestamp() - ts) * 1000.0 / Stopwatch.Frequency;
            _logger.Information(
                "[LAYOUT] Первые листы показаны до прогрева: листов {Pages}, слайсов {Slices}, за {Ms:F0} мс",
                _pages.Count, _layouts.Count, ms);
        }

        /// <summary>
        /// На экране нет раскладки этого документа: её не было вовсе или она осталась
        /// от прежнего содержимого (импорт заменил все абзацы).
        /// </summary>
        private bool LayoutsBelongToOtherDocument()
        {
            var layouts = _layouts;
            if (layouts.Count == 0) return true;

            foreach (var pl in layouts)
            {
                if (pl.Cell is not null) continue;
                return !DocVm!.Paragraphs.Contains(pl.Vm);
            }

            return true;
        }
    }
}
