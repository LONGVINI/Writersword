using System;
using System.Collections.Generic;
using System.Linq;

namespace Writersword.Modules.Characters.Models
{
    /// <summary>
    /// Как один персонаж называет другого. Не имя и не псевдоним: имя
    /// принадлежит персонажу, обращение — отношениям между двумя.
    ///
    /// Вариантов у одной пары бывает несколько: «Алинусик», «Хрюшка»,
    /// «Милашка», «Алина, когда злится». Это не конфликт данных, а регистры —
    /// поэтому у формы есть необязательный повод. Повод свободный, программа
    /// его не разбирает.
    /// </summary>
    public class CharacterAddressForm
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>Само обращение: «Алинусик», «Ваше величество», «Балбес».</summary>
        public string Value { get; set; } = string.Empty;

        /// <summary>
        /// Повод или регистр: «нежно», «в шутку», «когда злится», «при чужих».
        /// Ни на что не влияет механически — он для автора.
        /// </summary>
        public string Occasion { get; set; } = string.Empty;
    }

    /// <summary>
    /// Правило обращения для целой группы: «все из этой папки зовут её Аля».
    ///
    /// Группой служит папка персонажей — заводить отдельный справочник групп
    /// незачем: «друзья Алины» это и есть папка, а иметь два списка одних и тех
    /// же людей значит обречь их на расхождение.
    /// </summary>
    public class CharacterGroupAddress
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>Папка, чьи персонажи обращаются именно так.</summary>
        public string FolderId { get; set; } = string.Empty;

        public string Value { get; set; } = string.Empty;

        /// <summary>Повод или регистр — как у личных обращений.</summary>
        public string Occasion { get; set; } = string.Empty;
    }

    /// <summary>
    /// Разрешение обращения каскадом. Ищем сверху вниз, первое найденное
    /// побеждает — заполнять приходится только исключения, а их всегда мало.
    ///
    /// 1. персональное правило: лучший друг зовёт «Алинусик»;
    /// 2. правило группы: друзья зовут «Аля» — уровень пока не реализован,
    ///    для него нужен справочник групп обращающихся;
    /// 3. обращение по умолчанию у самого персонажа: все прочие зовут «Алина»;
    /// 4. отображаемое имя.
    ///
    /// Матрица «кто как кого называет» не хранится никогда: при трёхстах
    /// персонажах это девяносто тысяч ячеек, которые не заполнить и не
    /// нарисовать. Сетка остаётся способом посмотреть на выбранную группу.
    /// </summary>
    public static class CharacterAddress
    {
        /// <summary>
        /// Привести формы обращения к рабочему виду. У старых сохранений есть
        /// только список строк — собираем формы из него.
        /// </summary>
        public static void Normalize(CharacterRelationship relationship)
        {
            if (relationship == null) return;

            relationship.SourceCallsTargetForms ??= new List<CharacterAddressForm>();
            relationship.SourceCallsTargetAs ??= new List<string>();

            relationship.SourceCallsTargetForms.RemoveAll(
                f => f == null || string.IsNullOrWhiteSpace(f.Value));

            if (relationship.SourceCallsTargetForms.Count == 0)
            {
                foreach (var value in relationship.SourceCallsTargetAs)
                {
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    relationship.SourceCallsTargetForms.Add(new CharacterAddressForm { Value = value });
                }
            }

            // Старый список остаётся источником для кода, который ещё не знает
            // о формах: он всегда повторяет значения форм.
            relationship.SourceCallsTargetAs = relationship.SourceCallsTargetForms
                .Select(f => f.Value)
                .ToList();
        }

        /// <summary>
        /// Как источник назовёт цель. Возвращает первое подходящее по каскаду.
        /// </summary>
        /// <param name="sourceFolderIds">
        /// Папки, в которых состоит обращающийся. По ним ищется групповое
        /// правило — оно уступает личному и побеждает общее.
        /// </param>
        public static string Resolve(
            Character target,
            CharacterRelationship? relationshipFromSource,
            IEnumerable<string>? sourceFolderIds = null)
        {
            if (target == null) return string.Empty;

            var personal = relationshipFromSource?.SourceCallsTargetForms?
                .FirstOrDefault(f => !string.IsNullOrWhiteSpace(f.Value));

            if (personal != null) return personal.Value;

            if (sourceFolderIds != null && target.GroupAddresses != null)
            {
                var folders = sourceFolderIds as IList<string> ?? sourceFolderIds.ToList();

                var group = target.GroupAddresses.FirstOrDefault(
                    g => !string.IsNullOrWhiteSpace(g.Value) && folders.Contains(g.FolderId));

                if (group != null) return group.Value;
            }

            if (!string.IsNullOrWhiteSpace(target.DefaultAddress))
                return target.DefaultAddress;

            return target.Name;
        }

        /// <summary>
        /// Все варианты, которыми источник зовёт цель, — с поводами.
        /// Пустой список означает, что своего правила нет и работает каскад.
        /// </summary>
        public static IReadOnlyList<CharacterAddressForm> Variants(
            CharacterRelationship? relationshipFromSource)
        {
            return relationshipFromSource?.SourceCallsTargetForms
                ?? (IReadOnlyList<CharacterAddressForm>)Array.Empty<CharacterAddressForm>();
        }
    }
}
