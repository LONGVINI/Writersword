using System.Collections.Specialized;
using Avalonia.Layout;

namespace Writersword.Modules.Characters.Controls
{
    /// <summary>
    /// UniformGridLayout с обходом бага Avalonia 12: базовый OnItemsChangedCore
    /// вызывает UniformGridLayoutState.ClearElementOnDataSourceChange, который не
    /// реализован и кидает NotImplementedException при изменении коллекции в
    /// виртуализованном состоянии (перетаскивание карточек в прокрученном списке).
    /// Здесь пропускаем сломанный шаг и просто инвалидируем раскладку — позиции
    /// элементов пересчитываются заново.
    /// </summary>
    public class SafeUniformGridLayout : UniformGridLayout
    {
        protected override void OnItemsChangedCore(
            VirtualizingLayoutContext context, object? source, NotifyCollectionChangedEventArgs args)
        {
            InvalidateMeasure();
        }
    }
}
