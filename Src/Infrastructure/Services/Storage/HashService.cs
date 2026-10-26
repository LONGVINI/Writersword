﻿using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
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
    /// <para>
    /// Нормализованный JSON не собирается в строку: он пишется прямо в SHA-256
    /// одним проходом по дереву. Прежняя реализация на каждом уровне вложенности
    /// превращала поддерево в строку и разбирала её обратно, так что каждое
    /// значение (в том числе картинки в base64 на десятки мегабайт) копировалось
    /// столько раз, какова его глубина. Для книги с картинками это были десятки
    /// секунд и гигабайты мусора на каждом сравнении с файлом и записи кеша.
    /// </para>
    /// Байты на входе SHA-256 совпадают с прежними до последнего знака: значения
    /// внутри объектов и массивов, как и раньше, один раз проходят через
    /// запись и разбор JSON там, где это может их изменить (даты, decimal,
    /// массивы байт и прочие нестроковые типы). Строки, которые разбор вернул бы
    /// без изменений, пишутся как есть.
    /// </summary>
    public class HashService : IHashService
    {
        private readonly ILogger<HashService> _logger;

        private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

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
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using (var sink = new HashSinkStream(hash))
                using (var text = new StreamWriter(sink, Utf8NoBom, 64 * 1024, leaveOpen: true))
                {
                    WriteNormalized(data, text);
                    text.Flush();
                }
                return ToLowerHex(hash.GetHashAndReset());
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
            return ToLowerHex(hashBytes);
        }

        private static readonly JsonSerializer FingerprintSerializer =
            JsonSerializer.Create(new JsonSerializerSettings
            {
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore
            });

        /// <summary>
        /// Быстрый отпечаток данных: сериализация прямо в SHA256 без нормализации.
        /// Строка хешируется как есть, null — как "null".
        /// </summary>
        public string ComputeFingerprint(object? data)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            if (data == null)
            {
                hash.AppendData(Encoding.UTF8.GetBytes("null"));
            }
            else if (data is string text)
            {
                hash.AppendData(Encoding.UTF8.GetBytes(text));
            }
            else
            {
                using var sink = new HashSinkStream(hash);
                using var writer = new StreamWriter(sink, Utf8NoBom, 64 * 1024, leaveOpen: true);
                using (var json = new JsonTextWriter(writer) { CloseOutput = false })
                    FingerprintSerializer.Serialize(json, data);
                writer.Flush();
            }

            return ToLowerHex(hash.GetHashAndReset());
        }

        private static string ToLowerHex(byte[] hashBytes)
        {
            var builder = new StringBuilder(64);
            foreach (var b in hashBytes)
                builder.Append(b.ToString("x2"));
            return builder.ToString();
        }

        /// <summary>
        /// Пишет детерминированный JSON с сортировкой ключей.
        ///
        /// Три случая:
        /// 1. Строка начинается с '{' или '[' — это JSON-в-строке (двойная сериализация).
        ///    Распаковываем и нормализуем содержимое.
        /// 2. JToken — нормализуем напрямую.
        /// 3. Любой другой объект (POCO, Dictionary) — конвертируем в JToken и нормализуем.
        /// </summary>
        private static void WriteNormalized(object obj, TextWriter output)
        {
            if (obj is string str)
            {
                var trimmed = str.TrimStart();
                if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
                {
                    JToken? parsed = null;
                    try
                    {
                        parsed = JToken.Parse(str);
                    }
                    catch { }

                    if (parsed != null)
                    {
                        WriteRoot(parsed, output);
                        return;
                    }
                }
                output.Write(new JValue(str).ToString(Formatting.None));
                return;
            }

            JToken token;
            try
            {
                token = obj is JToken jt ? jt : JToken.FromObject(obj);
            }
            catch
            {
                output.Write(obj.ToString() ?? "null");
                return;
            }

            WriteRoot(token, output);
        }

        /// <summary>
        /// Корень дерева. Объекты и массивы раскрываются с сортировкой ключей,
        /// любое другое значение на верхнем уровне пишется как есть, без
        /// повторного разбора — так было и раньше.
        /// </summary>
        private static void WriteRoot(JToken token, TextWriter output)
        {
            if (token is JObject || token is JArray)
            {
                var writer = new JsonTextWriter(output)
                {
                    Formatting = Formatting.None,
                    CloseOutput = false,
                    AutoCompleteOnClose = false
                };
                WriteSorted(token, writer);
                writer.Flush();
                return;
            }

            output.Write(token.ToString(Formatting.None));
        }

        /// <summary>
        /// Рекурсивно пишет JToken с лексикографической сортировкой ключей объектов.
        /// Массивы не сортируются — порядок элементов семантически значим.
        /// </summary>
        private static void WriteSorted(JToken token, JsonWriter writer)
        {
            if (token is JObject obj)
            {
                var properties = new List<JProperty>();
                foreach (var property in obj.Properties())
                {
                    // Свойства со значением null отбрасываются. Проект сохраняется
                    // через JsonHelper с NullValueHandling.Ignore — на диске null-полей
                    // нет вовсе. Живой объект (POCO) при JToken.FromObject такие поля
                    // включает как "Field": null. Без этого отсева хеш живых данных
                    // никогда не совпадал с хешем сохранённых (первое расхождение —
                    // Characters[].AvatarPath: null), и модуль вечно считался изменённым:
                    // вкладки открывались в Compare, кеш переписывался на каждом
                    // переключении. Отсев делает "поле = null" и "поля нет" эквивалентными,
                    // как и трактует их формат сохранения.
                    if (property.Value.Type != JTokenType.Null)
                        properties.Add(property);
                }
                properties.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

                writer.WriteStartObject();
                foreach (var property in properties)
                {
                    writer.WritePropertyName(property.Name);
                    WriteChild(property.Value, writer);
                }
                writer.WriteEndObject();
                return;
            }

            if (token is JArray arr)
            {
                writer.WriteStartArray();
                foreach (var item in arr)
                    WriteChild(item, writer);
                writer.WriteEndArray();
                return;
            }

            token.WriteTo(writer);
        }

        /// <summary>
        /// Значение внутри объекта или массива. Вложенные объекты и массивы
        /// раскрываются рекурсивно; значение, которое запись и разбор JSON могут
        /// изменить, пишется в том виде, в каком оно вернулось бы после разбора.
        /// </summary>
        private static void WriteChild(JToken child, JsonWriter writer)
        {
            if (child is JObject || child is JArray)
            {
                WriteSorted(child, writer);
                return;
            }

            if (child is JValue value && IsStableUnderReparse(value))
            {
                value.WriteTo(writer);
                return;
            }

            JToken.Parse(child.ToString(Formatting.None)).WriteTo(writer);
        }

        /// <summary>
        /// Вернёт ли запись значения в JSON и разбор обратно то же самое значение.
        /// Для строк разбор меняет только то, что похоже на дату: читатель JSON
        /// пробует превратить в дату строку вида "/Date(...)/" и строку длиной
        /// 19–40 знаков, начинающуюся с цифры. Остальные строки, целые числа,
        /// логические значения, null и числа double/float возвращаются такими же.
        /// decimal (разбирается как double), даты, массивы байт, Guid и прочие
        /// типы считаются меняющимися — для них выполняется настоящий разбор.
        /// </summary>
        private static bool IsStableUnderReparse(JValue value)
        {
            switch (value.Type)
            {
                case JTokenType.String:
                    if (value.Value is not string s) return false;
                    if (s.Length >= 6 && s[0] == '/' && s.StartsWith("/Date(", StringComparison.Ordinal)) return false;
                    if (s.Length >= 19 && s.Length <= 40 && char.IsDigit(s[0])) return false;
                    return true;

                case JTokenType.Integer:
                    return value.Value is int or long or short or byte or sbyte or ushort or uint or ulong or BigInteger;

                case JTokenType.Float:
                    return value.Value is double or float;

                case JTokenType.Boolean:
                case JTokenType.Null:
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>Поток, который ничего не хранит: всё записанное уходит в хеш.</summary>
        private sealed class HashSinkStream : Stream
        {
            private readonly IncrementalHash _hash;

            public HashSinkStream(IncrementalHash hash) => _hash = hash;

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Write(byte[] buffer, int offset, int count) => _hash.AppendData(buffer, offset, count);
            public override void Write(ReadOnlySpan<byte> buffer) => _hash.AppendData(buffer);
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
