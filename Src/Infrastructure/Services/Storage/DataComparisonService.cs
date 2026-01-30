using System;
using System.Collections.Generic;
using Writersword.Core.Interfaces.Services;

namespace Writersword.Src.Infrastructure.Services
{
    /// <summary>
    /// Реализация сервиса сравнения данных модулей
    /// </summary>
    public class DataComparisonService : IDataComparisonService
    {
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
                Console.WriteLine($"[DataComparison] Different count: {data1.Count} vs {data2.Count}");
                return false;
            }

            // Сравниваем каждый ключ
            // EARLY EXIT: при первом различии сразу выходим
            foreach (var kvp in data1)
            {
                if (!data2.TryGetValue(kvp.Key, out var value2))
                {
                    Console.WriteLine($"[DataComparison] Module missing: {kvp.Key}");
                    return false;
                }

                if (!AreCustomDataEqual(kvp.Value, value2))
                {
                    Console.WriteLine($"[DataComparison] CustomData differs for module: {kvp.Key}");
                    return false;
                }
            }

            Console.WriteLine($"[DataComparison] All {data1.Count} modules are identical");
            return true;
        }

        /// <summary>
        /// Сравнить CustomData двух модулей
        /// ОПТИМИЗИРОВАНО: для строк сначала проверяем длину
        /// </summary>
        private bool AreCustomDataEqual(object? data1, object? data2)
        {
            // Оба null - одинаковые
            if (data1 == null && data2 == null)
                return true;

            // Один null - разные
            if (data1 == null || data2 == null)
                return false;

            // Если оба string - ОПТИМИЗИРОВАННОЕ сравнение
            if (data1 is string str1 && data2 is string str2)
            {
                // БЫСТРАЯ ПРОВЕРКА: разная длина - разные строки
                if (str1.Length != str2.Length)
                    return false;

                // Сравниваем содержимое
                return str1 == str2;
            }

            // Для остальных типов - простое сравнение
            return data1.Equals(data2);
        }
    }
}