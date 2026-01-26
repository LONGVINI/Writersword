using System;
using System.Collections.Generic;
using Writersword.Core.Models.WorkModes;

namespace Writersword.Core.Models.Settings
{
    /// <summary>
    /// Глобальная конфигурация рабочего пространства для типа проекта
    /// Хранится в Settings.json как глобальная настройка пользователя
    /// Применяется ко ВСЕМ проектам данного типа (Novel, Screenplay и т.д.)
    /// Приоритет: LOCAL (workspace.json в проекте) → GLOBAL (это) → DEFAULT (hardcoded)
    /// Пользователь может сохранить свою настройку и применить её ко всем новым проектам
    /// </summary>
    public class WorkspaceConfig
    {
        /// <summary>Тип проекта для которого эта конфигурация (Novel, Screenplay, Translation и т.д.)</summary>
        public string ProjectType { get; set; } = "";

        /// <summary>Название конфигурации (может быть изменено пользователем)</summary>
        public string Name { get; set; } = "Configuration";

        /// <summary>Дата создания конфигурации</summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>Дата последнего изменения</summary>
        public DateTime LastModified { get; set; } = DateTime.Now;

        /// <summary>
        /// Список WorkModes с их модулями
        /// Это полная копия того, что пользователь настроил
        /// Включает порядок, открытые модули, DockLayout
        /// </summary>
        public List<WorkMode> WorkModes { get; set; } = new();

        /// <summary>
        /// Конфигурация размеров окна и раскладки
        /// Содержит размеры главного окна, позиции сплиттеров и плавающие окна
        /// Автоматически сохраняется при изменении
        /// Пользователь может сохранить предпочитаемое расположение окон для всех проектов типа
        /// </summary>
        public WindowLayoutConfig? WindowLayout { get; set; }

        /// <summary>Версия формата (для совместимости при обновлениях)</summary>
        public string FormatVersion { get; set; } = "1.0";
    }
}