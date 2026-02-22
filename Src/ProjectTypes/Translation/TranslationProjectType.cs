using System.Collections.Generic;
using Writersword.Src.ProjectTypes.Common;

namespace Writersword.Src.ProjectTypes.Translation
{
    /// <summary>
    /// Тип проекта: Перевод
    /// </summary>
    public class TranslationProjectType : BaseProjectType
    {
        public override string Id => "Translation";

        public override string DisplayName => Resources.TranslationStrings.DisplayName;

        public override string Icon => Resources.TranslationStrings.Icon;

        public override List<string> WorkModes => new() { "editor" };
    }
}