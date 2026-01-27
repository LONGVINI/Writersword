using Writersword.Core.Interfaces.WorkModes;
using Writersword.Core.Models.WorkModes;

namespace Writersword.Src.WorkModes.Common
{
    /// <summary>
    /// Базовый интерфейс для всех WorkMode
    /// Наследует IWorkModeMetadata и добавляет метод для получения DEFAULT конфигурации
    /// </summary>
    public interface IWorkMode : IWorkModeMetadata
    {
        /// <summary>
        /// Получить DEFAULT конфигурацию для этого WorkMode
        /// Возвращает полностью настроенный WorkMode с модулями и структурой layout
        /// Используется когда нет GLOBAL или LOCAL настроек
        /// </summary>
        WorkMode GetDefaultConfig();
    }
}