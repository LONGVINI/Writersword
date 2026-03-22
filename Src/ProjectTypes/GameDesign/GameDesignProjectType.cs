using System.Collections.Generic;
using Writersword.ProjectTypes.Common;

namespace Writersword.ProjectTypes.GameDesign
{
    /// <summary>
    /// Тип проекта: Гейм-дизайн
    /// </summary>
    public class GameDesignProjectType : BaseProjectType
    {
        public override string Id => "GameDesign";

        public override string DisplayName => Resources.GameDesignStrings.DisplayName;

        public override string Icon => Resources.GameDesignStrings.Icon;

        public override List<string> WorkModes => new() { "editor", "timeline", "characters" };
    }
}