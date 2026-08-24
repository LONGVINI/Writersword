using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Writersword.Modules.TextEditor.Views.Reading
{
    /// <summary>
    /// Лента чтения. Одна полоса с группами — та же форма, что у ленты редактора,
    /// но всё в ней про то, как читателю смотреть на рукопись.
    /// </summary>
    public partial class ReadingRibbonView : UserControl
    {
        public ReadingRibbonView()
        {
            InitializeComponent();

            // Подписка кодом, а не в разметке: тип аргумента события фокуса зовётся
            // по-разному от версии к версии Avalonia, и упоминать его в подписи
            // обработчика значит ломать сборку на каждом обновлении. Лямбда с
            // отброшенными аргументами обходится без имени типа вовсе.
            if (this.FindControl<TextBox>("PageInputBox") is { } pageBox)
                pageBox.GotFocus += (_, _) =>
                    Dispatcher.UIThread.Post(() => BeginPageEdit(pageBox), DispatcherPriority.Background);
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        /// <summary>
        /// Enter в поле номера страницы: книга открывается на введённой странице.
        ///
        /// Фокус здесь не мелочь. Все прочие кнопки ленты фокуса не берут намеренно:
        /// листание идёт стрелками и пробелом, и разбирает их канвас. Поле ввода —
        /// единственное исключение, и оставить фокус в нём значит отобрать у книги
        /// клавиатуру до первого щелчка мимо. Забирает фокус обратно сама книга, когда
        /// получает просьбу открыться на странице: лента не знает про канвас и знать
        /// не должна.
        /// </summary>
        private void OnPageInputKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter && e.Key != Key.Escape) return;

            if (DataContext is ViewModels.Reading.ReadingRibbonViewModel vm
                && vm.GoToPageCommand.CanExecute(null))
            {
                // Escape тоже идёт сюда: введённое отбрасывается, поле возвращается к
                // тому, что открыто, и книга снова получает клавиатуру.
                if (e.Key == Key.Escape) vm.ResetPageInput();
                vm.GoToPageCommand.Execute(null);
            }

            e.Handled = true;
        }

        /// <summary>
        /// Поле страницы взято в правку: «из 8» убирается совсем, остаётся один номер,
        /// и он сразу выделен. Подпись нужна, только пока на поле смотрят: как только
        /// в нём начали набирать, она превращается в помеху — её приходилось бы
        /// обходить курсором и стирать руками.
        ///
        /// Зовётся отложенно: щелчок сначала отдаёт полю фокус, а уже потом ставит
        /// каретку по месту нажатия, и выделение, сделанное сразу, ею же и сбилось бы.
        /// </summary>
        private void BeginPageEdit(TextBox box)
        {
            if (!box.IsFocused) return;

            if (DataContext is ViewModels.Reading.ReadingRibbonViewModel vm)
                box.Text = vm.PageNumberText;

            box.SelectAll();
        }

        /// <summary>Уход из поля номера — то же самое, что Enter: введённое применяется.</summary>
        private void OnPageInputLostFocus(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not ViewModels.Reading.ReadingRibbonViewModel vm) return;
            if (vm.GoToPageCommand.CanExecute(null)) vm.GoToPageCommand.Execute(null);
        }
    }
}
