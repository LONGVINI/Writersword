namespace Writersword.Core.Interfaces.Modules
{
    /// <summary>
    /// Метаданные модуля (название, иконка, описание)
    /// Используется для отображения в UI и регистрации в системе
    /// </summary>
    public interface IModuleMetadata
    {
        /// <summary>Уникальный идентификатор модуля (например: "TextEditor", "Timer")</summary>
        string ModuleId { get; }

        /// <summary>Отображаемое название модуля</summary>
        string DisplayName { get; }

        /// <summary>Иконка модуля (emoji или символ)</summary>
        string Icon { get; }

        /// <summary>Описание модуля</summary>
        string Description { get; }

        /// <summary>Универсальный ли модуль (доступен во всех WorkModes)</summary>
        bool IsUniversal { get; }
    }
}