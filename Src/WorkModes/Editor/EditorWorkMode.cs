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

        // ===== DEFAULT КОНФИГУРАЦИЯ =====

        /// <summary>
        /// DEFAULT конфигурация Editor режима
        /// Возвращает полностью настроенный WorkMode с модулями и структурой layout
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

                // Список модулей с расположением
                ModuleSlots = new List<ModuleSlot>
                {
                    // TextEditor - левая панель
                    new ModuleSlot
                    {
                        ModuleId = "TextEditor",
                        ContainerId = "LeftPanel",
                        IsFloating = false,
                        TabOrder = 0,
                        IsActiveTab = true,
                        IsCloseable = false,  // Required модуль
                        MinWidth = 400,
                        MinHeight = 300,
                        PreferredPosition = PreferredDockPosition.RightAsTab
                    },

                    // Synonyms - правая верхняя панель
                    new ModuleSlot
                    {
                        ModuleId = "Synonyms",
                        ContainerId = "RightTop",
                        IsFloating = false,
                        TabOrder = 0,
                        IsActiveTab = true,
                        IsCloseable = true,
                        MinWidth = 250,
                        MinHeight = 200,
                        PreferredPosition = PreferredDockPosition.RightAsTab
                    },

                    // Timer - правая нижняя панель
                    new ModuleSlot
                    {
                        ModuleId = "Timer",
                        ContainerId = "RightBottom",
                        IsFloating = false,
                        TabOrder = 0,
                        IsActiveTab = true,
                        IsCloseable = true,
                        MinWidth = 200,
                        MinHeight = 150,
                        PreferredPosition = PreferredDockPosition.RightAsTab
                    }
                },

                // Структура контейнеров (split панелей)
                Containers = new List<SplitContainer>
                {
                    // Корневой контейнер - горизонтальный split
                    new SplitContainer
                    {
                        Id = "Root",
                        Proportion = 1.0,
                        Orientation = "Horizontal",
                        Children = new List<SplitContainer>
                        {
                            // Левая панель (70%)
                            new SplitContainer
                            {
                                Id = "LeftPanel",
                                Proportion = 0.7,
                                Orientation = null,  // Конечный узел
                                Children = null
                            },

                            // Правая панель (30%) - вертикальный split
                            new SplitContainer
                            {
                                Id = "RightPanel",
                                Proportion = 0.3,
                                Orientation = "Vertical",
                                Children = new List<SplitContainer>
                                {
                                    // Правая верхняя (50%)
                                    new SplitContainer
                                    {
                                        Id = "RightTop",
                                        Proportion = 0.5,
                                        Orientation = null,
                                        Children = null
                                    },

                                    // Правая нижняя (50%)
                                    new SplitContainer
                                    {
                                        Id = "RightBottom",
                                        Proportion = 0.5,
                                        Orientation = null,
                                        Children = null
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