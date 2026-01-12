using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Models.Modules;

namespace Writersword.Src.Infrastructure.Services
{
    /// <summary>
    /// Реализация сервиса сравнения данных модулей
    /// </summary>
    public class DataComparisonService : IDataComparisonService
    {
        /// <summary>
        /// Сравнить два словаря состояний модулей
        /// ОПТИМИЗИРОВАНО: early exit при первом различии, проверка с конца
        /// </summary>
        public bool AreStatesEqual(
            Dictionary<string, ModuleState>? states1,
            Dictionary<string, ModuleState>? states2)
        {
            // Если оба null - одинаковые
            if (states1 == null && states2 == null)
                return true;

            // Если один null - разные
            if (states1 == null || states2 == null)
                return false;

            // БЫСТРАЯ ПРОВЕРКА: разное количество - разные
            if (states1.Count != states2.Count)
            {
                Console.WriteLine($"[DataComparison] Different count: {states1.Count} vs {states2.Count}");
                return false;
            }

            // Сравниваем каждый модуль С КОНЦА (как запросил пользователь)
            // Reverse() создаёт копию, поэтому используем обратный цикл
            var keys = states1.Keys.ToList();

            for (int i = keys.Count - 1; i >= 0; i--)
            {
                var key = keys[i];

                // Если модуль отсутствует во втором словаре - разные
                if (!states2.TryGetValue(key, out var state2))
                {
                    Console.WriteLine($"[DataComparison] Module missing in cache: {key}");
                    return false;
                }

                var state1 = states1[key];

                // Сравниваем ТОЛЬКО CustomData (основные данные)
                // EARLY EXIT: при первом различии сразу выходим
                if (!AreCustomDataEqual(state1.CustomData, state2.CustomData))
                {
                    Console.WriteLine($"[DataComparison] CustomData differs for module: {key}");
                    return false;
                }
            }

            Console.WriteLine($"[DataComparison] All {states1.Count} modules are identical");
            return true;
        }

        /// <summary>Сравнить два словаря CustomData</summary>
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

            // Если разное количество - разные
            if (data1.Count != data2.Count)
                return false;

            // Сравниваем каждый ключ
            foreach (var kvp in data1)
            {
                if (!data2.TryGetValue(kvp.Key, out var value2))
                    return false;

                if (!AreCustomDataEqual(kvp.Value, value2))
                    return false;
            }

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