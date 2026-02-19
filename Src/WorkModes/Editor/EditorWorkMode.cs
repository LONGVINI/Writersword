using System.Collections.Generic;
using Writersword.Core.Enums;
using Writersword.Core.Models.WorkModes;
using Writersword.Src.WorkModes.Common;

namespace Writersword.Src.WorkModes.Editor
{
    /// <summary>
    /// WorkMode "Редактор" - основной режим работы с текстом
    /// Обязательный режим (нельзя закрыть)
    /// </summary>
    public class EditorWorkMode : IWorkMode
    {
        public string Id => "editor";
        public string DisplayName => "Редактор";
        public string Icon => "📝";
        public string Description => "Основной редактор текста";
        public bool IsCloseable => false;
        public int Order => 0;

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
                    { "TextEditor", ModuleCategory.Required },
                    { "CharacterTracker", ModuleCategory.Unwanted },
                    { "PlotStructure", ModuleCategory.Unwanted },
                    { "Timeline", ModuleCategory.Forbidden },
                    { "GameMechanics", ModuleCategory.Forbidden }
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
                        ModuleType = "Timer",
                        PreferredPosition = PreferredDockPosition.BottomRight,
                        Category = ModuleCategory.Optional
                    }
                },
                SerializedDockLayout = null
            };
        }
    }
}