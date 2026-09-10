using System;
using System.Collections.Generic;

namespace Writersword.Modules.TextEditor.Models.Toc
{
    /// <summary>
    /// Чем заполняется место между строкой оглавления и номером страницы.
    /// </summary>
    public enum TocLeader
    {
        /// <summary>Ничем — номер просто прижат к правому краю.</summary>
        None = 0,
        /// <summary>Точками — обычный вид книжного оглавления.</summary>
        Dots = 1,
        /// <summary>Короткими чёрточками.</summary>
        Dashes = 2,
        /// <summary>Сплошной линией подчёркивания.</summary>
        Line = 3
    }

    /// <summary>
    /// Настройки одного оглавления. Документ может нести несколько оглавлений —
    /// общее в начале и частные по разделам, — поэтому настройки хранятся списком,
    /// а не одним набором на документ.
    ///
    /// Здесь только то, что человек выбирает. Сами строки оглавления в настройках не
    /// лежат: они собираются заново из заголовков рукописи, и хранить их значило бы
    /// держать две правды об одном и том же.
    /// </summary>
    public sealed class TocSettings
    {
        /// <summary>Опознаватель оглавления. По нему абзацы находят своё оглавление.</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>
        /// Заголовок над списком. Пустая строка — взять название по умолчанию из ресурсов:
        /// так оглавление переезжает между языками, не унося с собой чужое слово.
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Показывать заголовок над списком.</summary>
        public bool ShowTitle { get; set; } = true;

        /// <summary>Самый верхний уровень заголовков, попадающих в оглавление.</summary>
        public int MinLevel { get; set; } = 1;

        /// <summary>Самый нижний уровень заголовков, попадающих в оглавление.</summary>
        public int MaxLevel { get; set; } = 3;

        /// <summary>Показывать номера страниц.</summary>
        public bool ShowPageNumbers { get; set; } = true;

        /// <summary>Заполнитель между строкой и номером страницы.</summary>
        public TocLeader Leader { get; set; } = TocLeader.Dots;

        /// <summary>Сдвигать строки вложенных уровней вправо.</summary>
        public bool IndentByLevel { get; set; } = true;

        /// <summary>Шаг сдвига на один уровень в пунктах.</summary>
        public double LevelIndentPt { get; set; } = 18.0;

        /// <summary>
        /// Брать в оглавление и абзацы, помеченные вручную
        /// (<see cref="Styles.ParagraphProperties.IncludeInToc"/>), а не только те,
        /// что носят стиль заголовка.
        /// </summary>
        public bool IncludeManualEntries { get; set; } = true;

        /// <summary>
        /// Пересобирать оглавление само, когда рукопись перекомпонована.
        /// Выключено — номера обновляются только по кнопке.
        /// </summary>
        public bool AutoUpdate { get; set; } = true;

        /// <summary>Создаёт копию настроек.</summary>
        public TocSettings Clone() => (TocSettings)MemberwiseClone();

        /// <summary>Уровень попадает в это оглавление.</summary>
        public bool AcceptsLevel(int level)
            => level >= MinLevel && level <= MaxLevel;
    }

    /// <summary>
    /// Заголовок рукописи глазами оглавления и навигатора.
    ///
    /// Живёт только в памяти: собирается по документу и умирает вместе с пересборкой.
    /// В файл не попадает — единственная правда о заголовках это сами абзацы.
    /// </summary>
    public sealed class TocHeading
    {
        public TocHeading(Guid blockId, string text, int level, int paragraphIndex)
        {
            BlockId = blockId;
            Text = text;
            Level = level;
            ParagraphIndex = paragraphIndex;
        }

        /// <summary>Опознаватель абзаца-заголовка.</summary>
        public Guid BlockId { get; }

        /// <summary>Текст заголовка одной строкой.</summary>
        public string Text { get; }

        /// <summary>Уровень: 1 — глава, 2 — подглава и так далее до 9.</summary>
        public int Level { get; }

        /// <summary>Место абзаца в потоке документа. По нему делается переход.</summary>
        public int ParagraphIndex { get; set; }

        /// <summary>Номер страницы (1-based). Ноль — раскладка ещё не считала.</summary>
        public int PageNumber { get; set; }

        /// <summary>Вложенные заголовки — для дерева навигатора.</summary>
        public List<TocHeading> Children { get; } = new();
    }
}
