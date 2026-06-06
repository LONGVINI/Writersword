using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Threading;
using Writersword.Core.Models.Rendering;
using Writersword.Modules.TextEditor.Rendering;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.ViewModels;

namespace Writersword.Modules.TextEditor.Document
{
    // Живое превью шрифта для всего выделения (несколько абзацев и ячеек таблицы).
    //
    // Идея: канвас знает полную картину выделения (обычные абзацы через
    // DocVm.SelectionParagraphs и ячейки через _tableSelections), поэтому именно он
    // снимает список целей. Превью строится ТОЛЬКО для материализованных (видимых)
    // записей _layouts — те что вне буфера (Layout == null) пропускаются и будут
    // досчитаны при коммите полным RebuildLayouts. Так не забивается ОЗУ/ЦП при
    // выделении больше экрана.
    //
    // Модель документа во время превью не меняется: подменяются только SKTextLayout
    // в оверлейном списке _layouts. Y-позиции абзацев после изменённого сдвигаются на
    // дельту высоты (как QuickUpdateParagraphLayout при вводе) — без переразбиения по
    // страницам. Ячейки подменяются на месте (высота строк и таблицы не пересчитывается).
    // Полный точный пересчёт выполняется один раз при коммите.
    public sealed partial class DocumentCanvas
    {
        // Активна ли сессия превью (открыт дропдаун шрифтов).
        private bool _fontPreviewActive;

        // Последний показанный в превью шрифт. Используется при коммите.
        private string? _previewFont;

        // Снимок целей превью на момент открытия дропдауна: абзац + диапазон [start,end).
        // Диапазон, равный всему тексту, означает "весь абзац".
        private readonly List<(ParagraphBlock block, int start, int end)> _previewTargets = new();

        // Базовый снимок _layouts на момент начала сессии — для отката при отмене.
        private List<ParaLayout>? _previewBaseLayouts;
        private float _previewBaseCanvasHeightPt;

        // Кеш построенных preview-layout по Id абзаца (диапазон фиксирован на сессию).
        // Сбрасывается при смене шрифта.
        private readonly Dictionary<Guid, SKTextLayout> _previewLayoutCache = new();

        // Счётчик поколений: применяется результат только последнего фонового задания.
        private int _previewGeneration;

        // ── Точки входа (вызываются делегатами из DocumentViewModel) ───────

        private void BeginFontPreviewSession()
        {
            ClearPreviewState();
            BuildPreviewTargets(_previewTargets);

            lock (_renderLock)
            {
                _previewBaseLayouts = _layouts;
                _previewBaseCanvasHeightPt = _canvasHeightPt;
            }

            _fontPreviewActive = _previewTargets.Count > 0;
            _previewFont = null;
        }

        private void PreviewFontFamilySession(string font)
        {
            if (!_fontPreviewActive || _previewBaseLayouts is null) return;
            if (string.IsNullOrEmpty(font)) return;
            if (_renderer is null) return;
            if (_styleResolver is null && DocVm is not null)
                _styleResolver = new StyleResolver(DocVm.Document.Styles, _scriptFontMap);
            if (_styleResolver is null) return;

            // Смена шрифта — старые preview-layout невалидны.
            if (!string.Equals(_previewFont, font, StringComparison.Ordinal))
            {
                _previewFont = font;
                _previewLayoutCache.Clear();
            }

            int gen = ++_previewGeneration;
            var baseLayouts = _previewBaseLayouts;
            var renderer = _renderer;
            var styleResolver = _styleResolver;
            float regularWidthPt = GetCurrentTextWidthPt();

            // Диапазоны превью по Id абзаца (фиксированы на сессию).
            var rangeById = new Dictionary<Guid, (int start, int end)>();
            foreach (var t in _previewTargets)
                rangeById[t.block.Id] = (t.start, t.end);

            // Снимок уже построенных layout — кеш читаем только на UI-потоке.
            var preBuilt = new Dictionary<Guid, SKTextLayout>(_previewLayoutCache);

            // Задания: только материализованные (видимые) целевые записи, по одному на абзац.
            // Временную копию абзаца (чтение модели) строим ЗДЕСЬ, на UI-потоке — доступ к
            // модели безопасен только отсюда. На фон уходит лишь Skia BuildLayout по копии.
            var buildList = new List<(Guid id, ParagraphBlock temp, bool isCell, float widthPt)>();
            var queued = new HashSet<Guid>();
            int targetsVisible = 0;
            foreach (var pl in baseLayouts)
            {
                var model = pl.Vm?.Model;
                if (model is null || pl.Layout is null) continue;
                if (!rangeById.TryGetValue(model.Id, out var r)) continue;
                if (!queued.Add(model.Id)) continue;

                targetsVisible++;
                if (preBuilt.ContainsKey(model.Id)) continue; // layout уже в кеше — копию не строим

                bool isCell = pl.Cell is not null;
                float w = isCell ? Math.Max(pl.Cell!.ClipW, 1f) : regularWidthPt;
                buildList.Add((model.Id, BuildPreviewBlock(model, r.start, r.end, font), isCell, w));
            }

            if (targetsVisible == 0)
            {
                // В видимой области целей нет — показываем базу без оверлея.
                lock (_renderLock)
                {
                    _layouts = baseLayouts;
                    _canvasHeightPt = _previewBaseCanvasHeightPt;
                    _canvasHeight = _canvasHeightPt * PtToPx;
                }
                InvalidateFull();
                return;
            }

            // BuildLayout — дорогая операция Skia — выносим в пул потоков.
            Task.Run(() =>
            {
                var built = new Dictionary<Guid, SKTextLayout>(preBuilt);
                foreach (var job in buildList)
                    built[job.id] = renderer.BuildLayout(job.temp, job.widthPt, styleResolver, job.isCell);
                return built;
            }).ContinueWith(task =>
            {
                if (!task.IsCompletedSuccessfully) return;
                Dispatcher.UIThread.Post(() =>
                {
                    if (gen != _previewGeneration) return;
                    foreach (var kv in task.Result)
                        _previewLayoutCache[kv.Key] = kv.Value;
                    AssemblePreviewOverlay(baseLayouts, task.Result);
                });
            });
        }

        private void EndFontPreviewSession(bool commit)
        {
            if (commit)
            {
                // Модель уже изменена боевым SetFontFamily (он вызывается в RibbonVM ДО
                // EndFontPreview): он применил шрифт с Undo, через OnParagraphFormatChanged
                // пересобрал _layouts и подравнял каретку. Здесь повторно применять шрифт
                // НЕЛЬЗЯ — двойной commit ломает Undo, каретку, выделение и cursor-context.
                // Просто снимаем сессию: оверлей уже заменён настоящим layout.
                ClearPreviewState();
                return;
            }

            // Отмена: возвращаем исходный layout.
            var baseLayouts = _previewBaseLayouts;
            if (baseLayouts is not null)
            {
                lock (_renderLock)
                {
                    _layouts = baseLayouts;
                    _canvasHeightPt = _previewBaseCanvasHeightPt;
                    _canvasHeight = _canvasHeightPt * PtToPx;
                }
                InvalidateMeasure();
                InvalidateFull();
            }
            ClearPreviewState();
        }

        // ── Сборка оверлея ────────────────────────────────────────────────

        private void AssemblePreviewOverlay(
            List<ParaLayout> baseLayouts,
            Dictionary<Guid, SKTextLayout> built)
        {
            var updated = new List<ParaLayout>(baseLayouts.Count);
            float cumShift = 0f;
            var deltaSeen = new HashSet<Guid>();

            foreach (var pl in baseLayouts)
            {
                var model = pl.Vm?.Model;
                bool isCell = pl.Cell is not null;

                if (model is not null && built.TryGetValue(model.Id, out var pv))
                {
                    if (isCell)
                    {
                        // Ячейка: подмена шрифта на месте. Высоту строки/таблицы и
                        // Y-позицию не трогаем — клип скрывает возможное переполнение.
                        // Слайс ограничиваем числом строк нового layout чтобы не выйти за границы.
                        int lf = Math.Min(pl.LineFrom, pv.Lines.Count);
                        int lt = Math.Min(pl.LineTo, pv.Lines.Count);
                        if (lt < lf) lt = lf;
                        updated.Add(pl with { Layout = pv, LineFrom = lf, LineTo = lt });
                    }
                    else if (deltaSeen.Add(model.Id))
                    {
                        // Первый (основной) слайс обычного абзаца: считаем дельту высоты.
                        float newH = Math.Max(pv.BlockHeightPt, FallbackLinePt);
                        float baseY = pl.Ypt + cumShift;
                        cumShift += newH - pl.HeightPt;
                        updated.Add(pl with
                        {
                            Layout = pv,
                            Ypt = baseY,
                            HeightPt = newH,
                            LineFrom = 0,
                            LineTo = pv.Lines.Count
                        });
                    }
                    else
                    {
                        // Повторный слайс того же абзаца (разбит по страницам в page-режиме):
                        // не дублируем текст — отдаём пустой слайс. Точную разбивку даст коммит.
                        updated.Add(pl with
                        {
                            Layout = pv,
                            Ypt = pl.Ypt + cumShift,
                            LineFrom = 0,
                            LineTo = 0
                        });
                    }
                }
                else if (!isCell && cumShift != 0f)
                {
                    // Обычный абзац после изменённого — сдвигаем на накопленную дельту.
                    // Ячейки и таблицы не двигаем (их Y скорректирует коммит).
                    updated.Add(pl with { Ypt = pl.Ypt + cumShift });
                }
                else
                {
                    updated.Add(pl);
                }
            }

            lock (_renderLock)
            {
                _layouts = updated;
                _canvasHeightPt = _previewBaseCanvasHeightPt + cumShift;
                _canvasHeight = _canvasHeightPt * PtToPx;
            }
            // ВНИМАНИЕ: только InvalidateFull (InvalidateVisual). InvalidateMeasure здесь
            // вызывать НЕЛЬЗЯ — MeasureOverride безусловно делает RebuildLayouts из модели
            // и мгновенно затирает оверлей превью в том же кадре. Высоту канваса при
            // наведении не обновляем (скроллбар скорректируется при коммите).
            InvalidateFull();
        }

        // ── Снимок целей выделения ─────────────────────────────────────────

        private void BuildPreviewTargets(List<(ParagraphBlock block, int start, int end)> into)
        {
            into.Clear();
            if (DocVm is null) return;

            var seen = new HashSet<ParagraphBlock>();

            // 1. Диапазон ячеек таблицы — целиком каждый абзац каждой выделенной ячейки.
            if (_tableSelections.Count > 0)
            {
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

                        foreach (var para in cell.Paragraphs)
                        {
                            if (para is null || !seen.Add(para)) continue;
                            into.Add((para, 0, para.TotalLength));
                        }
                    }
                }
            }

            // 2. Обычное выделение — берём напрямую из состояния канваса (_selStartPara/_selEndPara),
            //    а не из DocVm.SelectionParagraphs. Это первоисточник: он покрывает несколько абзацев,
            //    одиночную ячейку с выделением текста и смешанные случаи. Идём по индексам layout
            //    от sp до ep, для каждого уникального абзаца считаем частичный диапазон по краям.
            if (HasSel())
            {
                var (sp, sc, ep, ec) = NormalizeSelection();
                for (int i = sp; i <= ep && i < _layouts.Count; i++)
                {
                    var model = GetVmAt(i)?.Model;
                    if (model is null || !seen.Add(model)) continue;

                    int len = model.TotalLength;
                    int s = (i == sp) ? sc : 0;
                    int e = (i == ep) ? ec : len;
                    s = Math.Max(0, Math.Min(s, len));
                    e = Math.Max(s, Math.Min(e, len));
                    // Пустой кусок (selStart == selEnd) — селект лишь касается края абзаца,
                    // не захватывая текст. Пропускаем, как и коммит. НЕ превращаем в весь абзац:
                    // ветка "весь абзац" — только для случая, когда выделения нет вовсе (ниже).
                    if (e <= s) continue;
                    into.Add((model, s, e));
                }
            }

            // 3. Нет выделения вообще — активный абзац (или абзац активной ячейки) целиком.
            if (into.Count == 0)
            {
                var b = GetVmAt(_caretPara)?.Model;
                if (b is not null) into.Add((b, 0, b.TotalLength));
            }
        }

        private void ClearPreviewState()
        {
            _fontPreviewActive = false;
            _previewFont = null;
            _previewTargets.Clear();
            _previewBaseLayouts = null;
            _previewBaseCanvasHeightPt = 0f;
            _previewLayoutCache.Clear();
            // Аннулируем висящие фоновые задания.
            _previewGeneration++;
        }

        // Возврат клавиатурного фокуса редактору после работы с лентой (поле шрифта и т.п.).
        // Caret-состояние (_caretPara/_caretChar) уже корректное — нужен только фокус Control.
        // Откладываем на следующий цикл: дропдаун ленты ещё закрывается и может перехватить фокус.
        private void FocusEditorFromHost()
        {
            Dispatcher.UIThread.Post(() =>
            {
                Focus();
                ResetCaret();
                InvalidateVisual();
            }, DispatcherPriority.Background);
        }

        // ── Построение временной копии абзаца с preview-шрифтом ────────────

        /// <summary>
        /// Создаёт временную копию абзаца с применённым preview-шрифтом к диапазону
        /// [selStart, selEnd). Оригинальная модель не изменяется.
        ///
        /// Не пересобирает абзац посимвольно: идёт по существующим ранам и режет
        /// каждый ран максимум на 3 части по границам выделения (до / внутри / после),
        /// применяя шрифт только к средней. Раны вне выделения копируются как есть
        /// (свойства разделяются по ссылке). Склейка соседних равных ранов здесь не
        /// нужна — копия одноразовая, строится только под layout и сразу выбрасывается.
        /// </summary>
        private static ParagraphBlock BuildPreviewBlock(
            ParagraphBlock original, int selStart, int selEnd, string font)
        {
            var copy = new ParagraphBlock
            {
                Id = original.Id,
                Properties = original.Properties,
                ListProperties = original.ListProperties,
            };
            copy.Chunks.Clear();
            var newChunk = new TextChunk();
            copy.Chunks.Add(newChunk);

            // Нормализуем границы выделения к длине текста абзаца.
            int total = 0;
            foreach (var chunk in original.Chunks)
                foreach (var run in chunk.Runs)
                    total += run.Text.Length;

            int from = Math.Max(0, Math.Min(selStart, total));
            int to = Math.Max(from, Math.Min(selEnd, total));

            int offset = 0;
            foreach (var chunk in original.Chunks)
            {
                foreach (var run in chunk.Runs)
                {
                    int runStart = offset;
                    int runEnd = offset + run.Text.Length;
                    offset = runEnd;

                    if (run.Text.Length == 0) continue;

                    // Ран целиком вне выделения — копируем как есть (свойства по ссылке).
                    if (runEnd <= from || runStart >= to)
                    {
                        newChunk.Runs.Add(new RunModel { Text = run.Text, Properties = run.Properties });
                        continue;
                    }

                    // Граница выделения проходит внутри рана — режем на куски.
                    // a, b — локальные (относительно рана) индексы пересечения с [from, to).
                    int a = Math.Max(from, runStart) - runStart;
                    int b = Math.Min(to, runEnd) - runStart;

                    // Левый кусок [0, a) — вне выделения.
                    if (a > 0)
                        newChunk.Runs.Add(new RunModel
                        {
                            Text = run.Text.Substring(0, a),
                            Properties = run.Properties
                        });

                    // Средний кусок [a, b) — выделение, применяем preview-шрифт.
                    if (b > a)
                    {
                        var previewProps = run.Properties?.Clone() ?? new RunProperties();
                        previewProps.FontFamily = font;
                        newChunk.Runs.Add(new RunModel
                        {
                            Text = run.Text.Substring(a, b - a),
                            Properties = previewProps.IsDefault() ? null : previewProps
                        });
                    }

                    // Правый кусок [b, len) — вне выделения.
                    if (b < run.Text.Length)
                        newChunk.Runs.Add(new RunModel
                        {
                            Text = run.Text.Substring(b),
                            Properties = run.Properties
                        });
                }
            }

            // Пустой абзац — гарантируем хотя бы один пустой ран.
            if (newChunk.Runs.Count == 0)
                newChunk.Runs.Add(new RunModel { Text = string.Empty });

            return copy;
        }
    }
}