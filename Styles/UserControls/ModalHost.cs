using Avalonia;
using Avalonia.Controls;

namespace Writersword.Styles.UserControls
{
    /// <summary>
    /// Маркер области-хоста для модальных оверлеев общих контролов.
    /// Модуль вешает ModalHost.IsHost="True" на корень своего вью — и модальные
    /// окна (например, редактор цвета) затемняют/центрируются по этой области,
    /// а не по всему приложению.
    /// </summary>
    public static class ModalHost
    {
        public static readonly AttachedProperty<bool> IsHostProperty =
            AvaloniaProperty.RegisterAttached<Control, bool>("IsHost", typeof(ModalHost));

        public static bool GetIsHost(Control element) => element.GetValue(IsHostProperty);

        public static void SetIsHost(Control element, bool value) => element.SetValue(IsHostProperty, value);
    }
}
