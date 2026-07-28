using Avalonia.Controls;
using Avalonia.Interactivity;
using System;
using System.Reactive.Linq;
using Writersword.Modules.Characters.Models;
using Writersword.Modules.Characters.ViewModels.Tabs;

namespace Writersword.Modules.Characters.Views.Card.Tabs
{
    /// <summary>
    /// Code-behind вкладки "Контексты" карточки персонажа.
    /// Команды списка вызываются отсюда: каст типа вьюмодели внутри шаблона
    /// разрешается в рантайме и роняет вью на первом же элементе списка.
    /// </summary>
    public partial class CharacterContextsTabView : UserControl
    {
        public CharacterContextsTabView()
        {
            InitializeComponent();
        }

        private void OnContextSelectClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not Control c || c.DataContext is not CharacterContext ctx) return;
            if (DataContext is not CharacterContextsTabViewModel vm) return;

            vm.SelectContextCommand.Execute(ctx).Subscribe();
            e.Handled = true;
        }

        private void OnContextRemoveClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not Control c || c.DataContext is not CharacterContext ctx) return;
            if (DataContext is not CharacterContextsTabViewModel vm) return;

            vm.RemoveContextCommand.Execute(ctx.Id).Subscribe();
            e.Handled = true;
        }
    }
}
