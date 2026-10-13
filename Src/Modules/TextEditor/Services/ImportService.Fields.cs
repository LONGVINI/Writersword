using System;
using System.Collections.Generic;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Поля Word в тексте.
    ///
    /// Сложное поле у Word — цепочка ранов: w:fldChar begin, код в w:instrText,
    /// w:fldChar separate, раны со значением, w:fldChar end. Поля вкладываются друг в
    /// друга. Значение обычного поля остаётся текстом, как и раньше. Живое поле (дата,
    /// время, номер страницы, число страниц) становится объектом строки
    /// <see cref="FieldBlock"/>: его значение показывает вёрстка — дата на сегодня,
    /// номер того листа, где поле встало, как у Word.
    ///
    /// Что идёт в текст, решает стопка открытых полей: знак виден, только когда все
    /// открытые поля уже в своём значении. Значение вложенного поля внутри кода
    /// внешнего — часть кода внешнего, а не текст документа.
    /// </summary>
    public sealed partial class ImportService
    {
        /// <summary>Открытое сложное поле.</summary>
        private sealed class OpenField
        {
            /// <summary>Код поля.</summary>
            public StringBuilder Code { get; } = new();

            /// <summary>Пройден w:fldChar separate: дальше идёт значение.</summary>
            public bool InResult { get; set; }

            /// <summary>Живое поле: значение не идёт в текст, оно копится здесь.</summary>
            public bool Live { get; set; }

            /// <summary>Значение живого поля, сохранённое Word.</summary>
            public StringBuilder Result { get; } = new();

            /// <summary>Оформление значения живого поля: первого его рана.</summary>
            public RunProperties? ResultProperties { get; set; }
        }

        // Стопка открытых сложных полей разбираемого текста.
        private readonly List<OpenField> _openFields = new();

        /// <summary>Знаки сейчас не идут в текст: идёт код поля или значение живого.</summary>
        private bool FieldTextSuppressed
        {
            get
            {
                foreach (var open in _openFields)
                    if (!open.InResult || open.Live) return true;
                return false;
            }
        }

        /// <summary>
        /// Граница сложного поля или его код в ране. True — элемент разобран здесь.
        /// </summary>
        private bool TryHandleFieldElement(
            OpenXmlElement child, TextChunk chunk, SectionModel section, RunProperties runProps)
        {
            switch (child)
            {
                case W.FieldChar fieldChar when fieldChar.FieldCharType?.Value == W.FieldCharValues.Begin:
                    _openFields.Add(new OpenField());
                    return true;

                case W.FieldChar fieldChar when fieldChar.FieldCharType?.Value == W.FieldCharValues.Separate:
                {
                    if (_openFields.Count == 0) return true;

                    var field = _openFields[_openFields.Count - 1];
                    field.InResult = true;

                    // Живым поле становится, только когда его значение пошло бы в текст:
                    // поле в коде другого поля — часть того кода.
                    field.Live = FieldCodes.KindOf(field.Code.ToString()) != LiveFieldKind.None
                        && OuterFieldsInResult(_openFields.Count - 1);
                    return true;
                }

                case W.FieldChar fieldChar when fieldChar.FieldCharType?.Value == W.FieldCharValues.End:
                {
                    if (_openFields.Count == 0) return true;

                    var field = _openFields[_openFields.Count - 1];
                    _openFields.RemoveAt(_openFields.Count - 1);

                    // Поле без значения (Word его ещё не считал) тоже живое, если оно им
                    // должно быть: значение даст вёрстка.
                    bool live = field.Live
                        || (!field.InResult
                            && FieldCodes.KindOf(field.Code.ToString()) != LiveFieldKind.None
                            && OuterFieldsInResult(_openFields.Count));

                    if (live)
                    {
                        AppendLiveField(field.Code.ToString().Trim(), field.Result.ToString(),
                            field.ResultProperties ?? runProps, chunk, section);
                    }
                    else if (_openFields.Count > 0 && !_openFields[_openFields.Count - 1].InResult)
                    {
                        // Значение вложенного поля — продолжение кода внешнего.
                        _openFields[_openFields.Count - 1].Code.Append(field.Result);
                    }
                    return true;
                }

                case W.FieldCode code:
                    if (_openFields.Count > 0 && !_openFields[_openFields.Count - 1].InResult)
                        _openFields[_openFields.Count - 1].Code.Append(code.Text);
                    return true;
            }

            return false;
        }

        /// <summary>Все поля стопки ниже заданного уже в своём значении.</summary>
        private bool OuterFieldsInResult(int count)
        {
            for (int i = 0; i < count && i < _openFields.Count; i++)
                if (!_openFields[i].InResult || _openFields[i].Live) return false;
            return true;
        }

        /// <summary>
        /// Знак значения, который в текст не идёт: копится в значение живого поля или
        /// в код поля. True — знак забран полем.
        /// </summary>
        private bool TryCaptureFieldText(string text, RunProperties runProps)
        {
            if (_openFields.Count == 0 || !FieldTextSuppressed) return false;

            var field = _openFields[_openFields.Count - 1];
            if (field.Live)
            {
                field.Result.Append(text);
                field.ResultProperties ??= runProps;
            }
            else if (!field.InResult)
            {
                field.Code.Append(text);
            }
            else
            {
                // Значение обычного поля, вложенного в код или в живое поле: оно
                // уходит в ближайшее поле, которое знаки копит.
                for (int i = _openFields.Count - 2; i >= 0; i--)
                {
                    var outer = _openFields[i];
                    if (outer.Live) { outer.Result.Append(text); break; }
                    if (!outer.InResult) { outer.Code.Append(text); break; }
                }
            }

            return true;
        }

        /// <summary>Простое поле (w:fldSimple): живое — объектом строки, иначе — значением.</summary>
        private bool TryAppendLiveSimpleField(
            W.SimpleField simple, TextChunk chunk, SectionModel section,
            DocxFormatResolver resolver, EffectiveParagraph effPara)
        {
            string instruction = simple.Instruction?.Value ?? string.Empty;
            if (FieldCodes.KindOf(instruction) == LiveFieldKind.None) return false;
            if (_openFields.Count > 0 && FieldTextSuppressed) return false;

            RunProperties? resultProps = null;
            var result = new StringBuilder();
            foreach (var run in simple.Descendants<W.Run>())
            {
                resultProps ??= resolver.ResolveEffectiveRun(run, effPara).ToRunProperties();
                foreach (var text in run.Elements<W.Text>()) result.Append(text.Text);
            }

            AppendLiveField(instruction.Trim(), result.ToString(),
                resultProps ?? effPara.ToRunProperties(), chunk, section);
            return true;
        }

        private static void AppendLiveField(
            string instruction, string result, RunProperties props, TextChunk chunk, SectionModel section)
        {
            var field = new FieldBlock { Instruction = instruction, Result = result };
            section.InlineObjects.Add(field);

            chunk.Runs.Add(new RunModel
            {
                Text = RunModel.ObjectPlaceholder.ToString(),
                InlineImageId = field.Id,
                Properties = props.Clone()
            });
        }
    }
}
