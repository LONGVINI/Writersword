using System.Collections.Generic;
using Writersword.Core.Models.Preview;

namespace Writersword.Modules.Characters.Models
{
    /// <summary>
    /// Данные модуля персонажей — сохраняются в ZIP проекта.
    /// </summary>
    public class CharactersModuleData
    {
        /// <summary>
        /// Версия устройства данных. 2 — анкеты стали разделами с уровнями
        /// детализации, жанровые наборы переехали в шаблоны. Сохранения
        /// первой версии переводятся при загрузке (CharactersDataMigration).
        /// </summary>
        public const int CurrentSchemaVersion = 2;

        public int SchemaVersion { get; set; } = 1;

        /// <summary>
        /// Первый запуск модуля в этом проекте.
        /// При true — показывается онбординг при открытии.
        /// Хранится в данных проекта (не глобально).
        /// </summary>
        public bool IsFirstLaunch { get; set; } = true;

        public List<Character> Characters { get; set; } = new();

        /// <summary>
        /// Реестр меток проекта: по одной записи на метку, независимо от
        /// того, скольким персонажам она поставлена и поставлена ли вообще.
        ///
        /// До реестра общей метки не существовало — вид «Ранен» брался у
        /// первого попавшегося персонажа, у которого такая метка нашлась.
        /// Стоило поправить её у него — и новые персонажи получали новый
        /// вид, а стоило его удалить — вид терялся вместе с ним.
        ///
        /// У персонажа при этом остаётся своя копия метки с тем же
        /// идентификатором: цвета, значок и подпись он волен менять только
        /// себе, а реестр хранит образец, от которого пляшут остальные.
        /// </summary>
        public List<CharacterLabel> Labels { get; set; } = new();
        public List<CharacterRelationship> Relationships { get; set; } = new();
        public List<CharacterAnketa> CustomAnketas { get; set; } = new();

        /// <summary>
        /// Шаблоны проекта — списки анкет под тип персонажа. Живут рядом со
        /// своими анкетами: шаблон, ссылающийся на анкету из чужого проекта,
        /// был бы списком пустых мест.
        /// </summary>
        public List<CharacterTemplate> Templates { get; set; } = new();

        /// <summary>Папки своих шаблонов по порядку.</summary>
        public List<CharacterTemplateFolder> TemplateFolders { get; set; } = new();

        /// <summary>
        /// Порядок разделов вкладки шаблонов сверху вниз: ключи папок,
        /// «Мои шаблоны» (TemplateSections.Ungrouped) и «Встроенные»
        /// (TemplateSections.BuiltIn). Пусто — порядок по умолчанию.
        /// </summary>
        public List<string> TemplateSectionOrder { get; set; } = new();

        /// <summary>
        /// Свои заготовки полей анкет — поля со всеми настройками, сохранённые
        /// из анкет проекта. Встроенные заготовки собираются из кода.
        /// </summary>
        public List<CharacterFieldPreset> FieldPresets { get; set; } = new();

        /// <summary>
        /// Скрытые шаблоны — свои и встроенные. Во вкладке шаблонов они стоят
        /// на своих местах, только бледнее, а в поиске и в списках шаблонов
        /// (карточка персонажа) их нет.
        /// </summary>
        public List<string> HiddenTemplateIds { get; set; } = new();

        /// <summary>
        /// Свёрнутые разделы вкладки шаблонов: папки, «Мои шаблоны»
        /// (TemplateSections.Ungrouped) и «Встроенные» (TemplateSections.BuiltIn).
        /// </summary>
        public List<string> CollapsedTemplateFolderIds { get; set; } = new();

        /// <summary>
        /// Недавно подключённые анкеты, свежие впереди. Персонажей одного
        /// типа заводят подряд, и подключённое последним понадобится снова
        /// скорее всего. Список про привычку, а не про хранение: его чистят,
        /// и на сами анкеты это не влияет.
        /// </summary>
        public List<string> RecentAnketaIds { get; set; } = new();

        /// <summary>
        /// Библиотека своих значков проекта — картинки, которые автор принёс
        /// для анкет, шаблонов и меток. Одна на проект: значок, добавленный в
        /// одном месте, доступен во всех остальных.
        /// </summary>
        public List<CharacterIconEntry> CustomIcons { get; set; } = new();
        public List<CharacterFolder> Folders { get; set; } = new();

        /// <summary>
        /// Плоские снимки персонажей для чужих модулей. Пересобираются при
        /// каждом сохранении из Characters, при загрузке не применяются —
        /// это производные данные, источник истины остаётся один.
        ///
        /// Лежат в файле затем, чтобы карточку персонажа было чем показать,
        /// когда модуля Characters в сборке нет: ссылка из текста остаётся
        /// живой, меняться данные при этом не могут.
        /// </summary>
        public List<EntityPreview> Preview { get; set; } = new();
    }

    /// <summary>
    /// Сессионное состояние — восстанавливается при повторном открытии вкладки.
    /// </summary>
    public class CharactersModuleSession
    {
        public string? LastOpenedCharacterId { get; set; }
        public string LastViewMode { get; set; } = "List";
        public int MainTabIndex { get; set; } = 0;
        public List<string> ActiveTagFilters { get; set; } = new();
        public string LastSearchQuery { get; set; } = string.Empty;
        public double GraphOffsetX { get; set; } = 0;
        public double GraphOffsetY { get; set; } = 0;
        public double GraphScale { get; set; } = 1.0;

        /// <summary>Ширина бокового списка во вкладке Character Editor (px).</summary>
        public double EditorSidebarWidth { get; set; } = 240;

        /// <summary>Режим бокового списка Редактора: 0 — полный, 1 — только аватарки, 2 — скрыт.</summary>
        public int EditorSidebarMode { get; set; } = 0;
    }
}