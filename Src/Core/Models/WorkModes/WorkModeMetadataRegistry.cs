using System.Collections.Generic;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.WorkModes;

namespace Writersword.Core.Models.WorkModes
{
    /// <summary>
    /// Реестр метаданных всех WorkMode
    /// Hardcoded описания всех режимов работы
    /// Аналогично WorkModeRules - это данные, не сервис
    /// </summary>
    public static class WorkModeMetadataRegistry
    {
        private static readonly List<IWorkModeMetadata> _metadata = new()
        {
            new WorkModeMetadata(WorkModeType.Editor, "Редактор", "📝", "Основной редактор текста", false, 0),
            new WorkModeMetadata(WorkModeType.Timeline, "Таймлайн", "📅", "Временная шкала событий", true, 1),
            new WorkModeMetadata(WorkModeType.Characters, "Персонажи", "👥", "Управление персонажами", true, 2),
            new WorkModeMetadata(WorkModeType.PlotPoints, "Сюжетные точки", "🎯", "Ключевые точки сюжета", true, 3),
            new WorkModeMetadata(WorkModeType.Locations, "Локации", "📍", "Места действия", true, 4),
            new WorkModeMetadata(WorkModeType.Maps, "Карты", "🗺️", "Карты местности", true, 5),
            new WorkModeMetadata(WorkModeType.GameEconomy, "Экономика", "💰", "Игровая экономика", true, 6),
            new WorkModeMetadata(WorkModeType.Dialogues, "Диалоги", "💬", "Редактор диалогов", true, 7),
            new WorkModeMetadata(WorkModeType.Branching, "Ветвление", "🌿", "Ветвление сюжета", true, 8),
            new WorkModeMetadata(WorkModeType.Poetry, "Поэзия", "📖", "Редактор поэзии", true, 9)
        };

        /// <summary>Получить все метаданные WorkMode</summary>
        public static List<IWorkModeMetadata> GetAll()
        {
            return _metadata;
        }

        /// <summary>Получить метаданные конкретного WorkMode</summary>
        public static IWorkModeMetadata? Get(WorkModeType type)
        {
            return _metadata.Find(m => m.Type == type);
        }
    }

    /// <summary>Реализация метаданных WorkMode</summary>
    internal class WorkModeMetadata : IWorkModeMetadata
    {
        public WorkModeType Type { get; }
        public string DisplayName { get; }
        public string Icon { get; }
        public string Description { get; }
        public bool IsCloseable { get; }
        public int Order { get; }

        public WorkModeMetadata(
            WorkModeType type,
            string displayName,
            string icon,
            string description,
            bool isCloseable,
            int order)
        {
            Type = type;
            DisplayName = displayName;
            Icon = icon;
            Description = description;
            IsCloseable = isCloseable;
            Order = order;
        }
    }
}