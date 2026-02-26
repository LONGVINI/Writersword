using Writersword.Core.Interfaces.Modules;

namespace Writersword.Core.Interfaces.Modules
{
    /// <summary>
    /// Интерфейс для модулей которые имеют собственные горячие клавиши.
    /// Наследует IHotKeyDescriptor — предоставляет как описание клавиш,
    /// так и логику их выполнения.
    /// Реализуется живым экземпляром модуля.
    /// </summary>
    public interface IHotKeyProvider : IHotKeyDescriptor
    {
        /// <summary>
        /// Выполнить действие по ID горячей клавиши.
        /// Вызывается из HotKeyService когда комбинация совпала
        /// и для данного moduleType зарегистрирован активный executor.
        /// </summary>
        void ExecuteHotKey(string id);
    }
}