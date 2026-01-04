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
        // ===== МЕТАДАННЫЕ (из IWorkModeMetadata) =====

        public string Id => "editor";
        public string DisplayName => "Редактор";
        public string Icon => "📝";
        public string Description => "Основной редактор текста";
        public bool IsCloseable => false;
        public int Order => 0;
        public DockLayoutConfig DockLayout { get; set; } = new();

        // ===== DEFAULT КОНФИГУРАЦИЯ =====

        /// <summary>
        /// DEFAULT конфигурация Editor режима
        /// Описывает расположение модулей и их категории по умолчанию
        /// </summary>
        public WorkModeConfig GetDefaultConfig()
        {
            return new WorkModeConfig
            {
                Order = 0,
                ModuleSlots = new List<ModuleSlotConfig>
                 {
                    // ОБЯЗАТЕЛЬНЫЙ: TextEditor
                    new ModuleSlotConfig
                    {
                        ModuleId = "TextEditor",
                        IsVisible = true,
                        Category = ModuleCategory.Required,
                        MinWidth = 400,
                        MinHeight = 300,
                        PreferredPosition = PreferredDockPosition.RightAsTab
                    },

                    // НЕОБЯЗАТЕЛЬНЫЙ: Synonyms
                    new ModuleSlotConfig
                    {
                        ModuleId = "Synonyms",
                        IsVisible = true,
                        Category = ModuleCategory.Optional,
                        MinWidth = 250,
                        MinHeight = 200,
                        PreferredPosition = PreferredDockPosition.RightAsTab
                    },

                    // НЕОБЯЗАТЕЛЬНЫЙ: Timer
                    new ModuleSlotConfig
                    {
                        ModuleId = "Timer",
                        IsVisible = true,
                        Category = ModuleCategory.Optional,
                        MinWidth = 200,
                        MinHeight = 150,
                        PreferredPosition = PreferredDockPosition.RightAsTab
                    },

                    // НЕОБЯЗАТЕЛЬНЫЙ: Notes (скрыт)
                    new ModuleSlotConfig
                    {
                        ModuleId = "Notes",
                        IsVisible = false,
                        Category = ModuleCategory.Optional,
                        MinWidth = 200,
                        MinHeight = 200,
                        PreferredPosition = PreferredDockPosition.RightAsTab
                    }
                },

                // DEFAULT Dock-раскладка для Editor
                DockLayout = new DockLayoutConfig
                {
                    MainOrientation = DockOrientation.Horizontal,
                    Panels = new List<DockPanelConfig>
                    {
                        // Левая панель - TextEditor (70% ширины)
                        new DockPanelConfig
                        {
                            Id = "LeftPanel",
                            Proportion = 0.7,
                            Modules = new List<string> { "TextEditor" }
                        },

                        // Правая панель - Synonyms и Timer столбиком (30% ширины)
                        new DockPanelConfig
                        {
                            Id = "RightPanel",
                            Proportion = 0.3,
                            Modules = new List<string>(), // Пустой список - будет nested
                            NestedLayout = new DockLayoutConfig
                            {
                                MainOrientation = DockOrientation.Vertical,
                                Panels = new List<DockPanelConfig>
                                {
                                    new DockPanelConfig
                                    {
                                        Id = "RightTop",
                                        Proportion = 0.5,
                                        Modules = new List<string> { "Synonyms" }
                                    },
                                    new DockPanelConfig
                                    {
                                        Id = "RightBottom",
                                        Proportion = 0.5,
                                        Modules = new List<string> { "Timer" }
                                    }
                                }
                            }
                        }
                    }
                }
            };
        }
    }
}