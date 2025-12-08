using Avalonia;
using System;

namespace Writersword;

class Program
{
    /// <summary>
    /// Точка входа в приложение
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Конфигурация Avalonia приложения
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()           // Автоматическое определение платформы (Windows/Linux/macOS)
            .WithInterFont()               // Использование шрифта Inter
            .LogToTrace();                 // Логирование в Debug консоль
}
