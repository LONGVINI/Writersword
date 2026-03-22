using Newtonsoft.Json;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Writersword.Shared.Helpers
{
    /// <summary>
    /// Вспомогательный класс для работы с JSON
    /// Обёртка над Newtonsoft.Json для единообразия в проекте
    /// </summary>
    public static class JsonHelper
    {
        /// <summary>Настройки сериализации по умолчанию</summary>
        private static readonly JsonSerializerSettings DefaultSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore
        };

        /// <summary>
        /// Сериализовать объект в JSON строку
        /// </summary>
        public static string Serialize<T>(T obj)
        {
            return JsonConvert.SerializeObject(obj, DefaultSettings);
        }

        /// <summary>
        /// Десериализовать JSON строку в объект
        /// </summary>
        public static T? Deserialize<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return default;

            try
            {
                return JsonConvert.DeserializeObject<T>(json, DefaultSettings);
            }
            catch (Exception)
            {
                return default;
            }
        }

        /// <summary>
        /// Сохранить объект в JSON файл
        /// </summary>
        public static async Task SaveToFileAsync<T>(string filePath, T obj)
        {
            try
            {
                var json = Serialize(obj);
                // Создаём директорию если не существует
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                await File.WriteAllTextAsync(filePath, json);
            }
            catch (Exception)
            {
                throw;
            }
        }

        /// <summary>
        /// Загрузить объект из JSON файла
        /// </summary>
        public static async Task<T?> LoadFromFileAsync<T>(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    return default;
                }
                var json = await File.ReadAllTextAsync(filePath);
                return Deserialize<T>(json);
            }
            catch (Exception)
            {
                return default;
            }
        }
    }
}