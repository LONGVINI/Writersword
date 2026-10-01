using System;
using System.Collections.Generic;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Что известно о листах документа после раскладки: сколько их, где начинается
    /// каждый абзац-метка, на каких листах начинаются главы и каким абзацем открывается
    /// каждый лист. Нумерации нужно первое, второе и третье; выгрузке в Word — ещё и
    /// последнее: разрыв раздела можно поставить только перед абзацем.
    ///
    /// Собирает его тот, кто раскладывал: полотно, печать или PDF.
    /// </summary>
    public sealed class PageFacts
    {
        /// <summary>Сколько листов.</summary>
        public int PageCount { get; init; }

        /// <summary>Абзац → лист, на котором он начинается.</summary>
        public Dictionary<Guid, int> ParagraphStartPages { get; init; } = new();

        /// <summary>Листы, на которых начинается глава.</summary>
        public HashSet<int> ChapterStartPages { get; init; } = new();

        /// <summary>
        /// Абзац, открывающий лист своей первой строкой. Null — лист начинается
        /// продолжением абзаца или таблицей: разрыв раздела перед ним не поставить.
        /// </summary>
        public Guid?[] PageOpeningParagraphs { get; init; } = Array.Empty<Guid?>();

        /// <summary>Пустые сведения — листов нет.</summary>
        public static PageFacts Empty { get; } = new();
    }
}
