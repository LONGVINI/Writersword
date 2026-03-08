using Avalonia.Controls;
using Avalonia.Input;
using Writersword.ViewModels.Settings;

namespace Writersword.Views.Settings;

/// <summary>
/// Code-behind для вкладки настроек горячих клавиш.
///
/// Отвечает за:
/// 1. Перехват KeyDown/KeyUp пока активен режим редактирования биндинга или префикса.
///    Маршрутизирует события в VM через HandleKeyDown/HandleKeyUp.
///    Глобальный handler в MainWindowView при этом НЕ отрабатывает — e.Handled = true.
///
/// 2. Сохранение комментария префикса при потере фокуса полем TextBox.
///    VM хранит состояние, code-behind только передаёт событие.
///
/// 3. Захват фокуса при начале редактирования биндинга или префикса.
///    Без фокуса на UserControl KeyDown не будет получен.
/// </summary>
public partial class HotKeySettingsView : UserControl
{
    public HotKeySettingsView()
    {
        InitializeComponent();

        // Захватываем фокус когда VM переходит в режим редактирования
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is HotKeySettingsViewModel vm)
            vm.EditingStarted += OnEditingStarted;
    }

    /// <summary>
    /// Захватить фокус на UserControl чтобы начать получать KeyDown/KeyUp.
    /// Вызывается из VM через событие EditingStarted.
    /// </summary>
    private void OnEditingStarted()
    {
        Focus();
    }

    /// <summary>
    /// Перехватываем KeyDown пока активен режим редактирования.
    /// Передаём в VM и помечаем как Handled чтобы глобальный handler не срабатывал.
    /// </summary>
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

    /// <summary>
    /// Перехватываем KeyUp пока активен режим редактирования.
    /// Нужен для обновления live display при удержании модификаторов.
    /// </summary>
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

    /// <summary>
    /// Сохраняем комментарий префикса когда TextBox теряет фокус.
    /// Tag биндинга содержит PrefixRowViewModel — передаём его в команду VM.
    /// Вызов через туннелирование: обрабатываем только события от TextBox внутри строк префиксов.
    /// </summary>
    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLostFocus(e);

        if (e.Source is TextBox textBox
            && textBox.Tag is PrefixRowViewModel prefixRow
            && DataContext is HotKeySettingsViewModel vm)
        {
            vm.SavePrefixCommentCommand.Execute(prefixRow);
        }
    }
}