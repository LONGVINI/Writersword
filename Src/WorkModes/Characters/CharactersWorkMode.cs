using Writersword.Core.Enums;
using Writersword.Core.Models.WorkModes;
using Writersword.Src.WorkModes.Common;
using System.Collections.Generic;

namespace Writersword.Src.WorkModes.Characters
{
    /// <summary>
    /// WorkMode "Персонажи" - управление персонажами
    /// Необязательный режим (можно закрыть)
    /// </summary>
    public class CharactersWorkMode : IWorkMode
    {
        // ===== МЕТАДАННЫЕ (из IWorkModeMetadata) =====

        public string Id => "characters";
        public string DisplayName => "Персонажи";
        public string Icon => "👥";
        public string Description => "Управление персонажами";
        public bool IsCloseable => true;
        public int Order => 2;

        // ===== DEFAULT КОНФИГУРАЦИЯ =====

        /// <summary>
        /// DEFAULT конфигурация Characters режима
        /// Описывает расположение модулей для работы с персонажами
        /// </summary>
        public WorkModeConfig GetDefaultConfig()
        {
            return new WorkModeConfig
            {
                Order = 2,
                ModuleSlots = new List<ModuleSlotConfig>
                {
                    // ОБЯЗАТЕЛЬНЫЙ: Characters - список персонажей
                    new ModuleSlotConfig
                    {
                        ModuleId = "Characters",
                        MinWidth = 400,
                        MinHeight = 400,
                        IsVisible = true,
                        Category = ModuleCategory.Required,
                        PreferredPosition = PreferredDockPosition.RightAsTab
                    },

                    // НЕОБЯЗАТЕЛЬНЫЙ: Relationships - граф связей персонажей
                    new ModuleSlotConfig
                    {
                        ModuleId = "Relationships",
                        MinWidth = 400,
                        MinHeight = 400,
                        IsVisible = true,
                        Category = ModuleCategory.Optional,
                        PreferredPosition = PreferredDockPosition.RightAsTab
                    },

                    // НЕОБЯЗАТЕЛЬНЫЙ: Notes - заметки о персонажах
                    new ModuleSlotConfig
                    {
                        ModuleId = "Notes",
                        MinWidth = 300,
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