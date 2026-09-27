using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Writersword.ViewModels;

namespace Writersword.Views
{
    /// <summary>
    /// Окно выбора типа проекта для документа Word. Возвращает через ShowDialog
    /// выбранный тип или null, если человек передумал.
    /// </summary>
    public partial class ProjectTypePickerView : Window
    {
        private readonly ILogger<ProjectTypePickerView>? _logger;

        // Результат уже отдан: второе нажатие (двойной щелчок, Enter вслед за
        // щелчком) не должно закрывать окно повторно.
        private bool _closing;

        public ProjectTypePickerView()
        {
            _logger = App.Services?.GetService<ILogger<ProjectTypePickerView>>();

            InitializeComponent();

            KeyDown += OnWindowKeyDown;

            if (this.FindControl<Border>("RootPanel") is { } root)
                root.PointerPressed += OnRootPointerPressed;
        }

        private void CreateButton_Click(object? sender, RoutedEventArgs e) => Finish(true);

        private void CancelButton_Click(object? sender, RoutedEventArgs e) => Finish(false);

        private void OnWindowKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                Finish(true);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Finish(false);
                e.Handled = true;
            }
        }

        /// <summary>
        /// Окно без рамки таскается за свободное место. Нажатия по кнопкам и
        /// пунктам списка сюда не доходят — их разбирают сами элементы.
        /// </summary>
        private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        }

        /// <summary>
        /// Закрывает окно с результатом. Закрытие откладывается на следующий такт:
        /// синхронный Close внутри обработки щелчка ведёт к взаимной блокировке с
        /// потоком отрисовки — так же, как у окна сообщений.
        /// </summary>
        private void Finish(bool create)
        {
            if (_closing) return;
            _closing = true;

            string? result = create && DataContext is ProjectTypePickerViewModel vm
                ? vm.SelectedProjectType
                : null;

            _logger?.LogDebug("Project type picker closed: {Result}", result ?? "<cancel>");

            Dispatcher.UIThread.Post(() => Close(result));
        }
    }
}
