using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Writersword.Core.Interfaces.Services;

namespace Writersword.Src.Infrastructure.Services.Storage
{
    /// <summary>
    /// Реализация сервиса для вычисления SHA256 хешей
    /// Используется для быстрой проверки изменений данных модулей
    /// </summary>
    public class HashService : IHashService
    {
        private readonly ILogger<HashService> _logger;

        public HashService()
        {
            _logger = App.Services.GetService<ILogger<HashService>>()!;
        }

        /// <summary>
        /// Вычислить SHA256 хеш для объекта
        /// Объект сериализуется в JSON, затем вычисляется хеш
        /// </summary>
        /// <param name="data">Данные для хеширования (string, object, etc)</param>
        /// <returns>SHA256 хеш в виде строки (64 символа)</returns>
        public string ComputeHash(object? data)
        {
            if (data == null)
                return ComputeHashFromString("");

            // Если это строка - хешируем напрямую
            if (data is string str)
                return ComputeHashFromString(str);

            // Иначе сериализуем в JSON и хешируем
            try
            {
                var json = JsonConvert.SerializeObject(data);
                return ComputeHashFromString(json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error serializing data");
                return ComputeHashFromString(data.ToString() ?? "");
            }
        }

        /// <summary>
        /// Вычислить SHA256 хеш для строки напрямую
        /// Быстрее чем ComputeHash(object) для текстовых данных
        /// </summary>
        /// <param name="text">Текст для хеширования</param>
        /// <returns>SHA256 хеш в виде строки (64 символа)</returns>
        public string ComputeHashFromString(string text)
        {
            using (var sha256 = SHA256.Create())
            {
                // Конвертируем текст в байты
                var bytes = Encoding.UTF8.GetBytes(text);

                // Вычисляем хеш
                var hashBytes = sha256.ComputeHash(bytes);

                // Конвертируем в hex строку
                var builder = new StringBuilder();
                foreach (var b in hashBytes)
                {
                    builder.Append(b.ToString("x2"));
                }

                return builder.ToString();
            }
        }
    }
}