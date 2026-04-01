using System.Collections.Generic;
using Writersword.Modules.Characters.Models.Enums;

namespace Writersword.Modules.Characters.Models
{
    public class CharacterParameter
    {
        public string Id { get; set; } = System.Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public CharacterParameterType Type { get; set; } = CharacterParameterType.Numeric;
        public string GroupName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int Order { get; set; }

        // Числовой
        public double MinValue { get; set; } = 0;
        public double MaxValue { get; set; } = 100;
        public double NumericValue { get; set; } = 0;
        public double Step { get; set; } = 1;
        public string MinDescription { get; set; } = string.Empty;
        public string MaxDescription { get; set; } = string.Empty;

        /// <summary>Диапазон рандомизации — min. Ручной ввод за пределами не ограничен.</summary>
        public double? RandomRangeMin { get; set; }
        /// <summary>Диапазон рандомизации — max. Ручной ввод за пределами не ограничен.</summary>
        public double? RandomRangeMax { get; set; }

        /// <summary>Точки шкалы с описаниями для тултипов. Ключ — значение, значение — описание.</summary>
        public Dictionary<double, string> ScalePoints { get; set; } = new();

        // Текстовый
        public string TextValue { get; set; } = string.Empty;

        // Список состояний
        public List<string> States { get; set; } = new();
        public int CurrentStateIndex { get; set; } = 0;

        // Булевый
        public bool BoolValue { get; set; } = false;
        public string TrueLabel { get; set; } = "Да";
        public string FalseLabel { get; set; } = "Нет";
    }
}
