using System;
using System.Collections.Generic;
using Writersword.Core.Models.WorkModes;

namespace Writersword.Core.Models.Settings
{
    /// <summary>
    /// Локальная конфигурация рабочего пространства для конкретного проекта
    /// Хранится в workspace.json внутри ZIP архива проекта
    /// Применяется ТОЛЬКО к этому конкретному проекту
    /// Приоритет: LOCAL (это) → GLOBAL (Settings.json) → DEFAULT (hardcoded)
    /// Содержит:
    /// - Порядок WorkMode (можно настроить вручную)
    /// - Размеры панелей (автосохранение через 5 секунд)
    /// - Открытые модули (автосохранение)
    /// - Размеры главного окна и позиции сплиттеров
    /// - Плавающие окна (FloatWindows)
    /// </summary>
    public class WorkspaceLocalConfig
    {
        /// <summary>Версия формата конфигурации</summary>
        public string Version { get; set; } = "1.0";

        /// <summary>
        /// Для отладки - имя проекта, к которому относится эта конфигурация
        /// </summary>
        public string ProjectName { get; set; } = "";

        /// <summary>
        /// Список WorkMode с их конфигурациями
        /// Включает:
        /// - order (порядок кнопок)
        /// - moduleSlots (какие модули открыты)
        /// - dockLayout (размеры панелей)
        /// </summary>
        public List<WorkMode> WorkModes { get; set; } = new();

        /// <summary>
        /// Конфигурация размеров окна и раскладки
        /// Содержит размеры главного окна, позиции сплиттеров и плавающие окна
        /// Автоматически сохраняется при изменении
        /// </summary>
        public WindowLayoutConfig? WindowLayout { get; set; }
    }
}