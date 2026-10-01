using Writersword.Core.Models.Rendering;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Скрытый текст (w:vanish) и каретка.
    ///
    /// Пока непечатаемые знаки выключены, скрытый текст остаётся в абзаце, но места в
    /// строке не занимает: все его позиции каретки стоят в одной точке. Каретка
    /// перешагивает его целиком, как в Word, — одно нажатие стрелки сдвигает её на
    /// один видимый знак. Позиция каретки у спрятанного куска — перед ним.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        /// <summary>
        /// Знак абзаца с этим номером спрятан и места в строке не занимает.
        /// </summary>
        private static bool IsCollapsedHiddenChar(SKTextLayout? layout, int charIndex)
        {
            if (layout is null || charIndex < 0) return false;

            foreach (var line in layout.Lines)
            {
                if (charIndex < line.FirstCharIndex || charIndex > line.LastCharIndex) continue;

                foreach (var seg in line.Segments)
                {
                    int start = seg.GlobalCharOffset;
                    int end = start + System.Math.Max(seg.Text.Length, 1);
                    if (charIndex >= start && charIndex < end)
                        return seg.IsHidden;
                }
            }

            return false;
        }

        /// <summary>
        /// Позиция каретки перед спрятанным куском, в котором или сразу за которым она стоит.
        /// </summary>
        private static int SkipHiddenBackward(SKTextLayout? layout, int caretChar)
        {
            if (layout is null) return caretChar;

            while (caretChar > 0 && IsCollapsedHiddenChar(layout, caretChar - 1))
                caretChar--;

            return caretChar;
        }

        /// <summary>
        /// Позиция каретки за спрятанным куском, который начинается с неё.
        /// </summary>
        private static int SkipHiddenForward(SKTextLayout? layout, int caretChar, int length)
        {
            if (layout is null) return caretChar;

            while (caretChar < length && IsCollapsedHiddenChar(layout, caretChar))
                caretChar++;

            return caretChar;
        }

        /// <summary>
        /// Приводит правило раскладки скрытого текста к текущему состоянию
        /// (<see cref="ShowHiddenTextInLayout"/>): показан он или спрятан.
        ///
        /// Правило живёт в StyleResolver, поэтому он пересоздаётся при каждой смене —
        /// иначе скрытый текст, набранный уже после переключения, раскладывался бы по
        /// прежнему правилу. Недействительны только раскладки абзацев со скрытым текстом:
        /// весь кеш не чистится, иначе большой документ уходил в прогрев целиком, и до
        /// его конца на экране оставалась прежняя раскладка.
        ///
        /// Возвращает true, если раскладку нужно пересобрать.
        /// </summary>
        private bool ApplyHiddenTextRule()
        {
            if (DocVm is null || _styleResolver is null) return false;
            if (_styleResolver.ShowHiddenText == ShowHiddenTextInLayout) return false;

            var previousResolver = _styleResolver;
            _styleResolver = CreateStyleResolver();

            if (InvalidateHiddenTextLayouts()) return true;

            // Скрытого текста нет — готовая раскладка верна и для нового правила.
            // Отпечаток переносится на новый StyleResolver, чтобы проход measure не
            // пересобирал документ зря.
            if (ReferenceEquals(_layoutsFingerprintStyleResolver, previousResolver))
                _layoutsFingerprintStyleResolver = _styleResolver;

            return false;
        }

        /// <summary>
        /// Сбрасывает готовые раскладки абзацев со скрытым текстом — в потоке и в ячейках
        /// таблиц. Остальные абзацы от показа скрытого текста не зависят, их раскладки
        /// остаются в кеше, и пересборка берёт их оттуда без прогрева.
        /// Возвращает false, если скрытого текста в документе нет: тогда показ
        /// непечатаемых знаков раскладку не меняет, и пересборка не нужна.
        /// </summary>
        private bool InvalidateHiddenTextLayouts()
        {
            if (DocVm is null) return false;

            bool found = false;

            foreach (var pvm in DocVm.Paragraphs)
            {
                if (!ParagraphHasHiddenText(pvm.Model)) continue;
                _layoutCache.Remove(pvm);
                found = true;
            }

            bool tableTouched = false;
            foreach (var section in DocVm.Document.Sections)
            {
                foreach (var block in section.Blocks)
                {
                    if (block is not TableBlock table) continue;

                    foreach (var cell in table.Cells)
                    {
                        foreach (var cellParagraph in cell.Paragraphs)
                        {
                            if (!ParagraphHasHiddenText(cellParagraph)) continue;

                            tableTouched = true;
                            if (_cellVmCache.TryGetValue(cellParagraph, out var cellVm))
                                _layoutCache.Remove(cellVm);
                        }
                    }
                }
            }

            // Раскладки таблиц хранятся целиком, по ячейке их не сбросить.
            if (tableTouched) InvalidateCellLayoutCaches();

            return found || tableTouched;
        }

        private static bool ParagraphHasHiddenText(ParagraphBlock paragraph)
        {
            foreach (var chunk in paragraph.Chunks)
                foreach (var run in chunk.Runs)
                    if (run.Properties?.IsHidden == true)
                        return true;

            return false;
        }

        /// <summary>
        /// После того как скрытый текст спрятали, каретка без выделения встаёт перед
        /// спрятанным куском, а не остаётся внутри него.
        /// </summary>
        private void NormalizeCaretOutOfHiddenText()
        {
            if (HasSel()) return;

            int normalized = SkipHiddenBackward(GetLayoutAt(_caretPara), _caretChar);
            if (normalized == _caretChar) return;

            _caretChar = normalized;
            SyncSel();
        }
    }
}
