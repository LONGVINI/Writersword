using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Writersword.Modules.TextEditor.Views.Shared
{
    /// <summary>
    /// Галерея мест номера страницы — маленькие листы, на каждом видно, где встанет
    /// номер. Живёт в меню кнопки «Номер» на вкладках «Колонтитулы» и «Вставка».
    /// </summary>
    public partial class PageNumberPicker : UserControl
    {
        public PageNumberPicker()
        {
            InitializeComponent();

            // Выбор сделан — меню закрывается, как у обычного пункта меню.
            AddHandler(Button.ClickEvent, OnAnyButtonClick, RoutingStrategies.Bubble);
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        private void OnAnyButtonClick(object? sender, RoutedEventArgs e)
        {
            // Меню закрывается после команды, а не в самом щелчке: команда кнопки
            // исполняется следом за событием Click, а закрытое меню отвязывает кнопки
            // от вкладки — привязка команды обнуляется, и выбор не срабатывал.
            Avalonia.Threading.Dispatcher.UIThread.Post(ClosePopup, Avalonia.Threading.DispatcherPriority.Background);
        }

        private void ClosePopup()
        {
            StyledElement? element = this;
            while (element is not null)
            {
                if (element is Popup popup)
                {
                    popup.IsOpen = false;
                    return;
                }
                element = element.Parent;
            }
        }
    }
}
