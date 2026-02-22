using System;
using System.Collections.Generic;
using System.Text;
using Writersword.Core.Models.Project;
using Writersword.Core.Models.Settings;

namespace Writersword.Src.Core.Models.Settings
{
    public class AppSettings
    {
        public string Theme { get; set; } = "Dark";
        public string Language { get; set; } = "en";
        public string? LastOpenedProject { get; set; }
        public string DefaultProjectsFolder { get; set; } = string.Empty;
        public string? LastUsedPath { get; set; }
        public List<RecentProject> RecentProjects { get; set; } = new List<RecentProject>();
        // Список открытых вкладок из последней сессии
        public List<string> OpenProjectPaths { get; set; } = new List<string>();
        public Dictionary<string, WorkspaceConfig> WorkspaceConfigs { get; set; } = new();

        /// <summary>Настройки модулей (ключ = moduleType, значение = сериализованный объект настроек)</summary>
        public Dictionary<string, object?> ModuleSettings { get; set; } = new();
    }
}