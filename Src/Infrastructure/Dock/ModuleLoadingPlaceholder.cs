using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Writersword.Infrastructure.Dock
{
    /// <summary>
    /// Лёгкий плейсхолдер панели модуля на время отложенного прикрепления вьюхи.
    /// Переключение воркмода/вкладки сначала показывает такие плейсхолдеры —
    /// кадр уходит мгновенно, — а тяжёлые вьюхи модулей прикрепляются по одной
    /// в последующих проходах диспетчера (см. DockFactory.SetContentDeferred).
    /// <para>
    /// Плейсхолдер является триггером загрузки по видимости: LoadRequested
    /// вызывается когда плейсхолдер реально появился на экране (прикрепился
    /// к визуальному дереву). Модули на невидимых вкладках дока не загружаются
    /// вообще — сколько бы модулей ни было в воркмоде, гидрируются только
    /// видимые; остальные стартуют в момент первого показа их панели.
    /// </para>
    /// </summary>
    public sealed class ModuleLoadingPlaceholder : Border
    {
        private bool _loadRequested;

        /// <summary>
        /// Колбэк запуска загрузки модуля. Вызывается один раз, отложенным
        /// постом после первого появления плейсхолдера на экране.
        /// </summary>
        public Action? LoadRequested { get; set; }

        /// <summary>
        /// true — плейсхолдер уже показывался и загрузка была запущена.
        /// Используется вотчдогом: плейсхолдер невидимой вкладки не считается
        /// зависшим — его загрузка намеренно не стартовала.
        /// </summary>
        public bool LoadStarted => _loadRequested;

        public ModuleLoadingPlaceholder()
        {
            // Без IsIndeterminate-анимации: бесконечная анимация прогресс-бара
            // заставляет композитор рендерить кадры непрерывно, пока плейсхолдер
            // на экране — диспетчер никогда не простаивает, и задания с низким
            // приоритетом (Background) голодают бессрочно. Несколько плейсхолдеров
            // одновременно дополнительно жгли CPU на пустую анимацию.
            Child = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 12,
                Children =
                {
                    new ProgressBar
                    {
                        IsIndeterminate = false,
                        Minimum = 0,
                        Maximum = 100,
                        Value = 50,
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

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);

            if (_loadRequested || LoadRequested is null)
                return;

            _loadRequested = true;

            // Пост, а не прямой вызов: кадр с плейсхолдером сначала уходит на экран,
            // и только затем стартует загрузка. Приоритет Loaded — после текущего
            // layout-прохода, но без риска бессрочного голодания Background-очереди.
            var callback = LoadRequested;
            Dispatcher.UIThread.Post(() => callback(), DispatcherPriority.Loaded);
        }
    }
}
