using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using DynamicData;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Writersword.Core.Models.Rendering;
using Writersword.Modules.TextEditor.Rendering;
using Writersword.Modules.TextEditor.Commands;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.ViewModels;
using Writersword.Modules.TextEditor.ViewModels.Blocks;

namespace Writersword.Modules.TextEditor.Document
{
    public sealed partial class DocumentCanvas
    {
        // ── Undo ─────────────────────────────────────────────────────────
        // Глубина вложенности открытых шагов. Составные операции вызывают внутри
        // себя более мелкие, которые тоже открывают шаг: вставка таблицы дёргает
        // добавление строк и столбцов, те — свои правки. Без учёта вложенности
        // внутренний BeginEdit затирал снимок внешнего, внутренний коммит забирал
        // его себе, а внешнему доставалось пустое место — операция не попадала в
        // историю вообще, и порядок отмены рассыпался.
        // В стек уходит один снимок на всю составную операцию: «до» берётся у самого
        // внешнего вызова, «после» — у самого внешнего завершения.
        private int _editDepth;

        private void BeginEdit(string description)
        {
            if (DocVm is null) { _logger.Warning("[UNDO] BeginEdit({D}): DocVm is null", description); return; }

            if (_pendingSnapshot is not null)
            {
                _editDepth++;
                _logger.Debug("[UNDO] BeginEdit (nested, depth {N}): {D}", _editDepth, description);
                return;
            }

            _logger.Debug("[UNDO] BeginEdit: {D}", description);
            _editDepth = 1;
            _pendingSnapshot = new DocumentSnapshotCommand(DocVm, description, _caretPara, _caretChar);
            _pendingSnapshot.RestoreCaretCallback = (para, ch) =>
            {
                _caretPara = Clamp(para, 0, Math.Max(0, _layouts.Count - 1));
                _caretChar = ch;
            };
        }

        /// <summary>
        /// Выбросить незакрытый снимок, не кладя его в стек. Нужен жестам: снимок
        /// берётся на нажатии, до первой правки, но нажатие может закончиться
        /// ничем — тогда шаг отмены был бы пустым.
        /// </summary>
        private void DiscardPendingEdit()
        {
            if (_pendingSnapshot is null) return;
            if (_editDepth > 1) { _editDepth--; return; }
            _logger.Debug("[UNDO] DiscardPendingEdit: '{D}'", _pendingSnapshot.Description);
            _editDepth = 0;
            _pendingSnapshot = null;
        }

        private void CommitEdit()
        {
            if (_pendingSnapshot is null) { _logger.Warning("[UNDO] CommitEdit: no pending snapshot"); return; }

            // Вложенное завершение только уменьшает глубину: документ ещё
            // дорабатывается внешней операцией, и снимок «после» брать рано.
            if (_editDepth > 1)
            {
                _editDepth--;
                return;
            }

            if (UndoStack is null) { _logger.Warning("[UNDO] CommitEdit: UndoStack is null"); return; }
            _pendingSnapshot.Commit(_caretPara, _caretChar);
            UndoStack.Push(_pendingSnapshot);
            RecordSnapshotInOrder();
            DocVm?.RaiseContentModified();
            _logger.Debug("[UNDO] CommitEdit: pushed '{D}', stackSize={S}", _pendingSnapshot.Description, UndoStack.CanUndo);
            _editDepth = 0;
            _pendingSnapshot = null;
        }

        // ── Undo для операций с таблицами ────────────────────────────────
        // Правки внутри одной таблицы пишутся снимком этой таблицы, а не всего
        // документа: содержимое остальных страниц операция не затрагивает, а
        // сериализация документа целиком на каждый шаг стоила заметной паузы и
        // держала в стеке по две полные копии.
        // Через снимок документа по-прежнему идут операции, меняющие состав блоков
        // раздела: вставка и удаление таблицы, а также удаление последней строки или
        // столбца — там таблица исчезает из документа, и снимка самой таблицы для
        // возврата недостаточно.
        private Writersword.Modules.TextEditor.Commands.TableSnapshotCommand? _pendingTableEdit;
        private Models.Document.TableBlock? _pendingTableEditBlock;
        private int _tableEditDepth;

        private void BeginTableEdit(Models.Document.TableBlock? table, string description)
        {
            if (table is null) return;

            // Вложенность — как у снимков документа: составная операция открывает шаг
            // один раз, внутренние правки только увеличивают глубину.
            if (_pendingTableEdit is not null)
            {
                _tableEditDepth++;
                return;
            }

            _logger.Debug("[UNDO] BeginTableEdit: {D}", description);
            _tableEditDepth = 1;
            _pendingTableEditBlock = table;
            _pendingTableEdit = new Writersword.Modules.TextEditor.Commands.TableSnapshotCommand(table, description);
        }

        private void CommitTableEdit()
        {
            if (_pendingTableEdit is null || _pendingTableEditBlock is null) return;
            if (_tableEditDepth > 1) { _tableEditDepth--; return; }

            _pendingTableEdit.Commit(_pendingTableEditBlock);
            if (_pendingTableEdit.HasChanges)
            {
                PushTextCommand(_pendingTableEdit);
                DocVm?.RaiseContentModified();
                _logger.Debug("[UNDO] CommitTableEdit: pushed '{D}'", _pendingTableEdit.Description);
            }
            else
            {
                _logger.Debug("[UNDO] CommitTableEdit: '{D}' changed nothing", _pendingTableEdit.Description);
            }

            _tableEditDepth = 0;
            _pendingTableEdit = null;
            _pendingTableEditBlock = null;
        }

        private void DiscardTableEdit()
        {
            if (_pendingTableEdit is null) return;
            if (_tableEditDepth > 1) { _tableEditDepth--; return; }
            _tableEditDepth = 0;
            _pendingTableEdit = null;
            _pendingTableEditBlock = null;
        }

        // ── Undo для операций с картинками ───────────────────────────────
        // Свойства картинки меняются гранулярной командой (значения одного блока
        // до/после), а не снапшотом всего документа: сериализация DocumentModel
        // на больших документах занимала заметное время на каждый жест, а отмена —
        // полную десериализацию с пересбором всех вью-моделей.
        private ImagePropertiesCommand? _pendingImageCommand;

        private void BeginImageEdit(string description) => BeginImageEdit(_selectedImage, description);

        private void BeginImageEdit(ImageBlock? image, string description)
        {
            if (image is null) return;
            _logger.Debug("[UNDO] BeginImageEdit: {D}", description);
            _pendingImageCommand = new ImagePropertiesCommand(image, description)
            {
                Changed = () =>
                {
                    RebuildLayouts();
                    InvalidateMeasure();
                    InvalidateFull();
                    ImageSelectionChanged?.Invoke(_selectedImage is not null);
                }
            };
        }

        private void CommitImageEdit()
        {
            if (_pendingImageCommand is null) { _logger.Warning("[UNDO] CommitImageEdit: no pending command"); return; }
            // Уведомление идёт до проверки UndoStack: документ изменён независимо
            // от того, попала правка в стек отмены или нет.
            DocVm?.RaiseContentModified();
            if (UndoStack is null) { _pendingImageCommand = null; return; }
            _pendingImageCommand.Commit();
            UndoStack.Push(_pendingImageCommand);
            RecordSnapshotInOrder();
            _logger.Debug("[UNDO] CommitImageEdit: pushed '{D}'", _pendingImageCommand.Description);
            _pendingImageCommand = null;
        }

        private void CancelImageEdit() => _pendingImageCommand = null;

        // ── Selection ────────────────────────────────────────────────────
        private bool HasSel() =>
            _selStartPara != _selEndPara || _selStartChar != _selEndChar;

        private bool HasCellRangeSel() => _tableSelections.Count > 0;

        private bool IsCellSelected(TableCell cell)
        {
            foreach (var kv in _tableSelections)
            {
                int minRow = Math.Min(kv.Value.sr, kv.Value.er);
                int maxRow = Math.Max(kv.Value.sr, kv.Value.er);
                int minCol = Math.Min(kv.Value.sc, kv.Value.ec);
                int maxCol = Math.Max(kv.Value.sc, kv.Value.ec);
                // По пересечению: выделение идёт по клеткам сетки, и объединённая
                // ячейка часто задета частично.
                if (cell.Row + cell.RowSpan - 1 >= minRow && cell.Row <= maxRow
                    && cell.Column + cell.ColSpan - 1 >= minCol && cell.Column <= maxCol)
                    return true;
            }
            return false;
        }

        // Очищает содержимое всех выделенных ячеек и сбрасывает cell-range режим.
        private void ClearCellRangeSelection()
        {
            if (_tableSelections.Count == 0)
            {
                // Флаг режима сбрасывается даже когда очищать нечего. Диапазон ячеек
                // снимается и в других местах (клик, ввод текста, перестроение), а
                // флаг там оставался поднятым — и ранний выход отсюда его не трогал.
                // В таком состоянии Delete и Backspace попадали в ветку «идёт
                // выделение ячеек», уходили сюда и не удаляли ничего: набор текста
                // работал, а удаление было наглухо заблокировано до следующего клика.
                _isCellRangeSelecting = false;
                return;
            }

            BeginEdit("Delete cell contents");

            foreach (var kv in _tableSelections)
            {
                int minRow = Math.Min(kv.Value.sr, kv.Value.er);
                int maxRow = Math.Max(kv.Value.sr, kv.Value.er);
                int minCol = Math.Min(kv.Value.sc, kv.Value.ec);
                int maxCol = Math.Max(kv.Value.sc, kv.Value.ec);

                foreach (var cell in kv.Key.Cells)
                {
                    if (cell.Row + cell.RowSpan - 1 < minRow || cell.Row > maxRow) continue;
                    if (cell.Column + cell.ColSpan - 1 < minCol || cell.Column > maxCol) continue;

                    cell.Paragraphs.Clear();
                    cell.Paragraphs.Add(new Writersword.Modules.TextEditor.Models.Document.ParagraphBlock());
                }
            }

            _isCellRangeSelecting = false;
            _tableSelections.Clear();
            _cellFlowRanges.Clear();
            _cellFlowFull.Clear();

            CommitEdit();
            InvalidateCellLayoutCaches();

            // Вью-модели очищенных абзацев держат прежний текст: сами абзацы заменены на
            // новые объекты, и старые записи кэша к документу больше не относятся.
            _cellVmCache.Clear();

            // Очистка ячеек укорачивает строки, и таблица становится ниже. Без
            // InvalidateMeasure контрол остаётся с прежним измеренным размером: прокрутка
            // считает документ высоким, страница рисуется на новом месте, а сверху остаётся
            // пустое поле ровно на разницу высот. Соседний RebuildAfterCellEdit делает так же.
            double oldCanvasH = _canvasHeight;
            RebuildLayouts();
            if (Math.Abs(_canvasHeight - oldCanvasH) > 0.5)
                InvalidateMeasure();

            InvalidateFull();
        }

        private (int sp, int sc, int ep, int ec) NormalizeSelection()
        {
            // Используем layout-индексы напрямую — работает и для ячеек и для параграфов
            if (_selStartPara < _selEndPara)
                return (_selStartPara, _selStartChar, _selEndPara, _selEndChar);
            if (_selStartPara > _selEndPara)
                return (_selEndPara, _selEndChar, _selStartPara, _selStartChar);
            if (_selStartChar <= _selEndChar)
                return (_selStartPara, _selStartChar, _selEndPara, _selEndChar);
            return (_selEndPara, _selEndChar, _selStartPara, _selStartChar);
        }

        private void SyncSel()
        {
            _selStartPara = _caretPara; _selStartChar = _caretChar;
            _selEndPara = _caretPara; _selEndChar = _caretChar;
        }

        private void ExtendSel()
        {
            _selEndPara = _caretPara;
            _selEndChar = _caretChar;
        }

        private void DeleteSelection()
        {
            bool hasSel = HasSel();
            bool hasTables = _tableSelections.Count > 0;
            if (!hasSel && !hasTables) return;

            // Удаляем выделенные таблицы из документа.
            if (hasTables && DocVm is not null)
            {
                var blocks = DocVm.Document.Sections[0].Blocks;
                foreach (var tbl in _tableSelections.Keys.ToList())
                    blocks.Remove(tbl);
                _tableSelections.Clear();
                _cellFlowRanges.Clear();
                _cellFlowFull.Clear();
                _isCellRangeSelecting = false;
            }

            if (!hasSel) { _caretPara = Clamp(_caretPara, 0, Math.Max(0, _layouts.Count - 1)); return; }

            var (sp, sc, ep, ec) = NormalizeSelection();
            var sVm = GetVmAt(sp);
            var eVm = GetVmAt(ep);
            if (sVm is null || eVm is null) return;

            if (sVm == eVm)
            {
                string t = sVm.PlainText ?? "";
                int s2 = Clamp(sc, 0, t.Length);
                int e2 = Clamp(ec, 0, t.Length);
                sVm.Model.SpliceText(s2, e2, string.Empty);
                sVm.RefreshPlainTextFromModel();
                _caretChar = s2;
            }
            else if (!IsInCell(sp) && !IsInCell(ep))
            {
                string st = sVm.PlainText ?? "";
                string et = eVm.PlainText ?? "";
                int s2 = Clamp(sc, 0, st.Length);
                int e2 = Clamp(ec, 0, et.Length);

                int si = DocVm?.Paragraphs.IndexOf(sVm) ?? 0;
                int ei = DocVm?.Paragraphs.IndexOf(eVm) ?? 0;

                // Картинки, через которые прошло выделение, удаляются вместе с текстом:
                // они подсвечены как выделенные, и оставить их висеть в документе значило бы
                // соврать пользователю. Инлайн-картинки сюда не относятся — их символ
                // уходит вместе с текстом абзаца.
                var imagesToDelete = ImagesInTextSelection();
                if (imagesToDelete.Count > 0 && DocVm is not null)
                {
                    var flowBlocks = DocVm.Document.Sections[0].Blocks;
                    foreach (var image in imagesToDelete)
                    {
                        flowBlocks.Remove(image);
                        DocVm.Document.Sections[0].FloatingObjects.Remove(image);
                    }
                    _imagesInTextSelection = new HashSet<ImageBlock>();
                }

                var toDelete = new List<ParagraphViewModel>();
                for (int di = ei; di > si; di--)
                    if (di < (DocVm?.Paragraphs.Count ?? 0))
                        toDelete.Add(DocVm!.Paragraphs[di]);

                // Удаляем хвост первого параграфа и голову последнего, сохраняя форматирование.
                // Хвост последнего переносим посимвольно: плоский текст потерял бы и
                // форматирование символов, и картинки в строке.
                var headCells = sVm.Model.ToCharCells();
                if (headCells.Count > s2) headCells.RemoveRange(s2, headCells.Count - s2);

                var tailCells = eVm.Model.ToCharCells();
                if (e2 > 0) tailCells.RemoveRange(0, Math.Min(e2, tailCells.Count));
                headCells.AddRange(tailCells);

                sVm.Model.RebuildFromCharCells(headCells);
                sVm.RefreshPlainTextFromModel();
                foreach (var p in toDelete) DocVm?.DeleteParagraph(p);
                _caretChar = s2;
            }

            _caretPara = sp;
            SyncSel();
            SnapCaretToCorrectSlice();
            UpdatePreferredX();
        }

        // ── Clipboard ────────────────────────────────────────────────────
        private async Task CopyAsync()
        {
            // Копирование разрешено и в режиме сравнения (read-only) —
            // оно не меняет данные документа.
            _internalClipboardJson = null;
            _internalClipboardPlain = null;
            _clipboardCache = null;
            _clipboardImage = null;
            _clipboardImageBytes = null;

            // Картинка в приоритете: если она выделена — копируем именно её, даже при
            // остаточном текстовом выделении. Иначе Ctrl+C по картинке уходил в пустое
            // копирование текста и картинка в буфер не попадала.
            if (_selectedImage is not null)
            {
                await CopyImageToClipboard(_selectedImage);
                return;
            }

            bool hasSel = HasSel();
            bool hasCells = _tableSelections.Count > 0;
            if (!hasSel && !hasCells) return;

            var (sp, sc, ep, ec) = hasSel ? NormalizeSelection() : (0, 0, 0, 0);

            var blocks = new List<ClipboardBlock>();
            var plainParts = new List<string>();

            var seenParaVms = new HashSet<ParagraphViewModel>();
            var seenTables = new HashSet<TableBlock>();

            // Единый проход по layouts в порядке документа.
            // При наличии только табличного выделения берём весь диапазон layouts.
            int rangeStart = hasSel ? sp : 0;
            int rangeEnd = hasSel ? ep : _layouts.Count - 1;
            if (hasCells && hasSel)
            {
                // Расширяем диапазон до первого/последнего layout выделенных таблиц.
                foreach (var tbl in _tableSelections.Keys)
                {
                    for (int i = 0; i < _layouts.Count; i++)
                    {
                        if (_layouts[i].Cell?.Table != tbl) continue;
                        if (i < rangeStart) rangeStart = i;
                        if (i > rangeEnd) rangeEnd = i;
                    }
                }
            }
            else if (hasCells && !hasSel)
            {
                rangeStart = 0; rangeEnd = _layouts.Count - 1;
            }

            for (int i = rangeStart; i <= rangeEnd && i < _layouts.Count; i++)
            {
                var pl = _layouts[i];
                var pvm = GetVmAt(i);

                if (pl.Cell != null)
                {
                    // Ячейка таблицы.
                    var tbl = pl.Cell.Table;

                    if (_tableSelections.ContainsKey(tbl))
                    {
                        // Cell-range выделение — копируем таблицу (слайс) один раз.
                        if (!seenTables.Add(tbl)) continue;
                        var copied = SliceTable(tbl, _tableSelections[tbl].sr, _tableSelections[tbl].sc,
                            _tableSelections[tbl].er, _tableSelections[tbl].ec);
                        blocks.Add(new ClipboardBlock { Kind = ClipboardBlockKind.Table, Table = copied });
                        for (int r = 0; r < copied.RowCount; r++)
                        {
                            var rowCells = new List<string>();
                            for (int c = 0; c < copied.ColumnCount; c++)
                            {
                                var cell = copied.GetCell(r, c);
                                rowCells.Add(cell != null
                                    ? string.Join(" ", cell.Paragraphs.Select(p => p.GetPlainText()))
                                    : "");
                            }
                            plainParts.Add(string.Join("\t", rowCells));
                        }
                        plainParts.Add("");
                    }
                    else if (hasSel)
                    {
                        // Текстовое выделение внутри ячейки — копируем как обычный параграф.
                        if (pvm is null || !seenParaVms.Add(pvm)) continue;
                        string t2 = pvm.PlainText ?? "";
                        int from2 = (i == sp) ? Clamp(sc, 0, t2.Length) : 0;
                        int to2 = (i == ep) ? Clamp(ec, 0, t2.Length) : t2.Length;
                        if (from2 > to2) to2 = from2;
                        string txt2 = t2[from2..to2];
                        blocks.Add(new ClipboardBlock { Kind = ClipboardBlockKind.Paragraph, Text = txt2, Block = CloneParagraphBlock(pvm.Model, from2, to2) });
                        plainParts.Add(txt2);
                    }
                    continue;
                }

                // Обычный параграф.
                if (pvm is null || !seenParaVms.Add(pvm)) continue;
                // Якорные параграфы вокруг таблиц не включаем.
                if (IsBlockBeforeTable(pvm.Model) || IsBlockAfterTable(pvm.Model)) continue;

                string t = pvm.PlainText ?? "";
                int from = (i == sp && hasSel) ? Clamp(sc, 0, t.Length) : 0;
                int to = (i == ep && hasSel) ? Clamp(ec, 0, t.Length) : t.Length;
                if (from > to) to = from;
                string text = t[from..to];

                blocks.Add(new ClipboardBlock { Kind = ClipboardBlockKind.Paragraph, Text = text, Block = CloneParagraphBlock(pvm.Model, from, to) });
                plainParts.Add(text);

                // Картинки, стоящие в потоке сразу за этим абзацем и попавшие в выделение,
                // кладём следом — тогда при вставке они окажутся между теми же абзацами.
                AppendImagesAfterParagraph(blocks, pvm.Model);
            }

            if (blocks.Count == 0) return;

            // Убираем пустые строки в конце plain text.
            while (plainParts.Count > 0 && plainParts[^1] == "") plainParts.RemoveAt(plainParts.Count - 1);

            var opts = new JsonSerializerOptions { WriteIndented = false };
            _internalClipboardJson = JsonSerializer.Serialize(blocks, opts);

            string plain = string.Join(Environment.NewLine, plainParts);
            _clipboardCache = plain;

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
            {
#pragma warning disable CS0618
                await clipboard.SetTextAsync(plain);
#pragma warning restore CS0618

                // Что положили в буфер — то и запомнили. Вставка сверяется с этим
                // текстом: пока он в буфере, структурная копия описывает именно его.
                _internalClipboardPlain = plain;
            }
        }

        /// <summary>
        /// Копирует выделенную картинку: полную копию блока во внутренний буфер (для точной
        /// вставки внутри документа) и пиксели PNG в системный буфер (Avalonia 12 — через
        /// DataTransfer), чтобы картинку можно было вставить и во внешних приложениях.
        /// </summary>
        private async Task CopyImageToClipboard(ImageBlock img)
        {
            _logger.Debug("[CLIP] Copy image: {File}", img.ImageFileName);

            _clipboardImage = CloneImageFull(img);
            _clipboardImageBytes = null;

            byte[]? data = null;
            try
            {
                var ctx = Writersword.Core.Services.CoreServices
                    .GetService<Writersword.Core.Interfaces.WorkFlows.ITabCollection>()?.ActiveTab?.Context;
                // Байты — для вставки в другой проект (файл переносится в его хранилище).
                data = ctx?.ReadFile($"TextEditor/Images/{img.ImageFileName}");
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[CLIP] Read image file failed: {File}", img.ImageFileName);
            }

            _clipboardImageBytes = data;
            if (data is not { Length: > 0 })
            {
                _logger.Debug("[CLIP] Copy image: file not read, {File}", img.ImageFileName);
                return;
            }

            var top = TopLevel.GetTopLevel(this);
            var clip = top?.Clipboard;
            if (clip is null)
            {
                _logger.Debug(
                    "[CLIP] Copy image: no system clipboard (topLevel={Top})",
                    top?.GetType().Name ?? "null");
                return;
            }

            Avalonia.Media.Imaging.Bitmap? bmp = null;
            try
            {
                using var ms = new System.IO.MemoryStream(data);
                bmp = new Avalonia.Media.Imaging.Bitmap(ms);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[CLIP] Decode image for clipboard failed");
            }

            // Классический формат Windows строится здесь, своими руками.
            //
            // Раньше над этим местом стояло рассуждение, что раскладку по форматам
            // Windows делает сама Avalonia, если положить DataFormat.Bitmap. Опыт
            // говорит обратное: внутри редактора вставка работала — она читает свой
            // же «PNG», — а наружу, в Word, в Paint, в переписку, не выходило ничего.
            // DataFormat.Bitmap оказался понятием Avalonia, а не буфера обмена.
            byte[]? dib = BuildDeviceIndependentBitmap(data);

            try
            {
                var dt = new Avalonia.Input.DataTransfer();
                if (bmp is not null)
                    dt.Add(Avalonia.Input.DataTransferItem.Create(Avalonia.Input.DataFormat.Bitmap, bmp));

                // CF_DIB — то, чем берут картинку Word, Paint и почти всё, что
                // старше браузера.
                if (dib is not null)
                    dt.Add(Avalonia.Input.DataTransferItem.Create(ClipboardImageDibFormat, dib));

                // Плюс сырой PNG — приложениям, читающим формат «PNG». Он же служит
                // признаком владения: вставка сверяет байты из буфера с
                // _clipboardImageBytes и по совпадению понимает, что там всё ещё
                // лежит именно эта картинка.
                dt.Add(Avalonia.Input.DataTransferItem.Create(ClipboardImagePngFormat, data));

                _logger.Debug(
                    "[CLIP] Copy image: writing {Len} bytes to clipboard, bitmap={HasBitmap}, dib={DibLen}",
                    data.Length, bmp is not null, dib?.Length ?? -1);
                await clip.SetDataAsync(dt);
                _logger.Debug("[CLIP] Image copied");
                return;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[CLIP] SetDataAsync failed, falling back to bitmap only");
            }

            // Резерв: хотя бы пиксели должны попасть в буфер, иначе картинку не вставить
            // никуда за пределами редактора.
            if (bmp is null) return;
            try
            {
                await clip.SetBitmapAsync(bmp);
                _logger.Debug("[CLIP] Image copied as bitmap only: {Len} bytes", data.Length);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[CLIP] SetBitmapAsync failed");
            }
        }

        /// <summary>
        /// Собирает CF_DIB из байтов PNG.
        ///
        /// CF_DIB — это BITMAPINFOHEADER и следом пиксели, БЕЗ файлового заголовка
        /// BITMAPFILEHEADER: тот добавляет лишь тот, кто пишет .bmp на диск, а в
        /// буфере обмена его быть не должно — с ним картинку не примет никто.
        ///
        /// Строки идут снизу вверх, и это задаётся положительной высотой в
        /// заголовке. Отрицательную высоту — строки сверху вниз — понимают не все,
        /// а у непонявших картинка встаёт вверх ногами.
        ///
        /// Прозрачность подкладывается белым. Альфа в 32-битном DIB формально есть,
        /// но читают её единицы, а остальные показывают на месте прозрачных точек
        /// чёрное. Белое и меньшее из зол, и совпадает с бумагой документа.
        ///
        /// Подкладка считается вручную, в том же проходе, где строки переставляются
        /// снизу вверх. Первым заходом она делалась холстом Skia поверх растра с
        /// непредумноженной альфой — и картинка получалась целиком чёрной: Skia
        /// рисует в предумноженной альфе, и холст поверх непредумноженного растра
        /// даёт мусор. Проход по точкам от типа альфы не зависит вовсе, лишь бы он
        /// был известен, — а он известен из самого растра.
        /// </summary>
        private byte[]? BuildDeviceIndependentBitmap(byte[] png)
        {
            try
            {
                using var decoded = SKBitmap.Decode(png);
                if (decoded is null || decoded.Width <= 0 || decoded.Height <= 0)
                    return null;

                // Порядок каналов приводится к тому, в каком его ждёт DIB. Копия
                // делается всегда: у PNG он бывает и RGBA, и с иным типом альфы.
                using var image = decoded.Copy(SKColorType.Bgra8888);
                if (image is null)
                    return null;

                bool premultiplied = image.Info.AlphaType == SKAlphaType.Premul;

                int width = image.Width;
                int height = image.Height;
                int stride = width * 4;

                const int HeaderSize = 40;
                var result = new byte[HeaderSize + stride * height];

                void PutInt(int offset, int value)
                    => BitConverter.TryWriteBytes(result.AsSpan(offset, 4), value);

                void PutShort(int offset, short value)
                    => BitConverter.TryWriteBytes(result.AsSpan(offset, 2), value);

                PutInt(0, HeaderSize);          // biSize
                PutInt(4, width);               // biWidth
                PutInt(8, height);              // biHeight, положительная — снизу вверх
                PutShort(12, 1);                // biPlanes
                PutShort(14, 32);               // biBitCount
                PutInt(16, 0);                  // biCompression = BI_RGB
                PutInt(20, stride * height);    // biSizeImage
                PutInt(24, 0);                  // biXPelsPerMeter
                PutInt(28, 0);                  // biYPelsPerMeter
                PutInt(32, 0);                  // biClrUsed
                PutInt(36, 0);                  // biClrImportant

                var pixels = image.GetPixelSpan();

                for (int row = 0; row < height; row++)
                {
                    var from = pixels.Slice((height - 1 - row) * stride, stride);
                    var to = result.AsSpan(HeaderSize + row * stride, stride);

                    for (int x = 0; x < stride; x += 4)
                    {
                        byte b = from[x];
                        byte g = from[x + 1];
                        byte r = from[x + 2];
                        int a = from[x + 3];

                        if (a < 255)
                        {
                            int white = 255 - a;

                            if (premultiplied)
                            {
                                // Каналы уже умножены на альфу — белое просто
                                // добавляется тем, чего до непрозрачности не хватает.
                                b = (byte)(b + white);
                                g = (byte)(g + white);
                                r = (byte)(r + white);
                            }
                            else
                            {
                                b = (byte)((b * a + 255 * white) / 255);
                                g = (byte)((g * a + 255 * white) / 255);
                                r = (byte)((r * a + 255 * white) / 255);
                            }
                        }

                        to[x] = b;
                        to[x + 1] = g;
                        to[x + 2] = r;

                        // Непрозрачность выставляется всем точкам: подкладка уже
                        // сделана, и прежнее значение сбивало бы с толку тех, кто
                        // альфу всё-таки читает.
                        to[x + 3] = 255;
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[CLIP] Building DIB failed");
                return null;
            }
        }

        /// <summary>Полная копия блока картинки со всеми свойствами. Id — новый (по умолчанию).</summary>
        private static ImageBlock CloneImageFull(ImageBlock s) => new()
        {
            ImageFileName = s.ImageFileName,
            WidthPt = s.WidthPt,
            HeightPt = s.HeightPt,
            LockAspectRatio = s.LockAspectRatio,
            RotationDeg = s.RotationDeg,
            Opacity = s.Opacity,
            BorderColor = s.BorderColor,
            BorderThicknessPt = s.BorderThicknessPt,
            BorderAlign = s.BorderAlign,
            FlipHorizontal = s.FlipHorizontal,
            FlipVertical = s.FlipVertical,
            CropLeftFrac = s.CropLeftFrac,
            CropTopFrac = s.CropTopFrac,
            CropRightFrac = s.CropRightFrac,
            CropBottomFrac = s.CropBottomFrac,
            WrapMode = s.WrapMode,
            Alignment = s.Alignment,
            Anchor = s.Anchor,
            WrapPadTopPt = s.WrapPadTopPt,
            WrapPadBottomPt = s.WrapPadBottomPt,
            WrapPadLeftPt = s.WrapPadLeftPt,
            WrapPadRightPt = s.WrapPadRightPt,
            OffsetXPt = s.OffsetXPt,
            OffsetYPt = s.OffsetYPt,
            ZOrder = s.ZOrder,
            AltText = s.AltText
        };

        /// <summary>
        /// Строит 32-битный DIB (для формата CF_DIB) из закодированной картинки (PNG и т.п.):
        /// BITMAPINFOHEADER (40 байт, положительная высота = строки снизу вверх) + BGRA-пиксели.
        /// SkiaSharp BMP не кодирует, поэтому собираем вручную из декодированных пикселей.
        /// </summary>
        private static byte[]? BuildDibFromImage(byte[] encoded)
        {
            using var decoded = SkiaSharp.SKBitmap.Decode(encoded);
            if (decoded is null || decoded.Width <= 0 || decoded.Height <= 0) return null;

            // Приводим к BGRA8888 (в памяти байты идут B,G,R,A — как ждёт 32-битный DIB).
            SkiaSharp.SKBitmap src = decoded;
            SkiaSharp.SKBitmap? converted = null;
            if (decoded.ColorType != SkiaSharp.SKColorType.Bgra8888)
            {
                converted = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(
                    decoded.Width, decoded.Height,
                    SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Unpremul));
                if (!decoded.CopyTo(converted, SkiaSharp.SKColorType.Bgra8888))
                {
                    converted.Dispose();
                    return null;
                }
                src = converted;
            }

            int w = src.Width, h = src.Height;
            int srcStride = src.RowBytes;
            int dstStride = w * 4;                 // 32 бита выровнены по 4 байта
            byte[] px = src.Bytes;                 // сверху вниз, BGRA
            const int headerSize = 40;
            var dib = new byte[headerSize + dstStride * h];

            System.BitConverter.GetBytes(headerSize).CopyTo(dib, 0);      // biSize
            System.BitConverter.GetBytes(w).CopyTo(dib, 4);              // biWidth
            System.BitConverter.GetBytes(h).CopyTo(dib, 8);              // biHeight (>0 — снизу вверх)
            System.BitConverter.GetBytes((short)1).CopyTo(dib, 12);     // biPlanes
            System.BitConverter.GetBytes((short)32).CopyTo(dib, 14);    // biBitCount
            System.BitConverter.GetBytes(0).CopyTo(dib, 16);            // biCompression = BI_RGB
            System.BitConverter.GetBytes(dstStride * h).CopyTo(dib, 20);// biSizeImage

            int copyBytes = Math.Min(srcStride, dstStride);
            for (int row = 0; row < h; row++)
            {
                int srcOff = (h - 1 - row) * srcStride;   // строки снизу вверх
                int dstOff = headerSize + row * dstStride;
                System.Array.Copy(px, srcOff, dib, dstOff, copyBytes);
            }

            converted?.Dispose();
            return dib;
        }

        /// <summary>
        /// Создаёт новый TableBlock содержащий только ячейки в диапазоне
        /// [minRow..maxRow] × [minCol..maxCol] с перенумерованными строками/столбцами.
        /// </summary>
        private static TableBlock SliceTable(TableBlock src,
            int sr, int sc, int er, int ec)
        {
            int minRow = Math.Min(sr, er); int maxRow = Math.Max(sr, er);
            int minCol = Math.Min(sc, ec); int maxCol = Math.Max(sc, ec);

            var dst = new TableBlock
            {
                RowCount = maxRow - minRow + 1,
                ColumnCount = maxCol - minCol + 1,
                LeftIndentPt = src.LeftIndentPt,
                WidthPercent = src.WidthPercent,
                SplitMode = src.SplitMode,
                StyleName = src.StyleName,
            };

            // Колонки.
            for (int c = minCol; c <= maxCol && c < src.Columns.Count; c++)
            {
                var srcCol = src.Columns[c];
                dst.Columns.Add(new TableColumnDefinition
                {
                    WidthType = srcCol.WidthType,
                    WidthValue = srcCol.WidthValue,
                });
            }
            while (dst.Columns.Count < dst.ColumnCount)
                dst.Columns.Add(new TableColumnDefinition());

            // Ячейки.
            foreach (var srcCell in src.Cells)
            {
                if (srcCell.Row < minRow || srcCell.Row > maxRow) continue;
                if (srcCell.Column < minCol || srcCell.Column > maxCol) continue;

                var dstCell = new TableCell
                {
                    Id = Guid.NewGuid(),
                    Row = srcCell.Row - minRow,
                    Column = srcCell.Column - minCol,
                    RowSpan = Math.Min(srcCell.RowSpan, maxRow - srcCell.Row + 1),
                    ColSpan = Math.Min(srcCell.ColSpan, maxCol - srcCell.Column + 1),
                    BackgroundColor = srcCell.BackgroundColor,
                    Borders = srcCell.Borders.Clone(),
                    PaddingTopPt = srcCell.PaddingTopPt,
                    PaddingBottomPt = srcCell.PaddingBottomPt,
                    PaddingLeftPt = srcCell.PaddingLeftPt,
                    PaddingRightPt = srcCell.PaddingRightPt,
                };
                dstCell.Paragraphs.Clear();
                foreach (var para in srcCell.Paragraphs)
                {
                    var newPara = new ParagraphBlock
                    {
                        Properties = para.Properties.Clone(),
                        ListProperties = para.ListProperties?.Clone()
                    };
                    newPara.Chunks.Clear();
                    foreach (var chunk in para.Chunks)
                    {
                        var newChunk = new TextChunk();
                        foreach (var run in chunk.Runs)
                            newChunk.Runs.Add(run.Clone());
                        newPara.Chunks.Add(newChunk);
                    }
                    if (newPara.Chunks.Count == 0)
                        newPara.Chunks.Add(new TextChunk());
                    dstCell.Paragraphs.Add(newPara);
                }
                if (dstCell.Paragraphs.Count == 0)
                    dstCell.Paragraphs.Add(new ParagraphBlock());
                dst.Cells.Add(dstCell);
            }

            return dst;
        }

        private async Task CutAsync()
        {
            if (IsEditingBlocked) return;

            // Только картинка выделена — вырезаем её: копия в буфер + удаление из документа.
            if (_selectedImage is not null && !HasSel() && _tableSelections.Count == 0)
            {
                await CopyImageToClipboard(_selectedImage);
                var toRemove = _selectedImage;
                ExitImageCropMode(apply: false);
                MoveCaretToImage(toRemove);

                BeginEdit("Вырезание изображения");
                _selectedImage = null;
                DocVm?.RemoveImage(toRemove);
                ImageSelectionChanged?.Invoke(false);
                CommitEdit();

                InvalidateFull();
                return;
            }

            BeginEdit("Cut");
            await CopyAsync();

            if (IsInCell(_caretPara))
            {
                CellDeleteSelection();
                CommitEdit();
                RebuildAfterCellEdit();
            }
            else
            {
                DeleteSelection();
                CommitEdit();
                SnapCaretToCorrectSlice();
                UpdatePreferredX();
                SyncSel(); ResetCaret(); InvalidateFull();
            }
        }

        /// <summary>
        /// Добавляет в буфер картинки, идущие в потоке сразу за абзацем и попавшие
        /// в текстовое выделение. Порядок буфера повторяет порядок документа, поэтому
        /// вставка воспроизводит ту же структуру «абзац — картинка — абзац».
        /// </summary>
        private void AppendImagesAfterParagraph(List<ClipboardBlock> blocks, ParagraphBlock para)
        {
            if (_imagesInTextSelection.Count == 0 || DocVm is null) return;

            var flowBlocks = DocVm.Document.Sections[0].Blocks;
            int idx = flowBlocks.IndexOf(para);
            if (idx < 0) return;

            for (int i = idx + 1; i < flowBlocks.Count; i++)
            {
                if (flowBlocks[i] is not ImageBlock image) break;
                if (!_imagesInTextSelection.Contains(image)) break;
                blocks.Add(new ClipboardBlock
                {
                    Kind = ClipboardBlockKind.Image,
                    Image = CloneImageBlockForClipboard(image)
                });
            }
        }

        /// <summary>Копия картинки для буфера: все свойства, файл переиспользуется.</summary>
        private static ImageBlock CloneImageBlockForClipboard(ImageBlock src) => new()
        {
            ImageFileName = src.ImageFileName,
            WidthPt = src.WidthPt,
            HeightPt = src.HeightPt,
            LockAspectRatio = src.LockAspectRatio,
            RotationDeg = src.RotationDeg,
            Opacity = src.Opacity,
            BorderColor = src.BorderColor,
            BorderThicknessPt = src.BorderThicknessPt,
            BorderAlign = src.BorderAlign,
            FlipHorizontal = src.FlipHorizontal,
            FlipVertical = src.FlipVertical,
            CropLeftFrac = src.CropLeftFrac,
            CropTopFrac = src.CropTopFrac,
            CropRightFrac = src.CropRightFrac,
            CropBottomFrac = src.CropBottomFrac,
            WrapMode = src.WrapMode,
            WrapSide = src.WrapSide,
            PinnedPage = src.PinnedPage,
            Alignment = src.Alignment,
            Anchor = src.Anchor,
            WrapPadTopPt = src.WrapPadTopPt,
            WrapPadBottomPt = src.WrapPadBottomPt,
            WrapPadLeftPt = src.WrapPadLeftPt,
            WrapPadRightPt = src.WrapPadRightPt,
            OffsetXPt = src.OffsetXPt,
            OffsetYPt = src.OffsetYPt,
            ZOrder = src.ZOrder,
            AltText = src.AltText
        };

        /// <summary>
        /// Создаёт глубокую копию ParagraphBlock для внутреннего буфера обмена.
        /// При полном выделении (from==0, to==длина текста) копирует все chunks/runs с форматированием.
        /// При частичном выделении копирует только Properties и срезанный plain-text.
        /// </summary>
        private static ParagraphBlock CloneParagraphBlock(ParagraphBlock src, int from, int to)
        {
            var dst = new ParagraphBlock();
            dst.Properties = src.Properties.Clone();
            dst.ListProperties = src.ListProperties?.Clone();

            string fullText = src.GetPlainText();
            from = Math.Max(0, Math.Min(from, fullText.Length));
            to = Math.Max(from, Math.Min(to, fullText.Length));
            bool isFull = from == 0 && to == fullText.Length;

            if (isFull)
            {
                dst.Chunks.Clear();
                foreach (var chunk in src.Chunks)
                {
                    var dstChunk = new TextChunk();
                    foreach (var run in chunk.Runs)
                        dstChunk.Runs.Add(run.Clone());
                    dst.Chunks.Add(dstChunk);
                }
                if (dst.Chunks.Count == 0)
                    dst.Chunks.Add(new TextChunk());
            }
            else
            {
                dst.SetPlainText(fullText[from..to]);
            }

            return dst;
        }

        /// <summary>
        /// Применяет сохранённый ClipboardBlock к только что созданному ParagraphViewModel:
        /// восстанавливает Properties (отступы, выравнивание, стиль) и runs (форматирование текста).
        /// </summary>
        private static void ApplyClipboardParagraph(ParagraphViewModel nv, ClipboardBlock block)
        {
            if (block.Block != null)
            {
                nv.Model.Properties = block.Block.Properties.Clone();
                nv.Model.ListProperties = block.Block.ListProperties?.Clone();

                nv.Model.Chunks.Clear();
                foreach (var chunk in block.Block.Chunks)
                {
                    var c = new TextChunk();
                    foreach (var run in chunk.Runs)
                        c.Runs.Add(run.Clone());
                    nv.Model.Chunks.Add(c);
                }
                if (nv.Model.Chunks.Count == 0)
                    nv.Model.Chunks.Add(new TextChunk());
                nv.Model.InvalidateAllChunks();

                // Синхронизируем кеш PlainText в VM без перезаписи chunks.
                nv.RefreshPlainTextFromModel();
            }
            else
            {
                nv.PlainText = block.Text ?? "";
            }
        }

        // Вставляет скопированный блок ячеек в таблицу под кареткой «сетка в сетку».
        // Операционной командой PasteCellsCommand (обратимо, без снапшота документа).
        // Возвращает false если вставку выполнить нельзя (нет ячейки/стека/таблицы).
        private bool TryPasteCellsIntoTable(TableBlock copied)
        {
            if (TextUndoStack is null || DocVm is null) return false;

            var info = GetCurrentCell();
            if (info is null) return false;

            var table = info.Table;
            var anchorCell = info.Cell;
            int row0 = anchorCell.Row;
            int col0 = anchorCell.Column;

            // Источник: относительные координаты скопированных ячеек + параграфы + фон.
            var source = new List<(int r, int c, List<ParagraphBlock> paras, string? bg)>();
            foreach (var cell in copied.Cells)
                source.Add((cell.Row, cell.Column, cell.Paragraphs, cell.BackgroundColor));
            if (source.Count == 0) return false;

            var cmd = new Writersword.Modules.TextEditor.Commands.PasteCellsCommand(
                table, row0, col0, source);

            cmd.AfterChange = () =>
            {
                InvalidateCellLayoutCaches();
                _cellVmCache.Clear();
                RebuildLayouts();

                // Каретку — в якорную ячейку (её первый параграф).
                for (int i = 0; i < _layouts.Count; i++)
                {
                    if (ReferenceEquals(_layouts[i].Cell?.Cell, anchorCell))
                    {
                        _caretPara = i;
                        _caretChar = 0;
                        break;
                    }
                }
                SnapCaretToCorrectSlice();
                NotifyCaretEnteredTableCallback();
                SyncSel();
                ResetCaret();
                InvalidateFull();
            };

            cmd.Apply(DocVm.Document);
            PushTextCommand(cmd);
            return true;
        }

        /// <summary>
        /// Забывает внутренние копии буфера, если системный буфер уже занят чем-то чужим.
        /// Копия картинки статична и живёт до следующего копирования внутри редактора,
        /// структурная копия текста — до следующего Ctrl+C; копирование в другой программе
        /// их не трогало, и вставка подменяла содержимое буфера прежней картинкой документа
        /// или прежним фрагментом текста. Признак «наше» — совпадение с тем, что редактор
        /// сам положил в буфер: у картинки байты PNG, у текста простой текст.
        /// </summary>
        private async Task DropStaleClipboardAsync()
        {
            bool checkImage = _clipboardImage is not null && _clipboardImageBytes is { Length: > 0 };
            bool checkText = !string.IsNullOrEmpty(_internalClipboardJson) && _internalClipboardPlain is not null;
            if (!checkImage && !checkText) return;

            var clip = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clip is null) return;

            if (checkImage)
            {
                byte[]? png = null;
                try { png = await clip.TryGetValueAsync(ClipboardImagePngFormat); }
                catch { png = null; }

                bool ours = png is not null && png.AsSpan().SequenceEqual(_clipboardImageBytes);
                _logger.Debug(
                    "[CLIP] Image paste source: ours={Ours}, clipboard={ClipLen} bytes, copied={CopyLen} bytes",
                    ours, png?.Length ?? -1, _clipboardImageBytes!.Length);

                if (!ours)
                {
                    _clipboardImage = null;
                    _clipboardImageBytes = null;
                }
            }

            if (checkText)
            {
                string? plain = null;
                try
                {
#pragma warning disable CS0618
                    plain = await clip.TryGetTextAsync();
#pragma warning restore CS0618
                }
                catch { plain = null; }

                if (!string.Equals(plain, _internalClipboardPlain, StringComparison.Ordinal))
                {
                    _internalClipboardJson = null;
                    _internalClipboardPlain = null;
                }
            }
        }

        private async Task PasteAsync()
        {
            if (IsEditingBlocked) return;

            // Системный буфер мог быть перезаписан другой программой — снимком экрана,
            // картинкой или текстом из браузера. Внутренние копии об этом не знают,
            // поэтому сверяем их с буфером до того, как хоть одна из них будет вставлена.
            await DropStaleClipboardAsync();

            // Внутренняя копия картинки (её положил Ctrl+C по картинке) — вставляем её.
            // Внутренний буфер сбрасывается при любом другом копировании внутри
            // редактора (текст, ячейки), так что устаревания в обычном сценарии нет.
            if (_clipboardImage is not null && DocVm is not null)
            {
                // В клетке таблицы картинка встаёт только в текст.
                //
                // Плавающая картинка привязана к странице и живёт в её координатах, а
                // клетка своего места на странице не знает: привязанная копия внутри
                // клетки оказалась бы где угодно, только не в ней, — и ложилась бы
                // поверх соседних картинок. Поэтому обтекание здесь снимается, а всё
                // остальное — рамка, цвет, наклон, отражение, обрезка, размер —
                // переносится как есть.
                //
                // Раньше вставка в клетку сюда не заходила вовсе: условие исключало
                // клетки, и картинка уходила ниже, на путь «просто картинка из
                // системного буфера». Тот знает одни байты, и все свойства терялись.
                ImageBlock template = _clipboardImage;

                if (IsInCell(_caretPara) && template.WrapMode != WrapMode.Inline)
                {
                    template = CloneImageFull(template);
                    template.WrapMode = WrapMode.Inline;
                    template.PinnedPage = 0;
                    template.OffsetXPt = 0.0;
                    template.OffsetYPt = 0.0;
                }

                // Плавающая картинка (Обтекание/За текстом/Поверх) вставляется РОВНО туда,
                // где стоит курсор: считаем смещение от верха текстовой области страницы
                // каретки. Inline-картинка встаёт в поток у каретки (смещения не нужны).
                bool floating = template.WrapMode != WrapMode.Inline;
                double offX = 20.0, offY = 20.0;
                if (floating && _caretPara >= 0 && _caretPara < _layouts.Count)
                {
                    var pl = _layouts[_caretPara];
                    if (pl.PageIndex >= 0 && pl.PageIndex < _pages.Count)
                    {
                        var pg = _pages[pl.PageIndex];
                        offY = Math.Max(0.0, pl.Ypt - (pg.Ypt + pg.PadTopPt));
                    }
                }

                // Байты картинки пишем новым файлом в ZIP ТЕКУЩЕГО проекта и переносим
                // свойства — так вставка работает и в другом проекте. Якорь потока —
                // абзац каретки (передаётся внутри InsertImageWithProps через _activeParagraph).
                ImageBlock? pasted = _clipboardImageBytes is { Length: > 0 }
                    ? DocVm.InsertImageWithProps(_clipboardImageBytes, template, offX, offY)
                    : DocVm.InsertImageClone(template);
                if (pasted is not null)
                {
                    ExitImageCropMode(apply: false);
                    _selectedImage = pasted;
                    ImageSelectionChanged?.Invoke(true);
                }
                RebuildLayouts();
                InvalidateFull();
                return;
            }

            // Картинка из буфера обмена — вставляем как изображение и выходим.
            {
                var imgClip = TopLevel.GetTopLevel(this)?.Clipboard;
                if (imgClip is not null && DocVm is not null)
                {
                    Avalonia.Media.Imaging.Bitmap? bmp = null;
                    try { bmp = await imgClip.TryGetBitmapAsync(); }
                    catch { bmp = null; }

                    if (bmp is not null)
                    {
                        byte[]? bytes = null;
                        try { bytes = EncodeClipboardBitmapPng(bmp); }
                        catch { bytes = null; }
                        finally { bmp.Dispose(); }

                        if (bytes is { Length: > 0 })
                        {
                            DocVm.InsertImageBytes(bytes, ".png");
                            return;
                        }
                    }
                }
            }

            // Каретка в ячейке, а в буфере блок таблицы (2+ ячейки) — вставляем «сетка в сетку»:
            // содержимое скопированных ячеек ложится в целевые начиная с текущей. Операционно,
            // без снапшота. Одиночная ячейка сюда не попадает — она уходит на plain-text путь ниже.
            if (IsInCell(_caretPara) && TextUndoStack != null
                && !string.IsNullOrEmpty(_internalClipboardJson) && DocVm is not null)
            {
                var cellOpts = new JsonSerializerOptions();
                var cellBlocks = JsonSerializer.Deserialize<List<ClipboardBlock>>(_internalClipboardJson, cellOpts);
                var copiedTable = cellBlocks?
                    .FirstOrDefault(b => b.Kind == ClipboardBlockKind.Table && b.Table != null)?.Table;
                if (copiedTable != null && copiedTable.Cells.Count >= 2
                    && TryPasteCellsIntoTable(copiedTable))
                    return;
            }

            // Внутренний буфер используется только когда есть таблицы.
            // Вставка только параграфов идёт через plain-text путь — он проверен и работает.
            if (!string.IsNullOrEmpty(_internalClipboardJson) && DocVm is not null && !IsInCell(_caretPara))
            {
                var opts = new JsonSerializerOptions();
                var blocks = JsonSerializer.Deserialize<List<ClipboardBlock>>(_internalClipboardJson, opts);
                bool hasTable = blocks?.Any(b => b.Kind == ClipboardBlockKind.Table && b.Table != null) == true;
                _ = hasTable; // используется для совместимости с будущими расширениями

                if (blocks != null)
                {
                    BeginEdit("Paste");

                    if (_isCellRangeSelecting)
                        ClearCellRangeSelection();
                    else if (HasSel())
                        DeleteSelection();

                    var oldCts1 = _rebuildCts;
                    _rebuildCts = new System.Threading.CancellationTokenSource();
                    oldCts1.Cancel();
                    oldCts1.Dispose();

                    // Отслеживаем позицию вставки через модель (не через _layouts / _caretPara),
                    // потому что AddParagraphAfter не перестраивает _layouts между итерациями,
                    // а InsertTableBlockAfterParagraph вызывает RebuildParagraphViewModels и
                    // инвалидирует все ссылки на ParagraphViewModel.
                    ParagraphBlock? anchorBlock = GetVmAt(_caretPara)?.Model;
                    if (anchorBlock == null)
                    {
                        CommitEdit();
                        return;
                    }

                    bool isFirstBlock = true;
                    foreach (var block in blocks)
                    {
                        if (block.Kind == ClipboardBlockKind.Table && block.Table != null)
                        {
                            isFirstBlock = false;
                            foreach (var cell in block.Table.Cells)
                                cell.Id = Guid.NewGuid();
                            var postAnchor = DocVm.InsertTableBlockAfterParagraph(block.Table, anchorBlock);
                            if (postAnchor != null)
                                anchorBlock = postAnchor;
                        }
                        else if (block.Kind == ClipboardBlockKind.Image && block.Image != null)
                        {
                            // Картинка возвращается на то же место в потоке — сразу за
                            // абзацем, после которого она стояла при копировании.
                            isFirstBlock = false;
                            DocVm.InsertImageAfterBlock(block.Image, anchorBlock);
                        }
                        else if (block.Kind == ClipboardBlockKind.Paragraph)
                        {
                            var anchorVm = DocVm.Paragraphs.FirstOrDefault(v => v.Model == anchorBlock);
                            if (anchorVm == null) continue;

                            if (isFirstBlock)
                            {
                                // Первый блок: применяем стили к текущему параграфу и
                                // вставляем текст в позицию каретки (не создаём новый параграф).
                                isFirstBlock = false;
                                anchorBlock.Properties = block.Block?.Properties.Clone() ?? anchorBlock.Properties;
                                anchorBlock.ListProperties = block.Block?.ListProperties?.Clone();
                                string insertText = block.Block?.GetPlainText() ?? block.Text ?? "";
                                anchorBlock.SpliceText(_caretChar, _caretChar, insertText);
                                anchorVm.RefreshPlainTextFromModel();
                                _caretChar += insertText.Length;
                            }
                            else
                            {
                                var nv = DocVm.AddParagraphAfter(anchorVm);
                                if (nv != null)
                                {
                                    ApplyClipboardParagraph(nv, block);
                                    // Картинки в строках вставленного абзаца получают свои копии
                                    // объектов: иначе оригинал и копия делили бы одну картинку.
                                    DocVm.MaterializeInlineImages(nv.Model);
                                    anchorBlock = nv.Model;
                                }
                            }
                        }
                    }

                    CommitEdit();
                    InvalidateCellLayoutCaches();

                    var oldCts2 = _rebuildCts;
                    _rebuildCts = new System.Threading.CancellationTokenSource();
                    oldCts2.Cancel();
                    oldCts2.Dispose();

                    RebuildLayouts();

                    // Устанавливаем каретку на последний вставленный блок.
                    if (anchorBlock != null)
                    {
                        for (int li = 0; li < _layouts.Count; li++)
                        {
                            if (_layouts[li].Vm?.Model == anchorBlock)
                            {
                                _caretPara = li;
                                _caretChar = _layouts[li].Vm!.PlainText?.Length ?? 0;
                                break;
                            }
                        }
                    }

                    SnapCaretToCorrectSlice();
                    SyncSel(); ResetCaret(); InvalidateFull();
                    return;
                }
            }

            string? text = _clipboardCache;
            if (string.IsNullOrEmpty(text))
            {
                var cb = TopLevel.GetTopLevel(this)?.Clipboard;
                if (cb is null) return;
#pragma warning disable CS0618
                text = await cb.TryGetTextAsync();
#pragma warning restore CS0618
            }
            if (string.IsNullOrEmpty(text)) return;

            // Символ-заполнитель объекта из чужого текста вставлять нельзя: сам объект
            // в этот документ не переносится, и в строке осталась бы позиция-невидимка,
            // по которой ходит каретка, а показывать нечего.
            if (text.IndexOf(RunModel.ObjectPlaceholder) >= 0)
                text = text.Replace(RunModel.ObjectPlaceholder.ToString(), string.Empty);
            if (string.IsNullOrEmpty(text)) return;

            _ = PrefetchClipboardAsync();

            if (IsInCell(_caretPara))
            {
                var cellInfo = GetCurrentCell();
                if (cellInfo is null) return;

                string[] cellLines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                BeginEdit("Paste");
                if (HasSel()) { CellDeleteSelection(); RebuildAfterCellEdit(); }

                cellInfo = GetCurrentCell();
                if (cellInfo is null) { CommitEdit(); return; }

                string cur = cellInfo.ParaBlock.GetPlainText();
                int pos = Clamp(_caretChar, 0, cur.Length);
                string cellBefore = cur[..pos];
                string cellAfter = cur[pos..];

                if (cellLines.Length == 1)
                {
                    SetCellParaText(cellInfo.Cell, cellInfo.CellParaIndex, cellBefore + cellLines[0] + cellAfter);
                    _caretChar = pos + cellLines[0].Length;
                    CommitEdit();
                    RebuildAfterCellEdit();
                }
                else
                {
                    SetCellParaText(cellInfo.Cell, cellInfo.CellParaIndex, cellBefore + cellLines[0]);
                    int insertIdx = cellInfo.CellParaIndex + 1;

                    for (int li = 1; li < cellLines.Length - 1; li++)
                    {
                        var np = new ParagraphBlock();
                        np.SetPlainText(cellLines[li]);
                        cellInfo.Cell.Paragraphs.Insert(insertIdx++, np);
                    }

                    var lastPara = new ParagraphBlock();
                    lastPara.SetPlainText(cellLines[^1] + cellAfter);
                    cellInfo.Cell.Paragraphs.Insert(insertIdx, lastPara);
                    _caretChar = cellLines[^1].Length;

                    CommitEdit();
                    RebuildAfterCellEdit(lastPara);
                }
                return;
            }

            BeginEdit("Paste");
            DeleteSelection();

            // Если удалили таблицы — нужен rebuild перед вставкой текста.
            if (_tableSelections.Count == 0 && DocVm is not null)
            {
                InvalidateCellLayoutCaches();
                DocVm.RebuildParagraphViewModelsPublic();
                RebuildLayouts();
                _caretPara = Clamp(_caretPara, 0, Math.Max(0, _layouts.Count - 1));
            }

            string[] lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            var pvm = GetVmAt(_caretPara);
            if (pvm is null) return;

            string cur2 = pvm.PlainText ?? "";
            int pos2 = Clamp(_caretChar, 0, cur2.Length);
            string before = cur2[..pos2];
            string after = cur2[pos2..];

            if (lines.Length == 1)
            {
                pvm.Model.SpliceText(pos2, pos2, lines[0]);
                pvm.RefreshPlainTextFromModel();
                _caretChar = pos2 + lines[0].Length;
            }
            else
            {
                // Первая строка: вставляем в текущий параграф, удаляем хвост после каретки.
                pvm.Model.SpliceText(pos2, cur2.Length, lines[0]);
                pvm.RefreshPlainTextFromModel();
                var prev = pvm;
                for (int i = 1; i < lines.Length - 1; i++)
                {
                    var nv = DocVm?.AddParagraphAfter(prev);
                    if (nv is not null) { nv.PlainText = lines[i]; prev = nv; }
                }
                var last = DocVm?.AddParagraphAfter(prev);
                if (last is not null)
                {
                    // Последняя строка + остаток исходного параграфа.
                    last.Model.SpliceText(0, 0, lines[^1] + after);
                    last.RefreshPlainTextFromModel();
                    _caretPara = DocVm!.Paragraphs.IndexOf(last);
                    _caretChar = lines[^1].Length;
                }
            }

            CommitEdit();
            SnapCaretToCorrectSlice();
            UpdatePreferredX();
            SyncSel();
            ResetCaret();
        }

        // ── HitTest ───────────────────────────────────────────────────────
        /// <summary>
        /// Единый HitTest для всех элементов _layouts (параграфы и ячейки таблиц).
        /// Использует pl.AbsXPt — абсолютный X начала текстовой зоны.
        /// </summary>
        /// <summary>
        /// Кодирует картинку из буфера обмена в PNG.
        ///
        /// Пиксели забираются напрямую и кодируются Skia, а не через Bitmap.Save:
        /// та перегрузка объявлена устаревшей, а её замена принимает параметры
        /// кодировщика, которых здесь всё равно нет — нужен обычный PNG без настроек.
        /// Skia в модуле и так рисует всё остальное, так что новой зависимости нет.
        ///
        /// Формат пикселей задан явно (BGRA, прямая альфа): именно в нём Avalonia
        /// отдаёт содержимое буфера, и угадывание здесь давало бы перевёрнутые
        /// каналы у вставленных скриншотов.
        /// </summary>
        private static byte[]? EncodeClipboardBitmapPng(Avalonia.Media.Imaging.Bitmap bitmap)
        {
            var size = bitmap.PixelSize;
            if (size.Width <= 0 || size.Height <= 0) return null;

            var info = new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            using var skBitmap = new SKBitmap(info);

            var pixels = skBitmap.GetPixels();
            if (pixels == IntPtr.Zero) return null;

            bitmap.CopyPixels(
                new Avalonia.PixelRect(0, 0, size.Width, size.Height),
                pixels,
                info.BytesSize,
                info.RowBytes);

            using var image = SKImage.FromBitmap(skBitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data?.ToArray();
        }

        private (int parIdx, int charIdx) HitTest(Point ptLogPx)
        {
            List<ParaLayout> layouts;
            List<TableEntry> tables;
            lock (_renderLock) { layouts = _layouts; tables = _tables; }

            if (layouts.Count == 0) return (0, 0);

            double zoom = Zoom;
            float xPt = (float)(ptLogPx.X / zoom * PxToPt);
            float yPt = (float)(ptLogPx.Y / zoom * PxToPt);
            // Страницы рядом: переводим точку указателя в логические координаты раскладки.
            (xPt, yPt) = VisualToLogicalPt(xPt, yPt);

            // ── Фаза 0 (до всего): клик правее таблицы ────────────────────
            // Каретка обязана встать в якорь сбоку-снизу от этой таблицы, где можно печатать.
            // Решается по чистой геометрии — раньше это жило в ветке якорей ниже и работало
            // лишь когда поиск сам выбирал ячейку нужной таблицы; на практике клик правее
            // таблицы просто вставал на текст в ближайшей клетке.
            for (int ti = 0; ti < tables.Count; ti++)
            {
                var te = tables[ti];
                if (xPt <= te.XPt + te.Layout.TotalWidthPt) continue;

                int rowTo = te.RowTo < 0 ? te.Layout.Rows.Count : te.RowTo;
                float sliceH = 0f;
                for (int ri = te.RowFrom; ri < rowTo && ri < te.Layout.Rows.Count; ri++)
                    sliceH += te.Layout.Rows[ri].HeightPt;

                if (yPt < te.Ypt || yPt > te.Ypt + sliceH) continue;

                var anchorBlock = DocVm?.GetEmptyAnchorAfterTable(te.Table);
                if (anchorBlock is null) break;

                for (int i = 0; i < layouts.Count; i++)
                {
                    if (!ReferenceEquals(layouts[i].Vm.Model, anchorBlock)) continue;
                    _caretLineHint = -1;
                    return (i, 0);
                }
                break;
            }

            // ── Фаза 0: приоритет clip-прямоугольника ячейки ──────────────
            // Клик в любой точке внутри clip-области ячейки (включая пустое пространство
            // ниже текста, padding, область между параграфами) должен попасть в эту ячейку,
            // а не в соседнюю с yDist=0. Ищем ячейку по clip-прямоугольнику, затем внутри
            // неё — ближайший параграф по Y.
            int clipBestIdx = -1;
            float clipBestYDist = float.MaxValue;

            for (int i = 0; i < layouts.Count; i++)
            {
                var pl = layouts[i];
                if (pl.Cell == null) continue;

                var c = pl.Cell;
                if (xPt < c.ClipX || xPt > c.ClipX + c.ClipW) continue;
                if (yPt < c.ClipY || yPt > c.ClipY + c.ClipH) continue;

                // Клик внутри clip этой ячейки — вычисляем расстояние до параграфа.
                float top = pl.Ypt;
                float bot = pl.Ypt + pl.HeightPt;
                float yDist = yPt < top ? top - yPt : yPt > bot ? yPt - bot : 0f;

                if (yDist < clipBestYDist)
                {
                    clipBestYDist = yDist;
                    clipBestIdx = i;
                }
            }

            // ── Фаза 0.5: геометрия таблицы ───────────────────────────────
            // Клик внутри таблицы, но мимо clip-прямоугольников параграфов ячеек
            // (например пустая ячейка на странице продолжения): определяем ячейку
            // геометрически по строкам и колонкам слайса и берём ближайший по Y
            // параграф именно этой ячейки — иначе двухпроходной поиск ниже уводит
            // каретку к ближайшему тексту чужой ячейки.
            if (clipBestIdx < 0)
            {
                var geo = HitTestTableCellGeometric(xPt, yPt);
                if (geo.HasValue)
                {
                    float geoBestDist = float.MaxValue;
                    for (int i = 0; i < layouts.Count; i++)
                    {
                        var pl = layouts[i];
                        var c = pl.Cell;
                        if (c == null || c.Table != geo.Value.table) continue;
                        if (c.Cell.Row != geo.Value.row || c.Cell.Column != geo.Value.col) continue;

                        float top = pl.Ypt;
                        float bot = pl.Ypt + pl.HeightPt;
                        float yDist = yPt < top ? top - yPt : yPt > bot ? yPt - bot : 0f;
                        if (yDist < geoBestDist)
                        {
                            geoBestDist = yDist;
                            clipBestIdx = i;
                        }
                    }
                }
            }

            int bestIdx;

            if (clipBestIdx >= 0)
            {
                bestIdx = clipBestIdx;
            }
            else
            {
                // ── Двухпроходной поиск (клик вне любого clip ячейки) ─────────
                // Проход 1: находим минимальное Y-расстояние.
                float bestYDist = float.MaxValue;
                for (int i = 0; i < layouts.Count; i++)
                {
                    var pl = layouts[i];
                    float top = pl.Ypt;
                    float bot = pl.Ypt + pl.HeightPt;
                    float dist = yPt < top ? top - yPt : yPt > bot ? yPt - bot : 0f;
                    if (dist < bestYDist) bestYDist = dist;
                }

                // Проход 2: среди кандидатов с минимальным Y выбираем ближайший по X.
                bestIdx = 0;
                float bestXDist = float.MaxValue;

                for (int i = 0; i < layouts.Count; i++)
                {
                    var pl = layouts[i];
                    float top = pl.Ypt;
                    float bot = pl.Ypt + pl.HeightPt;
                    float yDist = yPt < top ? top - yPt : yPt > bot ? yPt - bot : 0f;

                    if (Math.Abs(yDist - bestYDist) > 0.5f) continue;

                    float xLeft, xRight;
                    if (pl.Cell != null)
                    {
                        xLeft = pl.Cell.ClipX;
                        xRight = pl.Cell.ClipX + pl.Cell.ClipW;
                    }
                    else
                    {
                        // Для обычного параграфа правая граница — правый край текстовой области
                        // страницы (не ширина текстового содержимого).
                        // Иначе клик правее короткого параграфа давал xDist > 0,
                        // а ячейка таблицы с широким ClipW выигрывала сравнение (xDist = 0).
                        xLeft = pl.AbsXPt;
                        var selLayout = pl.Layout ?? GetOrBuildLayout(pl.Vm, (float)(_canvasWidth * PxToPt));
                        xRight = pl.AbsXPt + (selLayout.TextAreaWidthPt > 0
                            ? selLayout.TextAreaWidthPt
                            : (selLayout.Lines.Count > 0
                                ? selLayout.Lines.Max(l => l.Segments.Count > 0
                                    ? l.Segments[^1].X + l.Segments[^1].Width : 0f)
                                : 100f));
                    }

                    float xDist = xPt < xLeft ? xLeft - xPt
                                 : xPt > xRight ? xPt - xRight
                                 : 0f;

                    if (xDist < bestXDist)
                    {
                        bestXDist = xDist;
                        bestIdx = i;
                    }
                }
            }

            // ── Клик выше таблицы: каретка идёт в ближайшую строку НАД точкой ──
            // Верх страницы может быть занят обтекаемой картинкой: своей каретки у
            // неё нет, и клик по ней или по пустому месту рядом не попадает ни в
            // одну строку. Ближайшей по Y оказывалась первая клетка таблицы, и
            // каретка уходила внутрь таблицы вместо абзаца над ней — сдвинуть
            // таблицу вниз было нечем.
            //
            // Работает только для точки ВНЕ любой таблицы: клик в верхнее поле
            // ячейки — это клик в ячейку, и уводить из неё каретку нельзя.
            if (layouts[bestIdx].Cell != null && yPt < layouts[bestIdx].Ypt)
            {
                bool insideAnyTable = false;
                foreach (var te in tables)
                {
                    int rowTo = te.RowTo < 0 ? te.Layout.Rows.Count : te.RowTo;
                    float sliceH = 0f;
                    for (int ri = te.RowFrom; ri < rowTo && ri < te.Layout.Rows.Count; ri++)
                        sliceH += te.Layout.Rows[ri].HeightPt;

                    if (yPt < te.Ypt || yPt > te.Ypt + sliceH) continue;
                    if (xPt < te.XPt || xPt > te.XPt + te.Layout.TotalWidthPt) continue;

                    insideAnyTable = true;
                    break;
                }

                if (!insideAnyTable)
                {
                    // Ближайший абзац вне таблиц выше точки: между кликом и им может
                    // стоять сколько угодно слайсов таблиц, поэтому идём до первого
                    // не-ячеечного, а не смотрим только предыдущий.
                    for (int i = bestIdx - 1; i >= 0; i--)
                    {
                        if (layouts[i].Cell != null) continue;
                        _caretLineHint = -1;
                        return (i, layouts[i].Vm.PlainText?.Length ?? 0);
                    }
                }
            }

            var best = layouts[bestIdx];

            // ── Якоря таблицы: клик снаружи таблицы по X ─────────────────
            if (best.Cell != null)
            {
                foreach (var te in tables)
                {
                    if (te.Table != best.Cell.Table) continue;

                    float tableLeft = te.XPt;
                    float tableRight = te.XPt + te.Layout.TotalWidthPt;

                    if (xPt >= tableLeft && xPt <= tableRight) break;

                    bool clickedLeft = xPt < tableLeft;

                    // Ищем именно абзац-якорь, а не «первый блок не из этой таблицы».
                    // Прежнее условие пропускало только ячейки ЭТОЙ таблицы, поэтому при
                    // двух таблицах подряд ближайшим кандидатом оказывалась ячейка соседней,
                    // и клик сбоку от таблицы отправлял каретку в её первую клетку.
                    // Ячейка любой таблицы якорем быть не может: якорь — блок вне таблиц.
                    int anchorIdx = -1;
                    if (clickedLeft)
                    {
                        for (int i = bestIdx - 1; i >= 0; i--)
                        {
                            // Слайсы этой же таблицы пропускаем — идём к её краю.
                            if (layouts[i].Cell?.Table == te.Table) continue;
                            // Дошли до края: якорь есть, только если сразу за ним стоит
                            // абзац. Ячейка соседней таблицы якорем не является, и уводить
                            // в неё каретку нельзя — раньше именно так клик сбоку и
                            // отправлял её в первую клетку следующей таблицы.
                            if (layouts[i].Cell == null) anchorIdx = i;
                            break;
                        }
                    }
                    else
                    {
                        for (int i = bestIdx + 1; i < layouts.Count; i++)
                        {
                            if (layouts[i].Cell?.Table == te.Table) continue;
                            if (layouts[i].Cell == null) anchorIdx = i;
                            break;
                        }
                    }

                    if (anchorIdx >= 0)
                    {
                        var anchor = layouts[anchorIdx];
                        int charIdx = clickedLeft
                            ? (anchor.Vm.PlainText?.Length ?? 0)
                            : 0;
                        _caretLineHint = -1;
                        return (anchorIdx, charIdx);
                    }

                    // Якоря нет: либо документ кончился, либо за таблицей сразу другая.
                    // Второй случай уже отмечен в фазе 0 выше — там он ловится по геометрии,
                    // независимо от того, какую ячейку выбрал поиск.
                    break;
                }
            }

            float padXPt = best.AbsXPt;

            var hitLayout = best.Layout ?? GetOrBuildLayout(best.Vm, (float)(_canvasWidth * PxToPt));
            float localX = xPt - padXPt - hitLayout.LeftIndentPt;
            // localY: переводим screen-Y в координаты лейаута.
            // RenderParagraphLines рисует строку i в точке absY + (lines[i].Y - lines[lineFrom].Y),
            // поэтому: layout_Y = (screenY - pl.Ypt) + lines[lineFrom].Y.
            float hitYBase = best.LineFrom < hitLayout.Lines.Count
                ? hitLayout.Lines[best.LineFrom].Y : 0f;
            float localY = yPt - best.Ypt + hitYBase;

            if (best.LineFrom < hitLayout.Lines.Count)
            {
                float fy = hitLayout.Lines[best.LineFrom].Y;
                int lto = best.LineTo > 0 && best.LineTo <= hitLayout.Lines.Count
                    ? best.LineTo : hitLayout.Lines.Count;
                float ly = hitLayout.Lines[lto - 1].Y + hitLayout.Lines[lto - 1].Height;
                localY = Clamp(localY, fy + 0.1f, ly - 0.1f);
            }

            // Определяем целевую строку по localY.
            // Если клик правее текста строки — возвращаем конец строки напрямую,
            // не вызывая HitTestPoint: тот пересчитывает строку по localY внутри
            // и при hitX > TextWidth всё равно уходит на начало следующей.
            _caretLineHint = -1;
            float hitX = localX;
            for (int li = best.LineFrom; li < Math.Min(best.LineTo, hitLayout.Lines.Count); li++)
            {
                var ln = hitLayout.Lines[li];

                // Первую строку занял номер списка — текста в ней нет, и подсказкой она быть
                // не может: каретка рисуется по подсказанной строке и встала бы у номера.
                // Клик по ней разбирает HitTestPoint ниже и отдаёт началу первой текстовой.
                if (hitLayout.MarkerOwnsFirstLine && li == 0) continue;

                if (localY <= ln.Y + ln.Height)
                {
                    _caretLineHint = li;
                    // Приводим X клика к координатам сегментов: убираем общий сдвиг выравнивания
                    // (центр/право + абзацный отступ первой строки), который рендер добавил к
                    // тексту. Иначе по центру/правому/первой строке клик попадал не в тот символ.
                    hitX = localX - LineAlignShift(hitLayout, li);
                    // Для выравнивания по ширине убираем растяжку межсловных пробелов: текст
                    // растянут вправо, и без этого клик попадал бы левее реального символа.
                    hitX = UnstretchJustifyX(hitLayout, li, hitX);
                    if (hitX >= ln.TextWidth)
                        return (bestIdx, ln.LastCharIndex + 1);
                    break;
                }
            }

            var hit = hitLayout.HitTestPoint(hitX, localY);

            return (bestIdx, hit.CharIndex);
        }

        // ── Scroll to caret ───────────────────────────────────────────────
        private void ScrollToCaret()
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_parentScrollViewer is null) return;
                if (_caretPara < 0 || _caretPara >= _layouts.Count) return;

                double zoom = Zoom;
                var pl = _layouts[_caretPara];

                // Страницы рядом: скроллим к визуальной позиции каретки (её страница
                // может стоять во второй колонке и другом ряду).
                List<PageRect> pagesForCaret;
                lock (_renderLock) { pagesForCaret = _pages; }
                var (_, caretDyPt) = PageVisualDelta(pl.PageIndex, pagesForCaret);

                double caretYPx;
                double caretHPx;

                if (string.IsNullOrEmpty(pl.Vm.PlainText))
                {
                    // Пустой параграф — например созданный после Enter.
                    // Используем Ypt параграфа как позицию каретки.
                    caretYPx = (pl.Ypt + caretDyPt) * PtToPx * zoom;
                    caretHPx = FallbackLinePt * PtToPx * zoom;
                }
                else
                {
                    int pos = Clamp(_caretChar, 0, pl.Vm.PlainText?.Length ?? 0);
                    var htLayout = pl.Layout ?? GetOrBuildLayout(pl.Vm, (float)(_canvasWidth * PxToPt));
                    var caret = htLayout.HitTestPosition(pos);

                    float yBase = pl.LineFrom < htLayout.Lines.Count
                        ? htLayout.Lines[pl.LineFrom].Y : 0f;

                    caretYPx = (pl.Ypt + (caret.Y - yBase) + caretDyPt) * PtToPx * zoom;
                    caretHPx = caret.Height * PtToPx * zoom;
                }

                double scrollY = _parentScrollViewer.Offset.Y;
                double viewportH = _parentScrollViewer.Viewport.Height;

                bool caretVisible = caretYPx >= scrollY && caretYPx + caretHPx <= scrollY + viewportH;
                if (caretVisible) return;

                double margin = caretHPx * 2.0;
                double newOffsetY;
                if (caretYPx < scrollY)
                    newOffsetY = caretYPx - margin;
                else
                    newOffsetY = caretYPx + caretHPx + margin - viewportH;

                newOffsetY = Math.Max(0, newOffsetY);

                SmoothScrollTo(newOffsetY);

            }, DispatcherPriority.Render);
        }

        // Запускает плавную анимацию скролла к targetOffsetY за ScrollAnimDurationMs мс
        // с ease-out кривой (кубическая: f(t) = 1 - (1-t)^3).
        private void SmoothScrollTo(double targetOffsetY)
        {
            if (_parentScrollViewer is null) return;

            double currentY = _parentScrollViewer.Offset.Y;
            if (Math.Abs(targetOffsetY - currentY) < 1.0)
                return;

            // Если анимация уже идёт — продолжаем от текущей позиции к новой цели.
            _scrollAnimFrom = currentY;
            _scrollAnimTo = targetOffsetY;
            _scrollAnimElapsedMs = 0.0;

            if (_scrollAnimTimer is null)
            {
                _scrollAnimTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(ScrollAnimTickMs)
                };
                _scrollAnimTimer.Tick += OnScrollAnimTick;
            }

            _scrollAnimTimer.Stop();
            _scrollAnimTimer.Start();
        }

        private void OnScrollAnimTick(object? sender, EventArgs e)
        {
            if (_parentScrollViewer is null) { _scrollAnimTimer?.Stop(); return; }

            _scrollAnimElapsedMs += ScrollAnimTickMs;
            double t = Math.Min(_scrollAnimElapsedMs / ScrollAnimDurationMs, 1.0);

            // Ease-out cubic: f(t) = 1 - (1-t)^3
            double eased = 1.0 - Math.Pow(1.0 - t, 3.0);
            double newY = _scrollAnimFrom + (_scrollAnimTo - _scrollAnimFrom) * eased;

            _parentScrollViewer.Offset = new Avalonia.Vector(
                _parentScrollViewer.Offset.X, newY);

            if (t >= 1.0)
                _scrollAnimTimer!.Stop();
        }

        /// <summary>
        /// Центрирует каретку вертикально в видимой области viewport.
        /// Используется при навигации с большим прыжком (вставка разрыва страницы и т.п.)
        /// чтобы каретка не оказывалась на самом краю экрана.
        /// </summary>
        private void ScrollToCenterCaret()
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_parentScrollViewer is null) return;
                if (_caretPara < 0 || _caretPara >= _layouts.Count) return;

                double zoom = Zoom;
                var pl = _layouts[_caretPara];

                double caretYPx;
                double caretHPx;

                if (string.IsNullOrEmpty(pl.Vm.PlainText))
                {
                    caretYPx = pl.Ypt * PtToPx * zoom;
                    caretHPx = FallbackLinePt * PtToPx * zoom;
                }
                else
                {
                    int pos = Clamp(_caretChar, 0, pl.Vm.PlainText?.Length ?? 0);
                    var htLayout = pl.Layout ?? GetOrBuildLayout(pl.Vm, (float)(_canvasWidth * PxToPt));
                    var caret = htLayout.HitTestPosition(pos);
                    caretYPx = (pl.Ypt + caret.Y) * PtToPx * zoom;
                    caretHPx = caret.Height * PtToPx * zoom;
                }

                double viewportH = _parentScrollViewer.Viewport.Height;
                double targetOffsetY = caretYPx + caretHPx / 2.0 - viewportH / 2.0;
                targetOffsetY = Math.Max(0, targetOffsetY);

                SmoothScrollTo(targetOffsetY);

            }, DispatcherPriority.Render);
        }

        // ── Helpers ───────────────────────────────────────────────────────
        private ParagraphViewModel? GetVmAt(int idx) =>
            idx >= 0 && idx < _layouts.Count ? _layouts[idx].Vm : null;

        private ParagraphViewModel? GetVmAt(int idx, List<ParaLayout> layouts) =>
            idx >= 0 && idx < layouts.Count ? layouts[idx].Vm : null;

        private SKTextLayout? GetLayoutAt(int idx) =>
            idx >= 0 && idx < _layouts.Count ? _layouts[idx].Layout : null;

        private int FindFirstSliceForDocVmParagraph(int paragraphIndex)
        {
            if (paragraphIndex < 0 || DocVm is null) return 0;
            if (paragraphIndex >= DocVm.Paragraphs.Count) return _layouts.Count - 1;
            var target = DocVm.Paragraphs[paragraphIndex];
            int first = -1;
            for (int i = 0; i < _layouts.Count; i++)
            {
                if (_layouts[i].Vm != target) continue;
                if (first < 0) first = i;

                // Для ячеек таблицы выбираем слайс, в котором каретка (по символу _caretChar)
                // попадает в видимый clip-прямоугольник. Это предотвращает невидимую каретку
                // когда параграф ячейки разорван на несколько страниц.
                var pl = _layouts[i];
                if (pl.Cell != null)
                {
                    var cellLayout = pl.Layout ?? GetOrBuildLayout(pl.Vm, (float)(_canvasWidth * PxToPt));
                    var caret = cellLayout.HitTestPosition(Clamp(_caretChar, 0, pl.Vm.PlainText?.Length ?? 0));
                    float caretAbsY = pl.Ypt + caret.Y;
                    float clipTop = pl.Cell.ClipY;
                    float clipBot = pl.Cell.ClipY + pl.Cell.ClipH;
                    if (caretAbsY >= clipTop - 0.5f && caretAbsY < clipBot + 0.5f)
                        return i;
                }
            }
            return first >= 0 ? first : 0;
        }

        // Снимает залипший флаг перехода, если канвас реально находится в живом визуальном
        // дереве. При выключении соседнего модуля dock переприцепляет канвас редактора
        // (detach -> reattach), и порядок событий Attached/Detached может закончиться на
        // Detached — тогда _isTransitioning остаётся true, и RenderWithSKCanvas глушит
        // отрисовку навсегда: модель печатает и счётчик символов растёт, но кадры не выходят
        // и каретка стоит на месте. Наличие TopLevel означает, что переход уже завершён,
        // поэтому флаг сбрасываем и форсируем полный кадр.
        private void RecoverFromStuckTransition()
        {
            if (_isTransitioning && TopLevel.GetTopLevel(this) is not null)
            {
                _isTransitioning = false;
                _caretOnlyRedraw = false;
                InvalidateVisual();
            }
        }

        private void InvalidateFull()
        {
            if (_isTransitioning && TopLevel.GetTopLevel(this) is not null)
                _isTransitioning = false;
            RefreshImagesInTextSelection();
            _contentDirty = true;
            _caretOnlyRedraw = false;
            InvalidateVisual();
        }

        /// <summary>
        /// Пересчитывает набор плавающих картинок, попавших в текстовое выделение:
        /// это те, что стоят в потоке между первым и последним выделенными абзацами.
        /// Считается здесь, на UI-потоке: рендер только читает готовый набор.
        ///
        /// Картинка «в тексте» сюда не входит — она обычный символ абзаца и попадает
        /// в выделение вместе с текстом.
        /// </summary>
        private void RefreshImagesInTextSelection()
        {
            // Выделение внутри одного абзаца картинок между абзацами не захватывает —
            // самый частый случай (набор текста) не платит ни за что.
            if (DocVm is null || !HasSel() || _selStartPara == _selEndPara)
            {
                if (_imagesInTextSelection.Count > 0)
                    _imagesInTextSelection = new HashSet<ImageBlock>();
                return;
            }

            var (sp, _, ep, _) = NormalizeSelection();
            var startModel = GetVmAt(sp)?.Model;
            var endModel = GetVmAt(ep)?.Model;
            if (startModel is null || endModel is null)
            {
                if (_imagesInTextSelection.Count > 0)
                    _imagesInTextSelection = new HashSet<ImageBlock>();
                return;
            }

            var found = new HashSet<ImageBlock>();
            foreach (var section in DocVm.Document.Sections)
            {
                // Абзацы ячеек таблиц в Blocks не лежат — там IndexOf вернёт -1,
                // и раздел просто пропускается.
                int si = section.Blocks.IndexOf(startModel);
                int ei = section.Blocks.IndexOf(endModel);
                if (si < 0 || ei < 0) continue;
                if (ei < si) (si, ei) = (ei, si);

                for (int i = si + 1; i < ei; i++)
                    if (section.Blocks[i] is ImageBlock image)
                        found.Add(image);
            }

            _imagesInTextSelection = found;
        }

        /// <summary>Картинки, попавшие в текущее текстовое выделение (для копирования и удаления).</summary>
        private List<ImageBlock> ImagesInTextSelection()
            => _imagesInTextSelection.Count == 0
                ? new List<ImageBlock>()
                : new List<ImageBlock>(_imagesInTextSelection);

        private void ResetCaret()
        {
            _caretVisible = true;
            _caretTimer.Stop();
            _caretTimer.Start();
            ScrollToCaret();
            NotifyInputMethod();

            // Уведомляем вертикальную линейку о странице каретки
            if (_caretPara >= 0 && _caretPara < _layouts.Count)
                CaretPageChanged?.Invoke(_layouts[_caretPara].PageIndex);
        }

        // Сброс каретки без прокрутки — используется при клике мышью.
        // Каретка ставится в позицию клика (может быть вне viewport),
        // скролл произойдёт только когда пользователь начнёт печатать.
        private void ResetCaretNoScroll()
        {
            _caretVisible = true;
            _caretTimer.Stop();
            _caretTimer.Start();
            NotifyInputMethod();

            if (_caretPara >= 0 && _caretPara < _layouts.Count)
                CaretPageChanged?.Invoke(_layouts[_caretPara].PageIndex);
        }

        private static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
        private static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;

        /// <summary>
        /// Возвращает true если block — пустой параграф расположенный сразу после TableBlock.
        /// Такие параграфы защищены от слияния через Delete.
        /// </summary>
        private bool IsBlockAfterTable(BlockModel block)
        {
            if (DocVm is null) return false;
            var blocks = DocVm.Document.Sections[0].Blocks;
            int idx = blocks.IndexOf(block);
            return idx > 0 && blocks[idx - 1] is TableBlock;
        }

        /// <summary>
        /// Возвращает true если block — параграф, расположенный сразу перед TableBlock.
        /// Delete на нём ничего не делает: присоединять таблицу к параграфу нельзя.
        /// От удаления такой параграф защищён только когда он единственная позиция каретки
        /// выше таблицы, то есть параграфа над ним нет — эту проверку делает вызывающий.
        /// </summary>
        private bool IsBlockBeforeTable(BlockModel block)
        {
            if (DocVm is null) return false;
            var blocks = DocVm.Document.Sections[0].Blocks;
            int idx = blocks.IndexOf(block);
            return idx >= 0 && idx + 1 < blocks.Count && blocks[idx + 1] is TableBlock;
        }

        /// <summary>
        /// Возвращает true если block — пустой параграф-якорь расположенный сразу после BreakBlock
        /// с типом Page. Такие параграфы являются единственным способом выделить и удалить разрыв
        /// страницы: Backspace в начале якоря удаляет и якорь, и сам BreakBlock.
        /// </summary>
        private bool IsBreakAnchor(BlockModel block)
        {
            if (DocVm is null) return false;
            var blocks = DocVm.Document.Sections[0].Blocks;
            int idx = blocks.IndexOf(block);
            return idx > 0 && blocks[idx - 1] is BreakBlock { BreakType: BreakType.Page };
        }

        /// <summary>
        /// Приводит контекст ячейки в соответствие текущему положению каретки: активная
        /// таблица, делегаты табличных операций и режим линейки.
        ///
        /// UpdateCellContext звали только мышь, Tab/Shift+Tab и Escape. Клавиатурная
        /// навигация стрелками и Home/End каретку из таблицы выводит, но контекст не
        /// обновляла: линейка оставалась в табличном режиме и продолжала держать маркеры
        /// абзаца в координатах покинутой ячейки — обычный список за её пределы не
        /// вытаскивался. Переход стрелками между ячейками не двигал границы активной ячейки
        /// по той же причине.
        ///
        /// Метод идемпотентен: пока каретка в той же ячейке (или вне таблиц), он ничего
        /// не делает, поэтому его безопасно звать на каждое перемещение каретки.
        /// </summary>
        private void SyncCellContextToCaret()
        {
            bool nowInCell = IsInCell(_caretPara);
            bool wasInCell = _activeTableBlock is not null;

            if (!wasInCell && !nowInCell) return;

            if (nowInCell && wasInCell)
            {
                var cell = _layouts[_caretPara].Cell!;
                if (ReferenceEquals(cell.Table, _activeTableBlock)
                    && cell.Cell.Row == _activeCellRow
                    && cell.Cell.Column == _activeCellCol)
                    return;
            }

            UpdateCellContext(wasInCell, nowInCell);
        }

        /// <summary>
        /// Снимает с раскладки фактическую геометрию абзаца под кареткой и отдаёт её линейке.
        /// Единственный источник положения стрелок: все правила — ограничители первой строки,
        /// перенос текста списка на вторую строку, поля и рамка ячейки — уже применены здесь,
        /// и повторять их расчётом по значениям модели не нужно.
        /// </summary>
        private void PublishRulerGeometry()
        {
            if (RulerGeometryChanged is null) return;
            if (_caretPara < 0 || _caretPara >= _layouts.Count) return;

            var pl = _layouts[_caretPara];
            var layout = pl.Layout;
            if (layout is null) return;

            // Начало текстовой области страницы: от него линейка отсчитывает всё остальное.
            float pageTextXPt = 0f;
            if (_pages.Count > 0 && pl.PageIndex >= 0 && pl.PageIndex < _pages.Count)
            {
                var pg = _pages[pl.PageIndex];
                pageTextXPt = pg.PadLeftPt + pg.MarginLeftPt;
            }

            // AbsXPt — левый край зоны абзаца: у обычного абзаца это начало текстовой области
            // страницы, у абзаца ячейки — её контентный бокс (Layout.cs, cellContentX: за
            // полем ячейки и рамкой). Именно от него раскладка откладывает отступы, поэтому
            // и линейка обязана мерить от него же.
            double zoneLeftPt = pl.AbsXPt - pageTextXPt;
            double zoneWidthPt = layout.TextAreaWidthPt + layout.LeftIndentPt + layout.RightIndentPt;

            // Насколько левее зоны разрешено уводить маркеры: до физического левого края
            // страницы. Для обычного абзаца это её левое поле — прежнее поведение. Для абзаца
            // ячейки к нему добавляется смещение самой зоны, поэтому номер списка уводится
            // не только в поле клетки, но и дальше влево, за её край. Ограничивать его
            // границами клетки не за чем: место там видно, и запрет выглядел произволом.
            double pageMarginLeftPt = _pages.Count > 0 && pl.PageIndex >= 0 && pl.PageIndex < _pages.Count
                ? _pages[pl.PageIndex].MarginLeftPt
                : 0f;
            double leftOverhangPt = Math.Max(0.0, zoneLeftPt) + pageMarginLeftPt;

            const double PtToMm = 25.4 / 72.0;

            var lp = pl.Vm.Model?.ListProperties;
            bool hasMarker = lp is not null
                && lp.MarkerType != Models.Document.ListMarkerType.None;

            RulerGeometryChanged.Invoke(new ViewModels.Components.RulerParagraphGeometry
            {
                ZoneLeftMm = zoneLeftPt * PtToMm,
                ZoneWidthMm = zoneWidthPt * PtToMm,
                LeftIndentMm = layout.LeftIndentPt * PtToMm,
                FirstLineMm = (layout.LeftIndentPt + layout.FirstLineIndentPt) * PtToMm,
                RightIndentMm = layout.RightIndentPt * PtToMm,
                MarkerMm = hasMarker ? lp!.ComputedMarkerIndentPt * PtToMm : 0.0,
                HasMarker = hasMarker,
                LeftOverhangMm = leftOverhangPt * PtToMm
            });
        }

        private void UpdateSelectionContext()
        {
            if (DocVm is null) return;

            // Каретка могла переехать в другую ячейку или вовсе выйти из таблицы —
            // контекст ячейки и режим линейки должны идти следом.
            SyncCellContextToCaret();
            PublishRulerGeometry();

            DocVm.SelectionParagraphs.Clear();

            var (sp, sc, ep, ec) = NormalizeSelection();

            // Синхронизируем выделение в DocVm для ApplyCharProperty.
            // Для ячеек: sp может != ep если ячейка разбита на несколько слайсов,
            // но sc/ec — это char-индексы внутри VM одного параграфа ячейки.
            // Если начало и конец выделения в одном VM — передаём sc/ec напрямую.
            if (HasSel())
            {
                var startVm = sp < _layouts.Count ? GetVmAt(sp) : null;
                var endVm = ep < _layouts.Count ? GetVmAt(ep) : null;
                if (startVm != null && startVm == endVm)
                    DocVm.SetSelection(sc, ec);
                else
                    DocVm.SetSelection(0, 0);
            }
            else
            {
                DocVm.SetSelection(0, 0);
            }

            if (!HasSel()) return;

            var startPvm = sp < _layouts.Count ? GetVmAt(sp) : null;
            var endPvm = ep < _layouts.Count ? GetVmAt(ep) : null;

            // O(1)-проверка принадлежности абзаца документу. Раньше тут был
            // DocVm.Paragraphs.Contains() прямо в цикле — O(n) на каждый из выделенных,
            // что давало O(n^2) и сильную просадку на больших выделениях.
            var docParas = new HashSet<ParagraphViewModel>(DocVm.Paragraphs);

            var seen = new HashSet<ParagraphViewModel>();
            for (int i = sp; i <= ep && i < _layouts.Count; i++)
            {
                var pvm = GetVmAt(i);
                if (pvm is null || !docParas.Contains(pvm) || !seen.Add(pvm)) continue;

                DocVm.SelectionParagraphs.Add(pvm);

                int textLen = pvm.PlainText?.Length ?? 0;
                pvm.SelectionStart = (pvm == startPvm) ? sc : 0;
                pvm.SelectionEnd = (pvm == endPvm) ? ec : textLen;
            }
        }

        public (int docParaIdx, int charIdx, double scrollY) GetCaretState()
        {
            int docIdx = 0;
            if (_caretPara >= 0 && _caretPara < _layouts.Count
                && DocVm is not null && !IsInCell(_caretPara))
            {
                int idx = DocVm.Paragraphs.IndexOf(_layouts[_caretPara].Vm);
                if (idx >= 0) docIdx = idx;
            }
            return (docIdx, _caretChar, _scrollOffsetY);
        }

        public void RestoreCaretState(int docParaIdx, int charIdx)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_layouts.Count == 0) return;
                _caretPara = FindFirstSliceForDocVmParagraph(docParaIdx);
                _caretChar = Clamp(charIdx, 0, GetVmAt(_caretPara)?.PlainText?.Length ?? 0);
                SnapCaretToCorrectSlice();
                UpdatePreferredX();
                SyncSel();
                ResetCaret();
                var pvm = GetVmAt(_caretPara);
                if (pvm is not null && DocVm?.Paragraphs.Contains(pvm) == true)
                    DocVm?.SetActiveParagraph(pvm);
                UpdateSelectionContext();
                Focus();
                InvalidateFull();
            }, DispatcherPriority.Loaded);
        }

    }
}