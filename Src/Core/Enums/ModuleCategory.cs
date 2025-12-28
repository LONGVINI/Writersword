namespace Writersword.Core.Enums
{
    /// <summary>
    /// Категория модуля в WorkMode - определяет можно ли его удалить/добавить
    /// </summary>
    public enum ModuleCategory
    {
        /// <summary>Обязательный - НЕЛЬЗЯ удалить (например TextEditor в Editor)</summary>
        Required,

        /// <summary>Необязательный - можно удалить/добавить (например Timer в Editor)</summary>
        Optional,

        /// <summary>Нежелательный - можно добавить, но зачем? (показывается ниже в списке)</summary>
        Unwanted,

        /// <summary>Недопустимый - НЕЛЬЗЯ добавить в этот WorkMode (зарезервировано на будущее)</summary>
        Forbidden
    }
}