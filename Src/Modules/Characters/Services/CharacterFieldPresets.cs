using System.Collections.Generic;
using System.Linq;
using Writersword.Modules.Characters.Models;
using Writersword.Modules.Characters.Models.Enums;

namespace Writersword.Modules.Characters.Services
{
    /// <summary>
    /// Встроенные заготовки полей — по нескольку на каждый тип: то, что в
    /// анкетах персонажей спрашивают чаще всего. Собираются при каждом
    /// запуске и в проект не пишутся; опознаватели постоянные, «builtin_preset_…».
    /// </summary>
    public static class CharacterFieldPresets
    {
        public static IReadOnlyList<CharacterFieldPreset> BuiltIn { get; } = Build();

        private static List<CharacterFieldPreset> Build() => new()
        {
            // ── Шкала ────────────────────────────────────────────────────
            Preset("scale_balls", "Шкала из 5", "Пять шариков: «вспыльчивость 4 из 5»", new()
            {
                Name = "Шкала", Type = CharacterParameterType.Numeric,
                DefaultMinValue = 0, DefaultMaxValue = 5, Step = 1,
                Display = CharacterFieldDisplay.Balls
            }),
            Preset("scale_stars", "Звёзды из 5", "Оценка звёздами", new()
            {
                Name = "Оценка", Type = CharacterParameterType.Numeric,
                DefaultMinValue = 0, DefaultMaxValue = 5, Step = 1,
                Display = CharacterFieldDisplay.Stars
            }),
            Preset("scale_ten", "Шкала из 10", "Десять делений: сила, ловкость, интеллект", new()
            {
                Name = "Шкала", Type = CharacterParameterType.Numeric,
                DefaultMinValue = 0, DefaultMaxValue = 10, Step = 1,
                Display = CharacterFieldDisplay.Bar
            }),
            Preset("scale_percent", "Проценты", "Ползунок от 0 до 100 с шагом 5", new()
            {
                Name = "Проценты", Type = CharacterParameterType.Numeric,
                DefaultMinValue = 0, DefaultMaxValue = 100, Step = 5,
                Display = CharacterFieldDisplay.Slider
            }),
            Preset("scale_poles", "Между полюсами", "Черта между двумя крайностями: интроверт — экстраверт", new()
            {
                Name = "Интроверсия", Type = CharacterParameterType.Numeric,
                DefaultMinValue = 0, DefaultMaxValue = 10, Step = 1,
                RandomRangeMin = 2, RandomRangeMax = 8,
                Display = CharacterFieldDisplay.Bipolar,
                MinDescription = "Интроверт", MaxDescription = "Экстраверт"
            }),

            // ── Число ────────────────────────────────────────────────────
            Preset("number_age", "Возраст", "Лет: точно, примерно, этап жизни или год рождения", new()
            {
                Name = "Возраст", Type = CharacterParameterType.Number,
                Unit = "лет", Step = 1, NumberMin = 0,
                NumberModes = CharacterNumberModes.Exact | CharacterNumberModes.Approx
                              | CharacterNumberModes.Stage | CharacterNumberModes.BirthYear,
                Stages = new List<CharacterNumberStage>
                {
                    new() { Name = "Ребёнок", From = 0, To = 12 },
                    new() { Name = "Подросток", From = 13, To = 17 },
                    new() { Name = "Молодой", From = 18, To = 29 },
                    new() { Name = "Взрослый", From = 30, To = 59 },
                    new() { Name = "Пожилой", From = 60 }
                }
            }),
            Preset("number_height", "Рост", "В сантиметрах", new()
            {
                Name = "Рост", Type = CharacterParameterType.Number,
                Unit = "см", Step = 1, NumberMin = 0, NumberMax = 300
            }),
            Preset("number_weight", "Вес", "В килограммах", new()
            {
                Name = "Вес", Type = CharacterParameterType.Number,
                Unit = "кг", Step = 1, NumberMin = 0, NumberMax = 500
            }),
            Preset("number_money", "Деньги", "Сбережения или долг, точно или «от — до»", new()
            {
                Name = "Деньги", Type = CharacterParameterType.Number,
                Unit = "монет", Step = 1,
                NumberModes = CharacterNumberModes.Exact | CharacterNumberModes.Approx | CharacterNumberModes.Range
            }),

            // ── Выбор ────────────────────────────────────────────────────
            Preset("choice_sex", "Пол", "Мужской, женский, другой", Choice("Пол", "Мужской, Женский, Другой")),
            Preset("choice_temperament", "Темперамент", "Холерик, сангвиник, флегматик, меланхолик",
                Choice("Темперамент", "Холерик, Сангвиник, Флегматик, Меланхолик")),
            Preset("choice_role", "Роль в сюжете", "Протагонист, антагонист, союзник…",
                Choice("Роль в сюжете", "Протагонист, Антагонист, Союзник, Наставник, Второстепенный")),
            Preset("choice_family", "Семейное положение", "Холост, в отношениях, в браке…",
                Choice("Семейное положение", "Холост, В отношениях, В браке, В разводе, Вдовец")),
            Preset("choice_blood", "Группа крови", "0, A, B, AB",
                Choice("Группа крови", "0 (I), A (II), B (III), AB (IV)")),
            Preset("choice_alignment", "Мировоззрение", "Девять мировоззрений: от законно-доброго до хаотично-злого",
                Choice("Мировоззрение",
                    "Законно-добрый, Нейтрально-добрый, Хаотично-добрый, Законно-нейтральный, Нейтральный, "
                    + "Хаотично-нейтральный, Законно-злой, Нейтрально-злой, Хаотично-злой",
                    CharacterFieldDisplay.Dropdown)),

            // ── Несколько ────────────────────────────────────────────────
            Preset("multi_traits", "Черты характера", "Отметить сколько угодно",
                Multi("Черты характера", "Честный, Упрямый, Добрый, Вспыльчивый, Хитрый, Трусливый")),
            Preset("multi_skills", "Навыки", "Бой, магия, скрытность, дипломатия, ремесло",
                Multi("Навыки", "Бой, Магия, Скрытность, Дипломатия, Ремесло")),
            Preset("multi_fears", "Страхи", "Темнота, высота, толпа…",
                Multi("Страхи", "Темнота, Высота, Пауки, Толпа, Одиночество")),

            // ── Да / нет ─────────────────────────────────────────────────
            Preset("bool_alive", "Жив", "Жив / мёртв", YesNo("Жив", "Жив", "Мёртв")),
            Preset("bool_married", "В браке", "В браке / холост", YesNo("В браке", "В браке", "Холост")),
            Preset("bool_secret", "Знает тайну", "Знает / не знает", YesNo("Знает тайну", "Знает", "Не знает")),

            // ── Цвет ─────────────────────────────────────────────────────
            Preset("color_eyes", "Цвет глаз", "Карие, ореховые, зелёные, голубые, серые",
                Color("Цвет глаз", "#3A2A20", "#6B4E3D", "#B08D57", "#7A9E7E", "#5B7FA8", "#8E9AA6")),
            Preset("color_hair", "Цвет волос", "От чёрного до седого",
                Color("Цвет волос", "#1E1B18", "#3A2A20", "#6B4E3D", "#A7764B", "#D9B26F", "#B5523B", "#C8C8C8", "#EDEDED")),
            Preset("color_skin", "Цвет кожи", "Светлая, смуглая, тёмная",
                Color("Цвет кожи", "#F3D9C4", "#E6BC98", "#C99A6E", "#A86E4A", "#7A4B2E", "#4A2E1E")),

            // ── Текст ────────────────────────────────────────────────────
            Preset("text_nickname", "Прозвище", "Одна строка", Text("Прозвище")),
            Preset("text_occupation", "Род занятий", "Одна строка: профессия, должность, ремесло", Text("Род занятий")),
            Preset("text_birthplace", "Место рождения", "Одна строка", Text("Место рождения")),

            // ── Описание ─────────────────────────────────────────────────
            Preset("long_appearance", "Внешность", "Несколько абзацев", Long("Внешность")),
            Preset("long_character", "Характер", "Несколько абзацев", Long("Характер")),
            Preset("long_backstory", "Прошлое", "Несколько абзацев: что с ним было до сюжета", Long("Прошлое", comparable: false)),
            Preset("long_motivation", "Мотивация", "Чего хочет и почему", Long("Мотивация", comparable: false))
        };

        private static CharacterFieldPreset Preset(string id, string name, string hint, CharacterAnketaField field) => new()
        {
            Id = "builtin_preset_" + id,
            Name = name,
            Hint = hint,
            IsBuiltIn = true,
            Field = field
        };

        private static CharacterAnketaField Choice(string name, string states,
            CharacterFieldDisplay display = CharacterFieldDisplay.Auto) => new()
        {
            Name = name,
            Type = CharacterParameterType.StateList,
            StatesRaw = states,
            Display = display
        };

        private static CharacterAnketaField Multi(string name, string states) => new()
        {
            Name = name,
            Type = CharacterParameterType.MultiChoice,
            StatesRaw = states
        };

        private static CharacterAnketaField YesNo(string name, string yes, string no) => new()
        {
            Name = name,
            Type = CharacterParameterType.Boolean,
            TrueLabel = yes,
            FalseLabel = no
        };

        private static CharacterAnketaField Color(string name, params string[] palette) => new()
        {
            Name = name,
            Type = CharacterParameterType.Color,
            Palette = palette.ToList(),
            AllowCustomColor = true
        };

        private static CharacterAnketaField Text(string name) => new()
        {
            Name = name,
            Type = CharacterParameterType.Text
        };

        private static CharacterAnketaField Long(string name, bool comparable = true) => new()
        {
            Name = name,
            Type = CharacterParameterType.LongText,
            IsComparable = comparable
        };
    }
}
