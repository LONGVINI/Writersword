using System;
using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using ReactiveUI.Avalonia;

namespace Writersword.Mobile
{
    /// <summary>
    /// Класс приложения Android — здесь настраивается AppBuilder.
    ///
    /// Конструктор с указателем на Java-объект не вызывается из кода: его
    /// вызывает среда выполнения Android, когда создаёт управляемую обёртку
    /// над своим экземпляром Application. Убрать его нельзя, хотя он и выглядит
    /// неиспользуемым.
    /// </summary>
    [Application]
    public class AndroidApplication : AvaloniaAndroidApplication<App>
    {
        protected AndroidApplication(IntPtr javaReference, JniHandleOwnership transfer)
            : base(javaReference, transfer)
        {
        }

        protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        {
            return base.CustomizeAppBuilder(builder)
                .WithInterFont()
                .UseReactiveUI(_ => { });
        }
    }
}
