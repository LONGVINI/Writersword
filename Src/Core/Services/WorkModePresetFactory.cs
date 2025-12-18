using System.Collections.Generic;
using Writersword.Core.Enums;
using Writersword.Core.Models.WorkModes;

namespace Writersword.Core.Services.WorkModes
{
    /// <summary>
    /// Фабрика для создания дефолтных пресетов WorkModes по типу проекта
    /// Содержит hardcoded конфигурации для каждого типа проекта
    /// </summary>
    public static class WorkModePresetFactory
    {
        /// <summary>Получить пресет для типа проекта</summary>
        public static WorkModePreset GetPreset(ProjectType projectType)
        {
            return projectType switch
            {
                ProjectType.Novel => CreateNovelPreset(),
                ProjectType.Screenplay => CreateScreenplayPreset(),
                ProjectType.Poetry => CreatePoetryPreset(),
                ProjectType.GameDesign => CreateGameDesignPreset(),
                ProjectType.Translation => CreateTranslationPreset(),
                _ => CreateNovelPreset() // По умолчанию - писательский
            };
        }

        /// <summary>Пресет для писательского проекта (Novel)</summary>
        private static WorkModePreset CreateNovelPreset()
        {
            return new WorkModePreset
            {
                ProjectType = ProjectType.Novel,
                WorkModes = new List<WorkModeTemplate>
                {
                    // Режим "Редактор" - основной
                    new WorkModeTemplate
                    {
                        Type = WorkModeType.Editor,
                        Title = "Редактор",
                        Icon = "📝",
                        Order = 0,
                        IsCloseable = false, // Нельзя закрыть
                        ModuleSlots = new List<ModuleSlotTemplate>
                        {
                            // TextEditor занимает большую часть экрана
                            new ModuleSlotTemplate
                            {
                                ModuleType = ModuleType.TextEditor,
                                Position = new WorkModeGridPosition { Row = 0, Column = 0 },
                                Size = new WorkModeGridSize(2, 3),
                                MinWidth = 400,
                                MinHeight = 300
                            },
                            // Synonyms справа сверху
                            new ModuleSlotTemplate
                            {
                                ModuleType = ModuleType.Synonyms,
                                Position = new WorkModeGridPosition { Row = 0, Column = 3 },
                                Size = new WorkModeGridSize(1, 1),
                                MinWidth = 200,
                                MinHeight = 150
                            },
                            // Timer справа снизу
                            new ModuleSlotTemplate
                            {
                                ModuleType = ModuleType.Timer,
                                Position = new WorkModeGridPosition { Row = 1, Column = 3 },
                                Size = new WorkModeGridSize(1, 1),
                                MinWidth = 200,
                                MinHeight = 150
                            },
                            // Notes внизу на всю ширину
                            new ModuleSlotTemplate
                            {
                                ModuleType = ModuleType.Notes,
                                Position = new WorkModeGridPosition { Row = 2, Column = 0 },
                                Size = new WorkModeGridSize(1, 4),
                                MinWidth = 600,
                                MinHeight = 100
                            }
                        }
                    },

                    // Режим "Таймлайн"
                    new WorkModeTemplate
                    {
                        Type = WorkModeType.Timeline,
                        Title = "Таймлайн",
                        Icon = "📅",
                        Order = 1,
                        IsCloseable = true,
                        ModuleSlots = new List<ModuleSlotTemplate>
                        {
                            // Timeline занимает основную часть
                            new ModuleSlotTemplate
                            {
                                ModuleType = ModuleType.Timeline,
                                Position = new WorkModeGridPosition { Row = 0, Column = 0 },
                                Size = new WorkModeGridSize(2, 3),
                                MinWidth = 500,
                                MinHeight = 400
                            },
                            // Characters справа (мини-версия)
                            new ModuleSlotTemplate
                            {
                                ModuleType = ModuleType.Characters,
                                Position = new WorkModeGridPosition { Row = 0, Column = 3 },
                                Size = new WorkModeGridSize(2, 1),
                                MinWidth = 200,
                                MinHeight = 400
                            }
                        }
                    },

                    // Режим "Персонажи"
                    new WorkModeTemplate
                    {
                        Type = WorkModeType.Characters,
                        Title = "Персонажи",
                        Icon = "👥",
                        Order = 2,
                        IsCloseable = true,
                        ModuleSlots = new List<ModuleSlotTemplate>
                        {
                            // Characters слева
                            new ModuleSlotTemplate
                            {
                                ModuleType = ModuleType.Characters,
                                Position = new WorkModeGridPosition { Row = 0, Column = 0 },
                                Size = new WorkModeGridSize(2, 2),
                                MinWidth = 300,
                                MinHeight = 400
                            },
                            // Relationships справа
                            new ModuleSlotTemplate
                            {
                                ModuleType = ModuleType.Relationships,
                                Position = new WorkModeGridPosition { Row = 0, Column = 2 },
                                Size = new WorkModeGridSize(2, 2),
                                MinWidth = 300,
                                MinHeight = 400
                            }
                        }
                    }
                }
            };
        }

        /// <summary>Пресет для сценарного проекта (Screenplay)</summary>
        private static WorkModePreset CreateScreenplayPreset()
        {
            return new WorkModePreset
            {
                ProjectType = ProjectType.Screenplay,
                WorkModes = new List<WorkModeTemplate>
                {
                    new WorkModeTemplate
                    {
                        Type = WorkModeType.Editor,
                        Title = "Сценарий",
                        Icon = "🎬",
                        Order = 0,
                        IsCloseable = false,
                        ModuleSlots = new List<ModuleSlotTemplate>
                        {
                            new ModuleSlotTemplate
                            {
                                ModuleType = ModuleType.TextEditor,
                                Position = new WorkModeGridPosition { Row = 0, Column = 0 },
                                Size = new WorkModeGridSize(2, 3)
                            },
                            new ModuleSlotTemplate
                            {
                                ModuleType = ModuleType.Dialogues,
                                Position = new WorkModeGridPosition { Row = 0, Column = 3 },
                                Size = new WorkModeGridSize(2, 1)
                            }
                        }
                    }
                }
            };
        }

        /// <summary>Пресет для поэтического проекта (Poetry)</summary>
        private static WorkModePreset CreatePoetryPreset()
        {
            return new WorkModePreset
            {
                ProjectType = ProjectType.Poetry,
                WorkModes = new List<WorkModeTemplate>
                {
                    new WorkModeTemplate
                    {
                        Type = WorkModeType.Poetry,
                        Title = "Поэзия",
                        Icon = "✒️",
                        Order = 0,
                        IsCloseable = false,
                        ModuleSlots = new List<ModuleSlotTemplate>
                        {
                            new ModuleSlotTemplate
                            {
                                ModuleType = ModuleType.Poetry,
                                Position = new WorkModeGridPosition { Row = 0, Column = 0 },
                                Size = new WorkModeGridSize(2, 3)
                            },
                            new ModuleSlotTemplate
                            {
                                ModuleType = ModuleType.RhymeHelper,
                                Position = new WorkModeGridPosition { Row = 0, Column = 3 },
                                Size = new WorkModeGridSize(2, 1)
                            }
                        }
                    }
                }
            };
        }

        /// <summary>Пресет для геймдизайн проекта (GameDesign)</summary>
        private static WorkModePreset CreateGameDesignPreset()
        {
            return new WorkModePreset
            {
                ProjectType = ProjectType.GameDesign,
                WorkModes = new List<WorkModeTemplate>
                {
                    new WorkModeTemplate
                    {
                        Type = WorkModeType.Editor,
                        Title = "Документация",
                        Icon = "📋",
                        Order = 0,
                        IsCloseable = false,
                        ModuleSlots = new List<ModuleSlotTemplate>
                        {
                            new ModuleSlotTemplate
                            {
                                ModuleType = ModuleType.TextEditor,
                                Position = new WorkModeGridPosition { Row = 0, Column = 0 },
                                Size = new WorkModeGridSize(2, 4)
                            }
                        }
                    },

                    new WorkModeTemplate
                    {
                        Type = WorkModeType.Maps,
                        Title = "Карты",
                        Icon = "🗺️",
                        Order = 1,
                        IsCloseable = true,
                        ModuleSlots = new List<ModuleSlotTemplate>
                        {
                            new ModuleSlotTemplate
                            {
                                ModuleType = ModuleType.Maps,
                                Position = new WorkModeGridPosition { Row = 0, Column = 0 },
                                Size = new WorkModeGridSize(2, 4)
                            }
                        }
                    }
                }
            };
        }

        /// <summary>Пресет для переводческого проекта (Translation)</summary>
        private static WorkModePreset CreateTranslationPreset()
        {
            return new WorkModePreset
            {
                ProjectType = ProjectType.Translation,
                WorkModes = new List<WorkModeTemplate>
                {
                    new WorkModeTemplate
                    {
                        Type = WorkModeType.Editor,
                        Title = "Перевод",
                        Icon = "🌐",
                        Order = 0,
                        IsCloseable = false,
                        ModuleSlots = new List<ModuleSlotTemplate>
                        {
                            new ModuleSlotTemplate
                            {
                                ModuleType = ModuleType.TextEditor,
                                Position = new WorkModeGridPosition { Row = 0, Column = 0 },
                                Size = new WorkModeGridSize(2, 4)
                            }
                        }
                    }
                }
            };
        }
    }
}