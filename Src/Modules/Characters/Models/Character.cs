using System;
using System.Collections.Generic;
using Writersword.Modules.Characters.Models.Enums;

namespace Writersword.Modules.Characters.Models
{
    /// <summary>
    /// Модель персонажа. Может быть как личностью так и коллективным персонажем (народ/группа).
    /// </summary>
    public class Character
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "Новый персонаж";
        public string ShortDescription { get; set; } = string.Empty;

        // Заметка свободным текстом — основное содержимое карточки. Поле
        // аддитивно: старые сохранения читаются с пустой строкой.
        public string Note { get; set; } = string.Empty;
        public string Color { get; set; } = "#607D8B";
        // Доп. функция: показывать кольцо цвета персонажа вокруг аватара.
        public bool AvatarRing { get; set; } = false;
        public string FallbackIcon { get; set; } = "?";
        public string? AvatarPath { get; set; }
        public CharacterImportanceLevel ImportanceLevel { get; set; } = CharacterImportanceLevel.Secondary;
        public string CustomImportanceLabel { get; set; } = string.Empty;

        // ── Имена ─────────────────────────────────────────────────────────
        // Список имён персонажа: первое — отображаемое (дублируется в Name,
        // чтобы остальной код продолжал работать без изменений), остальные —
        // прежние, будущие и псевдонимы. Поиск идёт по всем записям.
        // Аддитивно: у старых сохранений список собирается из Name и Aliases
        // при загрузке (CharacterNames.Normalize).
        public List<CharacterNameEntry> Names { get; set; } = new();

        // Как к персонажу обращаются те, у кого нет своего правила. Последняя
        // ступень каскада перед отображаемым именем: «все прочие зовут Алиной».
        // Обращение принадлежит отношениям, поэтому личные варианты живут
        // в связях, а здесь — общий случай.
        public string DefaultAddress { get; set; } = string.Empty;

        // Групповые обращения: «все из папки „Друзья“ зовут её Аля». Средняя
        // ступень каскада — уступает личному правилу из связи, побеждает общее.
        // Заполняются только исключения, поэтому список почти всегда короткий.
        public List<CharacterGroupAddress> GroupAddresses { get; set; } = new();

        // ── Псевдонимы (общие — "Ваше величество", "Балбес") ─────────────
        // Оставлены для совместимости старых проектов; источником истины
        // стал список Names.
        public List<string> Aliases { get; set; } = new();

        // ── Теги и статусы ────────────────────────────────────────────────
        public List<string> Tags { get; set; } = new();
        public List<CharacterStatus> ActiveStatuses { get; set; } = new();

        // ── Метки (состояния, финалы, эмблемы) ────────────────────────────
        // Заменяют собой статусы как механику; ActiveStatuses остаётся для
        // совместимости старых проектов. Свойство аддитивно — старые сейвы
        // загружаются с пустым списком.
        public List<CharacterLabel> Labels { get; set; } = new();

        // ── Нарративные точки ─────────────────────────────────────────────
        public string NarrativeStartPoint { get; set; } = string.Empty;
        public string NarrativeEndPoint { get; set; } = string.Empty;

        // ── Параметры ─────────────────────────────────────────────────────
        public List<CharacterParameter> Parameters { get; set; } = new();

        // Подключённые к карточке наборы полей (анкеты). До этого применение
        // анкеты было одноразовым: поля добавлялись, а связь с набором
        // терялась — карточка не знала, из чего она составлена, и отключить
        // набор было нечем.
        //
        // Аддитивно: у старых сохранений список пуст, параметры при этом
        // остаются на месте и работают как прежде.
        public List<string> AttachedAnketaIds { get; set; } = new();

        // ── Контексты (маски) ─────────────────────────────────────────────
        public List<CharacterContext> Contexts { get; set; } = new();

        // ── Заметки ───────────────────────────────────────────────────────
        public List<CharacterNote> Notes { get; set; } = new();

        // ── Персональный таймлайн ─────────────────────────────────────────
        public List<CharacterPersonalEvent> PersonalTimeline { get; set; } = new();

        // ── Привязка к проектному таймлайну ──────────────────────────────
        public List<string> LinkedProjectEventIds { get; set; } = new();

        // ── Галерея ───────────────────────────────────────────────────────
        // Картинки персонажа: эскизы, референсы, арт. Это не пикер аватаров —
        // тот про выбор одного значка. Галерея нужна, чтобы писать сцену,
        // глядя на образ. Любую картинку можно сделать аватаром.
        //
        // Хранятся ссылки того же вида, что AvatarPath, — файлы лежат
        // в проекте и уезжают вместе с ним.
        public List<string> Gallery { get; set; } = new();

        // ── Коллективный персонаж (народ / группа) ────────────────────────
        public bool IsCollective { get; set; } = false;
        public string PopulationNote { get; set; } = string.Empty;

        // Закладка-ленточка на карточке группы (визуальный признак «не одиночный
        // персонаж»). По умолчанию включена; переключается в редакторе цвета.
        public bool GroupBookmark { get; set; } = true;

        // Толщина цветной рамки карточки в списке. Настраивается из окна
        // настроек карточки (шестерёнка под пикером цвета).
        public double FrameThickness { get; set; } = 2;

        // Вид аватара на карточке: false — кружок, true — «полоска» (картинка
        // или заливка цветом на всю верхнюю зону карточки).
        public bool AvatarStrip { get; set; } = false;

        // ── Привязка к локациям (заглушки до модуля Locations) ───────────
        public string? BirthLocationId { get; set; }
        public string? CurrentLocationId { get; set; }

        // ── Предметы ──────────────────────────────────────────────────────
        public List<CharacterItem> Items { get; set; } = new();

        // ── Метаданные ────────────────────────────────────────────────────
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
