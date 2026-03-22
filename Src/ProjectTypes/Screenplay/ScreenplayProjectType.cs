using System.Collections.Generic;
using Writersword.ProjectTypes.Common;

namespace Writersword.ProjectTypes.Screenplay
{
    /// <summary>
    /// Тип проекта: Сценарий
    /// </summary>
    public class ScreenplayProjectType : BaseProjectType
    {
        public override string Id => "Screenplay";

        public override string DisplayName => Resources.ScreenplayStrings.DisplayName;

        public override string Icon => Resources.ScreenplayStrings.Icon;

        public override List<string> WorkModes => new() { "editor", "timeline" };
    }
}