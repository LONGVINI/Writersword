namespace Writersword.Core.Enums
{
    /// <summary>
    /// Типы режимов работы (WorkMode)
    /// Это кнопки под главной вкладкой документа: "Редактор", "Таймлайн", "Персонажи"
    /// </summary>
    public enum WorkModeType
    {
        /// <summary>Режим редактора - основной (всегда есть)</summary>
        Editor = 0,

        /// <summary>Режим таймлайна событий</summary>
        Timeline = 1,

        /// <summary>Режим работы с персонажами</summary>
        Characters = 2,

        /// <summary>Режим сюжетных точек</summary>
        PlotPoints = 3,

        /// <summary>Режим локаций</summary>
        Locations = 4,

        /// <summary>Режим карт (геймдизайн)</summary>
        Maps = 5,

        /// <summary>Режим игровой экономики</summary>
        GameEconomy = 6,

        /// <summary>Режим диалогов (сценарии)</summary>
        Dialogues = 7,

        /// <summary>Режим ветвления сюжета</summary>
        Branching = 8,

        /// <summary>Режим поэзии</summary>
        Poetry = 9,

        /// <summary>Кастомный режим (плагин)</summary>
        Custom = 1000
    }
}