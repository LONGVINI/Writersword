using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Core.Interfaces.Services;

namespace Writersword.Src.Infrastructure.Services
{
    /// <summary>
    /// Реализация сервиса сравнения данных модулей
    /// Поддерживает два режима:
    /// - Simple: хеширование всего объекта (быстрое сравнение)
    /// - Delta: сравнение хешей частей (для больших документов)
    /// </summary>
    public class DataComparisonService : IDataComparisonService
    {
        private readonly ILogger<DataComparisonService> _logger;
        private readonly IHashService _hashService;

        /// <summary>
        /// Конструктор сервиса сравнения данных
        /// </summary>
        /// <param name="hashService">Сервис хеширования для оптимизации сравнений</param>
        public DataComparisonService(IHashService hashService)
        {
            _logger = App.Services.GetService<ILogger<DataComparisonService>>()!;
            _hashService = hashService;
        }

        /// <summary>
        /// Сравнить два словаря CustomData
        /// ОПТИМИЗИРОВАНО: early exit при первом различии
        /// </summary>
        public bool AreDataEqual(
            Dictionary<string, object?>? data1,
            Dictionary<string, object?>? data2)
        {
            // Если оба null - одинаковые
            if (data1 == null && data2 == null)
                return true;

            // Если один null - разные
            if (data1 == null || data2 == null)
                return false;

            // БЫСТРАЯ ПРОВЕРКА: разное количество - разные
            if (data1.Count != data2.Count)
            {
                _logger.LogDebug("Different count: {Count1} vs {Count2}", data1.Count, data2.Count);
                return false;
            }

            // Сравниваем каждый ключ
            // EARLY EXIT: при первом различии сразу выходим
            foreach (var kvp in data1)
            {
                if (!data2.TryGetValue(kvp.Key, out var value2))
                {
                    _logger.LogDebug("Module missing: {ModuleKey}", kvp.Key);
                    return false;
                }

                if (!AreCustomDataEqual(kvp.Value, value2))
                {
                    _logger.LogDebug("CustomData differs for module: {ModuleKey}", kvp.Key);
                    return false;
                }
            }

            _logger.LogDebug("All {Count} modules are identical", data1.Count);
            return true;
        }

        /// <summary>
        /// Сравнить CustomData двух модулей
        /// Автоматически определяет режим сравнения (Simple или Delta)
        /// </summary>
        private bool AreCustomDataEqual(object? data1, object? data2)
        {
            // Оба null - одинаковые
            if (data1 == null && data2 == null)
                return true;

            // Один null - разные
            if (data1 == null || data2 == null)
                return false;

            // ПРОВЕРКА: Delta режим?
            if (IsDeltaMode(data1) && IsDeltaMode(data2))
            {
                _logger.LogDebug("Using Delta mode comparison");
                return CompareDeltaData(data1, data2);
            }

            // SIMPLE РЕЖИМ: хешируем весь объект целиком
            var hash1 = _hashService.ComputeHash(data1);
            var hash2 = _hashService.ComputeHash(data2);

            bool areEqual = hash1 == hash2;

            if (!areEqual)
            {
                _logger.LogDebug("Hash mismatch (Simple mode)");
            }

            return areEqual;
        }

        /// <summary>
        /// Проверить является ли объект данными в Delta режиме
        /// Delta режим определяется наличием маркера "__deltaMode": true
        /// </summary>
        private bool IsDeltaMode(object? data)
        {
            return data is Dictionary<string, object> dict &&
                   dict.TryGetValue("__deltaMode", out var mode) &&
                   mode is true;
        }

        /// <summary>
        /// Сравнить данные в Delta режиме
        /// Сравниваются хеши ЧАСТЕЙ, а не хеш всего объекта
        /// </summary>
        private bool CompareDeltaData(object? data1, object? data2)
        {
            var dict1 = (Dictionary<string, object>)data1!;
            var dict2 = (Dictionary<string, object>)data2!;

            // Получаем все ключи частей (кроме служебного __deltaMode)
            var parts1 = dict1.Keys.Where(k => k != "__deltaMode").ToHashSet();
            var parts2 = dict2.Keys.Where(k => k != "__deltaMode").ToHashSet();

            // БЫСТРАЯ ПРОВЕРКА: разное количество частей?
            if (parts1.Count != parts2.Count)
            {
                _logger.LogDebug("Different parts count: {Count1} vs {Count2}", parts1.Count, parts2.Count);
                return false;
            }

            // Сравниваем хеши КАЖДОЙ части
            foreach (var partKey in parts1)
            {
                // Проверяем что часть есть в обоих объектах
                if (!parts2.Contains(partKey))
                {
                    _logger.LogDebug("Part missing: {PartKey}", partKey);
                    return false;
                }

                // Получаем объекты частей
                var part1 = dict1[partKey] as Dictionary<string, object>;
                var part2 = dict2[partKey] as Dictionary<string, object>;

                if (part1 == null || part2 == null)
                {
                    _logger.LogDebug("Invalid part structure: {PartKey}", partKey);
                    return false;
                }

                // Сравниваем ХЕШИ частей (НЕ текст!)
                var hash1 = part1.TryGetValue("hash", out var h1) ? h1?.ToString() : null;
                var hash2 = part2.TryGetValue("hash", out var h2) ? h2?.ToString() : null;

                if (hash1 != hash2)
                {
                    _logger.LogDebug("Part hash differs: {PartKey} ({Hash1} vs {Hash2})", partKey, hash1, hash2);
                    return false;
                }
            }

            _logger.LogDebug("All {Count} parts identical (Delta mode)", parts1.Count);
            return true;
        }
    }
}