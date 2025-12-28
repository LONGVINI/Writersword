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
        /// Эта конфигурация hardcoded в коде и используется когда нет GLOBAL или PROJECT настроек
        /// Возвращает расположение модулей по умолчанию и их категории
        /// </summary>
        WorkModeConfig GetDefaultConfig();
    }
}