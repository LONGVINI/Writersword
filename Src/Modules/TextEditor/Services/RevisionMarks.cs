using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>Отметки правок у блоков, которые правка вставляет или удаляет целиком.</summary>
    public static class RevisionMarks
    {
        /// <summary>Отметка вставки блока; null — блок не вставлен правкой или правкой не отмечается.</summary>
        public static RevisionInfo? InsertedOf(BlockModel block) => (block as IRevisionMarked)?.Inserted;

        /// <summary>Отметка удаления блока; null — блок не удалён правкой или правкой не отмечается.</summary>
        public static RevisionInfo? DeletedOf(BlockModel block) => (block as IRevisionMarked)?.Deleted;

        /// <summary>Смена вида объекта правкой; null — вид не менялся или не отслеживается.</summary>
        public static ObjectChange? ChangeOf(BlockModel block) => (block as IAppearanceTracked)?.Change;

        /// <summary>
        /// Снимает с блока все отметки правок. Нужна копии объекта: вставка копии —
        /// новая правка того, кто вставляет, а не чужая вставка, удаление или смена
        /// вида оригинала.
        /// </summary>
        public static void Clear(BlockModel block)
        {
            if (block is IRevisionMarked marked)
            {
                marked.Inserted = null;
                marked.Deleted = null;
            }

            if (block is IAppearanceTracked tracked)
                tracked.Change = null;
        }

        /// <summary>У блока есть отметка правки: вставка, удаление или смена вида.</summary>
        public static bool HasMark(BlockModel block)
            => block is IRevisionMarked marked
               && (marked.Inserted is not null || marked.Deleted is not null
                   || (block is IAppearanceTracked { Change: not null }));
    }
}
