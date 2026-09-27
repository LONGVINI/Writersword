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

        private static readonly Serilog.ILogger _log = Serilog.Log.ForContext<DocumentSnapshotCommand>();

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
            long startTs = System.Diagnostics.Stopwatch.GetTimestamp();
            var restored = JsonSerializer.Deserialize<DocumentModel>(json, _jsonOptions);
            if (restored is null) return;

            var doc = _docVm.Document;

            // Откат снимка возвращает документ целиком, но обычно отличается в нём один-два
            // абзаца. Раньше все абзацы приходили новыми объектами, полотно теряло раскладку
            // каждого и заново раскладывало всю рукопись — на большом документе это секунды,
            // за которые на экране ничего не менялось, и отмена казалась несработавшей.
            //
            // Поэтому абзацы, совпадающие с текущими по идентификатору и содержимому, берутся
            // из текущего документа — теми же объектами, и их раскладка остаётся в кеше.
            // Это возможно, только если не менялось то, от чего зависит раскладка всех
            // абзацев сразу: стили, шаг табуляции, лист, колонки, колонтитулы. Иначе —
            // прежний путь с полной пересборкой.
            bool frameChanged = LayoutFrame(doc) != LayoutFrame(restored);
            int reused = frameChanged ? 0 : ReuseUnchangedParagraphs(doc, restored);

            doc.Sections.Clear();
            foreach (var section in restored.Sections)
                doc.Sections.Add(section);

            // Стили при неизменной рамке совпадают с текущими, и их объекты остаются
            // прежними: резолвер стилей полотна построен на них.
            if (frameChanged)
            {
                doc.Styles.Clear();
                foreach (var style in restored.Styles)
                    doc.Styles.Add(style);
            }

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
            doc.CollapseParagraphSpacing = restored.CollapseParagraphSpacing;
            doc.JustifyWithShrinking = restored.JustifyWithShrinking;
            doc.TableOfContents = restored.TableOfContents;
            doc.DocumentAutoReplaceRules = restored.DocumentAutoReplaceRules;

            doc.Annotations.Clear();
            foreach (var annotation in restored.Annotations)
                doc.Annotations.Add(annotation);

            if (frameChanged)
            {
                _docVm.RebuildParagraphViewModelsPublic();

                // Геометрию листа канвас пересобирает только по уведомлению о PageSettings.
                _docVm.RaisePageSettingsChanged();
                _docVm.FireParagraphFormatChanged();
            }
            else
            {
                // Лист и стили прежние: уведомления о них сбросили бы кеш раскладки
                // целиком. Изменившиеся абзацы пришли новыми объектами и получат новые VM,
                // а значит, будут разложены заново; остальные берутся из кеша.
                _docVm.SyncParagraphViewModelsPublic();
            }

            _log.Debug(
                "[UNDO] Восстановление снимка '{D}': рамка раскладки изменилась {Frame}, абзацев взято из текущего документа {Reused}, {Ms:F1} мс",
                Description, frameChanged, reused,
                System.Diagnostics.Stopwatch.GetElapsedTime(startTs).TotalMilliseconds);
        }

        /// <summary>
        /// Всё, от чего зависит раскладка всех абзацев сразу: стили, шаг табуляции,
        /// лист и колонки документа, а также лист, колонки и колонтитулы каждого раздела.
        /// Строка сравнивается целиком; совпадение значит, что раскладка неизменившихся
        /// абзацев остаётся верной.
        /// </summary>
        private static string LayoutFrame(DocumentModel doc)
        {
            var sections = new List<object?>();
            foreach (var section in doc.Sections)
                sections.Add(new
                {
                    section.PageSettings,
                    section.ColumnSettings,
                    section.Header,
                    section.Footer
                });

            return JsonSerializer.Serialize(new
            {
                doc.Styles,
                doc.DefaultTabStopPt,
                doc.CollapseParagraphSpacing,
                doc.JustifyWithShrinking,
                doc.PageSettings,
                doc.ColumnSettings,
                Sections = sections
            }, _jsonOptions);
        }

        /// <summary>
        /// Заменяет в восстановленном документе абзацы, совпадающие с текущими, на
        /// текущие объекты. Пара ищется по идентификатору блока, а совпадение
        /// проверяется по сериализованному содержимому — тем же способом, каким снимок
        /// сохраняется, поэтому подменённый абзац ничем не отличается от восстановленного.
        /// Каждый текущий абзац используется не больше одного раза.
        /// </summary>
        private static int ReuseUnchangedParagraphs(DocumentModel current, DocumentModel restored)
        {
            int reused = 0;
            int sectionCount = Math.Min(current.Sections.Count, restored.Sections.Count);
            for (int si = 0; si < sectionCount; si++)
            {
                var currentById = new Dictionary<Guid, ParagraphBlock>();
                foreach (var block in current.Sections[si].Blocks)
                    if (block is ParagraphBlock para)
                        currentById.TryAdd(para.Id, para);

                var restoredBlocks = restored.Sections[si].Blocks;
                for (int i = 0; i < restoredBlocks.Count; i++)
                {
                    if (restoredBlocks[i] is not ParagraphBlock restoredPara) continue;
                    if (!currentById.Remove(restoredPara.Id, out var currentPara)) continue;

                    string currentJson = JsonSerializer.Serialize(currentPara, _jsonOptions);
                    string restoredJson = JsonSerializer.Serialize(restoredPara, _jsonOptions);
                    if (!string.Equals(currentJson, restoredJson, StringComparison.Ordinal)) continue;

                    restoredBlocks[i] = currentPara;
                    reused++;
                }
            }
            return reused;
        }

        private static string Serialize(DocumentModel doc)
            => JsonSerializer.Serialize(doc, _jsonOptions);
    }
}
