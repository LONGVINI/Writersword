namespace Writersword.Core.Enums
{
    /// <summary>
    /// Результат диалога восстановления проекта из автосохранения
    /// </summary>
    public enum RecoveryDialogResult
    {
        /// <summary>Диалог не был показан или закрыт без выбора</summary>
        None,

        /// <summary>Восстановить из автосохранения (загрузить .wsasd и удалить его)</summary>
        Restore,

        /// <summary>Открыть сохранённую версию (загрузить .writersword, кеш оставить)</summary>
        OpenSaved,

        /// <summary>Просмотреть файлы (режим сравнения версий)</summary>
        Compare,

        /// <summary>Отменить открытие проекта</summary>
        Cancel
    }
}