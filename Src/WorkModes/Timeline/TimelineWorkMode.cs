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
                        ModuleId = "Timeline",
                        ContainerId = "Main",
                        IsFloating = false,
                        TabOrder = 0,
                        IsActiveTab = true,
                        IsCloseable = false,
                        MinWidth = 500,
                        MinHeight = 400,
                        PreferredPosition = PreferredDockPosition.RightAsTab
                    },

                    new ModuleSlot
                    {
                        ModuleId = "Characters",
                        ContainerId = "Main",
                        IsFloating = false,
                        TabOrder = 1,
                        IsActiveTab = false,
                        IsCloseable = true,
                        MinWidth = 250,
                        MinHeight = 300,
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