namespace Writersword.Core.Enums
{
    /// <summary>
    /// Типы модулей - виджеты внутри режимов работы
    /// Например: в режиме "Редактор" есть модули Synonyms, Timer, Notes
    /// </summary>
    public enum ModuleType
    {
        // ===== ОСНОВНЫЕ =====
        /// <summary>Текстовый редактор</summary>
        TextEditor = 0,

        // ===== ПИСАТЕЛЬСКИЕ =====
        /// <summary>Генератор синонимов</summary>
        Synonyms = 100,

        /// <summary>Статистика работы</summary>
        Statistics = 101,

        /// <summary>Таймер работы</summary>
        Timer = 102,

        /// <summary>Заметки и напоминания</summary>
        Notes = 103,

        /// <summary>Анализатор стиля</summary>
        StyleAnalyzer = 104,

        /// <summary>Музыкальный плеер</summary>
        MusicPlayer = 105,

        // ===== ПЛАНИРОВАНИЕ =====
        /// <summary>Таймлайн событий</summary>
        Timeline = 200,

        /// <summary>Персонажи</summary>
        Characters = 201,

        /// <summary>Сюжетные точки</summary>
        PlotPoints = 202,

        /// <summary>Взаимосвязи персонажей</summary>
        Relationships = 203,

        /// <summary>Локации</summary>
        Locations = 204,

        // ===== ГЕЙМДИЗАЙН =====
        /// <summary>Карты местности</summary>
        Maps = 300,

        /// <summary>Игровая экономика</summary>
        GameEconomy = 301,

        /// <summary>Ресурсы и баланс</summary>
        Resources = 302,

        /// <summary>Боевая система</summary>
        Combat = 303,

        // ===== СЦЕНАРИИ =====
        /// <summary>Диалоги</summary>
        Dialogues = 400,

        /// <summary>Сцены</summary>
        Scenes = 401,

        /// <summary>Ветвление сюжета</summary>
        Branching = 402,

        // ===== ПОЭЗИЯ =====
        /// <summary>Редактор поэзии</summary>
        Poetry = 500,

        /// <summary>Помощник рифм</summary>
        RhymeHelper = 501,

        // ===== КАСТОМНЫЕ =====
        /// <summary>Пользовательский плагин</summary>
        CustomPlugin = 1000
    }
}