using System;
using System.Collections.Generic;

namespace Writersword.Modules.Notes.Models
{
    /// <summary>
    /// Страница заметок: заголовок, даты и строки.
    /// </summary>
    public sealed class NotePage
    {
        /// <summary>Устойчивый идентификатор страницы. По нему сессия находит открытую страницу.</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Заголовок страницы.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Момент создания в UTC.</summary>
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Момент последнего изменения в UTC.</summary>
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Строки страницы по порядку.</summary>
        public List<NoteBlock> Blocks { get; set; } = new();

        /// <summary>Копия страницы вместе с идентификаторами строк — для снимков отмены.</summary>
        public NotePage Clone()
        {
            var blocks = new List<NoteBlock>(Blocks.Count);
            foreach (var block in Blocks)
                blocks.Add(block.Clone());

            return new NotePage
            {
                Id = Id,
                Title = Title,
                CreatedAtUtc = CreatedAtUtc,
                UpdatedAtUtc = UpdatedAtUtc,
                Blocks = blocks
            };
        }
    }

    /// <summary>
    /// Данные модуля, попадающие в файл проекта.
    /// </summary>
    public sealed class NotesData
    {
        /// <summary>
        /// Версия формата. Читается только своя версия: чужую нельзя разобрать
        /// как свою и затем перезаписать — это стёрло бы данные, записанные
        /// более новой сборкой.
        /// </summary>
        public int FormatVersion { get; set; } = CurrentFormatVersion;

        /// <summary>Страницы по порядку.</summary>
        public List<NotePage> Pages { get; set; } = new();

        /// <summary>Версия формата, которую понимает эта сборка.</summary>
        public const int CurrentFormatVersion = 1;
    }

    /// <summary>
    /// Рабочие данные сессии: что было открыто и как была разложена панель.
    /// В файл проекта не попадают, живут в кеше рабочего стола.
    /// </summary>
    public sealed class NotesSessionData
    {
        /// <summary>Открытая страница.</summary>
        public Guid? SelectedPageId { get; set; }

        /// <summary>Строка, на которой стояла каретка.</summary>
        public Guid? SelectedBlockId { get; set; }

        /// <summary>Панель страниц была развёрнута.</summary>
        public bool IsPagePanelOpen { get; set; } = true;

        /// <summary>
        /// Ширина панели страниц в точках. Ноль означает «ширина не сохранялась»
        /// — берётся значение по умолчанию, а не нулевая полоска.
        /// </summary>
        public double PagePanelWidth { get; set; }
    }
}
