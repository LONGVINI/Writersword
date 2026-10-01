using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.ViewModels.Toolbar;
using Writersword.Modules.TextEditor.Views.Dialogs;

namespace Writersword.Modules.TextEditor.Views.Shared
{
    /// <summary>
    /// Меню «Эффекты текста»: свои наборы, эффекты букв, регистр, знак ударения,
    /// рамка знаков и скрытый текст. Данные и команды берёт из DataContext вкладки
    /// «Главная».
    ///
    /// Здесь — только меню плитки набора: имя спрашивается окном, а окно открывает
    /// вид. Сами правки делает вьюмодель ленты.
    /// </summary>
    public partial class TextEffectsPicker : UserControl
    {
        public TextEffectsPicker()
        {
            InitializeComponent();
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        private async void OnRenamePresetClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { DataContext: TextEffectPreset preset }) return;
            if (DataContext is not RibbonHomeTabViewModel ribbon) return;

            var owner = OwnerWindow();
            if (owner is null) return;

            var dialog = new InputDialog("Переименовать эффект", "Название набора", preset.Name);
            string? name = await dialog.ShowDialog<string?>(owner);
            if (string.IsNullOrWhiteSpace(name)) return;

            ribbon.RenameTextEffectPreset(preset, name);
        }

        private void OnReplacePresetClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { DataContext: TextEffectPreset preset }) return;
            if (DataContext is not RibbonHomeTabViewModel ribbon) return;

            ribbon.ReplaceTextEffectPresetFromCaret(preset);
        }

        private void OnDeletePresetClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { DataContext: TextEffectPreset preset }) return;
            if (DataContext is not RibbonHomeTabViewModel ribbon) return;

            ribbon.DeleteTextEffectPreset(preset);
        }

        /// <summary>
        /// Окно, над которым спросить имя. Меню живёт во всплывающем слое, у которого
        /// своё окно-носитель, — владельцем диалога берётся главное окно программы.
        /// </summary>
        private Window? OwnerWindow()
        {
            if (TopLevel.GetTopLevel(this) is Window window) return window;

            return (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        }
    }
}
