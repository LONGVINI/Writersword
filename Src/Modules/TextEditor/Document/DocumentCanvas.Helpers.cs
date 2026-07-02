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
        private void BeginEdit(string description)
        {
            if (DocVm is null) { _logger.Warning("[UNDO] BeginEdit({D}): DocVm is null", description); return; }
            _logger.Debug("[UNDO] BeginEdit: {D}", description);
            _pendingSnapshot = new DocumentSnapshotCommand(DocVm, description, _caretPara, _caretChar);
            _pendingSnapshot.RestoreCaretCallback = (para, ch) =>
            {
                _caretPara = Clamp(para, 0, Math.Max(0, _layouts.Count - 1));
                _caretChar = ch;
            };
        }

        private void CommitEdit()
        {
            if (_pendingSnapshot is null) { _logger.Warning("[UNDO] CommitEdit: no pending snapshot"); return; }
            if (UndoStack is null) { _logger.Warning("[UNDO] CommitEdit: UndoStack is null"); return; }
            _pendingSnapshot.Commit(_caretPara, _caretChar);
            UndoStack.Push(_pendingSnapshot);
            RecordSnapshotInOrder();
            _logger.Debug("[UNDO] CommitEdit: pushed '{D}', stackSize={S}", _pendingSnapshot.Description, UndoStack.CanUndo);
            _pendingSnapshot = null;
        }

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
                if (cell.Row >= minRow && cell.Row <= maxRow
                    && cell.Column >= minCol && cell.Column <= maxCol)
                    return true;
            }
            return false;
        }

        // Очищает содержимое всех выделенных ячеек и сбрасывает cell-range режим.
        private void ClearCellRangeSelection()
        {
            if (_tableSelections.Count == 0) return;

            BeginEdit("Delete cell contents");

            foreach (var kv in _tableSelections)
            {
                int minRow = Math.Min(kv.Value.sr, kv.Value.er);
                int maxRow = Math.Max(kv.Value.sr, kv.Value.er);
                int minCol = Math.Min(kv.Value.sc, kv.Value.ec);
                int maxCol = Math.Max(kv.Value.sc, kv.Value.ec);

                foreach (var cell in kv.Key.Cells)
                {
                    if (cell.Row < minRow || cell.Row > maxRow) continue;
                    if (cell.Column < minCol || cell.Column > maxCol) continue;

                    cell.Paragraphs.Clear();
                    cell.Paragraphs.Add(new Writersword.Modules.TextEditor.Models.Document.ParagraphBlock());
                }
            }

            _isCellRangeSelecting = false;
            _tableSelections.Clear();
            _cellFlowRanges.Clear();
            _cellFlowFull.Clear();

            CommitEdit();
            _cellLayoutCache.Clear();
            RebuildLayouts();
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

                var toDelete = new List<ParagraphViewModel>();
                for (int di = ei; di > si; di--)
                    if (di < (DocVm?.Paragraphs.Count ?? 0))
                        toDelete.Add(DocVm!.Paragraphs[di]);

                // Удаляем хвост первого параграфа и голову последнего, сохраняя форматирование.
                sVm.Model.SpliceText(s2, st.Length, et[e2..]);
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
            _internalClipboardJson = null;
            _clipboardCache = null;

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
            }
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
                    var newPara = new ParagraphBlock();
                    newPara.Chunks.Clear();
                    foreach (var chunk in para.Chunks)
                    {
                        var newChunk = new TextChunk();
                        foreach (var run in chunk.Runs)
                            newChunk.Runs.Add(new RunModel { Text = run.Text });
                        newPara.Chunks.Add(newChunk);
                    }
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

        private async Task PasteAsync()
        {
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
                        try
                        {
                            using var ms = new System.IO.MemoryStream();
                            bmp.Save(ms);
                            bytes = ms.ToArray();
                        }
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

            // Внутренний буфер используется только когда есть таблицы.
            // Вставка только параграфов идёт через plain-text путь — он проверен и работает.
            if (!string.IsNullOrEmpty(_internalClipboardJson) && DocVm is not null)
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
                                    anchorBlock = nv.Model;
                                }
                            }
                        }
                    }

                    CommitEdit();
                    _cellLayoutCache.Clear();

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
                _cellLayoutCache.Clear();
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
        private (int parIdx, int charIdx) HitTest(Point ptLogPx)
        {
            List<ParaLayout> layouts;
            List<TableEntry> tables;
            lock (_renderLock) { layouts = _layouts; tables = _tables; }

            if (layouts.Count == 0) return (0, 0);

            double zoom = Zoom;
            float xPt = (float)(ptLogPx.X / zoom * PxToPt);
            float yPt = (float)(ptLogPx.Y / zoom * PxToPt);

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

                    int anchorIdx = -1;
                    if (clickedLeft)
                    {
                        for (int i = bestIdx - 1; i >= 0; i--)
                        {
                            if (layouts[i].Cell?.Table == te.Table) continue;
                            anchorIdx = i;
                            break;
                        }
                    }
                    else
                    {
                        for (int i = bestIdx + 1; i < layouts.Count; i++)
                        {
                            if (layouts[i].Cell?.Table == te.Table) continue;
                            anchorIdx = i;
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

                double caretYPx;
                double caretHPx;

                if (string.IsNullOrEmpty(pl.Vm.PlainText))
                {
                    // Пустой параграф — например созданный после Enter.
                    // Используем Ypt параграфа как позицию каретки.
                    caretYPx = pl.Ypt * PtToPx * zoom;
                    caretHPx = FallbackLinePt * PtToPx * zoom;
                }
                else
                {
                    int pos = Clamp(_caretChar, 0, pl.Vm.PlainText?.Length ?? 0);
                    var htLayout = pl.Layout ?? GetOrBuildLayout(pl.Vm, (float)(_canvasWidth * PxToPt));
                    var caret = htLayout.HitTestPosition(pos);

                    float yBase = pl.LineFrom < htLayout.Lines.Count
                        ? htLayout.Lines[pl.LineFrom].Y : 0f;

                    caretYPx = (pl.Ypt + (caret.Y - yBase)) * PtToPx * zoom;
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

        private void InvalidateFull()
        {
            _caretOnlyRedraw = false;
            InvalidateVisual();
        }

        private void ResetCaret()
        {
            _caretVisible = true;
            _caretTimer.Stop();
            _caretTimer.Start();
            ScrollToCaret();

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
        /// Возвращает true если block — пустой параграф расположенный сразу перед TableBlock.
        /// Такие параграфы защищены от удаления: Backspace должен удалять символ в предыдущем
        /// параграфе, а не сам якорь.
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

        private void UpdateSelectionContext()
        {
            if (DocVm is null) return;
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