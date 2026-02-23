using System;

namespace Writersword.Src.Core.Interfaces.Services.UI
{
    /// <summary>
    /// Сервис управления темой оформления приложения
    /// </summary>
    public interface IThemeService
    {
        /// <summary>Текущая тема (Dark, Light, Sepia)</summary>
        string CurrentTheme { get; }

        /// <summary>Сменить тему</summary>
        void SetTheme(string themeCode);

        /// <summary>Событие смены темы</summary>
        event Action<string>? ThemeChanged;
    }
}