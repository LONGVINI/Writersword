using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using Writersword.Core.Interfaces.Services;

namespace Writersword.Infrastructure.Services
{
    /// <summary>
    /// Реализация сервиса сравнения данных модулей.
    /// Поддерживает два режима:
    /// - Simple: хеш через HashService (детерминированный, с сортировкой ключей)
    /// - Delta: сравнение хешей частей (для больших документов)
    ///
    /// HashService.ComputeHash гарантирует одинаковый хеш для одинаковых данных
    /// независимо от типа объекта (POCO, JObject, string-с-JSON) и порядка ключей.
    /// </summary>
    public class DataComparisonService : IDataComparisonService
    {
        private readonly ILogger<DataComparisonService> _logger;
        private readonly IHashService _hashService;

        public DataComparisonService(IHashService hashService)
        {
            _logger = App.Services.GetService<ILogger<DataComparisonService>>()!;
            _hashService = hashService;
        }

        /// <summary>
        /// Сравнить два словаря CustomData.
        /// Early exit при первом различии.
        /// </summary>
        public bool AreDataEqual(
            Dictionary<string, object?>? data1,
            Dictionary<string, object?>? data2)
        {
            if (data1 == null && data2 == null) return true;
            if (data1 == null || data2 == null) return false;

            if (data1.Count != data2.Count)
            {
                _logger.LogDebug("Different count: {Count1} vs {Count2}", data1.Count, data2.Count);
                return false;
            }

            foreach (var kvp in data1)
            {
                if (!data2.TryGetValue(kvp.Key, out var value2))
                {
                    _logger.LogDebug("Module missing: {ModuleKey}", kvp.Key);
                    return false;
                }

                if (!AreValuesEqual(kvp.Value, value2))
                {
                    _logger.LogDebug("CustomData differs for module: {ModuleKey}", kvp.Key);
                    return false;
                }
            }

            _logger.LogDebug("All {Count} modules are identical", data1.Count);
            return true;
        }

        private bool AreValuesEqual(object? value1, object? value2)
        {
            if (value1 == null && value2 == null) return true;
            if (value1 == null || value2 == null) return false;

            if (IsDeltaMode(value1) && IsDeltaMode(value2))
            {
                _logger.LogDebug("Using Delta mode comparison");
                return CompareDeltaData(value1, value2);
            }

            var hash1 = _hashService.ComputeHash(value1);
            var hash2 = _hashService.ComputeHash(value2);
            bool equal = hash1 == hash2;

            if (!equal)
                _logger.LogDebug("Hash mismatch");

            return equal;
        }

        private static bool IsDeltaMode(object? data)
        {
            if (data is System.Collections.Generic.Dictionary<string, object> dict &&
                dict.TryGetValue("__deltaMode", out var mode) && mode is true)
                return true;

            if (data is Newtonsoft.Json.Linq.JObject jObj &&
                jObj.TryGetValue("__deltaMode", out var token) &&
                token.Type == Newtonsoft.Json.Linq.JTokenType.Boolean &&
                token.ToObject<bool>())
                return true;

            return false;
        }

        private bool CompareDeltaData(object? data1, object? data2)
        {
            var parts1 = ExtractDeltaParts(data1);
            var parts2 = ExtractDeltaParts(data2);

            if (parts1.Count != parts2.Count)
            {
                _logger.LogDebug("Different parts count: {Count1} vs {Count2}", parts1.Count, parts2.Count);
                return false;
            }

            foreach (var (key, hash1) in parts1)
            {
                if (!parts2.TryGetValue(key, out var hash2))
                {
                    _logger.LogDebug("Part missing: {PartKey}", key);
                    return false;
                }

                if (hash1 != hash2)
                {
                    _logger.LogDebug("Part hash differs: {PartKey}", key);
                    return false;
                }
            }

            _logger.LogDebug("All {Count} parts identical (Delta mode)", parts1.Count);
            return true;
        }

        private static Dictionary<string, string?> ExtractDeltaParts(object? data)
        {
            var result = new Dictionary<string, string?>(System.StringComparer.Ordinal);

            Newtonsoft.Json.Linq.JObject? jObj = data is Newtonsoft.Json.Linq.JObject j ? j
                : data is System.Collections.Generic.Dictionary<string, object> d
                    ? Newtonsoft.Json.Linq.JObject.FromObject(d)
                    : null;

            if (jObj == null) return result;

            foreach (var prop in jObj.Properties())
            {
                if (prop.Name == "__deltaMode") continue;
                var hash = prop.Value is Newtonsoft.Json.Linq.JObject partObj
                    && partObj.TryGetValue("hash", out var h)
                        ? h.ToObject<string>()
                        : null;
                result[prop.Name] = hash;
            }

            return result;
        }
    }
}