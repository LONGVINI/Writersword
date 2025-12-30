using System.Collections.Generic;
using Writersword.Src.ProjectTypes.Common;

namespace Writersword.Src.ProjectTypes.GameDesign
{
    /// <summary>
    /// Тип проекта: Гейм-дизайн
    /// </summary>
    public class GameDesignProjectType : BaseProjectType
    {
        public override string Id => "GameDesign";

        public override string DisplayName => Resources.Strings.DisplayName;

        public override string Icon => Resources.Strings.Icon;

        public override List<string> WorkModes => new() { "editor", "timeline", "characters" };
    }
}