using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Writersword.Core.Interfaces.Services;

namespace Writersword.Infrastructure.Services.Storage
{
    /// <summary>
    /// Реализация сервиса для вычисления SHA256 хешей.
    /// Используется для быстрой проверки изменений данных модулей.
    ///
    /// Ключевое требование: одинаковые данные ВСЕГДА дают одинаковый хеш,
    /// независимо от типа объекта (POCO, JObject, string с JSON)
    /// и порядка ключей при сериализации.
    /// Для этого все объекты проходят нормализацию:
    ///   object → JToken → сортировка ключей → детерминированный JSON → SHA256.
    /// </summary>
    public class HashService : IHashService
    {
        private readonly ILogger<HashService> _logger;

        public HashService()
        {
            _logger = App.Services.GetService<ILogger<HashService>>()!;
        }

        /// <summary>
        /// Вычислить SHA256 хеш для объекта.
        /// Объект нормализуется в JSON с сортировкой ключей, затем вычисляется хеш.
        /// Это гарантирует одинаковый хеш для одинаковых данных независимо от:
        /// - типа объекта (POCO vs JObject vs string-с-JSON)
        /// - порядка ключей при сериализации
        /// - форматирования (отступы, переносы строк)
        /// </summary>
        public string ComputeHash(object? data)
        {
            if (data == null)
                return ComputeHashFromString("");

            try
            {
                var normalized = NormalizeToSortedJson(data);
                return ComputeHashFromString(normalized);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error computing hash");
                return ComputeHashFromString(data.ToString() ?? "");
            }
        }

        /// <summary>
        /// Вычислить SHA256 хеш для строки напрямую.
        /// </summary>
        public string ComputeHashFromString(string text)
        {
            using var sha256 = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(text);
            var hashBytes = sha256.ComputeHash(bytes);
            var builder = new StringBuilder(64);
            foreach (var b in hashBytes)
                builder.Append(b.ToString("x2"));
            return builder.ToString();
        }

        /// <summary>
        /// Нормализует объект в детерминированный JSON с сортировкой ключей.
        ///
        /// Три случая:
        /// 1. Строка начинается с '{' или '[' — это JSON-в-строке (двойная сериализация).
        ///    Распаковываем и нормализуем содержимое.
        /// 2. JToken — нормализуем напрямую.
        /// 3. Любой другой объект (POCO, Dictionary) — конвертируем в JToken и нормализуем.
        /// </summary>
        private static string NormalizeToSortedJson(object? obj)
        {
            if (obj == null) return "null";

            if (obj is string str)
            {
                var trimmed = str.TrimStart();
                if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
                {
                    try
                    {
                        return SortedTokenToString(JToken.Parse(str));
                    }
                    catch { }
                }
                return new JValue(str).ToString(Formatting.None);
            }

            JToken token;
            try
            {
                token = obj is JToken jt ? jt : JToken.FromObject(obj);
            }
            catch
            {
                return obj.ToString() ?? "null";
            }

            return SortedTokenToString(token);
        }

        /// <summary>
        /// Рекурсивно сериализует JToken с лексикографической сортировкой ключей объектов.
        /// Массивы не сортируются — порядок элементов семантически значим.
        /// </summary>
        private static string SortedTokenToString(JToken token)
        {
            if (token is JObject obj)
            {
                var sorted = new JObject(
                    obj.Properties()
                       // Свойства со значением null отбрасываются. Проект сохраняется
                       // через JsonHelper с NullValueHandling.Ignore — на диске null-полей
                       // нет вовсе. Живой объект (POCO) при JToken.FromObject такие поля
                       // включает как "Field": null. Без этого отсева хеш живых данных
                       // никогда не совпадал с хешем сохранённых (первое расхождение —
                       // Characters[].AvatarPath: null), и модуль вечно считался изменённым:
                       // вкладки открывались в Compare, кеш переписывался на каждом
                       // переключении. Отсев делает "поле = null" и "поля нет" эквивалентными,
                       // как и трактует их формат сохранения.
                       .Where(p => p.Value.Type != JTokenType.Null)
                       .OrderBy(p => p.Name, StringComparer.Ordinal)
                       .Select(p => new JProperty(p.Name, JToken.Parse(SortedTokenToString(p.Value))))
                );
                return sorted.ToString(Formatting.None);
            }

            if (token is JArray arr)
            {
                var sortedArr = new JArray(arr.Select(item => JToken.Parse(SortedTokenToString(item))));
                return sortedArr.ToString(Formatting.None);
            }

            return token.ToString(Formatting.None);
        }
    }
}