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
        /// <summary>Идентификатор WorkMode</summary>
        public string Id => "editor";

        /// <summary>Отображаемое название</summary>
        public string DisplayName => "Редактор";

        /// <summary>Иконка</summary>
        public string Icon => "📝";

        /// <summary>Описание</summary>
        public string Description => "Основной редактор текста";

        /// <summary>Можно ли закрыть этот WorkMode</summary>
        public bool IsCloseable => false;

        /// <summary>Порядок отображения</summary>
        public int Order => 0;

        /// <summary>
        /// Дефолтная конфигурация Editor режима
        /// Определяет правила работы с модулями
        /// Модули НЕ указанные в ModuleCategories = Optional по умолчанию
        /// </summary>
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
                        Path = null,
                        IsFloating = false,
                        TabOrder = 0,
                        IsActiveTab = true,
                        IsCloseable = false,
                        IsCurrentlyOpen = true,
                        MinWidth = 400,
                        MinHeight = 300,
                        PreferredPosition = PreferredDockPosition.RightAsTab,
                        Category = ModuleCategory.Required
                    },

                    new ModuleSlot
                    {
                        ModuleType = "Synonyms",
                        Path = null,
                        IsFloating = false,
                        TabOrder = 0,
                        IsActiveTab = true,
                        IsCloseable = true,
                        IsCurrentlyOpen = true,
                        MinWidth = 250,
                        MinHeight = 200,
                        PreferredPosition = PreferredDockPosition.TopRight,
                        Category = ModuleCategory.Optional
                    },

                    new ModuleSlot
                    {
                        ModuleType = "Timer",
                        Path = null,
                        IsFloating = false,
                        TabOrder = 0,
                        IsActiveTab = true,
                        IsCloseable = true,
                        IsCurrentlyOpen = true,
                        MinWidth = 200,
                        MinHeight = 150,
                        PreferredPosition = PreferredDockPosition.BottomRight,
                        Category = ModuleCategory.Optional
                    }
                },

                LayoutTree = null
            };
        }
    }
}
