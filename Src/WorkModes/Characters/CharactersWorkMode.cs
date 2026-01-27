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
        public WorkMode GetDefaultConfig()
        {
            return new WorkMode
            {
                WorkModeId = Id,
                Title = DisplayName,
                Icon = Icon,
                IsActive = false,
                Order = Order,
                IsCloseable = IsCloseable,

                ModuleSlots = new List<ModuleSlot>
                {
                    new ModuleSlot
                    {
                        ModuleId = "Characters",
                        ContainerId = "Main",
                        IsFloating = false,
                        TabOrder = 0,
                        IsActiveTab = true,
                        IsCloseable = false,
                        MinWidth = 400,
                        MinHeight = 400,
                        PreferredPosition = PreferredDockPosition.RightAsTab
                    },

                    new ModuleSlot
                    {
                        ModuleId = "Relationships",
                        ContainerId = "Main",
                        IsFloating = false,
                        TabOrder = 1,
                        IsActiveTab = false,
                        IsCloseable = true,
                        MinWidth = 400,
                        MinHeight = 400,
                        PreferredPosition = PreferredDockPosition.RightAsTab
                    }
                },

                Containers = new List<SplitContainer>
                {
                    new SplitContainer
                    {
                        Id = "Main",
                        Proportion = 1.0,
                        Orientation = null,
                        Children = null
                    }
                }
            };
        }
    }
}