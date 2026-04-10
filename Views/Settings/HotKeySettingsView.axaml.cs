using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Writersword.ViewModels.Settings;

namespace Writersword.Views.Settings;

public partial class HotKeySettingsView : UserControl
{
    public HotKeySettingsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AddHandler(LostFocusEvent, OnAnyLostFocus, RoutingStrategies.Bubble);
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is HotKeySettingsViewModel vm)
            vm.EditingStarted += OnEditingStarted;
    }

    private void OnEditingStarted()
    {
        Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (DataContext is HotKeySettingsViewModel vm && vm.IsEditingActive)
        {
            vm.HandleKeyDown(e.Key, e.KeyModifiers);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (DataContext is HotKeySettingsViewModel vm && vm.IsEditingActive)
        {
            vm.HandleKeyUp(e.Key, e.KeyModifiers);
            e.Handled = true;
            return;
        }
        base.OnKeyUp(e);
    }

    private void OnAnyLostFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TextBox textBox
            && textBox.Tag is PrefixRowViewModel prefixRow
            && DataContext is HotKeySettingsViewModel vm)
        {
            vm.SavePrefixCommentCommand.Execute(prefixRow);
        }
    }
}