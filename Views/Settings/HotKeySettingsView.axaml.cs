using Avalonia.Controls;
using Avalonia.Input;
using System;
using Writersword.ViewModels.Settings;

namespace Writersword.Views.Settings
{
    /// <summary>
    /// Code-behind дл€ View настроек гор€чих клавиш.
    /// ѕерехватывает KeyDown и KeyUp дл€ live display при вводе жестов.
    /// ”правл€ет фокусом Ч захватывает его когда биндинг переходит в режим редактировани€.
    /// </summary>
    public partial class HotKeySettingsView : UserControl
    {
        private HotKeySettingsViewModel? _subscribedVm;

        public HotKeySettingsView()
        {
            InitializeComponent();
            KeyDown += OnKeyDown;
            KeyUp += OnKeyUp;
            DataContextChanged += OnDataContextChanged;
        }

        /// <summary>
        /// ѕри смене DataContext отписываемс€ от старого VM и подписываемс€ на новый
        /// </summary>
        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            if (_subscribedVm != null)
                _subscribedVm.EditingStarted -= OnEditingStarted;

            _subscribedVm = DataContext as HotKeySettingsViewModel;

            if (_subscribedVm != null)
                _subscribedVm.EditingStarted += OnEditingStarted;
        }

        /// <summary>
        ///  огда VM переводит биндинг в режим редактировани€ Ч захватываем фокус
        /// чтобы KeyDown/KeyUp гарантированно приходили в этот UserControl
        /// </summary>
        private void OnEditingStarted()
        {
            Focus();
        }

        /// <summary>
        /// ѕерехватывает нажатие клавиши и передаЄт в ViewModel.
        /// —рабатывает только когда есть активное редактирование.
        /// </summary>
        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (DataContext is not HotKeySettingsViewModel vm) return;
            vm.HandleKeyDown(e.Key, e.KeyModifiers);
            e.Handled = true;
        }

        /// <summary>
        /// ѕерехватывает отпускание клавиши и передаЄт в ViewModel.
        /// »спользуетс€ дл€ обновлени€ live display при изменении модификаторов.
        /// </summary>
        private void OnKeyUp(object? sender, KeyEventArgs e)
        {
            if (DataContext is not HotKeySettingsViewModel vm) return;
            vm.HandleKeyUp(e.Key, e.KeyModifiers);
            e.Handled = true;
        }
    }
}