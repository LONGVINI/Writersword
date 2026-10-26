namespace Writersword.Infrastructure.Diagnostics
{
    /// <summary>
    /// Переключатели для проверки гипотез о памяти и отклике. Читаются из
    /// appsettings.json, раздел "Diagnostics", при запуске программы.
    /// <para>
    /// DisableBackgroundWork — выключить всю фоновую работу с данными: запись кеша
    /// восстановления (тики CacheUpdateService, отложенная запись кеша при уходе с
    /// вкладки, сбор данных модулей при смене воркмода) и точки истории версий.
    /// Нужен, чтобы увидеть, сколько памяти и времени занимает программа без неё.
    /// Пока он включён, защиты от падения нет: несохранённое теряется при аварии.
    /// В обычной работе всегда false.
    /// </para>
    /// </summary>
    public static class DiagnosticSwitches
    {
        /// <summary>Фоновая работа с данными выключена (кеш восстановления и история версий).</summary>
        public static bool BackgroundWorkDisabled { get; set; }
    }
}
