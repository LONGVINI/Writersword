using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using DynamicData;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Writersword.Core.Models.Rendering;
using Writersword.Infrastructure.Rendering;
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
            _pendingSnapshot = new DocumentSnapshotCommand(DocVm, description);
        }

        private void CommitEdit()
        {
            if (_pendingSnapshot is null) { _logger.Warning("[UNDO] CommitEdit: no pending snapshot"); return; }
            if (UndoStack is null) { _logger.Warning("[UNDO] CommitEdit: UndoStack is null"); return; }
            _pendingSnapshot.Commit();
            UndoStack.Push(_pendingSnapshot);
            _logger.Debug("[UNDO] CommitEdit: pushed '{D}', stackSize={S}", _pendingSnapshot.Description, UndoStack.CanUndo);
            _pendingSnapshot = null;
        }

        // ── Selection ────────────────────────────────────────────────────
        private bool HasSel() =>
            _selStartPara != _selEndPara || _selStartChar != _selEndChar;

        private bool HasCellRangeSel() => _isCellRangeSelecting && _cellSelTable != null;

        private bool IsCellSelected(TableCell cell)
        {
            if (!HasCellRangeSel()) return false;
            int minRow = Math.Min(_cellSelStartRow, _cellSelEndRow);
            int maxRow = Math.Max(_cellSelStartRow, _cellSelEndRow);
            int minCol = Math.Min(_cellSelStartCol, _cellSelEndCol);
            int maxCol = Math.Max(_cellSelStartCol, _cellSelEndCol);
            return cell.Row >= minRow && cell.Row <= maxRow
                && cell.Column >= minCol && cell.Column <= maxCol;
        }

        // Очищает содержимое всех выделенных ячеек и сбрасывает cell-range режим.
        private void ClearCellRangeSelection()
        {
            if (_cellSelTable is null) return;

            BeginEdit("Delete cell contents");

            int minRow = Math.Min(_cellSelStartRow, _cellSelEndRow);
            int maxRow = Math.Max(_cellSelStartRow, _cellSelEndRow);
            int minCol = Math.Min(_cellSelStartCol, _cellSelEndCol);
            int maxCol = Math.Max(_cellSelStartCol, _cellSelEndCol);

            foreach (var cell in _cellSelTable.Cells)
            {
                if (cell.Row < minRow || cell.Row > maxRow) continue;
                if (cell.Column < minCol || cell.Column > maxCol) continue;

                // Оставляем один пустой параграф — минимальная структура ячейки.
                cell.Paragraphs.Clear();
                cell.Paragraphs.Add(new Writersword.Modules.TextEditor.Models.Document.ParagraphBlock());
            }

            _isCellRangeSelecting = false;
            _cellSelTable = null;

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
            if (!HasSel()) return;
            var (sp, sc, ep, ec) = NormalizeSelection();
            var sVm = GetVmAt(sp);
            var eVm = GetVmAt(ep);
            if (sVm is null || eVm is null) return;

            if (sVm == eVm)
            {
                string t = sVm.PlainText ?? "";
                int s2 = Clamp(sc, 0, t.Length);
                int e2 = Clamp(ec, 0, t.Length);
                sVm.PlainText = t[..s2] + t[e2..];
                _caretChar = s2;
            }
            else if (!IsInCell(sp) && !IsInCell(ep))
            {
                // Обычное межпараграфное удаление
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

                sVm.PlainText = st[..s2] + et[e2..];
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
            if (!HasSel()) return;
            var (sp, sc, ep, ec) = NormalizeSelection();
            var sVm = GetVmAt(sp);
            var eVm = GetVmAt(ep);
            if (sVm is null || eVm is null) return;

            var lines = new List<string>();
            var seenVms = new HashSet<ParagraphViewModel>();

            for (int i = sp; i <= ep && i < _layouts.Count; i++)
            {
                var pvm = GetVmAt(i);
                if (pvm is null || !seenVms.Add(pvm)) continue;

                string t = pvm.PlainText ?? "";
                int from = (i == sp) ? Clamp(sc, 0, t.Length) : 0;
                int to = (i == ep) ? Clamp(ec, 0, t.Length) : t.Length;
                if (from > to) to = from;
                lines.Add(t[from..to]);
            }

            string result = string.Join(Environment.NewLine, lines);
            _clipboardCache = result;

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
            {
#pragma warning disable CS0618
                await clipboard.SetTextAsync(result);
#pragma warning restore CS0618
            }
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

        private async Task PasteAsync()
        {
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
                // Вставка в ячейку — только первая строка (без разбиения ячейки на параграфы для простоты TODO)
                string firstLine = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n')[0];
                CellInsertText(firstLine);
                return;
            }

            BeginEdit("Paste");
            DeleteSelection();

            string[] lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            var pvm = GetVmAt(_caretPara);
            if (pvm is null) return;

            string cur = pvm.PlainText ?? "";
            int pos = Clamp(_caretChar, 0, cur.Length);
            string before = cur[..pos];
            string after = cur[pos..];

            if (lines.Length == 1)
            {
                pvm.PlainText = before + lines[0] + after;
                _caretChar = pos + lines[0].Length;
            }
            else
            {
                pvm.PlainText = before + lines[0];
                var prev = pvm;
                for (int i = 1; i < lines.Length - 1; i++)
                {
                    var nv = DocVm?.AddParagraphAfter(prev);
                    if (nv is not null) { nv.PlainText = lines[i]; prev = nv; }
                }
                var last = DocVm?.AddParagraphAfter(prev);
                if (last is not null)
                {
                    last.PlainText = lines[^1] + after;
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
                        xLeft = pl.AbsXPt;
                        xRight = pl.AbsXPt + (pl.Layout.Lines.Count > 0
                            ? pl.Layout.Lines.Max(l => l.Segments.Count > 0
                                ? l.Segments[^1].X + l.Segments[^1].Width : 0f)
                            : 100f);
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

            _logger.Debug("[HIT] bestIdx={BI} Cell={C} clipBest={CB} xPt={X:F1} yPt={Y:F1}",
                bestIdx, best.Cell != null ? $"row={best.Cell.Cell?.Row} col={best.Cell.Cell?.Column}" : "null",
                clipBestIdx, xPt, yPt);

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

            float localX = xPt - padXPt - best.Layout.LeftIndentPt;
            // localY: переводим screen-Y в координаты лейаута.
            // RenderParagraphLines рисует строку i в точке absY + (lines[i].Y - lines[lineFrom].Y),
            // поэтому: layout_Y = (screenY - pl.Ypt) + lines[lineFrom].Y.
            float hitYBase = best.LineFrom < best.Layout.Lines.Count
                ? best.Layout.Lines[best.LineFrom].Y : 0f;
            float localY = yPt - best.Ypt + hitYBase;

            if (best.LineFrom < best.Layout.Lines.Count)
            {
                float fy = best.Layout.Lines[best.LineFrom].Y;
                int lto = best.LineTo > 0 && best.LineTo <= best.Layout.Lines.Count
                    ? best.LineTo : best.Layout.Lines.Count;
                float ly = best.Layout.Lines[lto - 1].Y + best.Layout.Lines[lto - 1].Height;
                localY = Clamp(localY, fy + 0.1f, ly - 0.1f);
            }

            float hitX = localX;
            if (best.LineFrom == 0
                && best.Layout.FirstLineIndentPt != 0
                && best.Layout.Lines.Count > 0)
            {
                float line0Bottom = best.Layout.Lines[0].Y + best.Layout.Lines[0].Height;
                if (localY <= line0Bottom)
                    hitX -= best.Layout.FirstLineIndentPt;
            }

            // Определяем целевую строку по localY.
            // Если клик правее текста строки — возвращаем конец строки напрямую,
            // не вызывая HitTestPoint: тот пересчитывает строку по localY внутри
            // и при hitX > TextWidth всё равно уходит на начало следующей.
            _caretLineHint = -1;
            for (int li = best.LineFrom; li < Math.Min(best.LineTo, best.Layout.Lines.Count); li++)
            {
                var ln = best.Layout.Lines[li];
                if (localY <= ln.Y + ln.Height)
                {
                    _caretLineHint = li;
                    if (hitX >= ln.TextWidth)
                        return (bestIdx, ln.LastCharIndex + 1);
                    break;
                }
            }

            var hit = best.Layout.HitTestPoint(hitX, localY);

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
                    if (pl.Cell is null && IsBreakAnchor(pl.Vm.Model))
                    {
                        caretYPx = pl.Ypt * PtToPx * zoom;
                        caretHPx = FallbackLinePt * PtToPx * zoom;
                    }
                    else return;
                }
                else
                {
                    int pos = Clamp(_caretChar, 0, pl.Vm.PlainText?.Length ?? 0);
                    var caret = pl.Layout.HitTestPosition(pos);

                    float yBase = pl.LineFrom < pl.Layout.Lines.Count
                        ? pl.Layout.Lines[pl.LineFrom].Y : 0f;

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
                    var caret = pl.Layout.HitTestPosition(pos);
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
                    var caret = pl.Layout.HitTestPosition(Clamp(_caretChar, 0, pl.Vm.PlainText?.Length ?? 0));
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

            var seen = new HashSet<ParagraphViewModel>();
            for (int i = sp; i <= ep && i < _layouts.Count; i++)
            {
                var pvm = GetVmAt(i);
                if (pvm is not null && seen.Add(pvm) && DocVm.Paragraphs.Contains(pvm))
                    DocVm.SelectionParagraphs.Add(pvm);
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