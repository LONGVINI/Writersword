using Avalonia.Controls;
using Avalonia.Threading;
using System.ComponentModel;
using Writersword.ViewModels.Settings;

namespace Writersword.Views.Settings
{
    /// <summary>
    /// View вкладки «Резервные копии»: история версий проекта,
    /// папка хранения и список точек восстановления.
    /// </summary>
    public partial class BackupSettingsView : UserControl
    {
        private BackupSettingsViewModel? _viewModel;

        public BackupSettingsView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object? sender, System.EventArgs e)
        {
            if (_viewModel is not null)
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

            _viewModel = DataContext as BackupSettingsViewModel;

            if (_viewModel is not null)
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        /// <summary>
        /// Короткое проявление подписи при смене единицы измерения.
        /// Текст меняется мгновенно через привязку, а щелчок по кнопке при
        /// коротких словах («дней» → «недель») почти не читается — анимация
        /// делает переключение заметным.
        /// </summary>
        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(BackupSettingsViewModel.IntervalUnitText))
                return;

            var label = this.FindControl<TextBlock>("IntervalUnitLabel");
            if (label is null) return;

            label.Opacity = 0;

            // Возврат к единице на следующем проходе диспетчера: назначение
            // обоих значений в одном проходе Avalonia схлопывает, и перехода
            // не видно вовсе.
            Dispatcher.UIThread.Post(() => label.Opacity = 1, DispatcherPriority.Background);
        }
    }
}
