using System.Collections.Generic;
using Writersword.Core.Enums;
using Writersword.Core.Models.WorkModes;
using Writersword.Src.WorkModes.Common;

namespace Writersword.Src.WorkModes.Timeline
{
    /// <summary>
    /// WorkMode "Таймлайн" - работа с временной шкалой событий
    /// Необязательный режим (можно закрыть)
    /// </summary>
    public class TimelineWorkMode : IWorkMode
    {
        public string Id => "timeline";
        public string DisplayName => "Таймлайн";
        public string Icon => "📅";
        public string Description => "Временная шкала событий";
        public bool IsCloseable => true;
        public int Order => 1;

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
                ModuleCategories = new Dictionary<string, ModuleCategory>
                {
                    { "Notes", ModuleCategory.Required }
                },
                ModuleSlots = new List<ModuleSlot>
                {
                    new ModuleSlot
                    {
                        ModuleType = "Notes",
                        PreferredPosition = PreferredDockPosition.Left,
                        Category = ModuleCategory.Required
                    },
                    new ModuleSlot
                    {
                        ModuleType = "Timer",
                        PreferredPosition = PreferredDockPosition.Bottom,
                        Category = ModuleCategory.Optional
                    }
                },
                SerializedDockLayout = null
            };
        }
    }
}