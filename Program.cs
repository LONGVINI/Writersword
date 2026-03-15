using Avalonia;
using Avalonia.ReactiveUI;
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
        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Конфигурация Avalonia приложения.
    /// Здесь регистрируются все расширения и платформенные настройки.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()           // Автоматическое определение платформы (Windows/Linux/macOS)
            .WithInterFont()               // Подключение шрифта Inter
            .UseReactiveUI()               // Поддержка ReactiveUI для MVVM
            .LogToTrace();                 // Логирование в Debug консоль
}