using Avalonia.Controls;
using Avalonia.Interactivity;
using System;
using System.Reactive.Linq;
using Writersword.Modules.Characters.Models;
using Writersword.Modules.Characters.ViewModels.Tabs;

namespace Writersword.Modules.Characters.Views.Card.Tabs
{
    /// <summary>
    /// Code-behind вкладки "Заметки" карточки персонажа.
    /// Команды списка вызываются отсюда: каст типа вьюмодели внутри шаблона
    /// разрешается в рантайме и роняет вью на первом же элементе списка.
    /// </summary>
    public partial class CharacterNotesTabView : UserControl
    {
        public CharacterNotesTabView()
        {
            InitializeComponent();
        }

        private void OnNoteSelectClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not Control c || c.DataContext is not CharacterNote note) return;
            if (DataContext is not CharacterNotesTabViewModel vm) return;

            vm.SelectNoteCommand.Execute(note).Subscribe();
            e.Handled = true;
        }

        private void OnNoteRemoveClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not Control c || c.DataContext is not CharacterNote note) return;
            if (DataContext is not CharacterNotesTabViewModel vm) return;

            vm.RemoveNoteCommand.Execute(note.Id).Subscribe();
            e.Handled = true;
        }
    }
}
