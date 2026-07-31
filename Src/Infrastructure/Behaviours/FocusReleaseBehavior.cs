using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Writersword.Infrastructure.Behaviours
{
    /// <summary>
    /// Завершение ввода щелчком мимо поля и клавишей Enter — для всего окна.
    ///
    /// По умолчанию поле остаётся сфокусированным, пока фокус не перехватит
    /// другой focusable-элемент: щелчок по пустому месту его не снимает, поле
    /// продолжает выглядеть активным, а привязки с обновлением по потере фокуса
    /// не срабатывают. То же с раскрытым списком выбора.
    ///
    /// Поведение вешается на окно один раз и покрывает все поля внутри, включая
    /// поле ввода внутри редактируемого ComboBox.
    /// </summary>
    public static class FocusReleaseBehavior
    {
        public static readonly AttachedProperty<bool> EnabledProperty =
            AvaloniaProperty.RegisterAttached<Control, bool>(
                "Enabled",
                typeof(FocusReleaseBehavior));

        /// <summary>
        /// Невидимый приёмник фокуса для каждого окна.
        /// Window.Focusable == false, поэтому снять фокус можно только передав
        /// его другому элементу. Приёмник живёт в слое поверх содержимого и
        /// не влияет на разметку окна.
        /// </summary>
        private static readonly AttachedProperty<Panel?> SinkProperty =
            AvaloniaProperty.RegisterAttached<Control, Panel?>(
                "Sink",
                typeof(FocusReleaseBehavior));

        /// <summary>
        /// Снимать фокус со списка после закрытия выпадающего блока.
        /// Выбор сделан, держать подсветку не на чем: без этого список
        /// оставался выделенным до следующего щелчка.
        /// </summary>
        public static readonly AttachedProperty<bool> ReleaseOnSelectProperty =
            AvaloniaProperty.RegisterAttached<Control, bool>(
                "ReleaseOnSelect",
                typeof(FocusReleaseBehavior));

        private static readonly EventHandler<PointerPressedEventArgs> PointerHandler = OnPointerPressed;
        private static readonly EventHandler<KeyEventArgs> KeyHandler = OnKeyDown;

        static FocusReleaseBehavior()
        {
            EnabledProperty.Changed.AddClassHandler<Control>(OnEnabledChanged);
            ReleaseOnSelectProperty.Changed.AddClassHandler<Control>(OnReleaseOnSelectChanged);
        }

        public static bool GetReleaseOnSelect(Control element) => element.GetValue(ReleaseOnSelectProperty);
        public static void SetReleaseOnSelect(Control element, bool value) => element.SetValue(ReleaseOnSelectProperty, value);

        private static void OnReleaseOnSelectChanged(Control control, AvaloniaPropertyChangedEventArgs e)
        {
            if (control is not ComboBox combo) return;

            if (e.NewValue is true)
                combo.DropDownClosed += OnDropDownClosed;
            else
                combo.DropDownClosed -= OnDropDownClosed;
        }

        private static void OnDropDownClosed(object? sender, EventArgs e)
        {
            if (sender is not ComboBox combo) return;
            if (TopLevel.GetTopLevel(combo) is not Window window) return;

            // Перенос на следующий проход диспетчера: закрытие списка ещё идёт,
            // и снятие фокуса прямо здесь Avalonia отменяет собственной
            // расстановкой фокуса после закрытия.
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => GetOrCreateSink(window)?.Focus(),
                Avalonia.Threading.DispatcherPriority.Background);
        }

        public static bool GetEnabled(Control element) => element.GetValue(EnabledProperty);
        public static void SetEnabled(Control element, bool value) => element.SetValue(EnabledProperty, value);

        private static void OnEnabledChanged(Control control, AvaloniaPropertyChangedEventArgs e)
        {
            if (control is not Window window) return;

            if (e.NewValue is true)
            {
                // Щелчок ловится в туннельной фазе — раньше дочерних элементов,
                // клавиша в пузырьковой — позже, чтобы многострочные поля
                // успели обработать Enter как перенос строки.
                window.AddHandler(InputElement.PointerPressedEvent, PointerHandler, RoutingStrategies.Tunnel);
                window.AddHandler(InputElement.KeyDownEvent, KeyHandler, RoutingStrategies.Bubble);
            }
            else
            {
                window.RemoveHandler(InputElement.PointerPressedEvent, PointerHandler);
                window.RemoveHandler(InputElement.KeyDownEvent, KeyHandler);
            }
        }

        private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Window window) return;

            // Фокус удерживает не только поле ввода: закрытый список тоже
            // остаётся подсвеченным после выбора, и щелчок мимо его не снимал.
            if (window.FocusManager?.GetFocusedElement() is not Visual focused)
                return;

            if (focused is not TextBox && focused is not ComboBox)
                return;

            // Щелчок внутри самого элемента фокус не трогает: иначе пропадали бы
            // выделение и позиция каретки при обычном щелчке по слову, а список
            // закрывался бы сразу после открытия.
            Visual? source = e.Source as Visual;
            while (source != null)
            {
                if (ReferenceEquals(source, focused)) return;
                source = source.GetVisualParent();
            }

            ReleaseFocus(window, focused);
        }

        private static void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Handled || e.Key != Key.Enter) return;
            if (sender is not Window window) return;

            if (window.FocusManager?.GetFocusedElement() is not Visual focused)
                return;

            // Многострочные поля обрабатывают Enter сами — там это перенос строки.
            if (focused is TextBox { AcceptsReturn: true })
                return;

            if (focused is not TextBox && focused is not ComboBox)
                return;

            ReleaseFocus(window, focused);
            e.Handled = true;
        }

        /// <summary>
        /// Увести фокус в приёмник и закрыть раскрытый список.
        ///
        /// Список ищется и вверх по дереву: у редактируемого ComboBox фокус
        /// держит его внутреннее поле ввода, и без закрытия набранное значение
        /// остаётся непринятым, а список висит открытым.
        /// </summary>
        private static void ReleaseFocus(Window window, Visual focused)
        {
            var combo = focused as ComboBox ?? focused.FindAncestorOfType<ComboBox>();

            if (combo is { IsDropDownOpen: true })
                combo.IsDropDownOpen = false;

            GetOrCreateSink(window)?.Focus();
        }

        private static Panel? GetOrCreateSink(Window window)
        {
            var existing = window.GetValue(SinkProperty);
            if (existing != null) return existing;

            try
            {
                // Слой поверх содержимого: добавление в него ничего не смещает
                // и не требует знать, как устроена разметка конкретного окна.
                var layer = OverlayLayer.GetOverlayLayer(window);
                if (layer == null) return null;

                var sink = new Panel
                {
                    Width = 0,
                    Height = 0,
                    Focusable = true,
                    IsHitTestVisible = false
                };

                layer.Children.Add(sink);
                window.SetValue(SinkProperty, sink);

                return sink;
            }
            catch
            {
                return null;
            }
        }
    }
}
