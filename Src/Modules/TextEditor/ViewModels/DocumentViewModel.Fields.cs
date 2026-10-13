using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Services;
using Writersword.Modules.TextEditor.Views.Dialogs;

namespace Writersword.Modules.TextEditor.ViewModels
{
    /// <summary>
    /// Живые поля: вставка даты и времени. Поле стоит в абзаце одним символом, как
    /// картинка в строке, а его текст показывает вёрстка (DocumentCanvas.Fields).
    /// </summary>
    public sealed partial class DocumentViewModel
    {
        /// <summary>Виды даты и времени для вставки — как в окне «Дата и время» у Word.</summary>
        private static readonly string[] DateTimePictures =
        {
            "dd.MM.yyyy",
            "d MMMM yyyy",
            "d MMMM yyyy 'г.'",
            "dddd, d MMMM yyyy",
            "«d» MMMM yyyy 'г.'",
            "dd.MM.yy",
            "yyyy-MM-dd",
            "d MMMM",
            "MMMM yyyy",
            "dd.MM.yyyy H:mm",
            "H:mm",
            "H:mm:ss"
        };

        /// <summary>Вставляет у каретки дату или время живым полем — вид выбирается в окне.</summary>
        public void InsertDateTime() => _ = InsertDateTimeAsync();

        private async Task InsertDateTimeAsync()
        {
            try
            {
                if (IsReadOnly || _document.Sections.Count == 0) return;

                // Место запоминается до окна: окно забирает фокус.
                var target = GetCaretTargetDelegate?.Invoke();
                ParagraphBlock? para = target?.Para;
                int at = target?.CharIndex ?? 0;

                if (para is null && _activeParagraph is not null)
                {
                    para = _activeParagraph.Model;
                    at = _activeParagraph.SelectionStart;
                }

                if (para is null) return;

                var now = DateTime.Now;
                var choices = DateTimePictures
                    .Select(picture =>
                    {
                        bool timeOnly = !picture.Contains('d') && !picture.Contains('M') && !picture.Contains('y');
                        string instruction = (timeOnly ? "TIME" : "DATE") + " \\@ \"" + picture + "\"";
                        return (instruction, FieldCodes.FormatDateTime(instruction, now, null));
                    })
                    .ToList();

                string? instruction = await DateTimeFieldDialog.ShowAsync(choices);
                if (instruction is null || IsReadOnly) return;

                if (!EnumerateAllParagraphs().Any(p => ReferenceEquals(p, para))) return;

                InsertFieldAt(para, at, instruction, FieldCodes.FormatDateTime(instruction, now, null), "Вставка даты");
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[FIELDS] Дату вставить не удалось");
            }
        }

        /// <summary>
        /// Вставляет у каретки поле без окна — по горячей клавише, как у Word:
        /// Alt+Shift+D — дата, Alt+Shift+T — время, Alt+Shift+P — номер страницы.
        /// </summary>
        public void InsertQuickField(LiveFieldKind kind)
        {
            try
            {
                if (IsReadOnly || _document.Sections.Count == 0) return;

                var target = GetCaretTargetDelegate?.Invoke();
                ParagraphBlock? para = target?.Para;
                int at = target?.CharIndex ?? 0;

                if (para is null && _activeParagraph is not null)
                {
                    para = _activeParagraph.Model;
                    at = _activeParagraph.SelectionStart;
                }

                if (para is null) return;

                var now = DateTime.Now;
                switch (kind)
                {
                    case LiveFieldKind.Date:
                    {
                        string instruction = "DATE \\@ \"" + DateTimePictures[0] + "\"";
                        InsertFieldAt(para, at, instruction, FieldCodes.FormatDateTime(instruction, now, null), "Вставка даты");
                        break;
                    }

                    case LiveFieldKind.Time:
                    {
                        string instruction = "TIME \\@ \"H:mm\"";
                        InsertFieldAt(para, at, instruction, FieldCodes.FormatDateTime(instruction, now, null), "Вставка времени");
                        break;
                    }

                    case LiveFieldKind.Page:
                        InsertFieldAt(para, at, "PAGE", "1", "Вставка номера страницы");
                        break;
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[FIELDS] Поле вставить не удалось");
            }
        }

        /// <summary>Ставит поле в абзац одним символом в позицию at.</summary>
        private void InsertFieldAt(ParagraphBlock para, int at, string instruction, string result, string undoName)
        {
            at = Math.Max(0, Math.Min(at, para.TotalLength));

            BeginEditDelegate?.Invoke(undoName);

            var field = new FieldBlock
            {
                Instruction = instruction,
                Result = result
            };

            _document.Sections[0].InlineObjects.Add(field);
            para.InsertInlineObject(at, field.Id);
            InlineImageInserted?.Invoke(para, at);

            CommitEditDelegate?.Invoke();
            StructureChanged?.Invoke();
        }

        /// <summary>Живое поле, стоящее в строке текста, по Id.</summary>
        private FieldBlock? FindInlineFieldById(Guid id)
        {
            foreach (var section in _document.Sections)
                foreach (var block in section.InlineObjects)
                    if (block is FieldBlock field && field.Id == id)
                        return field;
            return null;
        }
    }
}
