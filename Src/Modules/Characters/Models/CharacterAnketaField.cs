using System.Collections.Generic;
using Writersword.Modules.Characters.Models.Enums;

namespace Writersword.Modules.Characters.Models
{
    public class CharacterAnketaField
    {
        public string Name { get; set; } = string.Empty;
        public CharacterParameterType Type { get; set; } = CharacterParameterType.Numeric;
        public string GroupName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int Order { get; set; }

        public double DefaultMinValue { get; set; } = 0;
        public double DefaultMaxValue { get; set; } = 100;
        public double Step { get; set; } = 1;
        public string MinDescription { get; set; } = string.Empty;
        public string MaxDescription { get; set; } = string.Empty;

        public double? RandomRangeMin { get; set; }
        public double? RandomRangeMax { get; set; }
        public Dictionary<double, string> ScalePoints { get; set; } = new();

        public string StatesRaw { get; set; } = string.Empty;
        public string TrueLabel { get; set; } = "Да";
        public string FalseLabel { get; set; } = "Нет";
    }
}
