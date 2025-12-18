using System;
using System.Collections.Generic;
using Writersword.Core.Models.WorkModes;
using System.Linq;

namespace Writersword.Core.Models.Project
{
    /// <summary>
    /// Документ (вкладка) в проекте
    /// Каждая вкладка = отдельный документ для письма
    /// </summary>
    public class DocumentTab
    {
        /// <summary>Уникальный ID документа</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>Название вкладки</summary>
        public string Title { get; set; } = "Document";

        /// <summary>Содержимое документа (текст)</summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>Активная вкладка (выбрана сейчас)</summary>
        public bool IsActive { get; set; } = false;

        /// <summary>Путь к файлу документа</summary>
        public string? FilePath { get; set; }

        /// <summary>Список режимов работы в этом документе</summary>
        public List<WorkMode> WorkModes { get; set; } = new();

        /// <summary>ID активного режима работы</summary>
        public string? ActiveWorkModeId { get; set; }

        /// <summary>Получить активный WorkMode</summary>
        public WorkMode? GetActiveWorkMode()
        {
            if (string.IsNullOrEmpty(ActiveWorkModeId))
                return WorkModes.FirstOrDefault();

            return WorkModes.FirstOrDefault(wm => wm.Id == ActiveWorkModeId);
        }

        /// <summary>Установить активный WorkMode</summary>
        public void SetActiveWorkMode(WorkMode workMode)
        {
            // Деактивируем все режимы
            foreach (var wm in WorkModes)
            {
                wm.IsActive = false;
            }

            // Активируем выбранный
            workMode.IsActive = true;
            workMode.LastAccessedAt = DateTime.Now;
            ActiveWorkModeId = workMode.Id;
        }
    }
}