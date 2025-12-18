using System.Collections.Generic;
using Writersword.Core.Enums;
using Writersword.Core.Models.WorkModes;

namespace Writersword.Core.Services.WorkModes
{
    /// <summary>
    /// Фабрика для создания дефолтных пресетов WorkModes по типу проекта.
    /// Содержит hardcoded конфигурации для каждого типа проекта.
    /// Определяет какие модули можно закрывать, а какие являются обязательными.
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
                _ => CreateNovelPreset()
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
                        IsCloseable = false,
                        ModuleSlots = new List<ModuleSlotTemplate>
                        {
                            // TextEditor - ГЛАВНЫЙ модуль, нельзя закрыть!
                            new ModuleSlotTemplate
                            {
                                ModuleType = ModuleType.TextEditor,
                                Position = new WorkModeGridPosition { Row = 0, Column = 0 },
                                Size = new WorkModeGridSize(2, 3),
                                MinWidth = 400,
                                MinHeight = 300,
                                IsCloseable = false
                            },
                            // Synonyms - можно закрыть
                            new ModuleSlotTemplate
                            {
                                ModuleType = ModuleType.Synonyms,
                                Position = new WorkModeGridPosition { Row = 0, Column = 3 },
                                Size = new WorkModeGridSize(1, 1),
                                MinWidth = 200,
                                MinHeight = 150,
                                IsCloseable = true
                            },
                            // Timer - можно закрыть
                            new ModuleSlotTemplate
                            {
                                ModuleType = ModuleType.Timer,
                                Position = new WorkModeGridPosition { Row = 1, Column = 3 },
                                Size = new WorkModeGridSize(1, 1),
                                MinWidth = 200,
                                MinHeight = 150,
                                IsCloseable = true
                            },
                            // Notes - можно закрыть
                            new ModuleSlotTemplate
                            {
                                ModuleType = ModuleType.Notes,
                                Position = new WorkModeGridPosition { Row = 2, Column = 0 },
                                Size = new WorkModeGridSize(1, 4),
                                MinWidth = 600,
                                MinHeight = 100,
                                IsCloseable = true
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
                            // Timeline - ГЛАВНЫЙ модуль этого режима
                            new ModuleSlotTemplate
                            {
                                ModuleType = ModuleType.Timeline,
                                Position = new WorkModeGridPosition { Row = 0, Column = 0 },
                                Size = new WorkModeGridSize(2, 3),
                                MinWidth = 500,
                                MinHeight = 400,
                                IsCloseable = false
                            },
                            // Characters - можно закрыть
                            new ModuleSlotTemplate
                            {
                                ModuleType = ModuleType.Characters,
                                Position = new WorkModeGridPosition { Row = 0, Column = 3 },
                                Size = new WorkModeGridSize(2, 1),
                                MinWidth = 200,
                                MinHeight = 400,
                                IsCloseable = true
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
                            // Characters - ГЛАВНЫЙ модуль
                            new ModuleSlotTemplate
                            {
                                ModuleType = ModuleType.Characters,
                                Position = new WorkModeGridPosition { Row = 0, Column = 0 },
                                Size = new WorkModeGridSize(2, 2),
                                MinWidth = 300,
                                MinHeight = 400,
                                IsCloseable = false
                            },
                            // Relationships - можно закрыть
                            new ModuleSlotTemplate
                            {
                                ModuleType = ModuleType.Relationships,
                                Position = new WorkModeGridPosition { Row = 0, Column = 2 },
                                Size = new WorkModeGridSize(2, 2),
                                MinWidth = 300,
                                MinHeight = 400,
                                IsCloseable = true
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
                            // TextEditor - главный модуль
                            new ModuleSlotTemplate
                            {
                                ModuleType = ModuleType.TextEditor,
                                Position = new WorkModeGridPosition { Row = 0, Column = 0 },
                                Size = new WorkModeGridSize(2, 3),
                                MinWidth = 400,
                                MinHeight = 300,
                                IsCloseable = false
                            },
                            // Dialogues - можно закрыть
                            new ModuleSlotTemplate
                            {
                                ModuleType = ModuleType.Dialogues,
                                Position = new WorkModeGridPosition { Row = 0, Column = 3 },
                                Size = new WorkModeGridSize(2, 1),
                                MinWidth = 200,
                                MinHeight = 300,
                                IsCloseable = true
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
                            // Poetry - главный модуль
                            new ModuleSlotTemplate
                            {
                                ModuleType = ModuleType.Poetry,
                                Position = new WorkModeGridPosition { Row = 0, Column = 0 },
                                Size = new WorkModeGridSize(2, 3),
                                MinWidth = 400,
                                MinHeight = 300,
                                IsCloseable = false
                            },
                            // RhymeHelper - можно закрыть
                            new ModuleSlotTemplate
                            {
                                ModuleType = ModuleType.RhymeHelper,
                                Position = new WorkModeGridPosition { Row = 0, Column = 3 },
                                Size = new WorkModeGridSize(2, 1),
                                MinWidth = 200,
                                MinHeight = 300,
                                IsCloseable = true
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
                    // Режим "Документация"
                    new WorkModeTemplate
                    {
                        Type = WorkModeType.Editor,
                        Title = "Документация",
                        Icon = "📋",
                        Order = 0,
                        IsCloseable = false,
                        ModuleSlots = new List<ModuleSlotTemplate>
                        {
                            // TextEditor - главный модуль
                            new ModuleSlotTemplate
                            {
                                ModuleType = ModuleType.TextEditor,
                                Position = new WorkModeGridPosition { Row = 0, Column = 0 },
                                Size = new WorkModeGridSize(2, 4),
                                MinWidth = 600,
                                MinHeight = 400,
                                IsCloseable = false
                            }
                        }
                    },

                    // Режим "Карты"
                    new WorkModeTemplate
                    {
                        Type = WorkModeType.Maps,
                        Title = "Карты",
                        Icon = "🗺️",
                        Order = 1,
                        IsCloseable = true,
                        ModuleSlots = new List<ModuleSlotTemplate>
                        {
                            // Maps - главный модуль режима
                            new ModuleSlotTemplate
                            {
                                ModuleType = ModuleType.Maps,
                                Position = new WorkModeGridPosition { Row = 0, Column = 0 },
                                Size = new WorkModeGridSize(2, 4),
                                MinWidth = 600,
                                MinHeight = 400,
                                IsCloseable = false
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
                            // TextEditor - главный модуль
                            new ModuleSlotTemplate
                            {
                                ModuleType = ModuleType.TextEditor,
                                Position = new WorkModeGridPosition { Row = 0, Column = 0 },
                                Size = new WorkModeGridSize(2, 4),
                                MinWidth = 600,
                                MinHeight = 400,
                                IsCloseable = false
                            }
                        }
                    }
                }
            };
        }
    }
}