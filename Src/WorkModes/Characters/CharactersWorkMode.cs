using System.Collections.Generic;
using Writersword.Core.Enums;
using Writersword.Core.Models.WorkModes;
using Writersword.Src.WorkModes.Common;

namespace Writersword.Src.WorkModes.Characters
{
    /// <summary>
    /// WorkMode "Персонажи" - управление персонажами
    /// Необязательный режим (можно закрыть)
    /// </summary>
    public class CharactersWorkMode : IWorkMode
    {
        public string Id => "characters";
        public string DisplayName => "Персонажи";
        public string Icon => "👥";
        public string Description => "Управление персонажами";
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
                    { "TextEditor", ModuleCategory.Required }
                },
                ModuleSlots = new List<ModuleSlot>
                {
                    new ModuleSlot
                    {
                        ModuleType = "TextEditor",
                        PreferredPosition = PreferredDockPosition.RightAsTab,
                        Category = ModuleCategory.Required
                    },
                    new ModuleSlot
                    {
                        ModuleType = "Synonyms",
                        PreferredPosition = PreferredDockPosition.TopRight,
                        Category = ModuleCategory.Optional
                    },
                    new ModuleSlot
                    {
                        ModuleType = "Notes",
                        PreferredPosition = PreferredDockPosition.BottomRight,
                        Category = ModuleCategory.Optional
                    }
                },
                SerializedDockLayout = null
            };
        }
    }
}