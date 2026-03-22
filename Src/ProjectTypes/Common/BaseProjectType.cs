using System.Collections.Generic;

namespace Writersword.ProjectTypes.Common
{
    /// <summary>
    /// Базовый класс для всех типов проектов
    /// Каждый тип проекта наследует этот класс
    /// </summary>
    public abstract class BaseProjectType
    {
        /// <summary>Уникальный идентификатор типа проекта</summary>
        public abstract string Id { get; }

        /// <summary>Локализованное название</summary>
        public abstract string DisplayName { get; }

        /// <summary>Иконка типа проекта</summary>
        public abstract string Icon { get; }

        /// <summary>Список WorkMode ID по порядку</summary>
        public abstract List<string> WorkModes { get; }
    }
}