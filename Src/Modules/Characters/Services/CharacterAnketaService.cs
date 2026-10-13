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
    /// Сервис анкет и шаблонов персонажей.
    ///
    /// Анкета — раздел полей с уровнями детализации, шаблон — сочетание
    /// анкет под тип персонажа. Встроенные анкеты и шаблоны лежат в
    /// CharacterBuiltInContent; здесь живут они и свои анкеты проекта.
    /// </summary>
    public class CharacterAnketaService : ICharacterAnketaService
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterAnketaService>();
        private static readonly Random _random = new();

        private readonly List<CharacterAnketa> _customAnketas = new();
        private readonly List<CharacterAnketa> _builtInAnketas;

        /// <summary>
        /// Режим сравнения версий: анкеты и шаблоны не создаются, не
        /// меняются, не удаляются и не переставляются. Последний рубеж —
        /// какой бы путь ни привёл к записи. Загрузка данных идёт как обычно:
        /// без неё нечего было бы сравнивать.
        /// </summary>
        public Func<bool>? IsReadOnlyProvider { get; set; }

        private bool IsReadOnly(string operation)
        {
            if (IsReadOnlyProvider?.Invoke() != true) return false;
            _logger.Debug("{Operation} ignored (compare mode)", operation);
            return true;
        }

        public CharacterAnketaService()
        {
            _builtInAnketas = CharacterBuiltInContent.BuildAnketas();
            foreach (var anketa in _builtInAnketas)
            {
                CharacterAnketaLayout.Normalize(anketa);
                CharacterAnketaLayout.ApplyOrderAndGroups(anketa);
            }

            // Встроенные шаблоны есть с первой секунды, ещё до открытия
            // проекта: без них список шаблонов у нового проекта был бы пуст,
            // и первое, что увидел бы человек, — пустоту вместо готовых
            // сочетаний. Загрузка проекта потом дополняет их своими.
            _templates.AddRange(CharacterBuiltInContent.BuildTemplates());
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

            // Анкета создаётся, но в список не встаёт: в проект она не попадёт.
            if (IsReadOnly("Anketa create")) return anketa;

            _customAnketas.Add(anketa);
            _logger.Debug("Anketa created: {Id} '{Name}'", anketa.Id, anketa.Name);
            return anketa;
        }

        /// <inheritdoc/>
        public void Update(CharacterAnketa anketa)
        {
            if (IsReadOnly("Anketa update")) return;

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

            // Раскладка — главная: порядок и группы полей выводятся из неё.
            CharacterAnketaLayout.Normalize(anketa);
            CharacterAnketaLayout.ApplyOrderAndGroups(anketa);

            var index = _customAnketas.IndexOf(existing);
            _customAnketas[index] = anketa;
            _logger.Debug("Anketa updated: {Id} '{Name}'", anketa.Id, anketa.Name);
        }

        /// <inheritdoc/>
        public void Delete(string id)
        {
            if (IsReadOnly("Anketa delete")) return;

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

            if (IsReadOnly("Anketa duplicate")) return copy;

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

            // Анкеты старых проектов раскладки не имеют — она строится из
            // порядка и групп полей.
            foreach (var anketa in _customAnketas)
            {
                CharacterAnketaLayout.Normalize(anketa);
                CharacterAnketaLayout.ApplyOrderAndGroups(anketa);
            }

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
            if (IsReadOnly("Recent anketa")) return;

            _recentAnketaIds.Remove(anketaId);
            _recentAnketaIds.Insert(0, anketaId);

            while (_recentAnketaIds.Count > RecentLimit)
                _recentAnketaIds.RemoveAt(_recentAnketaIds.Count - 1);
        }

        public void ClearRecentAnketas()
        {
            if (IsReadOnly("Recent anketas clear")) return;
            _recentAnketaIds.Clear();
        }

        public IReadOnlyList<string> GetRecentAnketaIds() => _recentAnketaIds;

        public void LoadRecentAnketas(List<string> ids)
        {
            _recentAnketaIds.Clear();
            if (ids != null)
                _recentAnketaIds.AddRange(CharacterBuiltInContent.ExpandLegacyIds(ids).Take(RecentLimit));
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

            if (IsReadOnly("Template create")) return template;

            _templates.Add(template);
            _logger.Debug("Template created: {Name}", template.Name);
            return template;
        }

        public void UpdateTemplate(CharacterTemplate template)
        {
            if (template == null) return;
            if (IsReadOnly("Template update")) return;

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
            if (IsReadOnly("Template delete")) return;

            var template = GetTemplateById(id);
            if (template == null || template.IsBuiltIn) return;

            _templates.Remove(template);
        }

        public void MoveTemplate(string id, int index)
        {
            if (IsReadOnly("Template move")) return;

            var template = GetTemplateById(id);
            if (template == null || template.IsBuiltIn) return;

            var custom = _templates.Where(t => !t.IsBuiltIn).ToList();
            var from = custom.IndexOf(template);
            if (from < 0) return;

            index = Math.Clamp(index, 0, custom.Count - 1);
            if (index == from) return;

            custom.RemoveAt(from);
            custom.Insert(index, template);

            // Встроенные держатся впереди, свои — следом в новом порядке:
            // порядок своих и есть тот, что сохраняется в проекте.
            var builtIn = _templates.Where(t => t.IsBuiltIn).ToList();
            _templates.Clear();
            _templates.AddRange(builtIn);
            _templates.AddRange(custom);
        }

        public void LoadTemplates(List<CharacterTemplate> templates)
        {
            _templates.Clear();
            _templates.AddRange(CharacterBuiltInContent.BuildTemplates());

            if (templates != null)
            {
                foreach (var template in templates.Where(t => !t.IsBuiltIn))
                {
                    // Свои шаблоны старых проектов ссылаются на прежние
                    // жанровые анкеты — они разошлись по разделам, и шаблон
                    // получает разделы вместо исчезнувшей анкеты.
                    template.AnketaIds = CharacterBuiltInContent.ExpandLegacyIds(template.AnketaIds);
                    template.AnketaDepths = ExpandLegacyDepths(template.AnketaDepths);
                    _templates.Add(template);
                }
            }

            _logger.Debug("Templates loaded: {Count}", _templates.Count);
        }

        /// <summary>
        /// Глубины по анкетам с прежними идентификаторами, разнесённые по
        /// новым разделам: глубина «Обычного человека» достаётся каждому из
        /// его разделов.
        /// </summary>
        private static Dictionary<string, int> ExpandLegacyDepths(Dictionary<string, int>? depths)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            if (depths == null) return result;

            foreach (var pair in depths)
            {
                if (CharacterBuiltInContent.LegacyAnketaIds.TryGetValue(pair.Key, out var replacement))
                {
                    foreach (var id in replacement)
                        if (!result.ContainsKey(id)) result[id] = CharacterDetailLevels.Clamp(pair.Value);
                    continue;
                }

                result[pair.Key] = CharacterDetailLevels.Clamp(pair.Value);
            }

            return result;
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

            // Вид поля и способы задать число едут вместе с определением:
            // значение должно показываться так, как настроено в анкете.
            CharacterFieldDefinition.CopyDisplay(field, param);

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
    }
}
