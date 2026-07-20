using System.Collections.Generic;

namespace Writersword.Modules.Characters.Models
{
    /// <summary>
    /// Данные модуля персонажей — сохраняются в ZIP проекта.
    /// </summary>
    public class CharactersModuleData
    {
        public int SchemaVersion { get; set; } = 1;

        /// <summary>
        /// Первый запуск модуля в этом проекте.
        /// При true — показывается онбординг при открытии.
        /// Хранится в данных проекта (не глобально).
        /// </summary>
        public bool IsFirstLaunch { get; set; } = true;

        /// <summary>Id активных шаблонов — применяются к каждому новому персонажу</summary>
        public List<string> ActiveTemplateIds { get; set; } = new();

        public List<Character> Characters { get; set; } = new();
        public List<CharacterRelationship> Relationships { get; set; } = new();
        public List<CharacterAnketa> CustomAnketas { get; set; } = new();
        public List<CharacterFolder> Folders { get; set; } = new();
    }

    /// <summary>
    /// Сессионное состояние — восстанавливается при повторном открытии вкладки.
    /// </summary>
    public class CharactersModuleSession
    {
        public string? LastOpenedCharacterId { get; set; }
        public string LastViewMode { get; set; } = "List";
        public int MainTabIndex { get; set; } = 0;
        public List<string> ActiveTagFilters { get; set; } = new();
        public string LastSearchQuery { get; set; } = string.Empty;
        public List<string> ActiveTemplateIds { get; set; } = new();
        public double GraphOffsetX { get; set; } = 0;
        public double GraphOffsetY { get; set; } = 0;
        public double GraphScale { get; set; } = 1.0;

        /// <summary>Ширина бокового списка во вкладке Character Editor (px).</summary>
        public double EditorSidebarWidth { get; set; } = 240;

        /// <summary>Режим бокового списка Редактора: 0 — полный, 1 — только аватарки, 2 — скрыт.</summary>
        public int EditorSidebarMode { get; set; } = 0;
    }
}