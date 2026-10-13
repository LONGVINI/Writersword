using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Modules.Characters.Models.Enums;

namespace Writersword.Modules.Characters.Models
{
    /// <summary>
    /// Уровни детализации анкеты: кратко, подробно, глубоко.
    ///
    /// Одна анкета раскрывается на разную глубину: эпизодическому персонажу
    /// хватит возраста, роста и «примечательной внешности», главному герою —
    /// походки, голоса и манеры одеваться. Делать для этого три анкеты
    /// нельзя: при правке они разъедутся, и значения перестанут сравниваться.
    /// Поэтому уровень — свойство поля, а глубину выбирают персонажу.
    ///
    /// Уровни накопительные: «Подробно» показывает и краткие поля, «Глубоко» —
    /// все. Их ровно три и названия фиксированы: так «подробно» значит одно
    /// и то же в любой анкете, и сравнение персонажей с разной глубиной
    /// понимает, что у одного поле не заполнено, а у другого не раскрыто.
    ///
    /// Уровень поля задаётся подзаголовком группы, под которым оно стоит, а
    /// само поле может перебить его своим. Хранится числом: расширить шкалу,
    /// если когда-нибудь понадобится, дёшево.
    /// </summary>
    public static class CharacterDetailLevels
    {
        public const int Brief = 1;
        public const int Detailed = 2;
        public const int Deep = 3;

        public const int Min = Brief;
        public const int Max = Deep;

        /// <summary>Все уровни по порядку.</summary>
        public static readonly IReadOnlyList<int> All = new[] { Brief, Detailed, Deep };

        public static int Clamp(int level) => Math.Clamp(level, Min, Max);

        public static string NameOf(int level) => Clamp(level) switch
        {
            Brief => "Кратко",
            Detailed => "Подробно",
            _ => "Глубоко"
        };

        /// <summary>
        /// Расшифровка значка уровня — для подсказок везде, где он стоит.
        /// Значок рисует DepthGlyph: три полоски по возрастанию.
        /// </summary>
        public const string SymbolHint =
            "Полоски — уровень детализации: одна — кратко, две — подробно, три — глубоко. " +
            "Контур без заливки — по важности персонажа: главному глубоко, эпизодическому кратко";

        public static string HintOf(int level) => Clamp(level) switch
        {
            Brief => "Самое заметное: возраст, рост, пара штрихов. Хватает эпизодическому персонажу",
            Detailed => "Краткое и то, что раскрывает персонажа второго плана",
            _ => "Всё, что есть в анкете: тип личности, мотивы, манеры. Для главных героев"
        };

        /// <summary>
        /// Глубина по важности персонажа — когда её не выбирали: главному
        /// всё, второстепенному — подробно, эпизодическому — кратко.
        /// </summary>
        public static int ForImportance(CharacterImportanceLevel importance) => importance switch
        {
            CharacterImportanceLevel.Primary => Deep,
            CharacterImportanceLevel.Secondary => Detailed,
            CharacterImportanceLevel.Tertiary => Brief,
            _ => Detailed
        };

        /// <summary>
        /// Уровень каждого поля анкеты по ключу. Подзаголовок задаёт уровень
        /// своим полям, поле со своим уровнем его перебивает. Поля до первого
        /// подзаголовка — краткие, если сами не сказали иного.
        /// </summary>
        public static Dictionary<string, int> ByKey(CharacterAnketa anketa)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            if (anketa?.Fields == null) return result;

            var byKey = anketa.Fields
                .Where(f => !string.IsNullOrEmpty(f.Key))
                .GroupBy(f => f.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var groupLevel = Brief;

            if (anketa.Layout != null)
            {
                foreach (var row in anketa.Layout)
                {
                    if (row.Kind == CharacterAnketaRowKind.Group)
                    {
                        groupLevel = Clamp(row.Depth);
                        continue;
                    }

                    if (row.Cells == null) continue;

                    foreach (var key in row.Cells)
                    {
                        if (string.IsNullOrEmpty(key) || result.ContainsKey(key)) continue;
                        if (!byKey.TryGetValue(key, out var field)) continue;
                        result[key] = field.Depth.HasValue ? Clamp(field.Depth.Value) : groupLevel;
                    }
                }
            }

            // Поле вне раскладки — у анкеты без раскладки или до её
            // приведения к полям: уровень только свой.
            foreach (var field in anketa.Fields)
            {
                if (string.IsNullOrEmpty(field.Key) || result.ContainsKey(field.Key)) continue;
                result[field.Key] = field.Depth.HasValue ? Clamp(field.Depth.Value) : Brief;
            }

            return result;
        }

        /// <summary>
        /// Уровень каждого поля по идентификатору поля — тому, по которому
        /// находятся значения у персонажа. Поле, встречающееся в анкете
        /// дважды, получает меньший из уровней: раз оно краткое в одном месте,
        /// прятать его глубже незачем.
        /// </summary>
        public static Dictionary<string, int> ByFieldId(CharacterAnketa anketa)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            if (anketa?.Fields == null) return result;

            var byKey = ByKey(anketa);

            foreach (var field in anketa.Fields)
            {
                var fieldId = CharacterFieldId.Resolve(field);
                if (string.IsNullOrEmpty(fieldId)) continue;

                var level = !string.IsNullOrEmpty(field.Key) && byKey.TryGetValue(field.Key, out var byLayout)
                    ? byLayout
                    : (field.Depth.HasValue ? Clamp(field.Depth.Value) : Brief);

                result[fieldId] = result.TryGetValue(fieldId, out var existing)
                    ? Math.Min(existing, level)
                    : level;
            }

            return result;
        }

        /// <summary>Самый глубокий уровень, на котором у анкеты есть поля. У пустой — кратко.</summary>
        public static int MaxOf(CharacterAnketa anketa)
        {
            var levels = ByKey(anketa);
            return levels.Count == 0 ? Brief : levels.Values.Max();
        }

        /// <summary>Анкета раскрывается глубже краткого — персонажу есть что выбирать.</summary>
        public static bool HasLevels(CharacterAnketa anketa) => MaxOf(anketa) > Brief;

        /// <summary>Выбранная персонажу глубина анкеты. Пусто — не выбирали.</summary>
        public static int? StoredFor(Character character, string anketaId)
        {
            if (character?.AnketaDepths == null || string.IsNullOrEmpty(anketaId)) return null;
            return character.AnketaDepths.TryGetValue(anketaId, out var level) ? Clamp(level) : null;
        }

        /// <summary>
        /// Глубина, на которую анкета раскрыта у персонажа: выбранная, а если
        /// не выбирали — по важности. Глубже последнего уровня анкеты не
        /// бывает: у анкеты из одних кратких полей и выбирать нечего.
        /// </summary>
        public static int ForCharacter(Character character, CharacterAnketa anketa, CharacterImportanceLevel? importance = null)
        {
            var max = MaxOf(anketa);
            var level = StoredFor(character, anketa.Id)
                        ?? ForImportance(importance ?? character.ImportanceLevel);
            return Math.Clamp(level, Brief, max);
        }

        /// <summary>
        /// Поля персонажа, которые есть в подключённых анкетах, но не раскрыты
        /// на выбранной глубине ни в одной из них. Значения у них сохраняются —
        /// поле просто не показано. Поле, раскрытое хотя бы в одной анкете,
        /// скрытым не считается.
        /// </summary>
        public static HashSet<string> HiddenFieldIds(
            Character character,
            Func<string, CharacterAnketa?> resolve,
            CharacterImportanceLevel? importance = null)
        {
            var belongs = new HashSet<string>(StringComparer.Ordinal);
            var visible = new HashSet<string>(StringComparer.Ordinal);

            if (character?.AttachedAnketaIds == null || resolve == null) return belongs;

            foreach (var anketaId in character.AttachedAnketaIds)
            {
                var anketa = resolve(anketaId);
                if (anketa == null) continue;

                var depth = ForCharacter(character, anketa, importance);
                foreach (var pair in ByFieldId(anketa))
                {
                    belongs.Add(pair.Key);
                    if (pair.Value <= depth) visible.Add(pair.Key);
                }
            }

            belongs.ExceptWith(visible);
            return belongs;
        }

        /// <summary>
        /// Глубина, которой хватает, чтобы показать все уже заведённые у
        /// персонажа значения анкеты. Нужна при переходе старых проектов:
        /// то, что автор уже видел и заполнял, не должно спрятаться.
        /// </summary>
        public static int CoveringExisting(Character character, CharacterAnketa anketa)
        {
            var existing = character.Parameters
                .Select(p => CharacterFieldId.Resolve(p))
                .Where(id => !string.IsNullOrEmpty(id))
                .ToHashSet(StringComparer.Ordinal);

            var level = Brief;
            foreach (var pair in ByFieldId(anketa))
                if (existing.Contains(pair.Key) && pair.Value > level)
                    level = pair.Value;

            return level;
        }
    }
}
