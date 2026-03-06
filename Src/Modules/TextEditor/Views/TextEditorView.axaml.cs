using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using Writersword.Src.Core.Interfaces.Services.Input;

namespace Writersword.Modules.TextEditor.Views
{
    /// <summary>
    /// View текстового редактора
    /// </summary>
    public partial class TextEditorView : UserControl
    {
        private readonly IHotKeyService _hotKeyService;

        public TextEditorView()
        {
            InitializeComponent();
            _hotKeyService = App.Services.GetRequiredService<IHotKeyService>();

            this.AddHandler(
                KeyDownEvent,
                OnKeyDownTunnel,
                RoutingStrategies.Tunnel
            );
        }

        /// <summary>
        /// Перехватывает нажатия клавиш до TextBox и передаёт в HotKeyService.
        /// Tunnel срабатывает раньше чем TextBox обрабатывает встроенные команды.
        /// </summary>
        private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
        {
            var gesture = new KeyGesture(e.Key, e.KeyModifiers);
            bool handled = _hotKeyService.HandleKeyPress(gesture);
            if (handled)
                e.Handled = true;
        }
    }
}