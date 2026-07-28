using Avalonia.Controls;
using Avalonia.Interactivity;
using Writersword.Modules.Characters.ViewModels.Tabs;

namespace Writersword.Modules.Characters.Views.Card.Tabs
{
    /// <summary>
    /// Code-behind вкладки "Параметры" карточки персонажа.
    /// </summary>
    public partial class CharacterParametersTabView : UserControl
    {
        public CharacterParametersTabView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Клик по кругляшку шкалы выставляет значение. Обработчик, а не
        /// команда с параметром: каст типа вьюмодели внутри шаблона
        /// разрешается в рантайме и роняет вью.
        /// </summary>
        private void OnScaleDotClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not Control c || c.DataContext is not CharacterScaleDotViewModel dot) return;

            // Владелец кругляшка — параметр: у ItemsControl точек DataContext
            // строки списка параметров.
            var owner = FindParameter(c);
            owner?.SetFromDot(dot.Value);
            e.Handled = true;
        }

        private static CharacterParameterItemViewModel? FindParameter(Control start)
        {
            var current = start.Parent;
            while (current is not null)
            {
                if (current.DataContext is CharacterParameterItemViewModel item) return item;
                current = current.Parent;
            }
            return null;
        }
    }
}
