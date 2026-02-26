using System.Collections.Generic;
using Writersword.Core.Models.Settings;

namespace Writersword.Core.Interfaces.Modules
{
    /// <summary>
    /// Интерфейс для предоставления статического списка горячих клавиш.
    /// Реализуется классами метаданных модулей — не требует живого экземпляра модуля.
    /// Позволяет регистрировать хоткеи в HotKeyService при старте приложения
    /// до того как модули будут фактически созданы и инициализированы.
    /// </summary>
    public interface IHotKeyDescriptor
    {
        /// <summary>
        /// Получить статический список горячих клавиш модуля.
        /// Вызывается один раз при старте приложения через ModuleFactory.
        /// Не должен зависеть от состояния модуля — только описание клавиш.
        /// </summary>
        IReadOnlyList<HotKey> GetHotKeys();
    }
}