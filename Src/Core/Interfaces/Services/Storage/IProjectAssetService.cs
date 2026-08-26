using System.Collections.Generic;
using System.Threading.Tasks;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Models.Project;

namespace Writersword.Core.Interfaces.Services
{
    /// <summary>
    /// Файлы проекта целиком: картинки, шрифты и всё, что модули держат рядом
    /// с текстом.
    ///
    /// Служба ничего не хранит сама. Она обходит модули, объявившие
    /// <see cref="IProjectAssetHolder"/>, и складывает их ответы в один. Нужна
    /// затем, что вопрос «уедет ли проект целиком» не помещается ни в один
    /// модуль: картинки персонажей знает один, бумагу вида чтения другой, а
    /// человек передаёт проект один раз и целиком.
    ///
    /// Список модулей передаётся снаружи — так же, как у сбора данных модулей
    /// (IModuleStateCollectorService): какие модули сейчас живы, знает рабочая
    /// область, а не хранилище.
    /// </summary>
    public interface IProjectAssetService
    {
        /// <summary>
        /// Что известно о файлах проекта. Ничего не меняет.
        /// </summary>
        ProjectAssetReport Inspect(IEnumerable<IModule> modules);

        /// <summary>
        /// Уложить в архив проекта всё, что лежит снаружи. Возвращает число
        /// уложенных файлов. Исходники остаются на местах.
        /// </summary>
        Task<int> EmbedAllAsync(IEnumerable<IModule> modules);

        /// <summary>
        /// Убрать из архива файлы, на которые не осталось ссылок. Зовётся
        /// только по прямой просьбе человека — см. пояснение у
        /// <see cref="IProjectAssetHolder.CompactUnusedAssets"/>.
        /// </summary>
        ProjectAssetCleanup CompactAll(IEnumerable<IModule> modules);
    }
}
