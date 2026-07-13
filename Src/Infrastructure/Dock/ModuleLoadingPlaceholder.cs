using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Writersword.Resources.Localization;

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
    /// <para>
    /// Полоса прогресса анимируется таймером с редкими тиками, а не
    /// IsIndeterminate: встроенная бесконечная анимация заставляла композитор
    /// рендерить кадры непрерывно, и низкоприоритетные задания диспетчера
    /// голодали, пока плейсхолдер был на экране.
    /// Текст зависит от состояния ворот гидрации: пока вкладку перетаскивают
    /// (ворота удержаны) — «отпустите, чтобы открыть», после — «модуль загружается».
    /// </para>
    /// </summary>
    public sealed class ModuleLoadingPlaceholder : Border
    {
        private bool _loadRequested;
        private readonly ProgressBar _bar;
        private readonly TextBlock _label;
        private DispatcherTimer? _animationTimer;

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
            _bar = new ProgressBar
            {
                IsIndeterminate = false,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Width = 160,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            _label = new TextBlock
            {
                Text = Strings.Module_Loading,
                Opacity = 0.7,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            Child = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 12,
                Children = { _bar, _label }
            };
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);

            UpdateVisualState();
            StartAnimation();

            if (_loadRequested || LoadRequested is null)
                return;

            _loadRequested = true;

            // Пост, а не прямой вызов: кадр с плейсхолдером сначала уходит на экран,
            // и только затем стартует загрузка. Приоритет Loaded — после текущего
            // layout-прохода, но без риска бессрочного голодания Background-очереди.
            // Запуск идёт через ворота гидрации: пока вкладку перетаскивают,
            // загрузка откладывается и стартует после отпускания кнопки мыши.
            var callback = LoadRequested;
            Dispatcher.UIThread.Post(
                () => ModuleHydrationGate.EnqueueOrRun(callback),
                DispatcherPriority.Loaded);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            StopAnimation();
        }

        private void StartAnimation()
        {
            if (_animationTimer == null)
            {
                // Редкие тики (10 в секунду) дают видимое движение полосы,
                // но оставляют диспетчеру простой между кадрами — в отличие
                // от IsIndeterminate, который рендерит непрерывно.
                _animationTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(100)
                };
                _animationTimer.Tick += (_, _) =>
                {
                    _bar.Value = _bar.Value >= 100 ? 0 : _bar.Value + 5;
                    UpdateVisualState();
                };
            }
            _animationTimer.Start();
        }

        private void StopAnimation()
        {
            _animationTimer?.Stop();
        }

        /// <summary>
        /// Обновляет текст по состоянию ворот гидрации: во время перетаскивания
        /// вкладки загрузка намеренно не идёт — плейсхолдер сообщает об ожидании
        /// отпускания, а не о загрузке.
        /// </summary>
        private void UpdateVisualState()
        {
            var text = ModuleHydrationGate.IsHeld
                ? Strings.TabBar_ReleaseToOpen
                : Strings.Module_Loading;

            if (!string.Equals(_label.Text, text, StringComparison.Ordinal))
                _label.Text = text;
        }
    }
}
