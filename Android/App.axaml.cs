using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Writersword.Mobile.Views;

namespace Writersword.Mobile
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            // На телефоне окон нет: жизненный цикл отдаёт единственное
            // представление, которое активность показывает целиком.
            if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
                singleView.MainView = new MainView();

            base.OnFrameworkInitializationCompleted();
        }
    }
}
