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

        // ── Вид ──────────────────────────────────────────────────────────
        //
        // Всё ниже — про то, как поле выглядит и как его заполняют, а не про
        // то, что в нём хранится. Настраивается в анкете и одинаково у всех
        // персонажей, к которым она подключена.

        /// <summary>Вид поля в карточке. Auto — вид по умолчанию для типа.</summary>
        public CharacterFieldDisplay Display { get; set; } = CharacterFieldDisplay.Auto;

        /// <summary>
        /// Свой цвет поля — шариков, полосы, отмеченного чипа — строкой #RRGGBB.
        /// Пусто — акцентный цвет темы.
        /// </summary>
        public string AccentColor { get; set; } = string.Empty;

        /// <summary>Единица свободного числа: «см», «кг», «лет». Пусто — без единицы.</summary>
        public string Unit { get; set; } = string.Empty;

        /// <summary>
        /// Какими способами можно задать свободное число. По умолчанию только
        /// точно — как было до появления способов.
        /// </summary>
        public CharacterNumberModes NumberModes { get; set; } = CharacterNumberModes.Exact;

        /// <summary>Этапы свободного числа — для способа «этап»: «Ребёнок 0–12».</summary>
        public List<CharacterNumberStage> Stages { get; set; } = new();

        /// <summary>Палитра поля цвета: варианты #RRGGBB, из которых выбирают.</summary>
        public List<string> Palette { get; set; } = new();

        /// <summary>Можно ли у поля цвета выбрать цвет вне палитры.</summary>
        public bool AllowCustomColor { get; set; } = true;

        /// <summary>
        /// Ширина поля ввода у текста, описания и числа, в точках. Задаётся в
        /// анкете перетаскиванием края поля: «имя» короче «места рождения».
        /// Пусто — ширина по умолчанию для типа.
        /// </summary>
        public double? InputWidth { get; set; }

        // ── Раскладка и подпись ──────────────────────────────────────────

        /// <summary>
        /// Ключ поля внутри анкеты — по нему раскладка (CharacterAnketa.Layout)
        /// ставит поле в ячейку. В отличие от FieldId, не связан с именем и не
        /// меняется при переименовании. Пусто — выдаётся при загрузке.
        /// </summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>Подпись поля жирным.</summary>
        public bool LabelBold { get; set; }

        /// <summary>Цвет подписи #RRGGBB. Пусто — цвет текста темы.</summary>
        public string LabelColor { get; set; } = string.Empty;

        /// <summary>
        /// Значок слева от подписи: ключ из CharacterAnketaIcons, «path:…»
        /// с геометрией или «png:…» с картинкой. Пусто — без значка.
        /// </summary>
        public string LabelIcon { get; set; } = string.Empty;

        // ── Свободное число ──────────────────────────────────────────────

        /// <summary>Знаков после запятой. Ноль — целое число.</summary>
        public int Decimals { get; set; }

        /// <summary>Наименьшее допустимое значение. Пусто — без нижней границы.</summary>
        public double? NumberMin { get; set; }

        /// <summary>Наибольшее допустимое значение. Пусто — без верхней границы.</summary>
        public double? NumberMax { get; set; }

        /// <summary>
        /// Число подчиняется шагу: вписанное значение притягивается к
        /// ближайшему допустимому, стрелки вверх и вниз идут по шагу.
        /// </summary>
        public bool UseStep { get; set; }

        /// <summary>Как считаются допустимые значения при включённом шаге.</summary>
        public CharacterStepRule StepRule { get; set; } = CharacterStepRule.Linear;

        /// <summary>Свой список допустимых значений через запятую — для правила List.</summary>
        public string StepValuesRaw { get; set; } = string.Empty;

        // ── Оценка ───────────────────────────────────────────────────────

        /// <summary>
        /// Значок оценки для вида Glyph — в тех же форматах, что LabelIcon.
        /// Пусто — сердечко.
        /// </summary>
        public string RatingGlyph { get; set; } = string.Empty;
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
