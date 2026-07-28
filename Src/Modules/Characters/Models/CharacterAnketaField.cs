using System.Collections.Generic;
using System.Linq;
using System.Text;
using Writersword.Modules.Characters.Models.Enums;

namespace Writersword.Modules.Characters.Models
{
    public class CharacterAnketaField
    {
        /// <summary>
        /// Стабильный идентификатор поля — общий для всех персонажей и всех
        /// анкет, где это поле встречается. По нему значения сравниваются
        /// между карточками и по нему же определение находит свои значения.
        ///
        /// Имя поля идентификатором быть не может: «Какого цвета волосы?»
        /// и «Цвет волос персонажа» — два вопроса к одному полю, а сравнивать
        /// нужно ответы, а не формулировки.
        ///
        /// Пустой идентификатор выводится из имени (см. CharacterFieldId):
        /// у встроенных анкет это даёт совпадение одинаковых полей, у своих —
        /// разумный старт до появления конструктора анкет.
        /// </summary>
        public string FieldId { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;
        public CharacterParameterType Type { get; set; } = CharacterParameterType.Numeric;
        public string GroupName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int Order { get; set; }

        /// <summary>
        /// Участвует ли поле в сравнении карточек. Данные — цвет глаз, рост,
        /// сила — участвуют. Упражнения вроде «опишите, как персонаж повёл бы
        /// себя в гипотетической ситуации» ценны не ответом, а тем, что
        /// заставили подумать: в таблицы они не лезут.
        /// </summary>
        public bool IsComparable { get; set; } = true;

        public double DefaultMinValue { get; set; } = 0;
        public double DefaultMaxValue { get; set; } = 100;
        public double Step { get; set; } = 1;
        public string MinDescription { get; set; } = string.Empty;
        public string MaxDescription { get; set; } = string.Empty;

        public double? RandomRangeMin { get; set; }
        public double? RandomRangeMax { get; set; }

        /// <summary>
        /// Подписи делений шкалы: ключ — значение, значение — слово.
        /// Именно они превращают пустое «агрессия 3 из 5» в осмысленное
        /// «заводится»: автор выбирает слово, программа хранит число
        /// и умеет сравнивать.
        /// </summary>
        public Dictionary<double, string> ScalePoints { get; set; } = new();

        public string StatesRaw { get; set; } = string.Empty;
        public string TrueLabel { get; set; } = "Да";
        public string FalseLabel { get; set; } = "Нет";
    }

    /// <summary>
    /// Вывод идентификатора поля из его имени. Временное соглашение: пока нет
    /// конструктора анкет, где автор выбирает поле из существующих, совпадение
    /// имён — единственный доступный способ понять, что «Цвет волос» в двух
    /// анкетах это одно и то же поле.
    ///
    /// Автоматическое сопоставление смыслов при этом не делается и делаться
    /// не будет: «Любовь_к_морковке» и «Like_a_carrot» останутся разными
    /// полями, и это не дефект. Сравнимость приходит от общей анкеты,
    /// а не от угадывания.
    /// </summary>
    public static class CharacterFieldId
    {
        public static string FromName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;

            var builder = new StringBuilder(name.Length);
            foreach (var ch in name.Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(ch)) builder.Append(ch);
                else if (char.IsWhiteSpace(ch) || ch == '-' || ch == '_') builder.Append('_');
                // Знаки препинания выбрасываются: «Цвет волос?» и «Цвет волос»
                // должны давать один идентификатор.
            }

            var result = builder.ToString().Trim('_');
            while (result.Contains("__")) result = result.Replace("__", "_");
            return result;
        }

        /// <summary>Идентификатор поля анкеты: заданный автором или выведенный из имени.</summary>
        public static string Resolve(CharacterAnketaField field) =>
            !string.IsNullOrWhiteSpace(field.FieldId) ? field.FieldId : FromName(field.Name);

        /// <summary>Идентификатор значения: заданный или выведенный из имени параметра.</summary>
        public static string Resolve(CharacterParameter parameter) =>
            !string.IsNullOrWhiteSpace(parameter.FieldId) ? parameter.FieldId : FromName(parameter.Name);
    }
}
