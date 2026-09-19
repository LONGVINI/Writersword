using Newtonsoft.Json;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Modules.Characters.Interfaces;
using Writersword.Modules.Characters.Services;
using Writersword.Modules.Characters.Models;
using Writersword.Modules.Characters.Models.Enums;

namespace Writersword.Modules.Characters.Services
{
    /// <summary>
    /// Реализация сервиса управления анкетами (шаблонами) персонажей.
    /// Содержит встроенные шаблоны с описанием точек шкалы и диапазонами рандомизации.
    /// Управляет пользовательскими шаблонами.
    /// </summary>
    public class CharacterAnketaService : ICharacterAnketaService
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterAnketaService>();
        private static readonly Random _random = new();

        private readonly List<CharacterAnketa> _customAnketas = new();
        private readonly List<CharacterAnketa> _builtInAnketas;

        public CharacterAnketaService()
        {
            _builtInAnketas = BuildBuiltInAnketas();

            // Встроенные шаблоны есть с первой секунды, ещё до открытия
            // проекта: без них список шаблонов у нового проекта был бы пуст,
            // и первое, что увидел бы человек, — пустоту вместо готовых
            // сочетаний. Загрузка проекта потом дополняет их своими.
            _templates.AddRange(BuildBuiltInTemplates());
        }

        // ── Получение ─────────────────────────────────────────────────────

        /// <inheritdoc/>
        public IReadOnlyList<CharacterAnketa> GetAll() =>
            _builtInAnketas.Concat(_customAnketas).ToList().AsReadOnly();

        /// <inheritdoc/>
        public IReadOnlyList<CharacterAnketa> GetBuiltIn() =>
            _builtInAnketas.AsReadOnly();

        /// <inheritdoc/>
        public IReadOnlyList<CharacterAnketa> GetCustom() =>
            _customAnketas.AsReadOnly();

        /// <inheritdoc/>
        public IReadOnlyList<CharacterAnketa> GetRecommended(IEnumerable<string> projectTypeTags)
        {
            var tags = projectTypeTags.ToList();
            return GetAll()
                .Where(a => a.ProjectTypeTags.Count == 0 || a.ProjectTypeTags.Any(t => tags.Contains(t)))
                .ToList()
                .AsReadOnly();
        }

        /// <inheritdoc/>
        public CharacterAnketa? GetById(string id) =>
            GetAll().FirstOrDefault(a => a.Id == id);

        // ── CRUD пользовательских ─────────────────────────────────────────

        /// <inheritdoc/>
        public CharacterAnketa Create(string name)
        {
            var anketa = new CharacterAnketa
            {
                Id = Guid.NewGuid().ToString(),
                Name = name,
                IsBuiltIn = false,
                CreatedAt = DateTime.UtcNow
            };

            _customAnketas.Add(anketa);
            _logger.Debug("Anketa created: {Id} '{Name}'", anketa.Id, anketa.Name);
            return anketa;
        }

        /// <inheritdoc/>
        public void Update(CharacterAnketa anketa)
        {
            if (anketa.IsBuiltIn)
            {
                _logger.Warning("Cannot update built-in anketa: {Id}", anketa.Id);
                return;
            }

            var existing = _customAnketas.FirstOrDefault(a => a.Id == anketa.Id);
            if (existing == null)
            {
                _logger.Warning("Update failed: anketa not found {Id}", anketa.Id);
                return;
            }

            var index = _customAnketas.IndexOf(existing);
            _customAnketas[index] = anketa;
            _logger.Debug("Anketa updated: {Id} '{Name}'", anketa.Id, anketa.Name);
        }

        /// <inheritdoc/>
        public void Delete(string id)
        {
            var anketa = _customAnketas.FirstOrDefault(a => a.Id == id);
            if (anketa == null)
            {
                _logger.Warning("Delete failed: custom anketa not found {Id}", id);
                return;
            }

            _customAnketas.Remove(anketa);
            _logger.Debug("Anketa deleted: {Id}", id);
        }

        /// <inheritdoc/>
        public CharacterAnketa Duplicate(string id)
        {
            var original = GetById(id);
            if (original == null)
                throw new InvalidOperationException($"Anketa not found: {id}");

            var json = JsonConvert.SerializeObject(original);
            var copy = JsonConvert.DeserializeObject<CharacterAnketa>(json)!;

            copy.Id = Guid.NewGuid().ToString();
            copy.Name = $"{original.Name} (копия)";
            copy.IsBuiltIn = false;
            copy.CreatedAt = DateTime.UtcNow;

            _customAnketas.Add(copy);
            _logger.Debug("Anketa duplicated: {OriginalId} -> {NewId}", id, copy.Id);
            return copy;
        }

        // ── Построение параметров ─────────────────────────────────────────

        /// <inheritdoc/>
        public List<CharacterParameter> BuildParameters(CharacterAnketa anketa)
        {
            return anketa.Fields
                .OrderBy(f => f.Order)
                .Select(f => FieldToParameter(f, randomize: false))
                .ToList();
        }

        /// <inheritdoc/>
        public List<CharacterParameter> BuildParametersRandomized(CharacterAnketa anketa)
        {
            return anketa.Fields
                .OrderBy(f => f.Order)
                .Select(f => FieldToParameter(f, randomize: true))
                .ToList();
        }

        /// <inheritdoc/>
        public List<CharacterParameter> MergeParameters(IEnumerable<CharacterAnketa> anketas)
        {
            var result = new List<CharacterParameter>();
            // Совпадение ищем по идентификатору поля, а не по имени: «Цвет
            // волос» из двух анкет — одно поле, и второй раз его заводить
            // не нужно.
            var existingFieldIds = new HashSet<string>();

            foreach (var anketa in anketas)
            {
                foreach (var field in anketa.Fields.OrderBy(f => f.Order))
                {
                    var fieldId = CharacterFieldId.Resolve(field);
                    if (existingFieldIds.Contains(fieldId)) continue;
                    result.Add(FieldToParameter(field, randomize: false));
                    existingFieldIds.Add(fieldId);
                }
            }

            return result;
        }

        /// <inheritdoc/>
        public List<CharacterParameter> MergeParametersRandomized(IEnumerable<CharacterAnketa> anketas)
        {
            var result = new List<CharacterParameter>();
            var existingFieldIds = new HashSet<string>();

            foreach (var anketa in anketas)
            {
                foreach (var field in anketa.Fields.OrderBy(f => f.Order))
                {
                    var fieldId = CharacterFieldId.Resolve(field);
                    if (existingFieldIds.Contains(fieldId)) continue;
                    result.Add(FieldToParameter(field, randomize: true));
                    existingFieldIds.Add(fieldId);
                }
            }

            return result;
        }

        /// <inheritdoc/>
        public void RandomizeParameters(List<CharacterParameter> parameters)
        {
            foreach (var param in parameters)
            {
                if (param.Type != CharacterParameterType.Numeric) continue;
                if (param.RandomRangeMin == null && param.RandomRangeMax == null) continue;

                var min = param.RandomRangeMin ?? param.MinValue;
                var max = param.RandomRangeMax ?? param.MaxValue;

                if (min > max) (min, max) = (max, min);

                param.NumericValue = Math.Round(
                    min + _random.NextDouble() * (max - min),
                    param.Step >= 1 ? 0 : 1);
            }
        }

        /// <summary>Загрузить пользовательские анкеты из данных модуля</summary>
        public void LoadCustomAnketas(List<CharacterAnketa> anketas)
        {
            _customAnketas.Clear();
            if (anketas != null)
                _customAnketas.AddRange(anketas.Where(a => !a.IsBuiltIn));

            _logger.Debug("Custom anketas loaded: {Count}", _customAnketas.Count);
        }

        // ── Недавние ──────────────────────────────────────────────────────

        private const int RecentLimit = 8;

        private readonly List<string> _recentAnketaIds = new();

        public IReadOnlyList<CharacterAnketa> GetRecentAnketas()
        {
            var all = GetAll();

            // Записи об анкетах, которых больше нет, просто не показываются:
            // чистить хранилище незачем — удалили анкету, список и так о ней
            // молчит.
            return _recentAnketaIds
                .Select(id => all.FirstOrDefault(a => a.Id == id))
                .Where(a => a != null)
                .Select(a => a!)
                .ToList();
        }

        public void RememberAnketa(string anketaId)
        {
            if (string.IsNullOrEmpty(anketaId)) return;

            _recentAnketaIds.Remove(anketaId);
            _recentAnketaIds.Insert(0, anketaId);

            while (_recentAnketaIds.Count > RecentLimit)
                _recentAnketaIds.RemoveAt(_recentAnketaIds.Count - 1);
        }

        public void ClearRecentAnketas() => _recentAnketaIds.Clear();

        public IReadOnlyList<string> GetRecentAnketaIds() => _recentAnketaIds;

        public void LoadRecentAnketas(List<string> ids)
        {
            _recentAnketaIds.Clear();
            if (ids != null) _recentAnketaIds.AddRange(ids.Where(id => !string.IsNullOrEmpty(id)));
        }

        // ── Шаблоны ───────────────────────────────────────────────────────
        //
        // Шаблон — список анкет под тип персонажа: «Эльф» = Внешность + Магия
        // + События. Хранит опознаватели, а не копии анкет: правка анкеты
        // должна доходить до всех, кто её подключил.

        private readonly List<CharacterTemplate> _templates = new();

        public IReadOnlyList<CharacterTemplate> GetTemplates() => _templates;

        public IReadOnlyList<CharacterTemplate> GetCustomTemplates() =>
            _templates.Where(t => !t.IsBuiltIn).ToList();

        public CharacterTemplate? GetTemplateById(string id) =>
            string.IsNullOrEmpty(id) ? null : _templates.FirstOrDefault(t => t.Id == id);

        public CharacterTemplate CreateTemplate(string name)
        {
            var template = new CharacterTemplate
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Новый шаблон" : name.Trim()
            };

            _templates.Add(template);
            _logger.Debug("Template created: {Name}", template.Name);
            return template;
        }

        public void UpdateTemplate(CharacterTemplate template)
        {
            if (template == null) return;

            var index = _templates.FindIndex(t => t.Id == template.Id);
            if (index < 0)
            {
                _templates.Add(template);
                return;
            }

            _templates[index] = template;
        }

        public void DeleteTemplate(string id)
        {
            // Встроенные не удаляются: они лежат в коде, и следующий запуск
            // вернул бы их обратно — удаление выглядело бы сработавшим ровно
            // до перезапуска.
            var template = GetTemplateById(id);
            if (template == null || template.IsBuiltIn) return;

            _templates.Remove(template);
        }

        public void LoadTemplates(List<CharacterTemplate> templates)
        {
            _templates.Clear();
            _templates.AddRange(BuildBuiltInTemplates());

            if (templates != null)
                _templates.AddRange(templates.Where(t => !t.IsBuiltIn));

            _logger.Debug("Templates loaded: {Count}", _templates.Count);
        }

        /// <summary>
        /// Встроенные шаблоны собираются из встроенных же анкет — новых полей
        /// они не заводят. Это готовые сочетания под частые виды историй:
        /// открыл проект, выбрал «Детективная история», и у персонажа сразу
        /// и общие данные, и профессиональные.
        /// </summary>
        private static List<CharacterTemplate> BuildBuiltInTemplates()
        {
            return new List<CharacterTemplate>
            {
                new CharacterTemplate
                {
                    Id = "builtin_template_person",
                    Name = "Обычная история",
                    Description = "Один раздел общих данных: возраст, здоровье, профессия.",
                    IsBuiltIn = true,
                    AnketaIds = new List<string> { "builtin_human" }
                },
                new CharacterTemplate
                {
                    Id = "builtin_template_detective",
                    Name = "Детективная история",
                    Description = "Общие данные плюс профессиональные качества сыщика.",
                    IsBuiltIn = true,
                    AnketaIds = new List<string> { "builtin_human", "builtin_detective" }
                },
                new CharacterTemplate
                {
                    Id = "builtin_template_fantasy",
                    Name = "Фэнтези",
                    Description = "Общие данные и боевые характеристики.",
                    IsBuiltIn = true,
                    AnketaIds = new List<string> { "builtin_human", "builtin_fantasy_warrior" }
                }
            };
        }

        // ── Вспомогательные ───────────────────────────────────────────────

        private static CharacterParameter FieldToParameter(CharacterAnketaField field, bool randomize)
        {
            var param = new CharacterParameter
            {
                Id = Guid.NewGuid().ToString(),
                // Id опознаёт значение у конкретного персонажа, FieldId —
                // само поле, одинаково у всех. Сравнение карточек идёт по нему.
                FieldId = CharacterFieldId.Resolve(field),
                IsComparable = field.IsComparable,
                Name = field.Name,
                Type = field.Type,
                GroupName = field.GroupName,
                Description = field.Description,
                MinValue = field.DefaultMinValue,
                MaxValue = field.DefaultMaxValue,
                MinDescription = field.MinDescription,
                MaxDescription = field.MaxDescription,
                Step = field.Step,
                TrueLabel = field.TrueLabel,
                FalseLabel = field.FalseLabel,
                Order = field.Order,
                RandomRangeMin = field.RandomRangeMin,
                RandomRangeMax = field.RandomRangeMax,
                ScalePoints = new Dictionary<double, string>(field.ScalePoints)
            };

            // Список вариантов нужен обоим выборам — и одиночному, и
            // множественному: разница между ними только в том, сколько из
            // этого списка можно отметить.
            if ((field.Type == CharacterParameterType.StateList ||
                 field.Type == CharacterParameterType.MultiChoice) &&
                !string.IsNullOrWhiteSpace(field.StatesRaw))
            {
                param.States = field.StatesRaw
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .ToList();
            }

            // Свободное число край шкалы не наследует: у «длины ушей» нет
            // ни минимума, ни максимума, и подставленный ноль был бы не
            // «пусто», а утверждением.
            if (param.Type == CharacterParameterType.Number)
            {
                param.NumericValue = 0;
                return param;
            }

            if (randomize && param.Type == CharacterParameterType.Numeric)
            {
                var min = param.RandomRangeMin ?? param.MinValue;
                var max = param.RandomRangeMax ?? param.MaxValue;
                if (min > max) (min, max) = (max, min);
                param.NumericValue = Math.Round(
                    min + _random.NextDouble() * (max - min),
                    param.Step >= 1 ? 0 : 1);
            }
            else
            {
                param.NumericValue = field.DefaultMinValue;
            }

            return param;
        }

        // ── Встроенные шаблоны ────────────────────────────────────────────

        private static List<CharacterAnketa> BuildBuiltInAnketas()
        {
            return new List<CharacterAnketa>
            {
                BuildHumanAnketa(),
                BuildDetectiveAnketa(),
                BuildFantasyWarriorAnketa(),
                BuildHorrorAnketa(),
                BuildScifiAnketa(),
                BuildCollectiveAnketa()
            };
        }

        private static CharacterAnketa BuildHumanAnketa()
        {
            return new CharacterAnketa
            {
                Id = "builtin_human",
                Name = "Обычный человек",
                Description = "Базовый набор параметров для любого человеческого персонажа",
                IsBuiltIn = true,
                ProjectTypeTags = new List<string>(),
                Fields = new List<CharacterAnketaField>
                {
                    new()
                    {
                        Name = "Возраст", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 120, Step = 1,
                        RandomRangeMin = 18, RandomRangeMax = 60,
                        GroupName = "Физическое", Description = "Возраст персонажа в годах",
                        Order = 0
                    },
                    new()
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
                        },
                        GroupName = "Физическое", Order = 1
                    },
                    new()
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
                        },
                        GroupName = "Физическое", Order = 2
                    },
                    new()
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
                        },
                        GroupName = "Физическое", Order = 3
                    },
                    new()
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
                        },
                        GroupName = "Интеллект", Order = 4
                    },
                    new()
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
                        },
                        GroupName = "Социальное", Order = 5
                    },
                    new()
                    {
                        Name = "Профессия", Type = CharacterParameterType.Text,
                        GroupName = "Биография", Order = 6
                    },
                    new()
                    {
                        Name = "Место рождения", Type = CharacterParameterType.Text,
                        GroupName = "Биография", Order = 7
                    },
                    new()
                    {
                        Name = "Статус жизни", Type = CharacterParameterType.StateList,
                        StatesRaw = "Жив, Ранен, В коме, Мёртв",
                        GroupName = "Статус", Order = 8
                    }
                }
            };
        }

        private static CharacterAnketa BuildDetectiveAnketa()
        {
            return new CharacterAnketa
            {
                Id = "builtin_detective",
                Name = "Детектив / Нуар",
                Description = "Параметры для детективных историй — психология, профессионализм, пороки",
                IsBuiltIn = true,
                ProjectTypeTags = new List<string> { "Детектив", "Нуар", "Триллер" },
                Fields = new List<CharacterAnketaField>
                {
                    new()
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
                        },
                        GroupName = "Профессиональное", Order = 0
                    },
                    new()
                    {
                        Name = "Дедукция", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 100,
                        RandomRangeMin = 20, RandomRangeMax = 90,
                        MinDescription = "Не может связать очевидные факты",
                        MaxDescription = "Строит цепочки из ничего",
                        GroupName = "Профессиональное", Order = 1
                    },
                    new()
                    {
                        Name = "Доверие к системе", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 100,
                        RandomRangeMin = 0, RandomRangeMax = 100,
                        MinDescription = "Полный циник, законы не для него",
                        MaxDescription = "Свято верит в закон и справедливость",
                        GroupName = "Психология", Order = 2
                    },
                    new()
                    {
                        Name = "Моральная гибкость", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 100,
                        RandomRangeMin = 0, RandomRangeMax = 100,
                        MinDescription = "Абсолютная принципиальность, никаких компромиссов",
                        MaxDescription = "Любые методы если цель оправдывает",
                        GroupName = "Психология", Order = 3
                    },
                    new()
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
                        },
                        GroupName = "Пороки", Order = 4
                    },
                    new()
                    {
                        Name = "Статус", Type = CharacterParameterType.StateList,
                        StatesRaw = "Активен, Отстранён, Под следствием, В бегах, Мёртв",
                        GroupName = "Статус", Order = 5
                    },
                    new()
                    {
                        Name = "Лицензия", Type = CharacterParameterType.Boolean,
                        TrueLabel = "Действующая", FalseLabel = "Отозвана",
                        GroupName = "Статус", Order = 6
                    }
                }
            };
        }

        private static CharacterAnketa BuildFantasyWarriorAnketa()
        {
            return new CharacterAnketa
            {
                Id = "builtin_fantasy_warrior",
                Name = "Фэнтези: Воин",
                Description = "Боевые и магические параметры для фэнтезийного воина",
                IsBuiltIn = true,
                ProjectTypeTags = new List<string> { "Фэнтези", "РПГ", "Эпик" },
                Fields = new List<CharacterAnketaField>
                {
                    new()
                    {
                        Name = "Сила", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 1, DefaultMaxValue = 20, Step = 1,
                        RandomRangeMin = 6, RandomRangeMax = 16,
                        MinDescription = "Слабак, не удержит меч двумя руками",
                        MaxDescription = "Легендарная сила, разрубает камень",
                        ScalePoints = new Dictionary<double, string>
                        {
                            { 5, "Слабый, ниже среднего" },
                            { 10, "Средний воин" },
                            { 16, "Ветеран, опытный боец" },
                            { 20, "Легенда" }
                        },
                        GroupName = "Боевое", Order = 0
                    },
                    new()
                    {
                        Name = "Ловкость", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 1, DefaultMaxValue = 20, Step = 1,
                        RandomRangeMin = 6, RandomRangeMax = 16,
                        MinDescription = "Неуклюжий, постоянно спотыкается",
                        MaxDescription = "Движется как тень, неуловим",
                        GroupName = "Боевое", Order = 1
                    },
                    new()
                    {
                        Name = "Выносливость", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 1, DefaultMaxValue = 20, Step = 1,
                        RandomRangeMin = 6, RandomRangeMax = 16,
                        GroupName = "Боевое", Order = 2
                    },
                    new()
                    {
                        Name = "Мана", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 100,
                        RandomRangeMin = 0, RandomRangeMax = 60,
                        MinDescription = "Нет магического дара",
                        MaxDescription = "Безграничный магический резерв",
                        GroupName = "Магическое", Order = 3
                    },
                    new()
                    {
                        Name = "Владеет магией", Type = CharacterParameterType.Boolean,
                        TrueLabel = "Да", FalseLabel = "Нет",
                        GroupName = "Магическое", Order = 4
                    },
                    new()
                    {
                        Name = "Мировоззрение", Type = CharacterParameterType.StateList,
                        StatesRaw = "Законопослушный добрый, Нейтральный добрый, Хаотичный добрый, Законопослушный нейтральный, Истинный нейтральный, Хаотичный нейтральный, Законопослушный злой, Нейтральный злой, Хаотичный злой",
                        GroupName = "Личность", Order = 5
                    },
                    new()
                    {
                        Name = "Класс", Type = CharacterParameterType.Text,
                        GroupName = "Личность", Order = 6
                    },
                    new()
                    {
                        Name = "Раса", Type = CharacterParameterType.Text,
                        GroupName = "Личность", Order = 7
                    }
                }
            };
        }

        private static CharacterAnketa BuildHorrorAnketa()
        {
            return new CharacterAnketa
            {
                Id = "builtin_horror",
                Name = "Хоррор / Психологический",
                Description = "Параметры психологического состояния для хоррора и триллера",
                IsBuiltIn = true,
                ProjectTypeTags = new List<string> { "Хоррор", "Психологический триллер", "Мистика" },
                Fields = new List<CharacterAnketaField>
                {
                    new()
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
                        },
                        GroupName = "Психология", Order = 0
                    },
                    new()
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
                        },
                        GroupName = "Психология", Order = 1
                    },
                    new()
                    {
                        Name = "Паранойя", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 100,
                        RandomRangeMin = 0, RandomRangeMax = 60,
                        MinDescription = "Полностью доверяет окружающим",
                        MaxDescription = "Видит угрозу в каждом человеке и предмете",
                        GroupName = "Психология", Order = 2
                    },
                    new()
                    {
                        Name = "Воля к выживанию", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 100,
                        RandomRangeMin = 30, RandomRangeMax = 100,
                        MinDescription = "Сдался, принял смерть",
                        MaxDescription = "Выживет любой ценой, инстинкт на пределе",
                        GroupName = "Психология", Order = 3
                    },
                    new()
                    {
                        Name = "Психологическое состояние", Type = CharacterParameterType.StateList,
                        StatesRaw = "Стабилен, Взволнован, В панике, Диссоциация, Психоз, Кататония",
                        GroupName = "Статус", Order = 4
                    },
                    new()
                    {
                        Name = "Видит сверхъестественное", Type = CharacterParameterType.Boolean,
                        TrueLabel = "Да", FalseLabel = "Нет",
                        GroupName = "Особое", Order = 5
                    }
                }
            };
        }

        private static CharacterAnketa BuildScifiAnketa()
        {
            return new CharacterAnketa
            {
                Id = "builtin_scifi",
                Name = "Научная фантастика",
                Description = "Параметры для sci-fi: кибернетика, фракции, технологии",
                IsBuiltIn = true,
                ProjectTypeTags = new List<string> { "Sci-fi", "Киберпанк", "Космическая опера" },
                Fields = new List<CharacterAnketaField>
                {
                    new()
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
                        },
                        GroupName = "Физическое", Order = 0
                    },
                    new()
                    {
                        Name = "Хакерство", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 100,
                        RandomRangeMin = 0, RandomRangeMax = 80,
                        MinDescription = "Не умеет пользоваться компьютером",
                        MaxDescription = "Взламывает защищённые системы за секунды",
                        GroupName = "Навыки", Order = 1
                    },
                    new()
                    {
                        Name = "Пилотирование", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 100,
                        RandomRangeMin = 0, RandomRangeMax = 80,
                        GroupName = "Навыки", Order = 2
                    },
                    new()
                    {
                        Name = "Фракция", Type = CharacterParameterType.Text,
                        GroupName = "Политическое", Order = 3
                    },
                    new()
                    {
                        Name = "Лояльность фракции", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 100,
                        RandomRangeMin = 20, RandomRangeMax = 90,
                        MinDescription = "Предатель, работает на несколько сторон",
                        MaxDescription = "Фанатично предан, умрёт за фракцию",
                        GroupName = "Политическое", Order = 4
                    },
                    new()
                    {
                        Name = "Биологический вид", Type = CharacterParameterType.StateList,
                        StatesRaw = "Человек, Клон, Андроид, Мутант, Инопланетянин, Гибрид, ИИ",
                        GroupName = "Физическое", Order = 5
                    },
                    new()
                    {
                        Name = "Бессмертен", Type = CharacterParameterType.Boolean,
                        TrueLabel = "Да", FalseLabel = "Нет",
                        GroupName = "Физическое", Order = 6
                    }
                }
            };
        }

        private static CharacterAnketa BuildCollectiveAnketa()
        {
            return new CharacterAnketa
            {
                Id = CharacterAnketa.CollectiveId,
                Name = "Народ / Группа",
                Description = "Среднестатистические параметры для коллективного персонажа — расы, народа, социальной группы",
                IsBuiltIn = true,
                ProjectTypeTags = new List<string>(),
                Fields = new List<CharacterAnketaField>
                {
                    new()
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
                        },
                        GroupName = "Характеристики группы", Order = 0
                    },
                    new()
                    {
                        Name = "Средняя сила", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 100,
                        RandomRangeMin = 20, RandomRangeMax = 60,
                        MinDescription = "Физически слабые существа",
                        MaxDescription = "Физически превосходящие людей в разы",
                        GroupName = "Характеристики группы", Order = 1
                    },
                    new()
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
                        },
                        GroupName = "Социальное", Order = 2
                    },
                    new()
                    {
                        Name = "Лояльность власти", Type = CharacterParameterType.Numeric,
                        DefaultMinValue = 0, DefaultMaxValue = 100,
                        RandomRangeMin = 20, RandomRangeMax = 80,
                        MinDescription = "Открытый бунт, не признают власть",
                        MaxDescription = "Слепое подчинение, не рассуждают",
                        GroupName = "Социальное", Order = 3
                    },
                    new()
                    {
                        Name = "Численность", Type = CharacterParameterType.Text,
                        Description = "Примерная численность группы или населения",
                        GroupName = "Описание группы", Order = 4
                    },
                    new()
                    {
                        Name = "Территория", Type = CharacterParameterType.Text,
                        Description = "Где проживает группа",
                        GroupName = "Описание группы", Order = 5
                    },
                    new()
                    {
                        Name = "Уровень угрозы", Type = CharacterParameterType.StateList,
                        StatesRaw = "Мирные, Нейтральные, Враждебные, Опасные, Смертоносные",
                        GroupName = "Статус", Order = 6
                    }
                }
            };
        }
    }
}
