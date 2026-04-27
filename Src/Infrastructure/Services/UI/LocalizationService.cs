using System;
using System.Globalization;
using Writersword.Resources.Localization;
using Writersword.Core.Interfaces.Services.UI;

namespace Writersword.Infrastructure.Services.UI
{
    /// <summary>
    /// Сервис локализации
    /// </summary>
    public class LocalizationService : ILocalizationService
    {
        private string _currentLanguage = "en";

        public string CurrentLanguage => _currentLanguage;

        public event Action? LanguageChanged;

        /// <summary>Получить строку по ресурсному ключу</summary>
        public string GetString(string key)
        {
            try
            {
                // Читает строки Strings (из Resources.Localization.Strings)
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

            // DefaultThreadCurrent* применяется глобально ко всем потокам,
            // включая пул потоков и async-контексты — это необходимо чтобы
            // модули, создающие вью асинхронно, получали правильную культуру.
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;

            CultureInfo.CurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;

            Strings.Culture = culture;

            LanguageChanged?.Invoke();
        }
    }
}