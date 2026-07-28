using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System.Collections.Generic;
using System.Linq;
using Writersword.Modules.Characters.Models;
using Writersword.Modules.Characters.ViewModels.Comparison;
using Writersword.Src.Modules.Characters.Resources;

namespace Writersword.Modules.Characters.Views
{
    /// <summary>
    /// Окно сравнения карточек. Открывается для того набора персонажей,
    /// который сейчас показан в списке: фильтр и поиск уже сделали выбор,
    /// заводить второй механизм выделения незачем.
    /// </summary>
    public partial class CharacterComparisonOverlay : UserControl
    {
        private readonly CharacterComparisonViewModel _model = new();

        public CharacterComparisonOverlay()
        {
            InitializeComponent();
            DataContext = _model;
        }

        public void ShowFor(IEnumerable<Character> characters)
        {
            var list = characters?.ToList() ?? new List<Character>();

            _model.Build(list);
            _model.Summary = string.Format(
                CharactersStrings.Compare_Summary, list.Count, _model.Rows.Count);

            IsVisible = true;
        }

        private void OnScrimPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

        private void OnCloseClick(object? sender, RoutedEventArgs e)
        {
            IsVisible = false;
            e.Handled = true;
        }
    }
}
