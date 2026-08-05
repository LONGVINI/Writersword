using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Core.Services;
using Writersword.Modules.Characters.Interfaces;
using Writersword.Modules.Characters.Models;

namespace Writersword.Modules.Characters.Services
{
    public class CharacterService : ICharacterService
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterService>();

        private readonly IRelationshipService _relationshipService;
        private readonly ICharacterAnketaService _anketaService;
        private readonly List<Character> _characters = new();
        private readonly List<CharacterLabel> _labels = new();
        private object? _context;

        private const string AvatarFolder = "Characters/avatars";

        public CharacterService(IRelationshipService relationshipService, ICharacterAnketaService anketaService)
        {
            _relationshipService = relationshipService;
            _anketaService = anketaService;
        }

        public void SetContext(object? context) => _context = context;

        // Режим сравнения версий (восстановление после несохранённой сессии):
        // любые изменения данных модуля запрещены, пока пользователь не выбрал
        // версию в баннере. Это последний рубеж — независимо от того, какой
        // путь UI привёл к записи, данные остаются нетронутыми.
        private bool IsReadOnly =>
            _context is DocumentContext ctx
            && ctx.IsInCompareMode;

        public IReadOnlyList<Character> GetAll() => _characters.AsReadOnly();

        public IReadOnlyList<Character> Search(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return GetAll();
            var q = query.ToLowerInvariant();
            // Поиск идёт по всем именам карточки, а не только по отображаемому:
            // персонаж, ставший по ходу истории Дианой, должен находиться и по
            // прежнему имени, и по прозвищу.
            return _characters
                .Where(c => CharacterNames.AllValues(c)
                                .Any(n => n.ToLowerInvariant().Contains(q)) ||
                            c.ShortDescription.ToLowerInvariant().Contains(q) ||
                            (c.Note ?? string.Empty).ToLowerInvariant().Contains(q) ||
                            c.Tags.Any(t => t.ToLowerInvariant().Contains(q)))
                .ToList().AsReadOnly();
        }

        public IReadOnlyList<Character> GetByTags(IEnumerable<string> tags)
        {
            var tagList = tags.ToList();
            if (!tagList.Any()) return GetAll();
            return _characters.Where(c => c.Tags.Any(t => tagList.Contains(t))).ToList().AsReadOnly();
        }

        public Character? GetById(string id) => _characters.FirstOrDefault(c => c.Id == id);

        /// <summary>
        /// Реестр меток проекта. Встроенные («Мёртв») идут первыми, остальные
        /// по алфавиту — порядок нужен только для подсказок при вводе.
        /// </summary>
        public IReadOnlyList<CharacterLabel> GetAllLabels() =>
            _labels
                .Where(l => !string.IsNullOrWhiteSpace(l.Name))
                .OrderByDescending(l => l.IsBuiltIn)
                .ThenBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList().AsReadOnly();

        public void SaveGlobalLabel(CharacterLabel label)
        {
            if (IsReadOnly) return;
            if (label == null || string.IsNullOrWhiteSpace(label.Name)) return;

            // Метка ищется по идентификатору, а при промахе — по имени:
            // проекты старше реестра собрали его из копий персонажей, и там
            // одно имя вполне могло разойтись по нескольким идентификаторам.
            var index = _labels.FindIndex(l => l.Id == label.Id);
            if (index < 0)
                index = _labels.FindIndex(l =>
                    string.Equals(l.Name, label.Name, StringComparison.CurrentCultureIgnoreCase));

            var entry = CloneAsTemplate(label);
            if (index >= 0) _labels[index] = entry;
            else _labels.Add(entry);

            _logger.Debug("Global label saved: {Name}", entry.Name);
        }

        public int ApplyLabelToAll(CharacterLabel label)
        {
            if (IsReadOnly) return 0;
            if (label == null) return 0;

            var affected = 0;
            foreach (var character in _characters)
            {
                var own = character.Labels.FirstOrDefault(l => l.Id == label.Id);
                if (own == null) continue;

                own.Name = label.Name;
                own.Icon = label.Icon;
                own.IconImage = label.IconImage;
                own.Color = label.Color;
                own.IconColor = label.IconColor;
                own.ShowBackdrop = label.ShowBackdrop;
                own.Effect = label.Effect;
                own.Description = label.Description;
                character.UpdatedAt = DateTime.UtcNow;
                affected++;
            }

            _logger.Debug("Label '{Name}' applied to {Count} characters", label.Name, affected);
            return affected;
        }

        // Образец для реестра: личные поля персонажа в него не уходят.
        // Порядок и показ на карточке каждый персонаж держит свои — иначе
        // сохранение общей метки переставляло бы значки у всех подряд.
        private static CharacterLabel CloneAsTemplate(CharacterLabel label) => new()
        {
            Id = label.Id,
            Name = label.Name,
            Icon = label.Icon,
            IconImage = label.IconImage,
            Color = label.Color,
            IconColor = label.IconColor,
            ShowBackdrop = label.ShowBackdrop,
            Effect = label.Effect,
            Description = label.Description,
            ShowOnCard = label.ShowOnCard
        };

        public IReadOnlyList<string> GetAllTags() =>
            _characters.SelectMany(c => c.Tags).Distinct().OrderBy(t => t).ToList().AsReadOnly();

        public Character Create(string name)
        {
            var c = new Character { Id = Guid.NewGuid().ToString(), Name = name };
            _characters.Add(c);
            _logger.Debug("Character created: {Id} '{Name}'", c.Id, c.Name);
            return c;
        }

        public Character CreateFromAnketas(string name, IEnumerable<CharacterAnketa> anketas, bool randomize = false)
        {
            var c = new Character { Id = Guid.NewGuid().ToString(), Name = name };
            var list = anketas.ToList();
            c.Parameters = randomize
                ? _anketaService.MergeParametersRandomized(list)
                : _anketaService.MergeParameters(list);

            // Наборы числятся подключёнными, а не растворяются в полях: иначе
            // персонаж, созданный по активным шаблонам, не знал бы, из чего
            // собран — в карточке не было бы чипов, а правки набора до него
            // не доезжали бы.
            c.AttachedAnketaIds = list.Select(a => a.Id).Distinct().ToList();

            _characters.Add(c);
            _logger.Debug("Character created from anketas: {Id}, params={Count}, rand={R}", c.Id, c.Parameters.Count, randomize);
            return c;
        }

        public Character CreateCollective(string name, IEnumerable<CharacterAnketa>? anketas = null)
        {
            var c = new Character { Id = Guid.NewGuid().ToString(), Name = name, IsCollective = true };
            var list = anketas?.ToList() ?? new List<CharacterAnketa>();
            if (list.Any())
            {
                c.Parameters = _anketaService.MergeParameters(list);
                c.AttachedAnketaIds = list.Select(a => a.Id).Distinct().ToList();
            }
            _characters.Add(c);
            _logger.Debug("Collective character created: {Id}", c.Id);
            return c;
        }

        public void Update(Character character)
        {
            if (IsReadOnly)
            {
                _logger.Debug("Update ignored (compare mode): {Id}", character.Id);
                return;
            }
            var idx = _characters.FindIndex(c => c.Id == character.Id);
            if (idx >= 0)
            {
                character.UpdatedAt = DateTime.UtcNow;
                _characters[idx] = character;
            }
        }

        public void Delete(string id)
        {
            if (IsReadOnly)
            {
                _logger.Debug("Delete ignored (compare mode): {Id}", id);
                return;
            }
            _characters.RemoveAll(c => c.Id == id);
            _logger.Debug("Character deleted: {Id}", id);
        }

        public Character Duplicate(string id)
        {
            var original = GetById(id);
            if (original == null) throw new InvalidOperationException($"Character not found: {id}");

            var copy = new Character
            {
                Id = Guid.NewGuid().ToString(),
                Name = $"{original.Name} (копия)",
                ShortDescription = original.ShortDescription,
                Note = original.Note,
                Color = original.Color,
                FallbackIcon = original.FallbackIcon,
                ImportanceLevel = original.ImportanceLevel,
                Tags = new List<string>(original.Tags),
                Labels = original.Labels.Select(l => new CharacterLabel
                {
                    Id = l.Id,
                    Name = l.Name,
                    Icon = l.Icon,
                    Color = l.Color,
                    IconColor = l.IconColor,
                    ShowBackdrop = l.ShowBackdrop,
                    Effect = l.Effect,
                    IconImage = l.IconImage,
                    ShowOnCard = l.ShowOnCard,
                    Order = l.Order,
                    Description = l.Description
                }).ToList(),
                Parameters = original.Parameters.Select(p => new CharacterParameter
                {
                    // Значение у копии своё, поле — то же самое: иначе копия
                    // выпала бы из сравнения с оригиналом.
                    Id = Guid.NewGuid().ToString(),
                    FieldId = p.FieldId,
                    IsComparable = p.IsComparable,
                    ValueNote = p.ValueNote,
                    Name = p.Name,
                    Type = p.Type,
                    GroupName = p.GroupName,
                    Description = p.Description,
                    MinValue = p.MinValue,
                    MaxValue = p.MaxValue,
                    NumericValue = p.NumericValue,
                    Step = p.Step,
                    MinDescription = p.MinDescription,
                    MaxDescription = p.MaxDescription,
                    ScalePoints = new System.Collections.Generic.Dictionary<double, string>(p.ScalePoints),
                    States = new List<string>(p.States),
                    CurrentStateIndex = p.CurrentStateIndex,
                    TextValue = p.TextValue,
                    BoolValue = p.BoolValue,
                    TrueLabel = p.TrueLabel,
                    FalseLabel = p.FalseLabel,
                    IsNotApplicable = p.IsNotApplicable,
                    Order = p.Order
                }).ToList(),
                IsCollective = original.IsCollective,
                PopulationNote = original.PopulationNote
            };

            // Имена копии: первым идёт её собственное имя с пометкой «копия»,
            // следом — остальные имена оригинала со своими новыми Id. Если
            // перенести список как есть, нормализация вернула бы отображаемым
            // имя оригинала и пометка «копия» исчезла бы.
            copy.Names = new List<CharacterNameEntry>
            {
                new CharacterNameEntry { Value = copy.Name }
            };
            copy.Names.AddRange(original.Names.Skip(1).Select(n => new CharacterNameEntry
            {
                Value = n.Value,
                Note = n.Note
            }));
            copy.Aliases = copy.Names.Skip(1).Select(n => n.Value).ToList();

            // Состав копии тот же: иначе дубликат выглядел бы собранным
            // вручную, отключить набор было бы нечем, а правки набора
            // до него не доезжали бы.
            copy.AttachedAnketaIds = new List<string>(original.AttachedAnketaIds);

            _characters.Add(copy);
            _logger.Debug("Character duplicated: {OrigId} -> {NewId}", id, copy.Id);
            return copy;
        }

        public void ApplyAnketa(string characterId, CharacterAnketa anketa, bool randomize = false)
        {
            if (IsReadOnly)
            {
                _logger.Debug("ApplyAnketa ignored (compare mode): {Id}", characterId);
                return;
            }
            var character = GetById(characterId);
            if (character == null) return;

            var newParams = randomize
                ? _anketaService.BuildParametersRandomized(anketa)
                : _anketaService.BuildParameters(anketa);

            // Совпадение ищем по идентификатору поля, а не по имени: одно и то
            // же поле в двух анкетах не должно задваиваться из-за разной
            // формулировки.
            var existingFieldIds = character.Parameters
                .Select(p => CharacterFieldId.Resolve(p))
                .ToHashSet();

            foreach (var p in newParams)
            {
                if (existingFieldIds.Add(CharacterFieldId.Resolve(p)))
                    character.Parameters.Add(p);
            }

            // Набор числится подключённым к карточке: по этому списку карточка
            // знает, из чего составлена, и его же показывает в интерфейсе.
            if (!character.AttachedAnketaIds.Contains(anketa.Id))
                character.AttachedAnketaIds.Add(anketa.Id);

            character.UpdatedAt = DateTime.UtcNow;
        }

        public int SyncAnketa(CharacterAnketa anketa)
        {
            if (IsReadOnly)
            {
                _logger.Debug("SyncAnketa ignored (compare mode): {Id}", anketa?.Id);
                return 0;
            }
            if (anketa == null) return 0;

            int changed = 0;

            foreach (var character in _characters)
            {
                if (!character.AttachedAnketaIds.Contains(anketa.Id)) continue;

                var existingFieldIds = character.Parameters
                    .Select(p => CharacterFieldId.Resolve(p))
                    .ToHashSet();

                bool touched = false;

                // Параметры строятся заново для каждой карточки: один список
                // на всех означал бы общие объекты значений, и правка у одного
                // персонажа меняла бы значение у всех.
                foreach (var parameter in _anketaService.BuildParameters(anketa))
                {
                    // Уже заполненные значения не трогаем: правка набора
                    // добавляет поля, а не переписывает работу автора.
                    // Удалённое из набора поле у персонажа тоже остаётся —
                    // убрать его можно поштучно и осознанно.
                    if (!existingFieldIds.Add(CharacterFieldId.Resolve(parameter))) continue;

                    character.Parameters.Add(parameter);
                    touched = true;
                }

                if (touched)
                {
                    character.UpdatedAt = DateTime.UtcNow;
                    changed++;
                }
            }

            if (changed > 0)
                _logger.Debug("Anketa '{Name}' synced into {Count} characters", anketa.Name, changed);

            return changed;
        }

        public void DetachAnketa(string characterId, string anketaId)
        {
            if (IsReadOnly)
            {
                _logger.Debug("DetachAnketa ignored (compare mode): {Id}", characterId);
                return;
            }
            var character = GetById(characterId);
            if (character == null) return;

            // Значения не трогаем намеренно: отключение набора — это про
            // состав карточки, а не про удаление написанного. Ненужные поля
            // убираются поштучно, чтобы случайный клик не стёр работу.
            if (character.AttachedAnketaIds.Remove(anketaId))
                character.UpdatedAt = DateTime.UtcNow;
        }

        public void RandomizeParameters(string characterId)
        {
            if (IsReadOnly)
            {
                _logger.Debug("RandomizeParameters ignored (compare mode): {Id}", characterId);
                return;
            }
            var character = GetById(characterId);
            if (character == null) return;
            _anketaService.RandomizeParameters(character.Parameters);
            character.UpdatedAt = DateTime.UtcNow;
        }

        public string? GetAvatarPath(string characterId) => GetById(characterId)?.AvatarPath;

        public void SetAvatar(string characterId, byte[] data, string extension)
        {
            if (IsReadOnly) return;
            var character = GetById(characterId);
            if (character == null) return;
            character.AvatarPath = $"{AvatarFolder}/{characterId}/avatar{extension}";
            character.UpdatedAt = DateTime.UtcNow;
        }

        public void DeleteAvatar(string characterId)
        {
            if (IsReadOnly) return;
            var character = GetById(characterId);
            if (character == null) return;
            character.AvatarPath = null;
            character.UpdatedAt = DateTime.UtcNow;
        }

        public CharactersModuleData GetModuleData() => new()
        {
            Characters = _characters.ToList(),
            Labels = _labels.ToList(),
            Relationships = _relationshipService.GetAll().ToList()
        };

        public void LoadModuleData(CharactersModuleData data)
        {
            _characters.Clear();
            if (data.Characters != null)
            {
                // Старые сохранения не знают списка имён — собираем его из
                // отображаемого имени и псевдонимов, чтобы поиск и карточка
                // работали одинаково для проектов любого возраста.
                foreach (var c in data.Characters)
                {
                    CharacterNames.Normalize(c);
                    c.AttachedAnketaIds ??= new List<string>();

                    // Значения, созданные до появления идентификатора поля,
                    // получают его из имени: иначе сравнивать карточки между
                    // собой будет нечем, а имя у значения и определения одно.
                    foreach (var p in c.Parameters)
                        if (string.IsNullOrWhiteSpace(p.FieldId))
                            p.FieldId = CharacterFieldId.FromName(p.Name);

                    // Встроенная «Мёртв» приводится к текущему виду сразу при
                    // загрузке, а не при первом открытии карточки: значок
                    // виден на карточках списков, куда карточку персонажа
                    // никто мог и не открывать.
                    foreach (var l in c.Labels)
                        CharacterBuiltinLabels.NormalizeBuiltIn(l);
                }

                _characters.AddRange(data.Characters);
            }

            LoadLabelRegistry(data);

            _logger.Debug("Module data loaded: {Count} characters, {LabelCount} labels",
                _characters.Count, _labels.Count);
        }

        /// <summary>
        /// Реестр меток. Проекты, сохранённые до его появления, реестра не
        /// содержат — он собирается из меток персонажей по одной на имя, как
        /// это раньше делалось на лету при каждом обращении. Собранное сразу
        /// уходит в файл при ближайшем сохранении, и дальше метка живёт в
        /// реестре сама по себе: правка перестаёт зависеть от того, у кого
        /// она нашлась первой, а удаление персонажа больше не уносит её вид.
        /// </summary>
        private void LoadLabelRegistry(CharactersModuleData data)
        {
            _labels.Clear();

            if (data.Labels != null && data.Labels.Count > 0)
            {
                foreach (var label in data.Labels)
                {
                    CharacterBuiltinLabels.NormalizeBuiltIn(label);
                    _labels.Add(label);
                }
                return;
            }

            var collected = _characters
                .SelectMany(c => c.Labels)
                .Where(l => !string.IsNullOrWhiteSpace(l.Name))
                .GroupBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(g => g.First());

            foreach (var label in collected)
                _labels.Add(CloneAsTemplate(label));
        }

        public Character CreateWithId(Character character)
        {
            var existing = _characters.FirstOrDefault(c => c.Id == character.Id);
            if (existing != null)
                return existing;

            _characters.Add(character);
            _logger.Debug("Character restored with original Id: {Id} = '{Name}'", character.Id, character.Name);
            return character;
        }

    }
}
