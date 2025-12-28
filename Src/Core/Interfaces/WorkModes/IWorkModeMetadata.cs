namespace Writersword.Core.Interfaces.WorkModes
{
    /// <summary>
    /// Метаданные WorkMode - описание для UI
    /// </summary>
    public interface IWorkModeMetadata
    {
        /// <summary>Уникальный ID режима (например: "editor", "timeline")</summary>
        string Id { get; }

        /// <summary>Отображаемое имя</summary>
        string DisplayName { get; }

        /// <summary>Иконка (Unicode emoji)</summary>
        string Icon { get; }

        /// <summary>Краткое описание</summary>
        string Description { get; }

        /// <summary>Можно ли закрыть этот WorkMode</summary>
        bool IsCloseable { get; }

        /// <summary>Порядок отображения в UI</summary>
        int Order { get; }
    }
}