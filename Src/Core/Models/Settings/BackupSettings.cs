namespace Writersword.Core.Models.Settings
{
    /// <summary>
    /// Настройки истории версий проекта.
    /// Хранятся глобально через ISettingsService под ключом "backups".
    /// </summary>
    public class BackupSettings
    {
        /// <summary>Вести ли историю версий вообще.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Папка, в которой лежат хранилища .wsbk.
        /// Пустая строка означает «рядом с самим проектом»: для
        /// D:\Books\Роман.writersword хранилище будет D:\Books\Роман.wsbk.
        /// </summary>
        public string StoragePath { get; set; } = string.Empty;

        // ── Когда снимать точку ───────────────────────────────────────────

        /// <summary>Снимать точку при сохранении пользователем (Ctrl+S).</summary>
        public bool OnManualSave { get; set; } = true;

        /// <summary>Снимать точку при закрытии программы.</summary>
        public bool OnAppClose { get; set; } = true;

        /// <summary>
        /// Снимать точки во время работы, опираясь на автосохранение.
        /// Частота при этом определяется не автосохранением, а MinIntervalMinutes:
        /// приложение сохраняется раз в две минуты, но точка возникает не чаще
        /// заданного интервала и только если содержимое действительно менялось.
        /// </summary>
        public bool OnTimer { get; set; } = true;

        /// <summary>
        /// Минимальный промежуток между автоматическими точками, минуты.
        /// Ноль отключает ограничение. На ручную точку, точку при закрытии и
        /// точку перед откатом не распространяется — они нужны всегда.
        /// </summary>
        public int MinIntervalMinutes { get; set; } = 60;

        // ── Сколько хранить ───────────────────────────────────────────────

        /// <summary>
        /// Прореживать старые точки: за сегодня хранятся все, за последнюю
        /// неделю — по одной на день, дальше — по одной на неделю. Список
        /// остаётся коротким, а глубина истории измеряется месяцами.
        /// Ручные точки прореживание не трогает.
        /// </summary>
        public bool Thinning { get; set; } = true;

        /// <summary>
        /// Жёсткий потолок числа точек после прореживания. Страховка от
        /// разрастания хранилища; самые старые удаляются вместе с объектами,
        /// на которые больше никто не ссылается.
        /// </summary>
        public int MaxSnapshots { get; set; } = 100;

        // ── Точки, созданные вручную ──────────────────────────────────────

        /// <summary>
        /// Что делать с точками, которые пользователь поставил кнопкой.
        /// По умолчанию они не удаляются: момент помечен осознанно.
        /// </summary>
        public UserPointRetention UserPointRetention { get; set; } = UserPointRetention.Never;

        /// <summary>Срок хранения в днях для режима <see cref="UserPointRetention.AfterAge"/>.</summary>
        public int UserPointMaxAgeDays { get; set; } = 90;

        /// <summary>Сколько последних ручных точек хранить в режиме <see cref="UserPointRetention.KeepLast"/>.</summary>
        public int UserPointKeepLast { get; set; } = 20;
    }

    /// <summary>
    /// Правило удаления точек, созданных пользователем вручную.
    /// Автоматические точки живут по общим правилам прореживания и лимита,
    /// а эти — по отдельному, потому что ставятся осмысленно.
    /// </summary>
    public enum UserPointRetention
    {
        /// <summary>Не удалять никогда.</summary>
        Never = 0,

        /// <summary>Удалять по возрасту.</summary>
        AfterAge = 1,

        /// <summary>Хранить заданное число последних, остальные удалять.</summary>
        KeepLast = 2,

        /// <summary>
        /// Участвовать в общем лимите, но вытесняться последними: пока есть
        /// автоматические точки, ручные не трогаются.
        /// </summary>
        WithLimit = 3
    }
}
