using Writersword.Core.Enums;

namespace Writersword.Core.Interfaces.Modules
{
    /// <summary>
    /// Метаданные модуля - описание для UI и конфигурации
    /// </summary>
    public interface IModuleMetadata
    {
        /// <summary>Тип модуля (из enum)</summary>
        ModuleType ModuleType { get; }

        /// <summary>Отображаемое имя модуля</summary>
        string DisplayName { get; }

        /// <summary>Иконка модуля (Unicode emoji)</summary>
        string Icon { get; }

        /// <summary>Краткое описание модуля</summary>
        string Description { get; }

        /// <summary>
        /// Универсальный модуль (доступен во всех WorkMode по умолчанию)
        /// Например: Notes, Timer - полезны везде
        /// </summary>
        bool IsUniversal { get; }

        /// <summary>Позиция по умолчанию (если WorkMode не переопределил)</summary>
        PreferredDockPosition DefaultPosition { get; }
    }
}