
namespace Writersword.Services
{
    /// <summary>
    /// Сервис локализации (перевода интерфейса)
    /// </summary>
    public interface ILocalizationService
    {
        /// <summary>Получить переведённую строку по ключу</summary>
        string GetString(string key);

        /// <summary>Текущий язык (ru, uk, en)</summary>
        string CurrentLanguage { get; }

        /// <summary>Сменить язык</summary>
        void SetLanguage(string languageCode);

        /// <summary>Событие смены языка</summary>
        event System.Action? LanguageChanged;
    }
}