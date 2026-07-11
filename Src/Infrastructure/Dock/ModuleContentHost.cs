using Avalonia.Controls;

namespace Writersword.Infrastructure.Dock
{
    /// <summary>
    /// Стабильный хост содержимого панели модуля.
    /// ContentPresenter-ы Dock 12 не отслеживают смену Document.Content напрямую:
    /// подмена плейсхолдера на готовую вьюху через doc.Content не отображалась —
    /// на экране оставался плейсхолдер, хотя Content уже содержал вьюху модуля.
    /// Хост присваивается в Document.Content один раз, его identity для презентера
    /// не меняется, а подмена содержимого (плейсхолдер → вьюха модуля) выполняется
    /// через ModuleContentHost.Content — штатный реактивный путь ContentControl.
    /// </summary>
    public sealed class ModuleContentHost : ContentControl
    {
    }
}
