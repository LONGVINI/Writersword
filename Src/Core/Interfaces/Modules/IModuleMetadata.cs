using System.Collections.Generic;
using Writersword.Core.Enums;

namespace Writersword.Core.Interfaces.Modules
{
    /// <summary>
    /// Метаданные модуля - описание для UI
    /// </summary>
    public interface IModuleMetadata
    {
        /// <summary>Тип модуля</summary>
        ModuleType ModuleType { get; }

        /// <summary>Отображаемое имя</summary>
        string DisplayName { get; }

        /// <summary>Иконка (Unicode emoji или путь)</summary>
        string Icon { get; }

        /// <summary>Краткое описание</summary>
        string Description { get; }

        /// <summary>Универсальный модуль (доступен во всех WorkMode)</summary>
        bool IsUniversal { get; }

        /// <summary>В каких WorkMode доступен (если не универсальный)</summary>
        List<WorkModeType> AvailableInWorkModes { get; }
    }
}