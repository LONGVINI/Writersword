using Android.App;
using Android.Content.PM;
using Avalonia.Android;

namespace Writersword.Mobile
{
    /// <summary>
    /// Активность, в которой живёт интерфейс.
    ///
    /// В Avalonia 12 она больше не параметризуется типом приложения:
    /// построение AppBuilder переехало в класс Application (см. AndroidApplication),
    /// чтобы приложение оставалось настроенным и для активностей, открытых
    /// системой в обход основной.
    ///
    /// ConfigurationChanges перечисляет то, что активность берёт на себя:
    /// без этого поворот экрана или появление клавиатуры пересоздают активность
    /// целиком, и приложение перезапускается на каждый поворот.
    /// </summary>
    [Activity(
        Label = "Writersword",
        Theme = "@style/WriterswordTheme",
        MainLauncher = true,
        LaunchMode = LaunchMode.SingleTop,
        ConfigurationChanges = ConfigChanges.Orientation
                               | ConfigChanges.ScreenSize
                               | ConfigChanges.UiMode
                               | ConfigChanges.KeyboardHidden)]
    public class MainActivity : AvaloniaMainActivity
    {
    }
}
