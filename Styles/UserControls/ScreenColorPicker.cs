using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;

namespace Writersword.Styles.UserControls
{
    /// <summary>
    /// Пипетка: берёт цвет пикселя из любой точки экрана.
    /// Реализация платформенная — сейчас есть Windows, остальные ОС возвращают null.
    /// </summary>
    public interface IScreenColorPicker
    {
        bool IsSupported { get; }

        /// <summary>
        /// Открывает экранную пипетку и возвращает выбранный цвет (или null при отмене).
        /// </summary>
        Task<Color?> PickAsync(TopLevel? owner);
    }

    public static class ScreenColorPicker
    {
        public static IScreenColorPicker Create()
            => OperatingSystem.IsWindows()
                ? new Win32ScreenColorPicker()
                : new UnsupportedScreenColorPicker();
    }

    internal sealed class UnsupportedScreenColorPicker : IScreenColorPicker
    {
        public bool IsSupported => false;
        public Task<Color?> PickAsync(TopLevel? owner) => Task.FromResult<Color?>(null);
    }

    internal sealed class Win32ScreenColorPicker : IScreenColorPicker
    {
        public bool IsSupported => true;

        public Task<Color?> PickAsync(TopLevel? owner) => EyedropperOverlay.PickAsync(owner);
    }
}
