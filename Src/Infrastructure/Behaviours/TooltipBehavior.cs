using Writersword.Core.Services;
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
        /// Задержка перед появлением подсказки по умолчанию, в миллисекундах.
        /// Одно число на всё приложение: подсказка, выскакивающая под курсором
        /// по дороге к кнопке, мешает не меньше, чем отсутствующая. Полсекунды
        /// не хватало — пока ведёшь мышь через панель, подсказки успевают
        /// вспыхнуть у каждого значка по пути.
        ///
        /// Отдельные места вправе назначить свою задержку — но только чтобы
        /// сделать её длиннее (крупная карточка с картинкой, всплывающая над
        /// половиной окна). Числа короче этого в разметке не ставятся: там, где
        /// они стояли, они лишь повторяли прежнее значение по умолчанию и
        /// мешали менять его в одном месте.
        /// </summary>
        public const int DefaultShowDelayMs = 900;

        /// <summary>
        /// Задержка перед появлением подсказки в миллисекундах.
        /// </summary>
        public static readonly AttachedProperty<int> ShowDelayProperty =
            AvaloniaProperty.RegisterAttached<Control, int>(
                "ShowDelay", typeof(TooltipBehavior), defaultValue: DefaultShowDelayMs);

        private static readonly AttachedProperty<Popup?> PopupProperty =
            AvaloniaProperty.RegisterAttached<Control, Popup?>(
                "Popup", typeof(TooltipBehavior));

        private static readonly AttachedProperty<DispatcherTimer?> TimerProperty =
            AvaloniaProperty.RegisterAttached<Control, DispatcherTimer?>(
                "Timer", typeof(TooltipBehavior));

        /// <summary>
        /// Какое место внутри элемента подсказывается сейчас. Пусто — подсказка вызвана
        /// обычным путём, через Tip, либо не показана вовсе.
        ///
        /// Нужно контролам, которые рисуют своё содержимое сами: у линейки внутри и
        /// указатель типа табуляции, и значки позиций, и пустая полоса, а контрол один,
        /// и повесить на него три разных Tip нельзя.
        /// </summary>
        private static readonly AttachedProperty<string?> SpotKeyProperty =
            AvaloniaProperty.RegisterAttached<Control, string?>(
                "SpotKey", typeof(TooltipBehavior));

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

        private static void SetSpotKey(Control element, string? value) =>
            element.SetValue(SpotKeyProperty, value);

        private static string? GetSpotKey(Control element) =>
            element.GetValue(SpotKeyProperty);

        /// <summary>
        /// Показывает подсказку про отдельное место внутри элемента, у заданной точки.
        ///
        /// Обычный путь — Tip в разметке — вешает одну подсказку на весь контрол и всплывает
        /// по его середине. Контролам, рисующим содержимое самостоятельно, этого мало: на
        /// линейке под указателем может оказаться и переключатель типа табуляции, и значок
        /// позиции, и пустая полоса, а контрол при этом один.
        ///
        /// spotKey — опознаватель места. Пока он не меняется, вызов ничего не делает: метод
        /// зовут на каждое движение мыши, и без этого подсказка пересобиралась бы кадр за
        /// кадром и не успевала показаться. Смена ключа закрывает прежнюю подсказку и
        /// заводит задержку заново.
        ///
        /// Горячих клавиш здесь нет: место внутри контрола своей клавиши не имеет, а те,
        /// что относятся ко всему контролу, ставятся обычным путём.
        /// </summary>
        public static void ShowSpot(
            Control element,
            string spotKey,
            double anchorX,
            string? tip,
            string? description = null,
            string? previewPath = null,
            int delayMs = DefaultShowDelayMs)
        {
            if (string.IsNullOrEmpty(spotKey)
                || (string.IsNullOrEmpty(tip) && string.IsNullOrEmpty(description)))
            {
                HideSpot(element);
                return;
            }

            if (GetSpotKey(element) == spotKey) return;

            HideTooltip(element);
            SetSpotKey(element, spotKey);

            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(delayMs <= 0 ? 1 : delayMs)
            };

            timer.Tick += (_, _) =>
            {
                timer.Stop();

                // Указатель мог уйти на другое место, пока шла задержка.
                if (GetSpotKey(element) != spotKey) return;

                OpenPopup(element, tip, description, previewPath, null, anchorX);
            };

            SetTimer(element, timer);
            timer.Start();
        }

        /// <summary>
        /// Убирает подсказку места. Зовётся, когда указатель ушёл с подсказываемой точки,
        /// покинул контрол или начал жест.
        /// </summary>
        public static void HideSpot(Control element)
        {
            if (GetSpotKey(element) is null) return;
            HideTooltip(element);
        }

        static TooltipBehavior()
        {
            TipProperty.Changed.AddClassHandler<Control>(OnHintSourceChanged);
            DescriptionProperty.Changed.AddClassHandler<Control>(OnHintSourceChanged);
        }

        /// <summary>
        /// Вызывается при изменении Tip или Description.
        /// Подписывает или отписывает обработчики событий указателя.
        /// Подсказке достаточно любого из двух текстов: у значка рядом
        /// с подписью раздела заголовок повторял бы саму подпись.
        /// </summary>
        private static void OnHintSourceChanged(Control element, AvaloniaPropertyChangedEventArgs e)
        {
            element.PointerEntered -= OnPointerEntered;
            element.PointerExited -= OnPointerExited;
            element.PointerPressed -= OnPointerPressed;

            bool hasContent = !string.IsNullOrEmpty(GetTip(element))
                              || !string.IsNullOrEmpty(GetDescription(element));

            if (hasContent)
            {
                element.PointerEntered += OnPointerEntered;
                element.PointerExited += OnPointerExited;
                element.PointerPressed += OnPointerPressed;
                _logger.Debug("TooltipBehavior: subscribed to {Element}, tip='{Tip}'",
                    element.GetType().Name, GetTip(element));
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
        /// Нажатие показывает подсказку немедленно, не дожидаясь задержки.
        ///
        /// Раньше нажатие подсказку скрывало. Но по значку с пояснением
        /// щёлкают именно затем, чтобы прочитать текст, а не ждать над ним
        /// с неподвижной мышью. Повторное нажатие закрывает — так значок
        /// работает переключателем.
        /// </summary>
        private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Control element) return;

            var popup = GetPopup(element);

            if (popup is { IsOpen: true })
            {
                _logger.Debug("TooltipBehavior: pointer pressed on {Element}, hiding tooltip",
                    element.GetType().Name);
                HideTooltip(element);
                return;
            }

            // Таймер наведения больше не нужен: показываем сразу.
            GetTimer(element)?.Stop();
            ShowTooltip(element);

            _logger.Debug("TooltipBehavior: pointer pressed on {Element}, showing tooltip",
                element.GetType().Name);
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
                var service = CoreServices.GetService<IHotKeyService>();
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
            string? description = GetDescription(element);
            if (string.IsNullOrEmpty(tip) && string.IsNullOrEmpty(description)) return;

            // HotKeyId имеет приоритет над HotKey
            var hotKeyString = ResolveHotKeyString(GetHotKeyId(element)) ?? GetHotKey(element);

            OpenPopup(element, tip, description, GetPreviewPath(element), hotKeyString, null);
        }

        /// <summary>
        /// Собирает и открывает всплывающее окно подсказки.
        ///
        /// Тексты приходят снаружи, а не читаются из свойств элемента: тем же кодом
        /// показываются и подсказка всего контрола, и подсказка отдельного места внутри
        /// него, у которого своих свойств нет.
        ///
        /// anchorX — точка внутри элемента, над которой встать. Пусто — встать по середине
        /// элемента, как было всегда.
        /// </summary>
        private static void OpenPopup(
            Control element,
            string? tip,
            string? description,
            string? previewPath,
            string? hotKeyString,
            double? anchorX)
        {
            var bgBrush = ResolveBrush(element, "AppTooltipBackground", new SolidColorBrush(Color.Parse("#2D2D30")));
            var borderBrush = ResolveBrush(element, "AppTooltipBorderBrush", new SolidColorBrush(Color.Parse("#3E3E42")));
            var fgBrush = ResolveBrush(element, "AppTooltipForeground", new SolidColorBrush(Color.Parse("#FFFFFF")));
            var keyBadgeBrush = ResolveBrush(element, "AppTooltipKeyBackground", new SolidColorBrush(Color.Parse("#3C3C3F")));

            _logger.Debug("TooltipBehavior: creating TooltipView, tip='{Tip}', description='{Description}'",
                tip, description);

            // ParsedHotKeys устанавливается последним — RebuildHotKeys использует кисти
            var view = new TooltipView
            {
                Title = tip,
                Description = description,
                PreviewPath = previewPath,
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

                // Точка, над которой встать: середина элемента, а для подсказки про место
                // внутри него — само это место.
                double anchorCenterX = anchorX is double ax ? elementLeft + ax : elementCenterX;

                double spaceAbove = pos.Value.Y;
                double windowWidth = topLevel.Bounds.Width;

                double popupWidth = view.DesiredSize.Width > 0 ? view.DesiredSize.Width : 200;
                double popupHeight = view.DesiredSize.Height > 0 ? view.DesiredSize.Height : 40;

                bool goAbove = spaceAbove >= popupHeight + 8;

                popup.Placement = goAbove ? PlacementMode.Top : PlacementMode.Bottom;
                view.ArrowAtBottom = goAbove;
                popup.VerticalOffset = goAbove ? -4 : 4;

                // Avalonia сама ставит окно по середине элемента, и HorizontalOffset —
                // сдвиг именно от этого места. Считать сдвиг от желаемого положения нельзя:
                // пока желаемое совпадало с серединой элемента, ошибка не проявлялась, но
                // с якорем внутри широкого контрола окно уезжало ровно на расстояние между
                // серединой и якорем.
                double baseLeft = elementCenterX - popupWidth / 2;

                double idealLeft = anchorCenterX - popupWidth / 2;
                double clampedLeft = Math.Max(8, Math.Min(idealLeft, windowWidth - popupWidth - 8));

                popup.HorizontalOffset = clampedLeft - baseLeft;
                view.ArrowHorizontalOffset = anchorCenterX - clampedLeft - 6;

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
            SetSpotKey(element, null);

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