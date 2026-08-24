using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Writersword.Modules.Characters.Models
{
    /// <summary>
    /// Одна запись папки «Недавние».
    ///
    /// Хранится полная ссылка вместе с кадром: миниатюра в списке показывает
    /// картинку ровно так, как её в прошлый раз обрезали, и повторный выбор
    /// даёт то же кадрирование. Совпадение записей при этом считается по
    /// адресу файла без кадра — одна картинка стоит в списке один раз, сколько
    /// бы раз её ни брали с разной обрезкой.
    /// </summary>
    public class CharacterAvatarRecentEntry
    {
        /// <summary>Полная ссылка на аватар, возможно с кадром.</summary>
        [JsonProperty("Ref")]
        public string AvatarRef { get; set; } = string.Empty;

        /// <summary>
        /// Отметка последнего использования. По ней список держится в порядке
        /// свежести и подрезается с хвоста, когда упирается в предел.
        /// </summary>
        [JsonProperty("UsedAt")]
        public DateTime UsedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Папка «Недавние» — глобальная, живёт в settings.json через
    /// module-settings (ключ CharacterAvatarRecents), как глобальные палитры.
    ///
    /// Список ссылается на аватарки, но не хранит их файлы. Отсюда два его
    /// свойства, заданные требованием: удаление записи из «Недавних» ничего не
    /// удаляет и ни о чём не спрашивает — картинка остаётся и в проекте, и на
    /// карточках, которые её носят; а ссылки на аватарки чужого проекта в
    /// списке просто не показываются — их нечем прочитать, и отсев идёт при
    /// сборке списка, без чистки хранилища.
    /// </summary>
    public class CharacterAvatarRecentsData
    {
        /// <summary>Свежие записи впереди.</summary>
        [JsonProperty("Entries")]
        public List<CharacterAvatarRecentEntry> Entries { get; set; } = new();
    }
}
