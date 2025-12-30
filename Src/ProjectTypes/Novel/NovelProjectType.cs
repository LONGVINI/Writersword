using System.Collections.Generic;
using Writersword.Src.ProjectTypes.Common;

namespace Writersword.Src.ProjectTypes.Novel
{
    /// <summary>
    /// Тип проекта: Роман
    /// </summary>
    public class NovelProjectType : BaseProjectType
    {
        public override string Id => "Novel";

        public override string DisplayName => Resources.Strings.DisplayName;

        public override string Icon => Resources.Strings.Icon;

        public override List<string> WorkModes => new() { "editor", "timeline", "characters" };
    }
}