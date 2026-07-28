using Avalonia.Controls;
using Avalonia.Interactivity;
using System;
using System.Reactive.Linq;
using Writersword.Modules.Characters.Models;
using Writersword.Modules.Characters.ViewModels.Tabs;

namespace Writersword.Modules.Characters.Views.Card.Tabs
{
    /// <summary>
    /// Code-behind вкладки "Персональный таймлайн" карточки персонажа.
    /// Команды списка вызываются отсюда: каст типа вьюмодели внутри шаблона
    /// разрешается в рантайме и роняет вью на первом же элементе списка.
    /// </summary>
    public partial class CharacterPersonalTimelineTabView : UserControl
    {
        public CharacterPersonalTimelineTabView()
        {
            InitializeComponent();
        }

        private void OnToggleKeyEventClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not Control c || c.DataContext is not CharacterPersonalEvent ev) return;
            if (DataContext is not CharacterPersonalTimelineTabViewModel vm) return;

            vm.ToggleKeyEventCommand.Execute(ev.Id).Subscribe();
            e.Handled = true;
        }

        private void OnEventRemoveClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not Control c || c.DataContext is not CharacterPersonalEvent ev) return;
            if (DataContext is not CharacterPersonalTimelineTabViewModel vm) return;

            vm.RemoveEventCommand.Execute(ev.Id).Subscribe();
            e.Handled = true;
        }
    }
}
