using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Serilog;
using System;
using Writersword.Styles.UserControls;

namespace Writersword.Infrastructure.Behaviours
{
    /// <summary>
    /// Прикреплённое поведение для отображения кастомной подсказки.
    /// Заменяет стандартный ToolTip Avalonia, который перехватывает первый клик.
    /// Использование в XAML: behaviours:TooltipBehavior.Tip="текст"
    /// Опционально:          behaviours:TooltipBehavior.Description="описание"
    ///                       behaviours:TooltipBehavior.HotKey="Ctrl+1"
    ///                       behaviours:TooltipBehavior.PreviewPath="avares://Writersword/Resources/Images/file.gif"
    ///                       behaviours:TooltipBehavior.ShowDelay="1200"
    /// </summary>
    public static class TooltipBehavior
    {
        private static readonly ILogger _logger = Log.ForContext(typeof(TooltipBehavior));

        /// <summary>
        /// Заголовок подсказки. При установке автоматически подписывается на события указателя.
        /// </summary>
        public static readonly AttachedProperty<string?> TipProperty =
            AvaloniaProperty.RegisterAttached<Control, string?>(
                "Tip", typeof(TooltipBehavior));

        /// <summary>
        /// Описание — дополнительный текст под заголовком. Скрывается если не задано.
        /// </summary>
        public static readonly AttachedProperty<string?> DescriptionProperty =
            AvaloniaProperty.RegisterAttached<Control, string?>(
                "Description", typeof(TooltipBehavior));

        /// <summary>
        /// Горячая клавиша — отображается в правом нижнем углу в рамочке. Скрывается если не задана.
        /// </summary>
        public static readonly AttachedProperty<string?> HotKeyProperty =
            AvaloniaProperty.RegisterAttached<Control, string?>(
                "HotKey", typeof(TooltipBehavior));

        /// <summary>
        /// Путь к картинке или гифке в формате avares://Assembly/Path/file.ext
        /// Поддерживает GIF (с анимацией), PNG, WebP, JPG.
        /// Скрывается если не задан.
        /// </summary>
        public static readonly AttachedProperty<string?> PreviewPathProperty =
            AvaloniaProperty.RegisterAttached<Control, string?>(
                "PreviewPath", typeof(TooltipBehavior));

        /// <summary>
        /// Задержка перед появлением подсказки в миллисекундах. По умолчанию 1200.
        /// </summary>
        public static readonly AttachedProperty<int> ShowDelayProperty =
            AvaloniaProperty.RegisterAttached<Control, int>(
                "ShowDelay", typeof(TooltipBehavior), defaultValue: 1200);

        private static readonly AttachedProperty<Popup?> PopupProperty =
            AvaloniaProperty.RegisterAttached<Control, Popup?>(
                "Popup", typeof(TooltipBehavior));

        private static readonly AttachedProperty<DispatcherTimer?> TimerProperty =
            AvaloniaProperty.RegisterAttached<Control, DispatcherTimer?>(
                "Timer", typeof(TooltipBehavior));

        /// <summary>Устанавливает заголовок подсказки для элемента.</summary>
        public static void SetTip(Control element, string? value) =>
            element.SetValue(TipProperty, value);

        /// <summary>Возвращает заголовок подсказки элемента.</summary>
        public static string? GetTip(Control element) =>
            element.GetValue(TipProperty);

        /// <summary>Устанавливает описание подсказки.</summary>
        public static void SetDescription(Control element, string? value) =>
            element.SetValue(DescriptionProperty, value);

        /// <summary>Возвращает описание подсказки.</summary>
        public static string? GetDescription(Control element) =>
            element.GetValue(DescriptionProperty);

        /// <summary>Устанавливает горячую клавишу подсказки.</summary>
        public static void SetHotKey(Control element, string? value) =>
            element.SetValue(HotKeyProperty, value);

        /// <summary>Возвращает горячую клавишу подсказки.</summary>
        public static string? GetHotKey(Control element) =>
            element.GetValue(HotKeyProperty);

        /// <summary>Устанавливает путь к картинке/гифке подсказки.</summary>
        public static void SetPreviewPath(Control element, string? value) =>
            element.SetValue(PreviewPathProperty, value);

        /// <summary>Возвращает путь к картинке/гифке подсказки.</summary>
        public static string? GetPreviewPath(Control element) =>
            element.GetValue(PreviewPathProperty);

        /// <summary>Устанавливает задержку перед показом подсказки в миллисекундах.</summary>
        public static void SetShowDelay(Control element, int value) =>
            element.SetValue(ShowDelayProperty, value);

        /// <summary>Возвращает задержку перед показом подсказки в миллисекундах.</summary>
        public static int GetShowDelay(Control element) =>
            element.GetValue(ShowDelayProperty);

        private static void SetPopup(Control element, Popup? value) =>
            element.SetValue(PopupProperty, value);

        private static Popup? GetPopup(Control element) =>
            element.GetValue(PopupProperty);

        private static void SetTimer(Control element, DispatcherTimer? value) =>
            element.SetValue(TimerProperty, value);

        private static DispatcherTimer? GetTimer(Control element) =>
            element.GetValue(TimerProperty);

        static TooltipBehavior()
        {
            TipProperty.Changed.AddClassHandler<Control>(OnTipChanged);
        }

        /// <summary>
        /// Вызывается при изменении свойства Tip.
        /// Подписывает или отписывает обработчики событий указателя.
        /// </summary>
        private static void OnTipChanged(Control element, AvaloniaPropertyChangedEventArgs e)
        {
            element.PointerEntered -= OnPointerEntered;
            element.PointerExited -= OnPointerExited;
            element.PointerPressed -= OnPointerPressed;

            if (!string.IsNullOrEmpty(e.NewValue as string))
            {
                element.PointerEntered += OnPointerEntered;
                element.PointerExited += OnPointerExited;
                element.PointerPressed += OnPointerPressed;
                _logger.Debug("TooltipBehavior: подписка на {Element}, текст='{Tip}'",
                    element.GetType().Name, e.NewValue);
            }
        }

        /// <summary>
        /// Запускает таймер задержки при наведении указателя на элемент.
        /// </summary>
        private static void OnPointerEntered(object? sender, PointerEventArgs e)
        {
            if (sender is not Control element) return;

            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(GetShowDelay(element))
            };

            timer.Tick += (_, _) =>
            {
                timer.Stop();
                ShowTooltip(element);
            };

            SetTimer(element, timer);
            timer.Start();
            _logger.Debug("TooltipBehavior: таймер запущен для {Element}, задержка={Delay}мс",
                element.GetType().Name, GetShowDelay(element));
        }

        /// <summary>
        /// Скрывает подсказку и останавливает таймер при уходе указателя.
        /// </summary>
        private static void OnPointerExited(object? sender, PointerEventArgs e)
        {
            if (sender is not Control element) return;
            _logger.Debug("TooltipBehavior: указатель ушёл с {Element}, скрываем подсказку",
                element.GetType().Name);
            HideTooltip(element);
        }

        /// <summary>
        /// Скрывает подсказку при нажатии — чтобы клик не блокировался.
        /// </summary>
        private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Control element) return;
            _logger.Debug("TooltipBehavior: клик на {Element}, скрываем подсказку",
                element.GetType().Name);
            HideTooltip(element);
        }

        /// <summary>
        /// Резолвит кисть из дерева ресурсов элемента.
        /// Возвращает fallback если ресурс не найден.
        /// </summary>
        private static IBrush ResolveBrush(Control element, string key, IBrush fallback)
        {
            if (element.TryFindResource(key, out var resource) && resource is IBrush brush)
                return brush;
            _logger.Warning("TooltipBehavior: ресурс '{Key}' не найден, используем fallback", key);
            return fallback;
        }

        /// <summary>
        /// Создаёт и открывает Popup с TooltipView.
        /// Автоматически определяет позицию — выше или ниже элемента.
        /// Учитывает горизонтальное положение — сдвигает если вылезает за край окна.
        /// Стрелка всегда указывает на центр кнопки.
        /// Кисти резолвятся из главного дерева ресурсов и передаются напрямую в TooltipView.
        /// </summary>
        private static void ShowTooltip(Control element)
        {
            string? tip = GetTip(element);
            if (string.IsNullOrEmpty(tip)) return;

            var bgBrush = ResolveBrush(element, "AppTooltipBackground", new SolidColorBrush(Color.Parse("#2D2D30")));
            var borderBrush = ResolveBrush(element, "AppTooltipBorderBrush", new SolidColorBrush(Color.Parse("#3E3E42")));
            var fgBrush = ResolveBrush(element, "AppTooltipForeground", new SolidColorBrush(Color.Parse("#FFFFFF")));

            var view = new TooltipView
            {
                Title = tip,
                Description = GetDescription(element),
                HotKey = GetHotKey(element),
                PreviewPath = GetPreviewPath(element),
                TooltipBackground = bgBrush,
                TooltipBorderBrush = borderBrush,
                TooltipForeground = fgBrush,
            };

            var popup = new Popup
            {
                Child = view,
                IsHitTestVisible = false,
                PlacementTarget = element,
                Placement = PlacementMode.Top,
                VerticalOffset = -4,
                IsLightDismissEnabled = false,
            };

            popup.Opened += (_, _) =>
            {
                var topLevel = TopLevel.GetTopLevel(element);
                if (topLevel is null)
                {
                    _logger.Warning("TooltipBehavior: TopLevel не найден для {Element}",
                        element.GetType().Name);
                    return;
                }

                var pos = element.TranslatePoint(new Point(0, 0), topLevel);
                if (pos is null) return;

                double elementLeft = pos.Value.X;
                double elementCenterX = elementLeft + element.Bounds.Width / 2;
                double spaceAbove = pos.Value.Y;
                double windowWidth = topLevel.Bounds.Width;

                double popupWidth = view.DesiredSize.Width > 0 ? view.DesiredSize.Width : 200;
                double popupHeight = view.DesiredSize.Height > 0 ? view.DesiredSize.Height : 40;

                bool goAbove = spaceAbove >= popupHeight + 8;

                popup.Placement = goAbove ? PlacementMode.Top : PlacementMode.Bottom;
                view.ArrowAtBottom = goAbove;
                popup.VerticalOffset = goAbove ? -4 : 4;

                double idealLeft = elementCenterX - popupWidth / 2;
                double clampedLeft = Math.Max(8, Math.Min(idealLeft, windowWidth - popupWidth - 8));

                popup.HorizontalOffset = clampedLeft - idealLeft;
                view.ArrowHorizontalOffset = elementCenterX - clampedLeft - 6;

                _logger.Debug(
                    "TooltipBehavior: показана подсказка для {Element}, позиция={Position}, текст='{Tip}'",
                    element.GetType().Name, goAbove ? "сверху" : "снизу", tip);
            };

            SetPopup(element, popup);
            popup.Open();
        }

        /// <summary>
        /// Останавливает таймер и закрывает Popup с подсказкой.
        /// </summary>
        private static void HideTooltip(Control element)
        {
            GetTimer(element)?.Stop();
            SetTimer(element, null);

            var popup = GetPopup(element);
            if (popup is not null)
            {
                popup.Close();
                SetPopup(element, null);
                _logger.Debug("TooltipBehavior: Popup закрыт для {Element}",
                    element.GetType().Name);
            }
        }
    }
}