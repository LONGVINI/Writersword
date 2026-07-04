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
        public string Color { get; set; } = "#607D8B";
        // Доп. функция: показывать кольцо цвета персонажа вокруг аватара.
        public bool AvatarRing { get; set; } = false;
        public string FallbackIcon { get; set; } = "?";
        public string? AvatarPath { get; set; }
        public CharacterImportanceLevel ImportanceLevel { get; set; } = CharacterImportanceLevel.Secondary;
        public string CustomImportanceLabel { get; set; } = string.Empty;

        // ── Псевдонимы (общие — "Ваше величество", "Балбес") ─────────────
        public List<string> Aliases { get; set; } = new();

        // ── Теги и статусы ────────────────────────────────────────────────
        public List<string> Tags { get; set; } = new();
        public List<CharacterStatus> ActiveStatuses { get; set; } = new();

        // ── Нарративные точки ─────────────────────────────────────────────
        public string NarrativeStartPoint { get; set; } = string.Empty;
        public string NarrativeEndPoint { get; set; } = string.Empty;

        // ── Параметры ─────────────────────────────────────────────────────
        public List<CharacterParameter> Parameters { get; set; } = new();

        // ── Контексты (маски) ─────────────────────────────────────────────
        public List<CharacterContext> Contexts { get; set; } = new();

        // ── Заметки ───────────────────────────────────────────────────────
        public List<CharacterNote> Notes { get; set; } = new();

        // ── Персональный таймлайн ─────────────────────────────────────────
        public List<CharacterPersonalEvent> PersonalTimeline { get; set; } = new();

        // ── Привязка к проектному таймлайну ──────────────────────────────
        public List<string> LinkedProjectEventIds { get; set; } = new();

        // ── Коллективный персонаж (народ / группа) ────────────────────────
        public bool IsCollective { get; set; } = false;
        public string PopulationNote { get; set; } = string.Empty;

        // Закладка-ленточка на карточке группы (визуальный признак «не одиночный
        // персонаж»). По умолчанию включена; переключается в редакторе цвета.
        public bool GroupBookmark { get; set; } = true;

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
