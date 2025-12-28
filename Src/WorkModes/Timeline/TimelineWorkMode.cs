using Writersword.Core.Enums;
using Writersword.Core.Models.WorkModes;
using Writersword.Src.WorkModes.Common;
using System.Collections.Generic;

namespace Writersword.Src.WorkModes.Timeline
{
    /// <summary>
    /// WorkMode "Таймлайн" - работа с временной шкалой событий
    /// Необязательный режим (можно закрыть)
    /// </summary>
    public class TimelineWorkMode : IWorkMode
    {
        // ===== МЕТАДАННЫЕ (из IWorkModeMetadata) =====

        public string Id => "timeline";
        public string DisplayName => "Таймлайн";
        public string Icon => "📅";
        public string Description => "Временная шкала событий";
        public bool IsCloseable => true;
        public int Order => 1;

        // ===== DEFAULT КОНФИГУРАЦИЯ =====

        /// <summary>
        /// DEFAULT конфигурация Timeline режима
        /// Описывает расположение модулей для работы с событиями
        /// </summary>
        public WorkModeConfig GetDefaultConfig()
        {
            return new WorkModeConfig
            {
                Order = 1,
                ModuleSlots = new List<ModuleSlotConfig>
                {
                    // ОБЯЗАТЕЛЬНЫЙ: Timeline - визуализация временной шкалы
                    new ModuleSlotConfig
                    {
                        ModuleType = ModuleType.Timeline,
                        MinWidth = 500,
                        MinHeight = 400,
                        IsVisible = true,
                        Category = ModuleCategory.Required,
                        PreferredPosition = PreferredDockPosition.RightAsTab
                    },

                    // НЕОБЯЗАТЕЛЬНЫЙ: Characters - список персонажей
                    new ModuleSlotConfig
                    {
                        ModuleType = ModuleType.Characters,
                        MinWidth = 250,
                        MinHeight = 300,
                        IsVisible = true,
                        Category = ModuleCategory.Optional,
                        PreferredPosition = PreferredDockPosition.RightAsTab
                    },

                    // НЕОБЯЗАТЕЛЬНЫЙ: Notes - заметки к событиям
                    new ModuleSlotConfig
                    {
                        ModuleType = ModuleType.Notes,
                        MinWidth = 250,
                        MinHeight = 200,
                        IsVisible = false,
                        Category = ModuleCategory.Optional,
                        PreferredPosition = PreferredDockPosition.RightAsTab
                    }
                }
            };
        }
    }
}