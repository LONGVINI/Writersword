using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using Writersword.Core.Interfaces.Services.UI;

namespace Writersword.Infrastructure.Services.UI
{
    /// <summary>
    /// Сервис управления темой оформления приложения
    /// Динамически меняет ResourceDictionary в App.Resources.MergedDictionaries
    /// и синхронизирует RequestedThemeVariant чтобы FluentTheme применял правильную палитру
    /// </summary>
    public class ThemeService : IThemeService
    {
        private readonly ILogger<ThemeService> _logger;
        private string _currentTheme = "Dark";

        /// <summary>Ссылка на текущий загруженный словарь темы — для удаления при смене</summary>
        private IResourceProvider? _currentThemeDictionary;

        private static readonly Dictionary<string, string> ThemePaths = new()
        {
            { "Dark",  "avares://Writersword/Styles/Themes/DarkTheme.axaml"  },
            { "Light", "avares://Writersword/Styles/Themes/LightTheme.axaml" },
            { "Sepia", "avares://Writersword/Styles/Themes/SepiaTheme.axaml" }
        };

        /// <summary>
        /// Маппинг темы приложения на ThemeVariant для FluentTheme.
        /// Light и Sepia используют светлый вариант FluentTheme чтобы его шаблоны
        /// не перебивали светлые цвета тёмными значениями.
        /// </summary>
        private static readonly Dictionary<string, ThemeVariant> ThemeVariantMap = new()
        {
            { "Dark",  ThemeVariant.Dark  },
            { "Light", ThemeVariant.Light },
            { "Sepia", ThemeVariant.Dark  }
        };

        /// <summary>Текущая тема (Dark, Light, Sepia)</summary>
        public string CurrentTheme => _currentTheme;

        /// <summary>Событие смены темы</summary>
        public event Action<string>? ThemeChanged;

        public ThemeService()
        {
            _logger = App.Services.GetService<ILogger<ThemeService>>()!;
        }

        /// <summary>Сменить тему — заменить ResourceDictionary и синхронизировать ThemeVariant</summary>
        public void SetTheme(string themeCode)
        {
            if (!ThemePaths.ContainsKey(themeCode))
            {
                _logger.LogWarning("Unknown theme: {Theme}", themeCode);
                return;
            }

            if (_currentTheme == themeCode && _currentThemeDictionary != null) return;

            try
            {
                var app = Application.Current;
                if (app == null) return;

                var mergedDictionaries = app.Resources.MergedDictionaries;

                var newDict = (ResourceDictionary)AvaloniaXamlLoader.Load(
                    new Uri(ThemePaths[themeCode]));

                if (_currentThemeDictionary != null)
                {
                    var index = mergedDictionaries.IndexOf(_currentThemeDictionary);
                    mergedDictionaries.Remove(_currentThemeDictionary);
                    _currentThemeDictionary = null;

                    if (index >= 0 && index <= mergedDictionaries.Count)
                        mergedDictionaries.Insert(index, newDict);
                    else
                        mergedDictionaries.Insert(0, newDict);
                }
                else
                {
                    mergedDictionaries.Insert(0, newDict);
                }

                _currentThemeDictionary = newDict;
                _currentTheme = themeCode;

                // Синхронизируем RequestedThemeVariant — FluentTheme использует его
                // для выбора своей встроенной палитры в шаблонах контролов
                if (ThemeVariantMap.TryGetValue(themeCode, out var variant))
                {
                    app.RequestedThemeVariant = variant;
                    _logger.LogDebug("RequestedThemeVariant set to: {Variant}", variant);
                }

                ThemeChanged?.Invoke(themeCode);
                _logger.LogDebug("Theme changed to: {Theme}", themeCode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error changing theme to {Theme}", themeCode);
            }
        }
    }
}