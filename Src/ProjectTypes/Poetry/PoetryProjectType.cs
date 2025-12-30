using System.Collections.Generic;
using Writersword.Src.ProjectTypes.Common;

namespace Writersword.Src.ProjectTypes.Poetry
{
    /// <summary>
    /// Тип проекта: Поэзия
    /// </summary>
    public class PoetryProjectType : BaseProjectType
    {
        public override string Id => "Poetry";

        public override string DisplayName => Resources.Strings.DisplayName;

        public override string Icon => Resources.Strings.Icon;

        public override List<string> WorkModes => new() { "editor" };
    }
}