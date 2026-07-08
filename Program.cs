using Avalonia;
using Avalonia.Logging;
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
            // Порядок рендер-бэкендов на Windows: сначала два GPU-пути (ANGLE через
            // DirectX, затем нативный WGL/OpenGL), и только если оба не поднялись —
            // software (CPU) как последняя страховка, чтобы приложение стартовало
            // при любом железе.
            .With(new Win32PlatformOptions
            {
                RenderingMode = new[]
                {
                    Win32RenderingMode.AngleEgl,
                    Win32RenderingMode.Wgl,
                    Win32RenderingMode.Software
                }
            })
            .WithInterFont()               // Подключение шрифта Inter
            .UseReactiveUI(_ => { })              // Поддержка ReactiveUI для MVVM
            // Warning (штатный уровень). Debug временно поднимали для диагностики
            // рендер-бэкенда, но при активном UI трассировка Debug пишет тысячи
            // строк (layout/привязки) на каждый кадр и сама по себе тормозит.
            .LogToTrace(LogEventLevel.Warning);
}