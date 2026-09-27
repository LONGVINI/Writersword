using System;
using System.Collections.Generic;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Картинки импортированного документа, которые ещё пишутся в проект.
    ///
    /// Раньше документ показывался только после того, как все его картинки были
    /// записаны в проект: по одной, каждая своей транзакцией и со сбросом на диск,
    /// в потоке интерфейса. У документа с десятком фотографий это секунды пустого
    /// экрана, за которые программа не отвечает.
    ///
    /// Теперь документ показывается сразу после разбора, а картинки пишутся в
    /// проект фоном. Пока запись не дошла до картинки, её байты берутся отсюда:
    /// лист, буфер обмена и выгрузка спрашивают сначала этот буфер, потом проект.
    /// Записанная картинка из буфера убирается. Не записавшаяся остаётся в нём до
    /// конца работы программы — на листе она видна, а предупреждение о том, что её
    /// нет в проекте, человек уже получил.
    ///
    /// Имена файлов картинок импорта уникальны (img_&lt;guid&gt;), поэтому буфер общий
    /// на все открытые проекты: чужую картинку по имени из него не достать.
    /// </summary>
    public static class PendingProjectImages
    {
        private static readonly object _lock = new();

        private static readonly Dictionary<string, byte[]> _images =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Кладёт картинки в буфер до записи в проект.</summary>
        public static void AddRange(IEnumerable<KeyValuePair<string, byte[]>> images)
        {
            if (images is null) return;

            lock (_lock)
            {
                foreach (var image in images)
                {
                    if (string.IsNullOrEmpty(image.Key) || image.Value is null) continue;
                    _images[image.Key] = image.Value;
                }
            }
        }

        /// <summary>Байты картинки из буфера или null, если её в буфере нет.</summary>
        public static byte[]? TryGet(string? fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;

            lock (_lock)
                return _images.TryGetValue(fileName!, out var data) ? data : null;
        }

        /// <summary>Картинка записана в проект — буфер её больше не держит.</summary>
        public static void Remove(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return;

            lock (_lock)
                _images.Remove(fileName);
        }

        /// <summary>
        /// Байты картинки проекта: сначала из буфера импорта, потом из самого проекта.
        /// relativePath — путь в проекте вида «TextEditor/Images/имя».
        /// </summary>
        public static byte[]? Read(Writersword.Core.Services.DocumentContext? ctx, string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return null;

            int slash = relativePath.LastIndexOf('/');
            string name = slash >= 0 ? relativePath.Substring(slash + 1) : relativePath;

            return TryGet(name) ?? ctx?.ReadFile(relativePath);
        }
    }
}
