using Avalonia.Controls;

namespace Writersword.Infrastructure.Dock
{
    /// <summary>
    /// Постоянный хост-контейнер панели модуля. Dock всегда держит в Document.Content
    /// один и тот же экземпляр хоста, а подмена содержимого (плейсхолдер загрузки →
    /// вьюха модуля) происходит внутри — обычным ContentControl-ом, который
    /// гарантированно обновляет визуальное дерево. Это убирает зависимость от
    /// капризной реакции Dock 12 на замену Document.Content, из-за которой вьюха
    /// могла остаться без родителя (ввод идёт, а рисовать некому).
    /// </summary>
    public sealed class ModulePaneHost : ContentControl
    {
    }
}
