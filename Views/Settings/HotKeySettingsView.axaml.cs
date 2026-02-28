using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using System;
using Writersword.ViewModels.Settings;

namespace Writersword.Views.Settings
{
    /// <summary>
    /// Code-behind для View настроек горячих клавиш.
    /// Перехватывает KeyDown и KeyUp только когда активно редактирование биндинга.
    /// Управляет фокусом — захватывает его когда биндинг переходит в режим редактирования.
    /// Отключает внешний ScrollViewer чтобы внутренние скроллы работали независимо.
    /// </summary>
    public partial class HotKeySettingsView : UserControl
    {
        private HotKeySettingsViewModel? _subscribedVm;
        private ScrollViewer? _parentScrollViewer;

        public HotKeySettingsView()
        {
            InitializeComponent();
            KeyDown += OnKeyDown;
            KeyUp += OnKeyUp;
            DataContextChanged += OnDataContextChanged;
            AttachedToVisualTree += OnAttachedToVisualTree;
            DetachedFromVisualTree += OnDetachedFromVisualTree;

            AddHandler(TextBox.LostFocusEvent, OnTextBoxLostFocus, handledEventsToo: true);
        }

        /// <summary>
        /// После прикрепления к дереву находим родительский ScrollViewer
        /// и отключаем его вертикальный скролл — HotKeySettingsView управляет скроллом сам.
        /// </summary>
        private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            _parentScrollViewer = this.FindAncestorOfType<ScrollViewer>();
            if (_parentScrollViewer != null)
                _parentScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        }

        /// <summary>
        /// При откреплении от дерева восстанавливаем скролл родительского ScrollViewer
        /// чтобы другие вкладки настроек работали нормально.
        /// </summary>
        private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            if (_parentScrollViewer != null)
            {
                _parentScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                _parentScrollViewer = null;
            }
        }

        /// <summary>
        /// При смене DataContext отписываемся от старого VM и подписываемся на новый.
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
        /// Когда VM переводит биндинг в режим редактирования — захватываем фокус
        /// чтобы KeyDown/KeyUp гарантированно приходили в этот UserControl.
        /// </summary>
        private void OnEditingStarted()
        {
            Focus();
        }

        /// <summary>
        /// Перехватывает потерю фокуса любым TextBox внутри UserControl.
        /// Если TextBox имеет Tag типа PrefixRowViewModel — сохраняет комментарий через VM.
        /// </summary>
        private void OnTextBoxLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (e.Source is not TextBox textBox) return;
            if (textBox.Tag is not PrefixRowViewModel row) return;
            if (DataContext is not HotKeySettingsViewModel vm) return;

            vm.SavePrefixCommentCommand.Execute(row);
        }

        /// <summary>
        /// Перехватывает нажатие клавиши и передаёт в ViewModel.
        /// Срабатывает только когда IsEditingActive == true.
        /// </summary>
        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (DataContext is not HotKeySettingsViewModel vm) return;
            if (!vm.IsEditingActive) return;
            vm.HandleKeyDown(e.Key, e.KeyModifiers);
            e.Handled = true;
        }

        /// <summary>
        /// Перехватывает отпускание клавиши и передаёт в ViewModel.
        /// Срабатывает только когда IsEditingActive == true.
        /// </summary>
        private void OnKeyUp(object? sender, KeyEventArgs e)
        {
            if (DataContext is not HotKeySettingsViewModel vm) return;
            if (!vm.IsEditingActive) return;
            vm.HandleKeyUp(e.Key, e.KeyModifiers);
            e.Handled = true;
        }
    }
}