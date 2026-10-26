namespace Writersword.Core.Interfaces.Services
{
    /// <summary>
    /// Сервис для вычисления SHA256 хешей
    /// Используется для быстрой проверки изменений данных модулей
    /// </summary>
    public interface IHashService
    {
        /// <summary>
        /// Вычислить SHA256 хеш для объекта
        /// Объект сериализуется в JSON, затем вычисляется хеш
        /// </summary>
        /// <param name="data">Данные для хеширования (string, object, etc)</param>
        /// <returns>SHA256 хеш в виде строки (64 символа)</returns>
        string ComputeHash(object? data);

        /// <summary>
        /// Вычислить SHA256 хеш для строки напрямую
        /// Быстрее чем ComputeHash(object) для текстовых данных
        /// </summary>
        /// <param name="text">Текст для хеширования</param>
        /// <returns>SHA256 хеш в виде строки (64 символа)</returns>
        string ComputeHashFromString(string text);

        /// <summary>
        /// Быстрый отпечаток данных: объект сериализуется (Newtonsoft, настройки по
        /// умолчанию) прямо в SHA256, без нормализации и без промежуточных строк.
        /// Одинаков для одного и того же состояния объекта одного типа; для сравнения
        /// данных разного вида (объект и JSON-строка) не годится — для этого ComputeHash.
        /// </summary>
        /// <param name="data">Данные для отпечатка</param>
        /// <returns>SHA256 в виде строки (64 символа)</returns>
        string ComputeFingerprint(object? data);
    }
}