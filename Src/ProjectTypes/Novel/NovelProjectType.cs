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

        public override string DisplayName => Resources.NovelStrings.DisplayName;

        public override string Icon => Resources.NovelStrings.Icon;

        public override List<string> WorkModes => new() { "editor", "timeline", "characters" };
    }
}