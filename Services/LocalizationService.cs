using System;
using System.Globalization;
using Writersword.Resources.Localization;

namespace Writersword.Services
{
    /// <summary>
    /// Сервис локализации
    /// </summary>
    public class LocalizationService : ILocalizationService
    {
        private string _currentLanguage = "en";

        public string CurrentLanguage => _currentLanguage;

        public event Action? LanguageChanged;

        /// <summary>Получить строку из ресурсов</summary>
        public string GetString(string key)
        {
            try
            {
                // Теперь просто Strings (не Resources.Localization.Strings)
                var value = Strings.ResourceManager.GetString(key, Strings.Culture);
                return value ?? $"[{key}]";
            }
            catch
            {
                return $"[{key}]";
            }
        }

        /// <summary>Сменить язык</summary>
        public void SetLanguage(string languageCode)
        {
            _currentLanguage = languageCode;
            var culture = new CultureInfo(languageCode);
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;

            Strings.Culture = culture;

            LanguageChanged?.Invoke();
        }
    }
}