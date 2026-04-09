using Avalonia;
using Avalonia.Controls;
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
            if (DocVm is null) return;
            _pendingSnapshot = new DocumentSnapshotCommand(DocVm, description);
        }

        private void CommitEdit()
        {
            if (_pendingSnapshot is null || UndoStack is null) return;
            _pendingSnapshot.Commit();
            UndoStack.Push(_pendingSnapshot);
            _pendingSnapshot = null;
        }

        // ── Selection ────────────────────────────────────────────────────
        private bool HasSel() =>
            _selStartPara != _selEndPara || _selStartChar != _selEndChar;

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
            DeleteSelection();
            CommitEdit();
            SnapCaretToCorrectSlice();
            UpdatePreferredX();
            SyncSel(); ResetCaret(); InvalidateFull();
        }

        private async Task PasteAsync()
        {
            string? text = _clipboardCache;
            if (string.IsNullOrEmpty(text))
            {
                var cb = TopLevel.GetTopLevel(this)?.Clipboard;
                if (cb is null) return;
#pragma warning disable CS0618
                text = await cb.GetTextAsync();
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

            // ── Двухпроходной поиск ───────────────────────────────────────
            // Для ячеек таблицы берём пересечение Y-диапазона параграфа и клип-прямоугольника.
            // Без клипа ByCell-split создаёт два слайса с одинаковым HeightPt (полная высота
            // параграфа), оба покрывают область второй страницы, и всегда выигрывает первый.
            // Клип в одиночку тоже не работает: все параграфы ячейки делят один ClipY/ClipH,
            // поэтому Y-дистанция до всех одинакова и побеждает первый параграф.
            // Пересечение устраняет оба случая: каждый слайс виден только в своём clip-окне,
            // а параграфы внутри ячейки сохраняют собственные Ypt/HeightPt.
            static (float top, float bot) GetYRange(ParaLayout pl)
            {
                float top = pl.Ypt;
                float bot = pl.Ypt + pl.HeightPt;
                if (pl.Cell != null)
                {
                    top = Math.Max(top, pl.Cell.ClipY);
                    bot = Math.Min(bot, pl.Cell.ClipY + pl.Cell.ClipH);
                }
                return (top, bot);
            }

            // Проход 1: находим минимальное Y-расстояние.
            float bestYDist = float.MaxValue;
            for (int i = 0; i < layouts.Count; i++)
            {
                var pl = layouts[i];
                var (top, bot) = GetYRange(pl);
                float dist = yPt < top ? top - yPt : yPt > bot ? yPt - bot : 0f;
                if (dist < bestYDist) bestYDist = dist;
            }

            // Проход 2: среди всех кандидатов с минимальным Y-расстоянием
            // выбираем тот, чей X-диапазон [AbsXPt .. AbsXPt + layoutWidth] ближайший к клику.
            // Это решает проблему таблиц: ячейки одной строки имеют dist==0 по Y,
            // и без X-проверки всегда выбирается первая (самая левая) ячейка.
            int bestIdx = 0;
            float bestXDist = float.MaxValue;

            for (int i = 0; i < layouts.Count; i++)
            {
                var pl = layouts[i];
                var (top, bot) = GetYRange(pl);
                float yDist = yPt < top ? top - yPt : yPt > bot ? yPt - bot : 0f;

                if (Math.Abs(yDist - bestYDist) > 0.5f) continue; // не с минимальным Y

                // Для ячейки используем полный X-диапазон clip-прямоугольника (включая границы),
                // иначе клик в области границы между ячейками даёт xDist>0 для всех
                // и выигрывает всегда крайняя левая ячейка.
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

            var best = layouts[bestIdx];

            _logger.Debug("[HIT] bestIdx={BI} Cell={C} bestXDist={XD:F1} xPt={X:F1} yPt={Y:F1}",
                bestIdx, best.Cell != null ? $"row={best.Cell.Cell?.Row} col={best.Cell.Cell?.Column}" : "null",
                bestXDist, xPt, yPt);

            // ── Якоря таблицы: клик снаружи таблицы по X ─────────────────
            // Если двухпроходной поиск выбрал ячейку таблицы, но клик по X
            // находится левее или правее таблицы — перенаправляем на якорь.
            if (best.Cell != null)
            {
                foreach (var te in tables)
                {
                    if (te.Table != best.Cell.Table) continue;

                    float tableLeft = te.XPt;
                    float tableRight = te.XPt + te.Layout.TotalWidthPt;

                    _logger.Debug("[HIT] table xPt={X:F1} tableLeft={TL:F1} tableRight={TR:F1}",
                        xPt, tableLeft, tableRight);

                    if (xPt >= tableLeft && xPt <= tableRight) break; // клик внутри — норм

                    bool clickedLeft = xPt < tableLeft;
                    _logger.Debug("[HIT] clickedLeft={CL}", clickedLeft);

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

                    _logger.Debug("[HIT] anchorIdx={AI} anchorYpt={AY:F1} anchorAbsX={AX:F1} anchorLines={AL}",
                        anchorIdx,
                        anchorIdx >= 0 ? layouts[anchorIdx].Ypt : -1f,
                        anchorIdx >= 0 ? layouts[anchorIdx].AbsXPt : -1f,
                        anchorIdx >= 0 ? layouts[anchorIdx].Layout.Lines.Count : -1);

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

            float yBase = best.LineFrom < best.Layout.Lines.Count
                ? best.Layout.Lines[best.LineFrom].Y : 0f;

            float localX = xPt - padXPt - best.Layout.LeftIndentPt;
            float localY = yPt - best.Ypt + yBase;

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

            var hit = best.Layout.HitTestPoint(hitX, localY);

            _caretLineHint = -1;
            for (int li = best.LineFrom; li < Math.Min(best.LineTo, best.Layout.Lines.Count); li++)
            {
                var ln = best.Layout.Lines[li];
                if (localY <= ln.Y + ln.Height) { _caretLineHint = li; break; }
            }

            return (bestIdx, hit.CharIndex);
        }

        // ── Scroll to caret ───────────────────────────────────────────────
        private void ScrollToCaret()
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_caretPara < 0 || _caretPara >= _layouts.Count) return;

                double zoom = Zoom;
                var pl = _layouts[_caretPara];

                // Пустой якорь у края таблицы — не прокручиваем.
                // Пустой якорь разрыва страницы — прокручиваем по Ypt параграфа.
                if (string.IsNullOrEmpty(pl.Vm.PlainText))
                {
                    if (pl.Cell is null && IsBreakAnchor(pl.Vm.Model))
                    {
                        double yPx = pl.Ypt * PtToPx * zoom;
                        double hPx = FallbackLinePt * PtToPx * zoom;
                        this.BringIntoView(new Rect(0, yPx - 10, 20, hPx + 20));
                    }
                    return;
                }

                int pos = Clamp(_caretChar, 0, pl.Vm.PlainText?.Length ?? 0);
                var caret = pl.Layout.HitTestPosition(pos);

                float yBase = pl.LineFrom < pl.Layout.Lines.Count
                    ? pl.Layout.Lines[pl.LineFrom].Y : 0f;

                // Используем AbsXPt который одинаково правильный для ячеек и параграфов
                double xPx = (pl.AbsXPt + caret.X) * PtToPx * zoom;
                double yPx2 = (pl.Ypt + (caret.Y - yBase)) * PtToPx * zoom;
                double hPx2 = caret.Height * PtToPx * zoom;

                this.BringIntoView(new Rect(xPx - 10, yPx2 - 10, 20, hPx2 + 20));
            }, DispatcherPriority.Render);
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
                    float yBase = pl.LineFrom < pl.Layout.Lines.Count
                        ? pl.Layout.Lines[pl.LineFrom].Y : 0f;
                    caretYPx = (pl.Ypt + (caret.Y - yBase)) * PtToPx * zoom;
                    caretHPx = caret.Height * PtToPx * zoom;
                }

                double viewportH = _parentScrollViewer.Viewport.Height;
                double targetOffsetY = caretYPx + caretHPx / 2.0 - viewportH / 2.0;
                targetOffsetY = Math.Max(0, targetOffsetY);

                _parentScrollViewer.Offset = new Avalonia.Vector(
                    _parentScrollViewer.Offset.X, targetOffsetY);

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
                    float yBase = pl.LineFrom < pl.Layout.Lines.Count ? pl.Layout.Lines[pl.LineFrom].Y : 0f;
                    float caretAbsY = pl.Ypt + (caret.Y - yBase);
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
            if (!HasSel()) return;

            var (sp, _, ep, _) = NormalizeSelection();
            var seen = new HashSet<ParagraphViewModel>();
            for (int i = sp; i <= ep && i < _layouts.Count; i++)
            {
                var pvm = GetVmAt(i);
                // Добавляем только VM из DocVm.Paragraphs (не ячеечные)
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