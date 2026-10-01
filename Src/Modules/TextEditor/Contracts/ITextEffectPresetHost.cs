using System;
using System.Collections.Generic;
using Writersword.Modules.TextEditor.Models.Inline;

namespace Writersword.Modules.TextEditor.Contracts
{
    /// <summary>
    /// Хранилище «Моих эффектов» — своих наборов эффектов букв — для ленты.
    ///
    /// Наборы живут в общих настройках модуля (их пишет редактор, он знает, куда), а
    /// ставятся и снимаются с текста документа. Лента видит их только через этот
    /// договор: ни настроек, ни документа она не касается.
    /// </summary>
    public interface ITextEffectPresetHost
    {
        /// <summary>Наборы в порядке, в каком их завели.</summary>
        IReadOnlyList<TextEffectPreset> TextEffectPresets { get; }

        /// <summary>Наборы поменялись: завели, переименовали, заменили, удалили — здесь или в другом редакторе.</summary>
        event Action? TextEffectPresetsChanged;

        /// <summary>
        /// Набор из эффектов текста под кареткой. Null — документа нет.
        /// </summary>
        TextEffectPreset? CaptureCaretTextEffectPreset(string name);

        /// <summary>Ставит набор выделению одной правкой.</summary>
        void ApplyTextEffectPreset(TextEffectPreset preset);

        /// <summary>
        /// Заводит набор. Пустой набор не заводится; имя, уже занятое другим набором,
        /// получает номер.
        /// </summary>
        void AddTextEffectPreset(TextEffectPreset preset);

        /// <summary>Заменяет набор с тем же Id (переименование, новый вид).</summary>
        void ReplaceTextEffectPreset(TextEffectPreset preset);

        /// <summary>Удаляет набор.</summary>
        void RemoveTextEffectPreset(string id);

        /// <summary>Свободное имя для нового набора: «Мой эффект 1», «Мой эффект 2»…</summary>
        string SuggestTextEffectPresetName();
    }
}
