namespace Writersword.Core.Interfaces.Modules
{
    /// <summary>
    /// Модуль с тяжёлой загрузкой данных, поддерживающий двухфазное восстановление.
    /// Фаза 1 — PrepareCustomData: парсинг и десериализация данных на любом потоке
    /// (модель ещё не привязана к вьюмоделям и UI, гонок с интерфейсом нет).
    /// Фаза 2 — ApplyPreparedCustomData: применение подготовленных данных на UI-потоке
    /// (создание вьюмоделей, загрузка документа во вью).
    /// Позволяет восстанавливать модуль без длительной блокировки UI-потока:
    /// вызов SetCustomData(data) эквивалентен ApplyPreparedCustomData(PrepareCustomData(data)).
    /// </summary>
    public interface IPreparedDataModule
    {
        /// <summary>
        /// Разобрать и десериализовать данные модуля. Можно вызывать с любого потока.
        /// </summary>
        /// <param name="data">Сырые данные в формате SetCustomData (строка, byte[], null)</param>
        /// <returns>Непрозрачный подготовленный объект или null если данные пусты либо нечитаемы</returns>
        object? PrepareCustomData(object? data);

        /// <summary>
        /// Применить подготовленные данные. Вызывать только на UI-потоке.
        /// При prepared == null модуль загружает пустое состояние по умолчанию
        /// (эквивалент SetCustomData(null)).
        /// </summary>
        /// <param name="prepared">Объект полученный из PrepareCustomData</param>
        void ApplyPreparedCustomData(object? prepared);
    }
}
