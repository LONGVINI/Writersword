using System.Collections.Generic;
using Writersword.Core.Models.Modules;

namespace Writersword.Core.Interfaces.Services
{
    /// <summary>
    /// Сервис сравнения данных модулей
    /// Используется для проверки изменений перед сохранением
    /// </summary>
    public interface IDataComparisonService
    {
        /// <summary>Сравнить два словаря состояний модулей</summary>
        bool AreStatesEqual(
            Dictionary<string, ModuleState>? states1,
            Dictionary<string, ModuleState>? states2);

        /// <summary>Сравнить два словаря CustomData</summary>
        bool AreDataEqual(
            Dictionary<string, object?>? data1,
            Dictionary<string, object?>? data2);
    }
}