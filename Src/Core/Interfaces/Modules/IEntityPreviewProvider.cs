using System.Collections.Generic;
using Writersword.Core.Models.Preview;

namespace Writersword.Core.Interfaces.Modules
{
    /// <summary>
    /// Модуль, умеющий отдавать плоские снимки своих сущностей. Контракт
    /// объявлен в ядре намеренно: модули не ссылаются друг на друга, только
    /// на ядро. Редактор текста спрашивает снимки у ядра и не знает, какой
    /// модуль их дал и есть ли он в сборке вообще.
    /// </summary>
    public interface IEntityPreviewProvider
    {
        /// <summary>
        /// Вид сущности: "character", "event", "location". По нему ссылка
        /// в чужом модуле понимает, у кого спрашивать.
        /// </summary>
        string PreviewKind { get; }

        /// <summary>Снимки всех сущностей модуля на текущий момент.</summary>
        IReadOnlyList<EntityPreview> GetPreviews();
    }
}
