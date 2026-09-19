using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Writersword.Core.Interfaces.Modules;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.ViewModels;

namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Место правки: выделение и каретка на момент снимка.
    ///
    /// Одной каретки здесь мало. Ctrl+A оставляет её в конце книги, и отмена,
    /// вернув только её, уносила вид на последнюю страницу — человек правил
    /// наверху, а оказывался внизу. Выделение говорит, где правка была на самом
    /// деле: от его начала до его конца.
    /// </summary>
    public readonly struct EditPlace
    {
        public EditPlace(
            int caretPara, int caretChar,
            int selStartPara, int selStartChar,
            int selEndPara, int selEndChar)
        {
            CaretPara = caretPara;
            CaretChar = caretChar;
            SelStartPara = selStartPara;
            SelStartChar = selStartChar;
            SelEndPara = selEndPara;
            SelEndChar = selEndChar;
        }

        /// <summary>Слайс раскладки, на котором стояла каретка.</summary>
        public int CaretPara { get; }

        /// <summary>Место каретки в тексте слайса.</summary>
        public int CaretChar { get; }

        /// <summary>Слайс, с которого выделение начали тянуть.</summary>
        public int SelStartPara { get; }

        /// <summary>Место в тексте, с которого выделение начали тянуть.</summary>
        public int SelStartChar { get; }

        /// <summary>Слайс, на котором выделение отпустили.</summary>
        public int SelEndPara { get; }

        /// <summary>Место в тексте, на котором выделение отпустили.</summary>
        public int SelEndChar { get; }

        /// <summary>Было ли выделение, или каретка просто стояла в тексте.</summary>
        public bool HasSelection
            => SelStartPara != SelEndPara || SelStartChar != SelEndChar;
    }

    /// <summary>
    /// Снапшот полного состояния документа — до и после операции.
    /// Сериализует DocumentModel в JSON при создании (before) и при Commit (after).
    /// Покрывает текст, таблицы, форматирование и структурные изменения.
    /// </summary>
    public sealed class DocumentSnapshotCommand : IUndoableCommand
    {
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly DocumentViewModel _docVm;
        private readonly string _before;
        private string? _after;

        // Места правки до и после операции: выделение плюс каретка.
        private readonly EditPlace _placeBefore;
        private EditPlace _placeAfter;

        /// <summary>
        /// Куда вернуть выделение и каретку после отмены или повтора. Полотно ставит
        /// обработчик при заведении снимка: само оно решает, как место разложить по
        /// своим полям, а команде про слайсы раскладки знать незачем.
        /// </summary>
        public Action<EditPlace>? RestorePlaceCallback { get; set; }

        public string Description { get; }

        /// <summary>
        /// Имена файлов картинок, на которые ссылается снимок (до и после). Используется
        /// очисткой неиспользуемых картинок, чтобы не удалять файлы, нужные для Undo/Redo.
        /// </summary>
        public IEnumerable<string> ReferencedImageFiles
        {
            get
            {
                foreach (var n in ExtractImageNames(_before)) yield return n;
                if (_after is not null)
                    foreach (var n in ExtractImageNames(_after)) yield return n;
            }
        }

        private static IEnumerable<string> ExtractImageNames(string json)
        {
            foreach (System.Text.RegularExpressions.Match m in
                System.Text.RegularExpressions.Regex.Matches(json, "\"ImageFileName\"\\s*:\\s*\"([^\"]+)\""))
            {
                yield return m.Groups[1].Value;
            }
        }

        public DocumentSnapshotCommand(DocumentViewModel docVm, string description, EditPlace place)
        {
            _docVm = docVm;
            Description = description;
            _before = Serialize(docVm.Document);
            _placeBefore = place;
        }

        public void Commit(EditPlace place)
        {
            _after = Serialize(_docVm.Document);
            _placeAfter = place;
        }

        public void Execute()
        {
            if (_after is not null)
            {
                Restore(_after);
                RestorePlaceCallback?.Invoke(_placeAfter);
            }
        }

        public void Undo()
        {
            Restore(_before);
            RestorePlaceCallback?.Invoke(_placeBefore);
        }

        private void Restore(string json)
        {
            var restored = JsonSerializer.Deserialize<DocumentModel>(json, _jsonOptions);
            if (restored is null) return;

            var doc = _docVm.Document;

            doc.Sections.Clear();
            foreach (var section in restored.Sections)
                doc.Sections.Add(section);

            doc.Styles.Clear();
            foreach (var style in restored.Styles)
                doc.Styles.Add(style);

            // Лист восстанавливается целиком, а не одними полями: операции вроде
            // импорта документа меняют и размер бумаги, и ориентацию, и колонки,
            // и без их отката Ctrl+Z возвращал бы текст на чужую страницу.
            doc.PageSettings.PaperSize = restored.PageSettings.PaperSize;
            doc.PageSettings.WidthMm = restored.PageSettings.WidthMm;
            doc.PageSettings.HeightMm = restored.PageSettings.HeightMm;
            doc.PageSettings.Orientation = restored.PageSettings.Orientation;
            doc.PageSettings.MarginTopMm = restored.PageSettings.MarginTopMm;
            doc.PageSettings.MarginBottomMm = restored.PageSettings.MarginBottomMm;
            doc.PageSettings.MarginLeftMm = restored.PageSettings.MarginLeftMm;
            doc.PageSettings.MarginRightMm = restored.PageSettings.MarginRightMm;
            doc.PageSettings.MarginGutterMm = restored.PageSettings.MarginGutterMm;
            doc.PageSettings.HeaderDistanceMm = restored.PageSettings.HeaderDistanceMm;
            doc.PageSettings.FooterDistanceMm = restored.PageSettings.FooterDistanceMm;

            doc.ColumnSettings.ColumnCount = restored.ColumnSettings.ColumnCount;
            doc.ColumnSettings.GapMm = restored.ColumnSettings.GapMm;
            doc.ColumnSettings.ShowSeparator = restored.ColumnSettings.ShowSeparator;

            // Свойства рукописи, живущие вне разделов. Снимок их сохранял, а
            // восстановление обходило стороной, и отмена возвращала текст, оставляя
            // на месте шаг табуляции, настройки оглавления, замены и примечания —
            // то есть ровно то, ради чего шаг отмены и открывали.
            doc.DefaultTabStopPt = restored.DefaultTabStopPt;
            doc.TableOfContents = restored.TableOfContents;
            doc.DocumentAutoReplaceRules = restored.DocumentAutoReplaceRules;

            doc.Annotations.Clear();
            foreach (var annotation in restored.Annotations)
                doc.Annotations.Add(annotation);

            _docVm.RebuildParagraphViewModelsPublic();

            // Геометрию листа канвас пересобирает только по уведомлению о PageSettings.
            _docVm.RaisePageSettingsChanged();
            _docVm.FireParagraphFormatChanged();
        }

        private static string Serialize(DocumentModel doc)
            => JsonSerializer.Serialize(doc, _jsonOptions);
    }
}
