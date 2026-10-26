using System.Collections.Generic;

namespace Writersword.Core.Interfaces.Services
{
    /// <summary>
    /// Интерфейс сервиса сравнения данных модулей
    /// </summary>
    public interface IDataComparisonService
    {
        /// <summary>
        /// Сравнить два словаря CustomData
        /// Используется для проверки несохранённых изменений
        /// </summary>
        bool AreDataEqual(
            Dictionary<string, object?>? data1,
            Dictionary<string, object?>? data2);

        /// <summary>
        /// Описать первое структурное расхождение двух значений (путь и оба значения)
        /// или вернуть null, если расхождения в структуре нет.
        /// Только для диагностики.
        /// </summary>
        string? DescribeFirstDifference(object? live, object? saved);
    }
}