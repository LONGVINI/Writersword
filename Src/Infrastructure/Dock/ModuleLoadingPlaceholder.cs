using Avalonia.Controls;
using Avalonia.Layout;

namespace Writersword.Infrastructure.Dock
{
    /// <summary>
    /// Лёгкий плейсхолдер панели модуля на время отложенного прикрепления вьюхи.
    /// Переключение воркмода/вкладки сначала показывает такие плейсхолдеры —
    /// кадр уходит мгновенно, — а тяжёлые вьюхи модулей прикрепляются по одной
    /// в последующих проходах диспетчера (см. DockFactory.SetContentDeferred).
    /// </summary>
    public sealed class ModuleLoadingPlaceholder : Border
    {
        public ModuleLoadingPlaceholder()
        {
            Child = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 12,
                Children =
                {
                    new ProgressBar
                    {
                        IsIndeterminate = true,
                        Width = 160,
                        HorizontalAlignment = HorizontalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = "Модуль загружается…",
                        Opacity = 0.7,
                        HorizontalAlignment = HorizontalAlignment.Center
                    }
                }
            };
        }
    }
}
