using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Writersword.Modules.Characters.Controls;
using Writersword.Modules.Characters.ViewModels;

namespace Writersword.Modules.Characters.Views
{
    /// <summary>
    /// Только чтение в режиме сравнения версий. Пока версии сравниваются,
    /// в модуле можно смотреть и переключаться — вкладки, карточки, листы
    /// анкет, раскрытие разделов, поиск, — но ничего нельзя создать,
    /// поменять, переставить или удалить.
    ///
    /// Запрет стоит на входе, а не в каждом обработчике: щелчок, клавиша,
    /// ввод текста, перетаскивание и контекстное меню перехватываются на
    /// корне модуля раньше, чем дойдут до кнопки или поля. Разрешено только
    /// то, что явно помечено классом «ro-allow», и то, что по своей природе
    /// лишь показывает: вкладки, строки списков, полосы прокрутки, заголовки
    /// раскрывающихся блоков. Новая кнопка, о которой забыли, по умолчанию
    /// запрещена — забытая правка опаснее забытого переключателя.
    ///
    /// Всплывающие окошки (меню, выпадающие панели) лежат не под корнем
    /// модуля, а в своём слое или окне. Для них тот же запрет стоит на
    /// окне верхнего уровня: окошко узнаётся по логическому родителю — тому
    /// элементу модуля, от которого оно открыто.
    ///
    /// Брошенные снаружи файлы корень перехватить не может — у Avalonia эти
    /// события только всплывают. Их обработчики сами спрашивают RejectDrop.
    /// Последний рубеж — службы данных: они тоже отказывают в записи.
    /// </summary>
    public sealed class CharactersReadOnlyGate
    {
        /// <summary>Класс элемента, которым можно пользоваться и в режиме только чтения.</summary>
        public const string AllowClass = "ro-allow";

        private readonly CharactersModuleView _view;

        // Нажатие, которое запрещено: его отпускание и движение тоже гасятся.
        private bool _pressBlocked;

        // Нажатие, после которого движение разрешено: выделение текста,
        // полоса прокрутки, помеченный элемент. Иначе движение с зажатой
        // кнопкой — это начало перетаскивания, а оно всегда что-то двигает.
        private bool _pressMayMove;

        private CharactersReadOnlyGate(CharactersModuleView view)
        {
            _view = view;
        }

        static CharactersReadOnlyGate()
        {
            const RoutingStrategies tunnel = RoutingStrategies.Tunnel;

            InputElement.PointerPressedEvent.AddClassHandler<TopLevel>(
                (_, e) => ForPopup(e)?.OnPointerPressed(null, e), tunnel, handledEventsToo: true);
            InputElement.PointerMovedEvent.AddClassHandler<TopLevel>(
                (_, e) => ForPopup(e)?.OnPointerMoved(null, e), tunnel, handledEventsToo: true);
            InputElement.PointerReleasedEvent.AddClassHandler<TopLevel>(
                (_, e) => ForPopup(e)?.OnPointerReleased(null, e), tunnel, handledEventsToo: true);
            InputElement.PointerWheelChangedEvent.AddClassHandler<TopLevel>(
                (_, e) => ForPopup(e)?.OnPointerWheel(null, e), tunnel, handledEventsToo: true);
            InputElement.KeyDownEvent.AddClassHandler<TopLevel>(
                (_, e) => ForPopup(e)?.OnKeyDown(null, e), tunnel, handledEventsToo: true);
            InputElement.TextInputEvent.AddClassHandler<TopLevel>(
                (_, e) => ForPopup(e)?.OnTextInput(null, e), tunnel, handledEventsToo: true);
            Control.ContextRequestedEvent.AddClassHandler<TopLevel>(
                (_, e) => ForPopup(e)?.OnContextRequested(null, e), tunnel, handledEventsToo: true);
        }

        /// <summary>
        /// Запрет для всплывающего окошка модуля. Событие внутри самого
        /// модуля разбирает его корень — здесь только окошки, открытые от
        /// его элементов: визуально они не под модулем, логически — под ним.
        /// </summary>
        private static CharactersReadOnlyGate? ForPopup(RoutedEventArgs e)
        {
            if (e.Source is not Visual source) return null;
            if (source.FindAncestorOfType<CharactersModuleView>(includeSelf: true) != null) return null;

            var view = (source as ILogical)?.FindLogicalAncestorOfType<CharactersModuleView>();
            return view?.ReadOnlyGate;
        }

        /// <summary>Поставить запрет на корень модуля.</summary>
        public static CharactersReadOnlyGate Install(CharactersModuleView view)
        {
            var gate = new CharactersReadOnlyGate(view);
            const RoutingStrategies tunnel = RoutingStrategies.Tunnel;

            view.AddHandler(InputElement.PointerPressedEvent, gate.OnPointerPressed, tunnel, handledEventsToo: true);
            view.AddHandler(InputElement.PointerMovedEvent, gate.OnPointerMoved, tunnel, handledEventsToo: true);
            view.AddHandler(InputElement.PointerReleasedEvent, gate.OnPointerReleased, tunnel, handledEventsToo: true);
            view.AddHandler(InputElement.PointerWheelChangedEvent, gate.OnPointerWheel, tunnel, handledEventsToo: true);
            view.AddHandler(InputElement.KeyDownEvent, gate.OnKeyDown, tunnel, handledEventsToo: true);
            view.AddHandler(InputElement.TextInputEvent, gate.OnTextInput, tunnel, handledEventsToo: true);
            view.AddHandler(Control.ContextRequestedEvent, gate.OnContextRequested, tunnel, handledEventsToo: true);

            return gate;
        }

        private bool IsActive => _view.DataContext is CharactersViewModel { IsReadOnly: true };

        // ── Для обработчиков ─────────────────────────────────────────────

        /// <summary>Модуль, в котором лежит элемент, сейчас только для чтения.</summary>
        public static bool IsReadOnly(object? element)
        {
            var view = ModuleViewOf(element);
            return view?.DataContext is CharactersViewModel { IsReadOnly: true };
        }

        /// <summary>
        /// Бросок файла в режиме только чтения: курсор показывает «нельзя»,
        /// сам бросок отклоняется с подсказкой. true — бросок отклонён, и
        /// обработчику делать больше нечего.
        /// </summary>
        public static bool RejectDrop(object? sender, DragEventArgs e)
        {
            var view = ModuleViewOf(sender);
            if (view?.DataContext is not CharactersViewModel { IsReadOnly: true }) return false;

            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            if (e.RoutedEvent == DragDrop.DropEvent) view.ShowReadOnlyNotice();
            return true;
        }

        private static CharactersModuleView? ModuleViewOf(object? element)
        {
            if (element is CharactersModuleView self) return self;
            if (element is not Visual visual) return null;

            return visual.FindAncestorOfType<CharactersModuleView>()
                   ?? (visual as ILogical)?.FindLogicalAncestorOfType<CharactersModuleView>();
        }

        // ── Указатель ────────────────────────────────────────────────────

        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            _pressBlocked = false;
            _pressMayMove = true;
            if (!IsActive) return;

            var source = e.Source as Visual;
            var props = e.GetCurrentPoint(_view).Properties;

            if (IsAllowed(source))
            {
                _pressMayMove = true;
                return;
            }

            _pressMayMove = MayMoveFrom(source);

            // Правая кнопка — контекстные меню, а в них почти одни правки.
            if (props.IsRightButtonPressed || FindEditor(source) != null)
            {
                Block(e);
                _pressBlocked = true;
            }
        }

        private void OnPointerMoved(object? sender, PointerEventArgs e)
        {
            if (!IsActive) return;
            if (_pressBlocked) { e.Handled = true; return; }

            var props = e.GetCurrentPoint(_view).Properties;
            var pressed = props.IsLeftButtonPressed || props.IsRightButtonPressed || props.IsMiddleButtonPressed;
            if (pressed && !_pressMayMove) e.Handled = true;
        }

        private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_pressBlocked && IsActive) e.Handled = true;
            _pressBlocked = false;
            _pressMayMove = true;
        }

        private void OnPointerWheel(object? sender, PointerWheelEventArgs e)
        {
            if (!IsActive) return;

            var source = e.Source as Visual;
            if (IsAllowed(source)) return;

            // Колесо над числом, ползунком и списком выбора меняет значение;
            // над остальным — прокручивает, и это остаётся.
            if (FindEditor(source) is NumericUpDown or Slider or ComboBox or ScaleControlBase)
                e.Handled = true;
        }

        private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
        {
            if (!IsActive || IsAllowed(e.Source as Visual)) return;
            Block(e);
        }

        // ── Клавиатура ───────────────────────────────────────────────────

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (!IsActive) return;

            var source = e.Source as Visual;
            if (IsAllowed(source)) return;

            if (IsTextField(source))
            {
                if (!IsTextNavigationKey(e)) Block(e);
                return;
            }

            if (IsEditingKey(e))
            {
                Block(e);
                return;
            }

            // Стрелки над переключателями, ползунком и списком выбора меняют
            // значение; над остальным — двигают выделение и прокрутку.
            if (IsArrow(e.Key) && FindEditor(source) != null)
                Block(e);
        }

        private void OnTextInput(object? sender, TextInputEventArgs e)
        {
            if (!IsActive || IsAllowed(e.Source as Visual)) return;
            Block(e);
        }

        private static bool IsTextNavigationKey(KeyEventArgs e)
        {
            var ctrl = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
            if (ctrl) return e.Key is Key.C or Key.A or Key.Insert or Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End;

            return e.Key is Key.Left or Key.Right or Key.Up or Key.Down
                or Key.Home or Key.End or Key.PageUp or Key.PageDown
                or Key.Tab or Key.Escape
                or Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
                or Key.LeftAlt or Key.RightAlt;
        }

        private static bool IsEditingKey(KeyEventArgs e)
        {
            if (e.Key is Key.Enter or Key.Space or Key.Delete or Key.Back or Key.Insert or Key.F2)
                return true;

            var ctrl = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
            var alt = (e.KeyModifiers & KeyModifiers.Alt) != 0;

            if (ctrl && e.Key is Key.V or Key.X or Key.Z or Key.Y or Key.D or Key.N or Key.R)
                return true;

            // Сдвиг выделенного стрелками с модификатором — перестановка.
            if ((ctrl || alt) && IsArrow(e.Key))
                return true;

            return false;
        }

        private static bool IsArrow(Key key) => key is Key.Left or Key.Right or Key.Up or Key.Down;

        // ── Разбор того, во что попали ───────────────────────────────────

        /// <summary>Элемент или кто-то над ним помечен как разрешённый в режиме только чтения.</summary>
        private bool IsAllowed(Visual? source)
        {
            for (var v = source; v != null && !ReferenceEquals(v, _view); v = v.GetVisualParent())
            {
                if (v is StyledElement { Classes: var classes } && classes.Contains(AllowClass)) return true;

                // То, что по природе только показывает.
                if (v is ScrollBar or GridSplitter or TabItem or TabStrip) return true;
                if (v is ToggleButton { TemplatedParent: Expander }) return true;
            }

            return false;
        }

        /// <summary>
        /// Ближайший элемент, который меняет значение: кнопки всех видов,
        /// переключатели, списки выбора, числа, ползунки, шкалы оценок.
        /// </summary>
        private Control? FindEditor(Visual? source)
        {
            for (var v = source; v != null && !ReferenceEquals(v, _view); v = v.GetVisualParent())
            {
                switch (v)
                {
                    case ScrollBar:
                        return null;
                    case Button:
                    case ComboBox:
                    case NumericUpDown:
                    case Slider:
                    case ScaleControlBase:
                    case MenuItem:
                    case DatePicker:
                    case CalendarDatePicker:
                    case TimePicker:
                    case AutoCompleteBox:
                        return (Control)v;
                }
            }

            return null;
        }

        /// <summary>Нажатие в текстовом поле или на полосе прокрутки — движение после него не перетаскивание.</summary>
        private bool MayMoveFrom(Visual? source)
        {
            for (var v = source; v != null && !ReferenceEquals(v, _view); v = v.GetVisualParent())
            {
                if (v is TextBox or ScrollBar or GridSplitter) return true;
            }

            return false;
        }

        private bool IsTextField(Visual? source)
        {
            for (var v = source; v != null && !ReferenceEquals(v, _view); v = v.GetVisualParent())
            {
                if (v is TextBox) return true;
            }

            return false;
        }

        private void Block(RoutedEventArgs e)
        {
            e.Handled = true;
            _view.ShowReadOnlyNotice();
        }
    }
}
