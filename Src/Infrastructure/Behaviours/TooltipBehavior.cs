using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Core.Interfaces.Services.Input;
using Writersword.Styles.UserControls;

namespace Writersword.Infrastructure.Behaviours
{
    /// <summary>
    /// Прикреплённое поведение для отображения кастомной подсказки.
    /// Заменяет стандартный ToolTip Avalonia, который перехватывает первый клик.
    /// Использование в XAML: behaviours:TooltipBehavior.Tip="текст"
    /// Опционально:          behaviours:TooltipBehavior.Description="описание"
    ///                       behaviours:TooltipBehavior.HotKey="Ctrl+1 ;; Ctrl+Shift+P"
    ///                       behaviours:TooltipBehavior.HotKeyId="HotKey_Edit_Undo"
    ///                       behaviours:TooltipBehavior.PreviewPath="avares://Writersword/Resources/Images/file.gif"
    ///                       behaviours:TooltipBehavior.ShowDelay="1200"
    /// Формат HotKey:
    ///   "Ctrl+1"                   — один хорд
    ///   "Ctrl+K Ctrl+C"            — последовательность двух хордов (пробел между хордами)
    ///   "Ctrl+1 ;; Ctrl+Shift+P"   — два альтернативных бинда через ;;
    /// HotKeyId имеет приоритет над HotKey если оба заданы.
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
        /// Горячие клавиши в строковом формате.
        /// Пробел разделяет хорды в последовательности, ";;" разделяет альтернативные бинды,
        /// "+" разделяет клавиши внутри одного хорда.
        /// Скрывается если не задано. Игнорируется если задан HotKeyId.
        /// </summary>
        public static readonly AttachedProperty<string?> HotKeyProperty =
            AvaloniaProperty.RegisterAttached<Control, string?>(
                "HotKey", typeof(TooltipBehavior));

        /// <summary>
        /// ID горячей клавиши из IHotKeyService.
        /// Если задан — резолвит актуальный жест автоматически.
        /// Имеет приоритет над HotKey если оба заданы.
        /// </summary>
        public static readonly AttachedProperty<string?> HotKeyIdProperty =
            AvaloniaProperty.RegisterAttached<Control, string?>(
                "HotKeyId", typeof(TooltipBehavior));

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

        /// <summary>Устанавливает горячие клавиши подсказки в строковом формате.</summary>
        public static void SetHotKey(Control element, string? value) =>
            element.SetValue(HotKeyProperty, value);

        /// <summary>Возвращает горячие клавиши подсказки в строковом формате.</summary>
        public static string? GetHotKey(Control element) =>
            element.GetValue(HotKeyProperty);

        /// <summary>Устанавливает ID горячей клавиши для резолва из IHotKeyService.</summary>
        public static void SetHotKeyId(Control element, string? value) =>
            element.SetValue(HotKeyIdProperty, value);

        /// <summary>Возвращает ID горячей клавиши.</summary>
        public static string? GetHotKeyId(Control element) =>
            element.GetValue(HotKeyIdProperty);

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
                _logger.Debug("TooltipBehavior: subscribed to {Element}, text='{Tip}'",
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
            _logger.Debug("TooltipBehavior: timer started for {Element}, delay={Delay}ms",
                element.GetType().Name, GetShowDelay(element));
        }

        /// <summary>
        /// Скрывает подсказку и останавливает таймер при уходе указателя.
        /// </summary>
        private static void OnPointerExited(object? sender, PointerEventArgs e)
        {
            if (sender is not Control element) return;
            _logger.Debug("TooltipBehavior: pointer exited {Element}, hiding tooltip",
                element.GetType().Name);
            HideTooltip(element);
        }

        /// <summary>
        /// Скрывает подсказку при нажатии — чтобы клик не блокировался.
        /// </summary>
        private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Control element) return;
            _logger.Debug("TooltipBehavior: pointer pressed on {Element}, hiding tooltip",
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
            _logger.Warning("TooltipBehavior: resource '{Key}' not found, using fallback", key);
            return fallback;
        }

        /// <summary>
        /// Резолвит строку горячих клавиш из IHotKeyService по ID.
        /// Конвертирует ActiveGestures в формат парсера:
        /// шаги хорда через пробел, альтернативные бинды через ;;
        /// Возвращает null если ID не найден или сервис недоступен.
        /// </summary>
        private static string? ResolveHotKeyString(string? hotKeyId)
        {
            if (string.IsNullOrEmpty(hotKeyId)) return null;

            try
            {
                var service = App.Services.GetService<IHotKeyService>();
                if (service is null)
                {
                    _logger.Warning("TooltipBehavior: IHotKeyService not available");
                    return null;
                }

                var hotKey = service.GetHotKey(hotKeyId);
                if (hotKey is null || hotKey.ActiveGestures.Count == 0)
                {
                    _logger.Debug("TooltipBehavior: hotkey not found or no gestures for id='{Id}'",
                        hotKeyId);
                    return null;
                }

                // Каждый ActiveGesture — один бинд, шаги через пробел
                var bindings = hotKey.ActiveGestures.Select(g =>
                    string.Join(" ", g.Steps.Select(s => s.ToString())));

                return string.Join(" ;; ", bindings);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "TooltipBehavior: failed to resolve hotkey id='{Id}'", hotKeyId);
                return null;
            }
        }

        /// <summary>
        /// Парсит строку горячих клавиш в список строк токенов.
        /// ";;" разделяет альтернативные бинды — каждый на отдельной строке.
        /// Пробел разделяет хорды в последовательности.
        /// "+" разделяет клавиши внутри одного хорда.
        /// Возвращает null если строка пустая.
        /// </summary>
        private static List<List<HotKeyToken>>? ParseHotKey(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                _logger.Debug("TooltipBehavior: ParseHotKey — empty string");
                return null;
            }

            var bindings = raw.Split(
                new[] { ";;" }, StringSplitOptions.RemoveEmptyEntries);

            var result = new List<List<HotKeyToken>>();

            foreach (var binding in bindings)
            {
                var chords = binding.Trim().Split(
                    new[] { " " }, StringSplitOptions.RemoveEmptyEntries);

                var tokens = new List<HotKeyToken>();

                foreach (var chord in chords)
                {
                    // Весь хорд — один токен, например "Ctrl+A"
                    string trimmed = chord.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                        tokens.Add(new HotKeyToken(trimmed, true));
                }

                if (tokens.Count > 0)
                {
                    result.Add(tokens);
                    _logger.Debug("TooltipBehavior: ParseHotKey — binding with {Count} tokens",
                        tokens.Count);
                }
            }

            _logger.Debug("TooltipBehavior: ParseHotKey — total {Count} bindings from '{Raw}'",
                result.Count, raw);

            return result.Count > 0 ? result : null;
        }

        /// <summary>
        /// Создаёт и открывает Popup с TooltipView.
        /// Автоматически определяет позицию — выше или ниже элемента.
        /// Учитывает горизонтальное положение — сдвигает если вылезает за край окна.
        /// Стрелка всегда указывает на центр кнопки.
        /// Кисти резолвятся из главного дерева ресурсов и передаются напрямую в TooltipView.
        /// ParsedHotKeys устанавливается последним — после всех кистей.
        /// HotKeyId имеет приоритет над HotKey для резолва жестов.
        /// </summary>
        private static void ShowTooltip(Control element)
        {
            string? tip = GetTip(element);
            if (string.IsNullOrEmpty(tip)) return;

            var bgBrush = ResolveBrush(element, "AppTooltipBackground", new SolidColorBrush(Color.Parse("#2D2D30")));
            var borderBrush = ResolveBrush(element, "AppTooltipBorderBrush", new SolidColorBrush(Color.Parse("#3E3E42")));
            var fgBrush = ResolveBrush(element, "AppTooltipForeground", new SolidColorBrush(Color.Parse("#FFFFFF")));
            var keyBadgeBrush = ResolveBrush(element, "AppTooltipKeyBackground", new SolidColorBrush(Color.Parse("#3C3C3F")));

            // HotKeyId имеет приоритет над HotKey
            var hotKeyString = ResolveHotKeyString(GetHotKeyId(element)) ?? GetHotKey(element);

            _logger.Debug("TooltipBehavior: creating TooltipView, text='{Tip}'", tip);

            // ParsedHotKeys устанавливается последним — RebuildHotKeys использует кисти
            var view = new TooltipView
            {
                Title = tip,
                Description = GetDescription(element),
                PreviewPath = GetPreviewPath(element),
                TooltipBackground = bgBrush,
                TooltipBorderBrush = borderBrush,
                TooltipForeground = fgBrush,
                KeyBadgeBackground = keyBadgeBrush,
                ParsedHotKeys = ParseHotKey(hotKeyString),
            };

            var popup = new Popup
            {
                Child = view,
                IsHitTestVisible = false,
                PlacementTarget = element,
                Placement = PlacementMode.Top,
                VerticalOffset = -4,
                IsLightDismissEnabled = false
            };

            popup.Opened += (_, _) =>
            {
                if (TopLevel.GetTopLevel(view) is PopupRoot popupRoot)
                {
                    popupRoot.Background = Brushes.Transparent;
                    popupRoot.TransparencyLevelHint = new[]
                    {
                        WindowTransparencyLevel.Transparent
                    };
                }

                var topLevel = TopLevel.GetTopLevel(element);
                if (topLevel is null)
                {
                    _logger.Warning("TooltipBehavior: TopLevel not found for {Element}",
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
                    "TooltipBehavior: tooltip shown for {Element}, position={Position}, text='{Tip}'",
                    element.GetType().Name, goAbove ? "above" : "below", tip);
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
                _logger.Debug("TooltipBehavior: popup closed for {Element}",
                    element.GetType().Name);
            }
        }
    }
}