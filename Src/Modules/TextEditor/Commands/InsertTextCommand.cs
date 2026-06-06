using System;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;

namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Команда вставки текста в параграф.
    /// Покрывает: обычный ввод символов, вставку из буфера (plain text).
    /// Для вставки форматированного текста (несколько runs) используй CompositeCommand
    /// из нескольких InsertTextCommand с явными Properties.
    /// Поддерживает слияние последовательных вставок в одну запись undo:
    /// отдельные символы объединяются до пробела — Ctrl+Z откатывает по слову.
    /// </summary>
    public sealed class InsertTextCommand : ITextCommand
    {
        /// <summary>Id параграфа-получателя.</summary>
        public Guid ParaId { get; }

        /// <summary>Позиция вставки (плоский символьный индекс).</summary>
        public int CharPos { get; private set; }

        /// <summary>Вставляемый текст (может расти при слиянии с последующими командами).</summary>
        public string Text { get; private set; }

        /// <summary>
        /// Явное форматирование вставляемого текста.
        /// Null — наследовать форматирование run-а в точке вставки.
        /// </summary>
        public RunProperties? Properties { get; }

        public string Description { get; }

        public InsertTextCommand(Guid paraId, int charPos, string text,
            RunProperties? properties = null, string description = "Type text")
        {
            ParaId = paraId;
            CharPos = charPos;
            Text = text;
            Properties = properties;
            Description = description;
        }

        /// <summary>
        /// Вызывается после Apply (Redo) и Revert (Undo) для восстановления позиции каретки.
        /// Параметры: Id параграфа и символьная позиция каретки.
        /// Устанавливается DocumentCanvas после Push чтобы не тянуть зависимость на UI в команду.
        /// </summary>
        public Action<Guid, int>? RestoreCaretCallback { get; set; }

        public void Apply(DocumentModel doc)
        {
            var para = DocumentModelHelper.FindParagraph(doc, ParaId);
            if (para is null) return;
            DocumentModelHelper.InsertText(para, CharPos, Text, Properties);
            // После Redo каретка встаёт в конец вставленного текста.
            RestoreCaretCallback?.Invoke(ParaId, CharPos + Text.Length);
        }

        public void Revert(DocumentModel doc)
        {
            var para = DocumentModelHelper.FindParagraph(doc, ParaId);
            if (para is null) return;
            DocumentModelHelper.DeleteRange(para, CharPos, Text.Length);
            // После Undo каретка возвращается к позиции до вставки.
            RestoreCaretCallback?.Invoke(ParaId, CharPos);
        }

        /// <summary>
        /// Слияние с следующей командой вставки.
        /// Объединяем если: тот же параграф, то же форматирование,
        /// следующая вставка начинается ровно там где заканчивается текущая,
        /// и текущий текст не заканчивается пробелом (не начинаем новое слово).
        /// </summary>
        public bool TryMerge(ITextCommand next)
        {
            if (next is not InsertTextCommand other) return false;
            if (other.ParaId != ParaId) return false;
            if (other.CharPos != CharPos + Text.Length) return false;
            if (!RunPropertiesEqual(Properties, other.Properties)) return false;

            // Не сливаем если текущий текст заканчивается пробелом или переносом —
            // пользователь ожидает что слова откатываются по одному.
            if (Text.EndsWith(' ') || Text.EndsWith('\n') || Text.EndsWith('\t'))
                return false;

            Text += other.Text;
            return true;
        }

        private static bool RunPropertiesEqual(RunProperties? a, RunProperties? b)
        {
            if (a is null && b is null) return true;
            if (a is null || b is null) return false;
            return a.IsBold == b.IsBold
                && a.IsItalic == b.IsItalic
                && a.FontFamily == b.FontFamily
                && a.FontSize == b.FontSize;
        }
    }
}
