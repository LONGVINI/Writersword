using System.Collections.Generic;
using Writersword.Core.Enums;
using Writersword.Core.Models.WorkModes;
using Writersword.WorkModes.Common;

namespace Writersword.WorkModes.Characters
{
    /// <summary>
    /// WorkMode "Персонажи" — работа с персонажами проекта.
    /// Необязательный режим, можно закрыть.
    /// </summary>
    public class CharactersWorkMode : IWorkMode
    {
        public string Id => "characters";
        public string DisplayName => "Персонажи";
        public string Icon => "👥";
        public string Description => "Управление персонажами, связями и параметрами";
        public bool IsCloseable => true;
        public int Order => 2;

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
                    { "Characters", ModuleCategory.Required }
                },
                ModuleSlots = new List<ModuleSlot>
                {
                    new ModuleSlot
                    {
                        ModuleType = "Characters",
                        PreferredPosition = PreferredDockPosition.Left,
                        Category = ModuleCategory.Required
                    },
                    new ModuleSlot
                    {
                        ModuleType = "Notes",
                        PreferredPosition = PreferredDockPosition.RightAsTab,
                        Category = ModuleCategory.Optional
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
