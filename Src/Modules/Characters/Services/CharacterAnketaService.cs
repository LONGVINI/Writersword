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
            _hiddenTemplateIds.Remove(id);
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

        public void ArrangeTemplates(IReadOnlyList<(string Id, string FolderId)> order)
        {
            if (IsReadOnly("Templates arrange")) return;
            if (order == null) return;

            var custom = _templates.Where(t => !t.IsBuiltIn).ToList();
            var arranged = new List<CharacterTemplate>(custom.Count);

            foreach (var (id, folderId) in order)
            {
                var template = custom.FirstOrDefault(t => t.Id == id);
                if (template == null || arranged.Contains(template)) continue;

                template.FolderId = folderId ?? string.Empty;
                arranged.Add(template);
            }

            foreach (var template in custom)
                if (!arranged.Contains(template))
                    arranged.Add(template);

            // Встроенные держатся впереди, свои — следом в новом порядке.
            var builtIn = _templates.Where(t => t.IsBuiltIn).ToList();
            _templates.Clear();
            _templates.AddRange(builtIn);
            _templates.AddRange(arranged);
        }

        // ── Папки шаблонов ────────────────────────────────────────────────
        //
        // Папка — только место на вкладке шаблонов: персонажу шаблон
        // подключают одинаково, в какой бы папке он ни лежал. Шаблон знает
        // свою папку сам; папку, которой больше нет, он считает отсутствующей
        // и лежит в «Моих шаблонах».

        private readonly List<CharacterTemplateFolder> _templateFolders = new();

        public IReadOnlyList<CharacterTemplateFolder> GetTemplateFolders() => _templateFolders;

        public CharacterTemplateFolder CreateTemplateFolder(string name)
        {
            var folder = new CharacterTemplateFolder
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Новая папка" : name.Trim()
            };

            if (IsReadOnly("Template folder create")) return folder;

            _templateFolders.Add(folder);
            _logger.Debug("Template folder created: {Name}", folder.Name);
            return folder;
        }

        public void UpdateTemplateFolder(CharacterTemplateFolder folder)
        {
            if (folder == null) return;
            if (IsReadOnly("Template folder update")) return;

            var index = _templateFolders.FindIndex(f => f.Id == folder.Id);
            if (index < 0) return;
            _templateFolders[index] = folder;
        }

        public void DeleteTemplateFolder(string id)
        {
            if (IsReadOnly("Template folder delete")) return;

            var folder = _templateFolders.FirstOrDefault(f => f.Id == id);
            if (folder == null) return;

            // Шаблоны папки не пропадают вместе с ней: они уходят в конец
            // «Моих шаблонов» в том порядке, в каком лежали в папке.
            var moved = _templates.Where(t => !t.IsBuiltIn && t.FolderId == id).ToList();
            foreach (var template in moved)
            {
                template.FolderId = string.Empty;
                _templates.Remove(template);
            }
            _templates.AddRange(moved);

            _templateFolders.Remove(folder);
            _collapsedTemplateFolderIds.Remove(id);
            _logger.Debug("Template folder deleted: {Name}, templates moved: {Count}", folder.Name, moved.Count);
        }

        /// <summary>
        /// Сдвинуть раздел вкладки шаблонов на shift мест: свою папку, «Мои
        /// шаблоны» или «Встроенные» — порядок разделов любой. Свои папки в
        /// своём списке следуют за порядком разделов.
        /// </summary>
        public void MoveTemplateFolder(string id, int shift)
        {
            if (IsReadOnly("Template folder move")) return;
            if (string.IsNullOrEmpty(id)) return;

            NormalizeTemplateSectionOrder();

            var from = _templateSectionOrder.IndexOf(id);
            if (from < 0) return;

            var to = Math.Clamp(from + shift, 0, _templateSectionOrder.Count - 1);
            if (to == from) return;

            _templateSectionOrder.RemoveAt(from);
            _templateSectionOrder.Insert(to, id);
            SyncTemplateFoldersToSectionOrder();
        }

        public void LoadTemplateFolders(List<CharacterTemplateFolder> folders)
        {
            _templateFolders.Clear();
            if (folders != null)
                _templateFolders.AddRange(folders.Where(f => f != null && !string.IsNullOrEmpty(f.Id)));

            // Порядок разделов — от этих же папок: прежний принадлежал
            // другому проекту. Свой порядок приходит следом, LoadTemplateSectionOrder.
            _templateSectionOrder.Clear();
        }

        // ── Заготовки полей ───────────────────────────────────────────────
        //
        // Встроенные заготовки — из кода (CharacterFieldPresets), свои — из
        // проекта: их сохраняют из полей анкет. Своя заготовка с тем же
        // названием и типом заменяется — так её и обновляют.

        private readonly List<CharacterFieldPreset> _fieldPresets = new();

        public IReadOnlyList<CharacterFieldPreset> GetFieldPresets() =>
            _fieldPresets.Concat(CharacterFieldPresets.BuiltIn).ToList();

        public IReadOnlyList<CharacterFieldPreset> GetCustomFieldPresets() => _fieldPresets;

        public CharacterFieldPreset? SaveFieldPreset(CharacterAnketaField field)
        {
            if (field == null) return null;
            if (IsReadOnly("Field preset save")) return null;

            var copy = JsonConvert.DeserializeObject<CharacterAnketaField>(JsonConvert.SerializeObject(field))!;
            copy.Key = string.Empty;
            copy.FieldId = string.Empty;
            copy.GroupName = string.Empty;
            copy.Order = 0;
            copy.Depth = null;

            var name = string.IsNullOrWhiteSpace(copy.Name) ? "Поле" : copy.Name.Trim();
            copy.Name = name;

            var existing = _fieldPresets.FirstOrDefault(p =>
                p.Field.Type == copy.Type &&
                string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
            if (existing != null)
            {
                existing.Field = copy;
                _logger.Debug("Field preset updated: {Name}", name);
                return existing;
            }

            var preset = new CharacterFieldPreset { Name = name, Hint = "Своя заготовка", Field = copy };
            _fieldPresets.Insert(0, preset);
            _logger.Debug("Field preset saved: {Name}", name);
            return preset;
        }

        public void DeleteFieldPreset(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (IsReadOnly("Field preset delete")) return;

            _fieldPresets.RemoveAll(p => p.Id == id);
        }

        public void LoadFieldPresets(List<CharacterFieldPreset> presets)
        {
            _fieldPresets.Clear();
            if (presets == null) return;

            foreach (var preset in presets)
            {
                if (preset?.Field == null || string.IsNullOrEmpty(preset.Id)) continue;
                preset.IsBuiltIn = false;
                _fieldPresets.Add(preset);
            }
        }

        // ── Порядок разделов вкладки шаблонов ─────────────────────────────
        //
        // Разделы — свои папки, «Мои шаблоны» и «Встроенные» — стоят в любом
        // порядке. Ключи двух последних — TemplateSections. Раздел, которого
        // в сохранённом порядке нет, встаёт на место по умолчанию: «Мои
        // шаблоны» — первыми, новая папка — перед «Встроенными», если те
        // последние, иначе в самый конец, «Встроенные» — последними.

        private readonly List<string> _templateSectionOrder = new();

        public IReadOnlyList<string> GetTemplateSectionOrder()
        {
            NormalizeTemplateSectionOrder();
            return _templateSectionOrder;
        }

        public void LoadTemplateSectionOrder(List<string> ids)
        {
            _templateSectionOrder.Clear();
            if (ids != null)
                _templateSectionOrder.AddRange(ids.Where(i => !string.IsNullOrEmpty(i)));

            NormalizeTemplateSectionOrder();
            SyncTemplateFoldersToSectionOrder();
        }

        /// <summary>
        /// Свести порядок разделов к тем, что есть: удалённые папки уходят,
        /// повторы тоже, недостающие разделы встают на места по умолчанию.
        /// </summary>
        private void NormalizeTemplateSectionOrder()
        {
            var known = new HashSet<string>(StringComparer.Ordinal)
            {
                TemplateSections.Ungrouped,
                TemplateSections.BuiltIn
            };
            foreach (var folder in _templateFolders) known.Add(folder.Id);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            _templateSectionOrder.RemoveAll(id => !known.Contains(id) || !seen.Add(id));

            if (!seen.Contains(TemplateSections.Ungrouped))
            {
                _templateSectionOrder.Insert(0, TemplateSections.Ungrouped);
                seen.Add(TemplateSections.Ungrouped);
            }

            foreach (var folder in _templateFolders)
            {
                if (!seen.Add(folder.Id)) continue;

                var builtIn = _templateSectionOrder.IndexOf(TemplateSections.BuiltIn);
                if (builtIn >= 0 && builtIn == _templateSectionOrder.Count - 1)
                    _templateSectionOrder.Insert(builtIn, folder.Id);
                else
                    _templateSectionOrder.Add(folder.Id);
            }

            if (!seen.Contains(TemplateSections.BuiltIn))
                _templateSectionOrder.Add(TemplateSections.BuiltIn);
        }

        /// <summary>Свои папки в своём списке — в том же порядке, что и среди разделов.</summary>
        private void SyncTemplateFoldersToSectionOrder()
        {
            var position = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < _templateSectionOrder.Count; i++)
                position[_templateSectionOrder[i]] = i;

            var sorted = _templateFolders
                .OrderBy(f => position.TryGetValue(f.Id, out var i) ? i : int.MaxValue)
                .ToList();

            _templateFolders.Clear();
            _templateFolders.AddRange(sorted);
        }

        // ── Скрытые шаблоны и свёрнутые разделы ───────────────────────────
        //
        // Скрытым бывает и встроенный шаблон, поэтому скрытие хранится
        // списком опознавателей, а не в самом шаблоне: встроенные каждый
        // запуск собираются из кода заново.

        private readonly HashSet<string> _hiddenTemplateIds = new(StringComparer.Ordinal);
        private readonly HashSet<string> _collapsedTemplateFolderIds = new(StringComparer.Ordinal);

        public bool IsTemplateHidden(string id) =>
            !string.IsNullOrEmpty(id) && _hiddenTemplateIds.Contains(id);

        public void SetTemplateHidden(string id, bool hidden)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (IsReadOnly("Template hide")) return;

            if (hidden) _hiddenTemplateIds.Add(id);
            else _hiddenTemplateIds.Remove(id);
        }

        public IReadOnlyCollection<string> GetHiddenTemplateIds() => _hiddenTemplateIds;

        public void LoadHiddenTemplateIds(List<string> ids)
        {
            _hiddenTemplateIds.Clear();
            if (ids != null)
                foreach (var id in ids.Where(i => !string.IsNullOrEmpty(i)))
                    _hiddenTemplateIds.Add(id);
        }

        public bool IsTemplateFolderCollapsed(string id) =>
            !string.IsNullOrEmpty(id) && _collapsedTemplateFolderIds.Contains(id);

        // Свёрнутость — вид, а не содержимое: в режиме сравнения раздел
        // сворачивают и разворачивают свободно, как и любую другую часть
        // экрана.
        public void SetTemplateFolderCollapsed(string id, bool collapsed)
        {
            if (string.IsNullOrEmpty(id)) return;

            if (collapsed) _collapsedTemplateFolderIds.Add(id);
            else _collapsedTemplateFolderIds.Remove(id);
        }

        public IReadOnlyCollection<string> GetCollapsedTemplateFolderIds() => _collapsedTemplateFolderIds;

        public void LoadCollapsedTemplateFolderIds(List<string> ids)
        {
            _collapsedTemplateFolderIds.Clear();
            if (ids != null)
                foreach (var id in ids.Where(i => !string.IsNullOrEmpty(i)))
                    _collapsedTemplateFolderIds.Add(id);
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
