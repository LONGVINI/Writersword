using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Modules.Characters.Models.Enums;

namespace Writersword.Modules.Characters.Models
{
    /// <summary>Что за строка раскладки анкеты.</summary>
    public enum CharacterAnketaRowKind
    {
        /// <summary>Строка полей: одна, две или три ячейки.</summary>
        Fields,

        /// <summary>Подзаголовок группы: «Эмоции», «Внешность».</summary>
        Group
    }

    /// <summary>
    /// Строка раскладки анкеты. Ячейки хранят ключи полей
    /// (CharacterAnketaField.Key); пустая строка — пустая ячейка, в неё
    /// можно поставить поле.
    /// </summary>
    public class CharacterAnketaRow
    {
        public CharacterAnketaRowKind Kind { get; set; } = CharacterAnketaRowKind.Fields;

        /// <summary>Название группы — у строки-подзаголовка.</summary>
        public string Title { get; set; } = string.Empty;

        public List<string> Cells { get; set; } = new();

        public CharacterAnketaRow Clone() => new()
        {
            Kind = Kind,
            Title = Title,
            Cells = Cells.ToList()
        };
    }

    /// <summary>
    /// Свой значок анкеты: картинка из файла, которую ставят оценкам и
    /// подписям. Glyph — в формате CharacterAnketaField.LabelIcon.
    /// </summary>
    public class CharacterAnketaAsset
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;
        public string Glyph { get; set; } = string.Empty;
    }

    /// <summary>
    /// Раскладка анкеты: приведение к порядку и согласование с полями.
    ///
    /// Раскладка — главная: из неё выводятся порядок полей и их группы
    /// (ApplyOrderAndGroups), потому что ими пользуются сравнение карточек и
    /// всё, что было написано до появления раскладки. Старые анкеты раскладки
    /// не имеют — она строится из порядка и групп так, как поля и стояли в
    /// карточке: короткие парами, широкие строкой.
    /// </summary>
    public static class CharacterAnketaLayout
    {
        public const int MaxColumns = 3;

        /// <summary>Выдать ключи полям без ключа. Ключ выводится из идентификатора поля.</summary>
        public static void EnsureKeys(CharacterAnketa anketa)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in anketa.Fields)
            {
                if (!string.IsNullOrWhiteSpace(field.Key) && used.Add(field.Key)) continue;

                var baseKey = "f_" + (string.IsNullOrEmpty(CharacterFieldId.Resolve(field))
                    ? "field"
                    : CharacterFieldId.Resolve(field));
                var key = baseKey;
                for (int i = 2; used.Contains(key); i++) key = baseKey + "_" + i;

                field.Key = key;
                used.Add(key);
            }
        }

        /// <summary>
        /// Привести раскладку к полям: у каждого поля ровно одна ячейка,
        /// чужих ключей нет, в строке от одной до трёх ячеек. Пустые строки
        /// остаются — их поставил автор, чтобы положить туда поля.
        /// </summary>
        public static void Normalize(CharacterAnketa anketa)
        {
            anketa.Fields ??= new List<CharacterAnketaField>();
            anketa.Assets ??= new List<CharacterAnketaAsset>();
            EnsureKeys(anketa);

            if (anketa.Layout == null || anketa.Layout.Count == 0)
            {
                anketa.Layout = BuildDefault(anketa);
                return;
            }

            var keys = anketa.Fields.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
            var used = new HashSet<string>(StringComparer.Ordinal);

            foreach (var row in anketa.Layout)
            {
                row.Cells ??= new List<string>();
                row.Title ??= string.Empty;

                if (row.Kind == CharacterAnketaRowKind.Group)
                {
                    row.Cells.Clear();
                    continue;
                }

                row.Cells = row.Cells
                    .Take(MaxColumns)
                    .Select(key => !string.IsNullOrEmpty(key) && keys.Contains(key) && used.Add(key) ? key : string.Empty)
                    .ToList();
                if (row.Cells.Count == 0) row.Cells.Add(string.Empty);
            }

            foreach (var field in anketa.Fields.OrderBy(f => f.Order))
            {
                if (used.Contains(field.Key)) continue;
                anketa.Layout.Add(new CharacterAnketaRow { Cells = new List<string> { field.Key } });
                used.Add(field.Key);
            }
        }

        /// <summary>
        /// Раскладка старой анкеты — как поля стояли в карточке до неё:
        /// подзаголовок над каждой группой, короткие поля парами, широкие
        /// строкой целиком.
        /// </summary>
        public static List<CharacterAnketaRow> BuildDefault(CharacterAnketa anketa)
        {
            var rows = new List<CharacterAnketaRow>();
            var ordered = anketa.Fields.OrderBy(f => f.Order).ToList();
            var anyGroup = ordered.Any(f => !string.IsNullOrWhiteSpace(f.GroupName));

            // Поля одной группы держатся вместе в порядке первого появления
            // группы — так их и показывала карточка.
            var groups = new List<string>();
            var byGroup = new Dictionary<string, List<CharacterAnketaField>>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var field in ordered)
            {
                var group = (field.GroupName ?? string.Empty).Trim();
                if (!byGroup.TryGetValue(group, out var list))
                {
                    list = new List<CharacterAnketaField>();
                    byGroup[group] = list;
                    groups.Add(group);
                }
                list.Add(field);
            }

            foreach (var group in groups)
            {
                if (anyGroup && group.Length > 0)
                    rows.Add(new CharacterAnketaRow { Kind = CharacterAnketaRowKind.Group, Title = group });

                CharacterAnketaField? pending = null;
                foreach (var field in byGroup[group])
                {
                    if (IsWideField(field))
                    {
                        if (pending != null)
                        {
                            rows.Add(new CharacterAnketaRow { Cells = new List<string> { pending.Key } });
                            pending = null;
                        }
                        rows.Add(new CharacterAnketaRow { Cells = new List<string> { field.Key } });
                        continue;
                    }

                    if (pending == null)
                    {
                        pending = field;
                        continue;
                    }

                    rows.Add(new CharacterAnketaRow { Cells = new List<string> { pending.Key, field.Key } });
                    pending = null;
                }

                if (pending != null)
                    rows.Add(new CharacterAnketaRow { Cells = new List<string> { pending.Key } });
            }

            return rows;
        }

        /// <summary>
        /// Порядок и группы полей — из раскладки: слева направо, сверху вниз,
        /// группа — ближайший подзаголовок выше.
        /// </summary>
        public static void ApplyOrderAndGroups(CharacterAnketa anketa)
        {
            var byKey = anketa.Fields
                .Where(f => !string.IsNullOrEmpty(f.Key))
                .GroupBy(f => f.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var order = 0;
            var group = string.Empty;

            foreach (var row in anketa.Layout)
            {
                if (row.Kind == CharacterAnketaRowKind.Group)
                {
                    group = row.Title?.Trim() ?? string.Empty;
                    continue;
                }

                foreach (var key in row.Cells)
                {
                    if (string.IsNullOrEmpty(key) || !byKey.TryGetValue(key, out var field)) continue;
                    field.Order = order++;
                    field.GroupName = group;
                }
            }
        }

        /// <summary>
        /// Поле, которому мало половины строки: текст и описание, выбор
        /// чипами, полюса, число с несколькими способами, большая палитра.
        /// </summary>
        public static bool IsWideField(CharacterAnketaField field)
        {
            switch (field.Type)
            {
                case CharacterParameterType.LongText:
                case CharacterParameterType.Text:
                case CharacterParameterType.MultiChoice:
                    return true;

                case CharacterParameterType.StateList:
                    if (field.Display == CharacterFieldDisplay.Chips) return true;
                    if (field.Display == CharacterFieldDisplay.Dropdown) return false;
                    return CharacterFieldDefinition.SplitStates(field.StatesRaw).Count <= 6;

                case CharacterParameterType.Numeric:
                    return field.Display == CharacterFieldDisplay.Bipolar;

                case CharacterParameterType.Number:
                    var modes = field.NumberModes == CharacterNumberModes.None ? CharacterNumberModes.Exact : field.NumberModes;
                    return CountFlags(modes) > 1;

                case CharacterParameterType.Color:
                    return (field.Palette?.Count ?? 0) > 6;

                default:
                    return false;
            }
        }

        private static int CountFlags(CharacterNumberModes modes)
        {
            var count = 0;
            foreach (CharacterNumberModes flag in Enum.GetValues(typeof(CharacterNumberModes)))
                if (flag != CharacterNumberModes.None && modes.HasFlag(flag)) count++;
            return count;
        }
    }
}
