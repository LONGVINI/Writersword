namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Табуляция со стороны полотна.
    ///
    /// Позиции табуляции самого абзаца полотну знать не надо: они лежат в его свойствах,
    /// и правка через <c>ApplyParaProperty</c> уже помечает нужные абзацы к пересчёту.
    /// А вот шаг табуляции по умолчанию — свойство всего документа: он попадает в
    /// резолвер стилей при сборке, и пока резолвер не пересоздан, новый шаг не действует
    /// ни в одном абзаце. Поэтому его смена и требует отдельного пути, полной пересборки.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        /// <summary>
        /// Отдаёт модели документа способ пересобрать раскладку целиком.
        /// Зовётся оттуда же, откуда все остальные делегаты полотна.
        /// </summary>
        private void WireTabDelegates()
        {
            if (DocVm is null) return;
            DocVm.RelayoutAllDelegate = RelayoutEverything;
        }

        /// <summary>
        /// Сбрасывает всё, что помнит прежние настройки вёрстки, и верстает заново.
        /// Тот же порядок, что при смене переноса по дефису: сначала новый резолвер,
        /// затем кэши абзацев и ячеек, и только потом пересборка — иначе пересборка
        /// успела бы взять из кэша строки, посчитанные по старому шагу.
        /// </summary>
        private void RelayoutEverything()
        {
            if (DocVm is null) return;

            _styleResolver = CreateStyleResolver();
            _layoutCache.Clear();
            InvalidateCellLayoutCaches();
            RebuildLayouts();
            InvalidateFull();
        }

        /// <summary>
        /// У абзаца под кареткой есть свои позиции табуляции.
        ///
        /// По этому и решается, что делает Tab в самом начале абзаца: править отступ или
        /// прыгать к первой отметке. Читается прямо из свойств абзаца, а не из раскладки:
        /// раскладка хранит результат — ширину прыжка на конкретной строке, — а вопрос
        /// здесь в том, заданы ли отметки вообще.
        /// </summary>
        private bool CaretParagraphHasTabStops()
        {
            if (_caretPara < 0 || _caretPara >= _layouts.Count) return false;

            var props = _layouts[_caretPara].Vm.Model?.Properties;
            return props?.TabStops is { Count: > 0 };
        }
    }
}
