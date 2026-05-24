using Avalonia;
using ReactiveUI.Avalonia;
using System;

namespace Writersword;

class Program
{
    /// <summary>
    /// Точка входа в приложение.
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            // Гарантированный сброс буферов Serilog при любом завершении,
            // включая исключения и принудительное закрытие окна.
            Serilog.Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// Конфигурация Avalonia приложения.
    /// Здесь регистрируются все расширения и платформенные настройки.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()           // Автоматическое определение платформы (Windows/Linux/macOS)
            .WithInterFont()               // Подключение шрифта Inter
            .UseReactiveUI(_ => { })              // Поддержка ReactiveUI для MVVM
            .LogToTrace();                 // Логирование в Debug консоль
}