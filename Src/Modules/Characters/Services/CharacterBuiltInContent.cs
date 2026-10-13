using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Modules.Characters.Models;
using Writersword.Modules.Characters.Models.Enums;

namespace Writersword.Modules.Characters.Services
{
    /// <summary>
    /// Встроенные анкеты и шаблоны.
    ///
    /// Три уровня, и путать их нельзя: поле спрашивает одну вещь, анкета —
    /// раздел про одну сторону персонажа («Внешность», «Характер», «Бой»),
    /// шаблон — сочетание разделов под тип персонажа или жанр («Детектив /
    /// Нуар» = Внешность + Характер + Биография + Сыщик).
    ///
    /// Раньше встроенные анкеты назывались жанрами и смешивали в себе
    /// несколько разделов сразу: «Обычный человек» держал и тело, и ум, и
    /// биографию, а «Детектив / Нуар» жил и анкетой, и шаблоном одновременно.
    /// Теперь жанры — шаблоны, а анкеты — разделы, и у каждого раздела три
    /// уровня детализации: кратко, подробно, глубоко.
    ///
    /// Имена полей, перешедших из прежних анкет, не меняются: идентификатор
    /// поля выводится из имени, и по нему значения старых персонажей находят
    /// своё место в новых разделах.
    /// </summary>
    public static class CharacterBuiltInContent
    {
        // ── Идентификаторы анкет ─────────────────────────────────────────

        public const string AppearanceId = "builtin_appearance";
        public const string BodyId = "builtin_body";
        public const string PersonalityId = "builtin_personality";
        public const string BiographyId = "builtin_biography";
        public const string DetectiveId = "builtin_detective";
        public const string HorrorId = "builtin_horror";
        public const string FantasyOriginId = "builtin_fantasy_origin";
        public const string CombatId = "builtin_combat";
        public const string MagicId = "builtin_magic";
        public const string TechId = "builtin_tech";
        public const string FactionId = "builtin_faction";

        // ── Прежние анкеты ───────────────────────────────────────────────

        public const string LegacyHumanId = "builtin_human";
        public const string LegacyFantasyWarriorId = "builtin_fantasy_warrior";
        public const string LegacyScifiId = "builtin_scifi";

        /// <summary>
        /// Во что превратились прежние жанровые анкеты. «Сыщик», «Рассудок» и
        /// «Народ / Группа» сохранили свои идентификаторы — они и были одним
        /// разделом, и в перечне их нет. Остальные разошлись по разделам так,
        /// чтобы каждое прежнее поле нашло новое место.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string[]> LegacyAnketaIds =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [LegacyHumanId] = new[] { AppearanceId, BodyId, PersonalityId, BiographyId },
                [LegacyFantasyWarriorId] = new[] { BodyId, FantasyOriginId, CombatId, MagicId },
                [LegacyScifiId] = new[] { TechId, FactionId }
            };

        public static bool IsLegacyId(string? id) =>
            !string.IsNullOrEmpty(id) && LegacyAnketaIds.ContainsKey(id);

        /// <summary>
        /// Список анкет с прежними идентификаторами заменёнными на новые
        /// разделы. Порядок сохраняется, повторы убираются: «Тело» из
        /// «Обычного человека» и из «Воина» — один раздел.
        /// </summary>
        public static List<string> ExpandLegacyIds(IEnumerable<string>? ids)
        {
            var result = new List<string>();
            if (ids == null) return result;

            foreach (var id in ids)
            {
                if (string.IsNullOrEmpty(id)) continue;

                if (LegacyAnketaIds.TryGetValue(id, out var replacement))
                {
                    foreach (var newId in replacement)
                        if (!result.Contains(newId)) result.Add(newId);
                    continue;
                }

                if (!result.Contains(id)) result.Add(id);
            }

            return result;
        }

        /// <summary>Список содержит прежние идентификаторы — его пора переписать.</summary>
        public static bool HasLegacyIds(IEnumerable<string>? ids) =>
            ids != null && ids.Any(IsLegacyId);

        // ── Анкеты ───────────────────────────────────────────────────────

        public static List<CharacterAnketa> BuildAnketas() => new()
        {
            BuildAppearance(),
            BuildBody(),
            BuildPersonality(),
            BuildBiography(),
            BuildDetective(),
            BuildHorror(),
            BuildFantasyOrigin(),
            BuildCombat(),
            BuildMagic(),
            BuildTech(),
            BuildFaction(),
            BuildCollective()
        };

        private static CharacterAnketa BuildAppearance() =>
            new Sheet(AppearanceId, "Внешность",
                    "Облик персонажа: от пары штрихов до манеры держаться",
                    "palette", "#5B7FA8", CharacterAnketaAudience.Individual)
                .Group("Главное", CharacterDetailLevels.Brief)
                .Row(
                    new CharacterAnketaField
                    {
                        Name = "Возраст", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 120, Step = 1,
                        RandomRangeMin = 18, RandomRangeMax = 60,
                        Description = "Возраст персонажа в годах"
                    },
                    Number("Рост", "см", "Рост в сантиметрах"),
                    Number("Вес", "кг", "Вес в килограммах"))
                .Row(Long("Примечательная внешность",
                    "Что бросается в глаза с первого взгляда. Эпизодическому персонажу этого достаточно"))
                .Group("Черты", CharacterDetailLevels.Detailed)
                .Row(
                    Choice("Телосложение", "Хрупкое, Худощавое, Стройное, Среднее, Плотное, Атлетичное, Крупное, Полное"),
                    Color("Цвет глаз", "#3A2A20", "#6B4E3D", "#B08D57", "#7A9E7E", "#5B7FA8", "#8E9AA6"),
                    Color("Цвет волос", "#1E1B18", "#3A2A20", "#6B4E3D", "#A7764B", "#D9B26F", "#B5523B", "#C8C8C8", "#EDEDED"))
                .Row(Text("Особые приметы", "Шрамы, татуировки, родинки — то, по чему узнают"))
                .Group("Манера", CharacterDetailLevels.Deep)
                .Row(
                    Text("Походка и осанка", "Как двигается и держится"),
                    Text("Мимика и жесты", "Привычные выражения лица и движения рук"))
                .Row(Long("Голос и речь", "Тембр, темп, словечки, акцент"))
                .Row(Long("Стиль одежды", "Что носит и почему: вкус, достаток, привычка, маскировка"))
                .Build();

        private static CharacterAnketa BuildBody() =>
            new Sheet(BodyId, "Тело и здоровье",
                    "Физическое состояние: здоровье, сила, выносливость, травмы",
                    "health", "#7FB069", CharacterAnketaAudience.Individual)
                .Group("Состояние", CharacterDetailLevels.Brief)
                .Row(
                    new CharacterAnketaField
                    {
                        Name = "Здоровье", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 100,
                        RandomRangeMin = 40, RandomRangeMax = 100,
                        MinDescription = "При смерти, нуждается в экстренной помощи",
                        MaxDescription = "Абсолютно здоров, никаких симптомов",
                        ScalePoints = new Dictionary<double, string>
                        {
                            { 10, "Тяжёлое состояние, не может передвигаться" },
                            { 30, "Ранен или болен, с трудом держится на ногах" },
                            { 60, "Работоспособен несмотря на усталость" },
                            { 90, "Отличная форма, полная боеспособность" }
                        }
                    },
                    new CharacterAnketaField
                    {
                        Name = "Статус жизни", Type = CharacterParameterType.StateList,
                        StatesRaw = "Жив, Ранен, В коме, Мёртв"
                    })
                .Group("Физическая форма", CharacterDetailLevels.Detailed)
                .Row(
                    new CharacterAnketaField
                    {
                        Name = "Сила", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 100,
                        RandomRangeMin = 10, RandomRangeMax = 70,
                        MinDescription = "Не может провернуть дверную ручку",
                        MaxDescription = "Содрогает землю ударом",
                        ScalePoints = new Dictionary<double, string>
                        {
                            { 10, "Может поднять нетяжёлые предметы, сломать ветку" },
                            { 30, "Таскает мешки с цементом, рубит дрова" },
                            { 60, "Опрокидывает шкаф, ломает доску об колено" },
                            { 80, "Ломает кирпичи руками, гнёт арматуру" },
                            { 100, "Абсолют — физически невозможное для обычного человека" }
                        }
                    },
                    new CharacterAnketaField
                    {
                        Name = "Выносливость", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 100,
                        RandomRangeMin = 20, RandomRangeMax = 80,
                        MinDescription = "Задыхается от подъёма на один пролёт",
                        MaxDescription = "Может сутками двигаться без отдыха",
                        ScalePoints = new Dictionary<double, string>
                        {
                            { 20, "Выдыхается при лёгкой нагрузке" },
                            { 50, "Средний уровень физической подготовки" },
                            { 80, "Марафонец, выносливость выше нормы" }
                        }
                    })
                .Group("Особенности", CharacterDetailLevels.Deep)
                .Row(Long("Травмы и болезни", "Что болит, что было сломано, с чем живёт"))
                .Row(
                    Text("Режим и привычки", "Сон, еда, вредные привычки"),
                    Text("Непереносимости", "Аллергии, на что реагирует тело"))
                .Build();

        private static CharacterAnketa BuildPersonality() =>
            new Sheet(PersonalityId, "Характер",
                    "Личность: от пары слов до типа личности, страхов и мотивов",
                    "mood", "#C06FA0", CharacterAnketaAudience.Individual)
                .Group("Главное", CharacterDetailLevels.Brief)
                .Row(Text("Характер в двух словах", "«Ворчливый добряк», «тихий фанатик»"))
                .Row(
                    new CharacterAnketaField
                    {
                        Name = "Интеллект", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 100,
                        RandomRangeMin = 30, RandomRangeMax = 80,
                        MinDescription = "Не способен к абстрактному мышлению",
                        MaxDescription = "Гений, видит закономерности недоступные другим",
                        ScalePoints = new Dictionary<double, string>
                        {
                            { 30, "Справляется с простыми задачами и инструкциями" },
                            { 50, "Средний уровень, ~100 IQ" },
                            { 70, "Образован, быстро обучается, ~120 IQ" },
                            { 90, "Редкий интеллект, ~140+ IQ" }
                        }
                    },
                    new CharacterAnketaField
                    {
                        Name = "Харизма", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 100,
                        RandomRangeMin = 20, RandomRangeMax = 80,
                        MinDescription = "Отталкивает людей одним присутствием",
                        MaxDescription = "Ведёт за собой толпы, прирождённый лидер",
                        ScalePoints = new Dictionary<double, string>
                        {
                            { 20, "Замкнут, избегает контактов" },
                            { 50, "Обычный в общении, не выделяется" },
                            { 80, "Располагает к себе, умеет убеждать" }
                        }
                    })
                .Group("Черты", CharacterDetailLevels.Detailed)
                .Row(Choice("Темперамент", "Холерик, Сангвиник, Флегматик, Меланхолик"))
                .Row(
                    Text("Сильные стороны", "За что его ценят"),
                    Text("Слабости", "Где он уязвим"))
                .Row(Text("Привычки и причуды", "Что делает, сам того не замечая"))
                .Group("Глубина", CharacterDetailLevels.Deep)
                .Row(Choice("Тип личности",
                    "ISTJ, ISFJ, INFJ, INTJ, ISTP, ISFP, INFP, INTP, ESTP, ESFP, ENFP, ENTP, ESTJ, ESFJ, ENFJ, ENTJ",
                    CharacterFieldDisplay.Dropdown))
                .Row(
                    Pole("Открытость опыту", "Держится привычного", "Жаден до нового"),
                    Pole("Добросовестность", "Живёт порывом", "Всё по плану"))
                .Row(
                    Pole("Экстраверсия", "Заряжается в одиночестве", "Заряжается от людей"),
                    Pole("Доброжелательность", "Подозрителен и колюч", "Мягок и доверчив"))
                .Row(Pole("Эмоциональная устойчивость", "Легко выбить из колеи", "Невозмутим"))
                .Row(
                    Text("Главный страх", "Чего боится больше всего"),
                    Text("Заветное желание", "Чего хочет на самом деле"))
                .Row(Multi("Ценности",
                    "Свобода, Семья, Власть, Знание, Справедливость, Вера, Богатство, Любовь, Честь, Безопасность, Слава, Месть"))
                .Row(Long("Внутренний конфликт", "Что тянет его в разные стороны"))
                .Row(Long("Под давлением", "Как ведёт себя, когда всё рушится", comparable: false))
                .Build();

        private static CharacterAnketa BuildBiography() =>
            new Sheet(BiographyId, "Биография",
                    "Прошлое и место в мире: от профессии до тайн",
                    "history", "#D4A33B", CharacterAnketaAudience.Individual)
                .Group("Кратко", CharacterDetailLevels.Brief)
                .Row(
                    Text("Профессия"),
                    Text("Место рождения"))
                .Group("Жизнь", CharacterDetailLevels.Detailed)
                .Row(
                    Text("Образование"),
                    Choice("Социальное положение", "Низы, Бедность, Средний достаток, Состоятельность, Знать, Элита"))
                .Row(Long("Семья и близкие", "Кто рядом, кто был рядом, с кем порвал"))
                .Group("Прошлое", CharacterDetailLevels.Deep)
                .Row(Long("Ключевые события", "Что сделало его таким, какой он есть"))
                .Row(Long("Лучшее воспоминание", "К чему он возвращается мыслями", comparable: false))
                .Row(Long("Тайна", "Что он скрывает — и от кого", comparable: false))
                .Build();

        private static CharacterAnketa BuildDetective() =>
            new Sheet(DetectiveId, "Сыщик",
                    "Профессиональные качества, психология и пороки сыщика",
                    "search", "#9A8F85", CharacterAnketaAudience.Individual,
                    "Детектив", "Нуар", "Триллер")
                .Group("Профессиональное", CharacterDetailLevels.Brief)
                .Row(
                    new CharacterAnketaField
                    {
                        Name = "Наблюдательность", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 100,
                        RandomRangeMin = 30, RandomRangeMax = 90,
                        MinDescription = "Ничего не замечает, легко обмануть",
                        MaxDescription = "Видит всё — каждую деталь, каждую ложь",
                        ScalePoints = new Dictionary<double, string>
                        {
                            { 30, "Замечает очевидное, упускает детали" },
                            { 60, "Профессиональный уровень" },
                            { 90, "Шерлоковский уровень" }
                        }
                    },
                    new CharacterAnketaField
                    {
                        Name = "Дедукция", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 100,
                        RandomRangeMin = 20, RandomRangeMax = 90,
                        MinDescription = "Не может связать очевидные факты",
                        MaxDescription = "Строит цепочки из ничего"
                    })
                .Group("Статус", CharacterDetailLevels.Brief)
                .Row(
                    new CharacterAnketaField
                    {
                        Name = "Статус", Type = CharacterParameterType.StateList,
                        StatesRaw = "Активен, Отстранён, Под следствием, В бегах, Мёртв"
                    },
                    new CharacterAnketaField
                    {
                        Name = "Лицензия", Type = CharacterParameterType.Boolean,
                        TrueLabel = "Действующая", FalseLabel = "Отозвана"
                    })
                .Group("Психология", CharacterDetailLevels.Detailed)
                .Row(
                    new CharacterAnketaField
                    {
                        Name = "Доверие к системе", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 100,
                        RandomRangeMin = 0, RandomRangeMax = 100,
                        MinDescription = "Полный циник, законы не для него",
                        MaxDescription = "Свято верит в закон и справедливость"
                    },
                    new CharacterAnketaField
                    {
                        Name = "Моральная гибкость", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 100,
                        RandomRangeMin = 0, RandomRangeMax = 100,
                        MinDescription = "Абсолютная принципиальность, никаких компромиссов",
                        MaxDescription = "Любые методы если цель оправдывает"
                    })
                .Group("Пороки", CharacterDetailLevels.Detailed)
                .Row(
                    new CharacterAnketaField
                    {
                        Name = "Алкоголизм", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 100,
                        RandomRangeMin = 0, RandomRangeMax = 50,
                        MinDescription = "Абсолютный трезвенник",
                        MaxDescription = "Не может функционировать без бутылки",
                        ScalePoints = new Dictionary<double, string>
                        {
                            { 30, "Пьёт по вечерам, но контролирует себя" },
                            { 60, "Заметная зависимость, влияет на работу" },
                            { 90, "Хронический алкоголик" }
                        }
                    })
                .Group("Методы", CharacterDetailLevels.Deep)
                .Row(Long("Методы работы", "Как ведёт расследование: давит, выжидает, подкупает"))
                .Row(Long("Дело, которое не отпускает", "Нераскрытое, проигранное, личное", comparable: false))
                .Build();

        private static CharacterAnketa BuildHorror() =>
            new Sheet(HorrorId, "Рассудок",
                    "Психика под давлением — для хоррора и психологического триллера",
                    "skull", "#8A6FC0", CharacterAnketaAudience.Individual,
                    "Хоррор", "Психологический триллер", "Мистика")
                .Group("Психология", CharacterDetailLevels.Brief)
                .Row(
                    new CharacterAnketaField
                    {
                        Name = "Рассудок", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 100,
                        RandomRangeMin = 50, RandomRangeMax = 100,
                        MinDescription = "Полное безумие — оторван от реальности",
                        MaxDescription = "Абсолютно здрав, критически мыслит",
                        ScalePoints = new Dictionary<double, string>
                        {
                            { 10, "Галлюцинации, неотличимые от реальности" },
                            { 30, "Тяжёлые психические расстройства" },
                            { 60, "Нестабилен, срывы под давлением" },
                            { 80, "Небольшие тики, фобии" }
                        }
                    },
                    new CharacterAnketaField
                    {
                        Name = "Страх", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 100,
                        RandomRangeMin = 20, RandomRangeMax = 80,
                        MinDescription = "Абсолютное бесстрашие — патологическое отсутствие страха",
                        MaxDescription = "Парализующий ужас от малейшей угрозы",
                        ScalePoints = new Dictionary<double, string>
                        {
                            { 20, "Смелый, контролирует страх" },
                            { 50, "Нормальная реакция на опасность" },
                            { 80, "Панические атаки в стрессе" }
                        }
                    })
                .Group("Статус", CharacterDetailLevels.Brief)
                .Row(
                    new CharacterAnketaField
                    {
                        Name = "Психологическое состояние", Type = CharacterParameterType.StateList,
                        StatesRaw = "Стабилен, Взволнован, В панике, Диссоциация, Психоз, Кататония"
                    })
                .Group("Под давлением", CharacterDetailLevels.Detailed)
                .Row(
                    new CharacterAnketaField
                    {
                        Name = "Паранойя", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 100,
                        RandomRangeMin = 0, RandomRangeMax = 60,
                        MinDescription = "Полностью доверяет окружающим",
                        MaxDescription = "Видит угрозу в каждом человеке и предмете"
                    },
                    new CharacterAnketaField
                    {
                        Name = "Воля к выживанию", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 100,
                        RandomRangeMin = 30, RandomRangeMax = 100,
                        MinDescription = "Сдался, принял смерть",
                        MaxDescription = "Выживет любой ценой, инстинкт на пределе"
                    })
                .Group("Особое", CharacterDetailLevels.Detailed)
                .Row(
                    new CharacterAnketaField
                    {
                        Name = "Видит сверхъестественное", Type = CharacterParameterType.Boolean,
                        TrueLabel = "Да", FalseLabel = "Нет"
                    })
                .Group("Тьма внутри", CharacterDetailLevels.Deep)
                .Row(Text("Главная фобия", "Чего боится до потери контроля"))
                .Row(Long("Пережитый ужас", "Что он уже видел и не может забыть", comparable: false))
                .Row(Long("Что его сломает", "Последняя капля", comparable: false))
                .Build();

        private static CharacterAnketa BuildFantasyOrigin() =>
            new Sheet(FantasyOriginId, "Происхождение",
                    "Раса, класс и место в фэнтезийном мире",
                    "globe", "#E08A3C", CharacterAnketaAudience.Individual,
                    "Фэнтези", "РПГ", "Эпик")
                .Group("Кто он", CharacterDetailLevels.Brief)
                .Row(
                    Text("Раса"),
                    Text("Класс"))
                .Group("Мир", CharacterDetailLevels.Detailed)
                .Row(Choice("Мировоззрение",
                    "Законопослушный добрый, Нейтральный добрый, Хаотичный добрый, Законопослушный нейтральный, Истинный нейтральный, Хаотичный нейтральный, Законопослушный злой, Нейтральный злой, Хаотичный злой",
                    CharacterFieldDisplay.Dropdown))
                .Row(Text("Родина", "Откуда он и что о ней думает"))
                .Group("Судьба", CharacterDetailLevels.Deep)
                .Row(Long("Предназначение", "Пророчество, клятва, долг крови"))
                .Row(Long("Отношение к своему народу", "Гордится, стыдится, бежал"))
                .Build();

        private static CharacterAnketa BuildCombat() =>
            new Sheet(CombatId, "Бой",
                    "Как персонаж дерётся: опыт, оружие, стиль",
                    "shield", "#C75B5B", CharacterAnketaAudience.Individual,
                    "Фэнтези", "РПГ", "Эпик")
                .Group("Главное", CharacterDetailLevels.Brief)
                .Row(
                    new CharacterAnketaField
                    {
                        Name = "Ловкость", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 1, DefaultMaxValue = 20, Step = 1,
                        RandomRangeMin = 6, RandomRangeMax = 16,
                        MinDescription = "Неуклюжий, постоянно спотыкается",
                        MaxDescription = "Движется как тень, неуловим"
                    },
                    Choice("Боевой опыт", "Новичок, Обученный, Ветеран, Мастер, Легенда"))
                .Row(Text("Оружие", "Чем сражается"))
                .Group("Стиль", CharacterDetailLevels.Detailed)
                .Row(
                    Choice("Стиль боя", "Напор, Защита, Уклонение, Хитрость, Дистанция, Магия"),
                    Text("Доспех"))
                .Group("Шрамы", CharacterDetailLevels.Deep)
                .Row(Text("Слабое место в бою", "Где его можно достать"))
                .Row(Long("Памятные схватки", "Бои, которые оставили след", comparable: false))
                .Build();

        private static CharacterAnketa BuildMagic() =>
            new Sheet(MagicId, "Магия",
                    "Магический дар: есть ли он, откуда и чего стоит",
                    "magic", "#8A6FC0", CharacterAnketaAudience.Any,
                    "Фэнтези", "РПГ", "Эпик")
                .Group("Дар", CharacterDetailLevels.Brief)
                .Row(
                    new CharacterAnketaField
                    {
                        Name = "Владеет магией", Type = CharacterParameterType.Boolean,
                        TrueLabel = "Да", FalseLabel = "Нет"
                    },
                    new CharacterAnketaField
                    {
                        Name = "Мана", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 100,
                        RandomRangeMin = 0, RandomRangeMax = 60,
                        MinDescription = "Нет магического дара",
                        MaxDescription = "Безграничный магический резерв"
                    })
                .Group("Школа", CharacterDetailLevels.Detailed)
                .Row(
                    Text("Школа или стихия"),
                    Text("Источник силы", "Откуда берётся магия"))
                .Group("Цена", CharacterDetailLevels.Deep)
                .Row(Long("Цена магии", "Чем приходится платить"))
                .Row(Long("Предел возможностей", "Чего магия не может даже у него"))
                .Build();

        private static CharacterAnketa BuildTech() =>
            new Sheet(TechId, "Технологии",
                    "Тело и навыки в мире научной фантастики",
                    "flight", "#4FA39A", CharacterAnketaAudience.Individual,
                    "Sci-fi", "Киберпанк", "Космическая опера")
                .Group("Кто он", CharacterDetailLevels.Brief)
                .Row(
                    new CharacterAnketaField
                    {
                        Name = "Биологический вид", Type = CharacterParameterType.StateList,
                        StatesRaw = "Человек, Клон, Андроид, Мутант, Инопланетянин, Гибрид, ИИ"
                    },
                    new CharacterAnketaField
                    {
                        Name = "Уровень кибернетизации", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 100,
                        RandomRangeMin = 0, RandomRangeMax = 70,
                        MinDescription = "Чистый биологический организм",
                        MaxDescription = "Почти полная машина — человеческого почти не осталось",
                        ScalePoints = new Dictionary<double, string>
                        {
                            { 10, "Незначительные импланты (сетчатка, слух)" },
                            { 30, "Протезы конечностей или органов" },
                            { 60, "Кибернетический скелет, нейроинтерфейс" },
                            { 90, "Почти полная замена тела" }
                        }
                    })
                .Group("Навыки", CharacterDetailLevels.Detailed)
                .Row(
                    new CharacterAnketaField
                    {
                        Name = "Хакерство", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 100,
                        RandomRangeMin = 0, RandomRangeMax = 80,
                        MinDescription = "Не умеет пользоваться компьютером",
                        MaxDescription = "Взламывает защищённые системы за секунды"
                    },
                    new CharacterAnketaField
                    {
                        Name = "Пилотирование", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 100,
                        RandomRangeMin = 0, RandomRangeMax = 80
                    })
                .Row(
                    new CharacterAnketaField
                    {
                        Name = "Бессмертен", Type = CharacterParameterType.Boolean,
                        TrueLabel = "Да", FalseLabel = "Нет"
                    })
                .Group("Начинка", CharacterDetailLevels.Deep)
                .Row(Long("Импланты", "Что в нём от машины и зачем"))
                .Row(Long("Отношение к своему телу", "Гордость, отвращение, равнодушие", comparable: false))
                .Build();

        private static CharacterAnketa BuildFaction() =>
            new Sheet(FactionId, "Фракция",
                    "Принадлежность к стороне: кому служит и насколько верен",
                    "crown", "#5B7FA8", CharacterAnketaAudience.Any,
                    "Sci-fi", "Киберпанк", "Космическая опера", "Эпик")
                .Group("Сторона", CharacterDetailLevels.Brief)
                .Row(
                    Text("Фракция"),
                    new CharacterAnketaField
                    {
                        Name = "Лояльность фракции", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 100,
                        RandomRangeMin = 20, RandomRangeMax = 90,
                        MinDescription = "Предатель, работает на несколько сторон",
                        MaxDescription = "Фанатично предан, умрёт за фракцию"
                    })
                .Group("Положение", CharacterDetailLevels.Detailed)
                .Row(Text("Положение во фракции", "Рядовой, офицер, глава"))
                .Row(Long("Цели фракции", "Чего добивается сторона"))
                .Group("Связи", CharacterDetailLevels.Deep)
                .Row(Long("Что связывает и что держит", "Долг, страх, вера, выгода", comparable: false))
                .Build();

        private static CharacterAnketa BuildCollective() =>
            new Sheet(CharacterAnketa.CollectiveId, "Народ / Группа",
                    "Общее для народа, расы, организации: численность, нравы, культура",
                    "group", "#D4A33B", CharacterAnketaAudience.Collective)
                .Group("Описание группы", CharacterDetailLevels.Brief)
                .Row(
                    new CharacterAnketaField
                    {
                        Name = "Численность", Type = CharacterParameterType.Text,
                        Description = "Примерная численность группы или населения"
                    },
                    new CharacterAnketaField
                    {
                        Name = "Территория", Type = CharacterParameterType.Text,
                        Description = "Где проживает группа"
                    })
                .Group("Статус", CharacterDetailLevels.Brief)
                .Row(
                    new CharacterAnketaField
                    {
                        Name = "Уровень угрозы", Type = CharacterParameterType.StateList,
                        StatesRaw = "Мирные, Нейтральные, Враждебные, Опасные, Смертоносные"
                    })
                .Group("Характеристики группы", CharacterDetailLevels.Detailed)
                .Row(
                    new CharacterAnketaField
                    {
                        Name = "Средний интеллект", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 100,
                        RandomRangeMin = 40, RandomRangeMax = 70,
                        MinDescription = "Примитивное мышление, нет абстракции",
                        MaxDescription = "Высокоразвитая цивилизация, средний IQ 140+",
                        ScalePoints = new Dictionary<double, string>
                        {
                            { 40, "Ниже среднего, ~85 IQ" },
                            { 50, "Средний, ~100 IQ" },
                            { 70, "Выше среднего, ~115 IQ" },
                            { 90, "Высокоразвитые, ~130+ IQ" }
                        }
                    },
                    new CharacterAnketaField
                    {
                        Name = "Средняя сила", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 100,
                        RandomRangeMin = 20, RandomRangeMax = 60,
                        MinDescription = "Физически слабые существа",
                        MaxDescription = "Физически превосходящие людей в разы"
                    })
                .Group("Социальное", CharacterDetailLevels.Detailed)
                .Row(
                    new CharacterAnketaField
                    {
                        Name = "Отношение к чужакам", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 100,
                        RandomRangeMin = 0, RandomRangeMax = 100,
                        MinDescription = "Убивают чужаков на месте",
                        MaxDescription = "Открытое гостеприимство, принимают всех",
                        ScalePoints = new Dictionary<double, string>
                        {
                            { 10, "Агрессивная ксенофобия" },
                            { 30, "Недоверие, избегают контактов" },
                            { 50, "Нейтральны, торгуют но не доверяют" },
                            { 70, "Дружелюбны при знакомстве" }
                        }
                    },
                    new CharacterAnketaField
                    {
                        Name = "Лояльность власти", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 100,
                        RandomRangeMin = 20, RandomRangeMax = 80,
                        MinDescription = "Открытый бунт, не признают власть",
                        MaxDescription = "Слепое подчинение, не рассуждают"
                    })
                .Group("Культура", CharacterDetailLevels.Deep)
                .Row(Long("Обычаи и традиции", "Праздники, запреты, ритуалы"))
                .Row(Long("Устройство власти", "Кто правит и как"))
                .Row(Long("Вера и легенды", "Во что верят, что рассказывают детям"))
                .Row(Long("Внутренние противоречия", "Что раскалывает группу изнутри"))
                .Build();

        // ── Шаблоны ──────────────────────────────────────────────────────

        /// <summary>
        /// Встроенные шаблоны — жанровые сочетания встроенных разделов. Новых
        /// полей они не заводят. Глубину не задают: разделы раскрываются по
        /// важности персонажа — главному глубоко, эпизодическому кратко.
        /// </summary>
        public static List<CharacterTemplate> BuildTemplates() => new()
        {
            new CharacterTemplate
            {
                Id = "builtin_template_person",
                Name = "Обычный человек",
                Description = "Внешность, тело, характер и биография — основа любого персонажа.",
                IsBuiltIn = true,
                Icon = "person",
                IconColor = "#5B7FA8",
                AnketaIds = new List<string> { AppearanceId, BodyId, PersonalityId, BiographyId }
            },
            new CharacterTemplate
            {
                Id = "builtin_template_detective",
                Name = "Детектив / Нуар",
                Description = "Основа персонажа и профессиональные качества сыщика.",
                IsBuiltIn = true,
                Icon = "search",
                IconColor = "#9A8F85",
                AnketaIds = new List<string> { AppearanceId, PersonalityId, BiographyId, DetectiveId }
            },
            new CharacterTemplate
            {
                Id = "builtin_template_fantasy",
                Name = "Фэнтези: Воин",
                Description = "Облик, характер, происхождение, бой и магия.",
                IsBuiltIn = true,
                Icon = "shield",
                IconColor = "#C75B5B",
                AnketaIds = new List<string> { AppearanceId, BodyId, PersonalityId, FantasyOriginId, CombatId, MagicId }
            },
            new CharacterTemplate
            {
                Id = "builtin_template_horror",
                Name = "Хоррор / Психологический",
                Description = "Основа персонажа и рассудок под давлением.",
                IsBuiltIn = true,
                Icon = "skull",
                IconColor = "#8A6FC0",
                AnketaIds = new List<string> { AppearanceId, PersonalityId, BiographyId, HorrorId }
            },
            new CharacterTemplate
            {
                Id = "builtin_template_scifi",
                Name = "Научная фантастика",
                Description = "Облик, тело, характер, технологии и фракция.",
                IsBuiltIn = true,
                Icon = "flight",
                IconColor = "#4FA39A",
                AnketaIds = new List<string> { AppearanceId, BodyId, PersonalityId, TechId, FactionId }
            },
            new CharacterTemplate
            {
                Id = "builtin_template_collective",
                Name = "Народ / Группа",
                Description = "Народ, раса, организация: общее описание и фракция.",
                IsBuiltIn = true,
                Icon = "group",
                IconColor = "#D4A33B",
                AnketaIds = new List<string> { CharacterAnketa.CollectiveId, FactionId }
            }
        };

        /// <summary>
        /// Шаблон под жанровые пометки проекта — для первого знакомства:
        /// ответы о том, что пишет автор, превращаются в готовый набор
        /// разделов. Без совпадений — «Обычный человек».
        /// </summary>
        public static string RecommendTemplateId(IEnumerable<string> tags)
        {
            var set = new HashSet<string>(tags ?? Array.Empty<string>(), StringComparer.CurrentCultureIgnoreCase);

            if (set.Contains("Детектив") || set.Contains("Нуар") || set.Contains("Триллер"))
                return "builtin_template_detective";
            if (set.Contains("Хоррор") || set.Contains("Психологический триллер") || set.Contains("Мистика"))
                return "builtin_template_horror";
            if (set.Contains("Фэнтези") || set.Contains("РПГ"))
                return "builtin_template_fantasy";
            if (set.Contains("Sci-fi") || set.Contains("Киберпанк") || set.Contains("Космическая опера"))
                return "builtin_template_scifi";

            return "builtin_template_person";
        }

        // ── Сборка ───────────────────────────────────────────────────────

        private static CharacterAnketaField Text(string name, string description = "") => new()
        {
            Name = name,
            Type = CharacterParameterType.Text,
            Description = description
        };

        private static CharacterAnketaField Long(string name, string description = "", bool comparable = true) => new()
        {
            Name = name,
            Type = CharacterParameterType.LongText,
            Description = description,
            IsComparable = comparable
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

        private static CharacterAnketaField Number(string name, string unit, string description) => new()
        {
            Name = name,
            Type = CharacterParameterType.Number,
            Unit = unit,
            Description = description,
            Step = 1,
            NumberMin = 0
        };

        private static CharacterAnketaField Color(string name, params string[] palette) => new()
        {
            Name = name,
            Type = CharacterParameterType.Color,
            Palette = palette.ToList(),
            AllowCustomColor = true
        };

        /// <summary>Шкала между двумя полюсами — черта личности, а не «сколько».</summary>
        private static CharacterAnketaField Pole(string name, string left, string right) => new()
        {
            Name = name,
            Type = CharacterParameterType.Numeric,
            DefaultMinValue = 0,
            DefaultMaxValue = 10,
            Step = 1,
            RandomRangeMin = 2,
            RandomRangeMax = 8,
            Display = CharacterFieldDisplay.Bipolar,
            MinDescription = left,
            MaxDescription = right
        };

        /// <summary>
        /// Анкета, собираемая сверху вниз: подзаголовок с уровнем, под ним
        /// строки полей. Ключ поля выводится из имени — раскладка ссылается на
        /// него, и он одинаков при каждом запуске.
        /// </summary>
        private sealed class Sheet
        {
            private readonly CharacterAnketa _anketa;

            public Sheet(string id, string name, string description, string icon, string iconColor,
                CharacterAnketaAudience audience, params string[] tags)
            {
                _anketa = new CharacterAnketa
                {
                    Id = id,
                    Name = name,
                    Description = description,
                    IsBuiltIn = true,
                    Icon = icon,
                    IconColor = iconColor,
                    Audience = audience,
                    ProjectTypeTags = tags.ToList()
                };
            }

            public Sheet Group(string title, int depth)
            {
                _anketa.Layout.Add(new CharacterAnketaRow
                {
                    Kind = CharacterAnketaRowKind.Group,
                    Title = title,
                    Depth = depth
                });
                return this;
            }

            public Sheet Row(params CharacterAnketaField[] fields)
            {
                var row = new CharacterAnketaRow();
                foreach (var field in fields.Take(CharacterAnketaLayout.MaxColumns))
                {
                    field.Key = "f_" + CharacterFieldId.FromName(field.Name);
                    field.Order = _anketa.Fields.Count;
                    _anketa.Fields.Add(field);
                    row.Cells.Add(field.Key);
                }
                _anketa.Layout.Add(row);
                return this;
            }

            public CharacterAnketa Build() => _anketa;
        }
    }
}
