using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Serilog;
using System;
using System.IO;
using Writersword.Mobile.Views;

namespace Writersword.Mobile
{
    public partial class App : Application
    {
        /// <summary>
        /// Папка журналов в песочнице приложения.
        ///
        /// Своей папкой, а не файлом рядом с настройками: приёмник Serilog режет
        /// журнал по дням и держит несколько последних, и в общей папке они
        /// перемешались бы с настройками подключения и состоянием читалки.
        /// </summary>
        public static string LogDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "logs");

        public override void Initialize()
        {
            ConfigureLogging();
            AvaloniaXamlLoader.Load(this);
        }

        /// <summary>
        /// Журнал в файл, а не только в консоль.
        ///
        /// Консоль на телефоне видна лишь через adb при подключённом проводе —
        /// то есть разобрать, что случилось вчера вечером, по ней нельзя. До сих
        /// пор на телефоне не настраивался вообще никакой приёмник, и всё, что
        /// приложение писало, уходило в пустоту: Serilog без настройки молчит.
        ///
        /// Ограничения жёсткие намеренно: журнал живёт в песочнице, места там
        /// столько же, сколько у книг, и раздуваться ему нельзя.
        /// </summary>
        private static void ConfigureLogging()
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);

                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .WriteTo.Console()
                    .WriteTo.File(
                        Path.Combine(LogDirectory, "writersword-.log"),
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 3,
                        fileSizeLimitBytes: 4 * 1024 * 1024,
                        rollOnFileSizeLimit: true,
                        shared: true)
                    .CreateLogger();

                Log.Information("Application started");
            }
            catch (Exception ex)
            {
                // Без журнала приложение работать обязано: он нужен для разбора
                // бед, а не для работы.
                Console.WriteLine("Failed to configure logging: " + ex);
            }
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
