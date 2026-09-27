using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Writersword.Modules.TextEditor.Views.Shared
{
    /// <summary>
    /// Список видов плитками с кнопкой «Настроить виды». Общий для ленты чтения и
    /// вкладки «Вид» редактора: пункты, выбор и окно видов задаёт владелец.
    /// </summary>
    public partial class ThemeTilePicker : UserControl
    {
        /// <summary>Пункты списка — ReadingThemeItem.</summary>
        public static readonly StyledProperty<IEnumerable?> ItemsProperty =
            AvaloniaProperty.Register<ThemeTilePicker, IEnumerable?>(nameof(Items));

        /// <summary>Выбор плитки. Параметр — пункт, по которому нажали.</summary>
        public static readonly StyledProperty<ICommand?> SelectCommandProperty =
            AvaloniaProperty.Register<ThemeTilePicker, ICommand?>(nameof(SelectCommand));

        /// <summary>Открыть окно видов.</summary>
        public static readonly StyledProperty<ICommand?> ConfigureCommandProperty =
            AvaloniaProperty.Register<ThemeTilePicker, ICommand?>(nameof(ConfigureCommand));

        public IEnumerable? Items
        {
            get => GetValue(ItemsProperty);
            set => SetValue(ItemsProperty, value);
        }

        public ICommand? SelectCommand
        {
            get => GetValue(SelectCommandProperty);
            set => SetValue(SelectCommandProperty, value);
        }

        public ICommand? ConfigureCommand
        {
            get => GetValue(ConfigureCommandProperty);
            set => SetValue(ConfigureCommandProperty, value);
        }

        private readonly ItemsControl? _tiles;

        public ThemeTilePicker()
        {
            InitializeComponent();
            _tiles = this.FindControl<ItemsControl>("TilesList");
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == ItemsProperty && _tiles is not null)
                _tiles.ItemsSource = change.GetNewValue<IEnumerable?>();
        }

        /// <summary>
        /// «Настроить виды»: окно видов открывается, список закрывается.
        ///
        /// Закрывается он следующим тактом, а не сейчас: закрытый сразу, он уносит с
        /// собой и нажатую кнопку — она уходит из дерева раньше, чем доходит до своей
        /// команды, и окно не открывалось вовсе. А сам по себе список не закроется:
        /// окно встаёт поверх него, и висящий у ленты список читается как подвисшая
        /// панель.
        /// </summary>
        private void OnConfigureClick(object? sender, RoutedEventArgs e)
        {
            var command = ConfigureCommand;
            if (command is not null && command.CanExecute(null))
                command.Execute(null);

            var source = sender;
            Dispatcher.UIThread.Post(() => CloseHostFlyout(source), DispatcherPriority.Background);
        }

        /// <summary>
        /// Закрывает всплывающий список, в котором стоит кнопка. Через сам флайаут, а
        /// не через окно: тогда кнопка-владелец узнаёт, что список закрыт, и не
        /// остаётся в нажатом виде.
        /// </summary>
        private static void CloseHostFlyout(object? sender)
        {
            if (sender is not Control control) return;
            if (TopLevel.GetTopLevel(control) is not PopupRoot root) return;
            if (root.Parent is not Popup popup) return;

            if (popup.PlacementTarget is Button { Flyout: { } flyout })
            {
                flyout.Hide();
                return;
            }

            popup.Close();
        }
    }
}
