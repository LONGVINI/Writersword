using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using Writersword.Core.Models.Rendering;
using Writersword.Modules.TextEditor.Commands;
using Writersword.Modules.TextEditor.Rendering;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Models.Styles;
using Writersword.Modules.TextEditor.ViewModels;
using Writersword.Modules.TextEditor.ViewModels.Blocks;

namespace Writersword.Modules.TextEditor.Document
{
    // Живое превью шрифта для всего выделения (несколько абзацев).
    //
    // Механизм: preview-раскладки затронутых абзацев кладутся в общий _layoutCache
    // (он валидируется по Text+Width, а смена шрифта их не меняет, поэтому подмена
    // корректна), после чего вызывается настоящий RebuildLayouts. Он пересчитывает
    // высоты строк, позиции и разбивку по страницам с реальными метриками шрифта —
    // ровно как при коммите, включая добавление страниц. Skia при этом не вызывается
    // (все цели уже в кэше), поэтому пересчёт дешёвый.
    //
    // Превью строится только для ВИДИМЫХ (материализованных) целей — остальные вне
    // буфера остаются со старым шрифтом до коммита (полный RebuildLayouts их досчитает).
    //
    // Модель документа во время превью не меняется. При отмене исходные записи кэша
    // восстанавливаются. Ячейки таблицы через этот путь не превьюятся (cache-injection
    // рассчитан на обычные абзацы; ячейки корректно применяются при коммите).
    public sealed partial class DocumentCanvas
    {
        // Активна ли сессия превью (открыт дропдаун шрифтов).
        private bool _fontPreviewActive;

        // Последний показанный в превью шрифт.
        private string? _previewFont;

        // Снимок целей превью на момент открытия дропдауна: абзац + VM + диапазон [start,end).
        // vm == null для абзацев ячеек таблицы (они в превью не участвуют).
        private readonly List<(ParagraphBlock block, ParagraphViewModel? vm, int start, int end)> _previewTargets = new();

        // Исходные записи _layoutCache затронутых абзацев — для отката при отмене.
        // Значение null => записи в кэше не было (нужно удалить при откате).
        private readonly Dictionary<ParagraphViewModel,
            (string Text, float Width, SKTextLayout Layout)?> _previewSavedLayouts = new();

        // Счётчик поколений: применяется результат только последнего фонового задания.
        private int _previewGeneration;

        // Схлопывание пересчёта раскладки во время превью.
        //
        // Тяжёлый Skia уходит на фон, но сам RebuildLayouts остаётся на UI-потоке и
        // на рукописи в восемь десятков абзацев стоит около двухсот миллисекунд даже
        // при полностью заполненном кэше. Пока человек идёт стрелками по списку
        // шрифтов, таких пересчётов набегает под сотню подряд, и поток стоит
        // секундами. Это не только рывки: события указателя копятся, нажатие
        // приходит без парного отпускания, и полоса вкладок Dock принимает это за
        // начало перетаскивания — в воздухе повисает призрак вкладки, а окно
        // выглядит намертво зависшим.
        //
        // Подстановка раскладок в кэш остаётся мгновенной — дёшево и нужно сразу.
        // Откладывается только пересчёт: пока список листают, он не запускается,
        // остановился на шрифте — сработал один раз.
        private DispatcherTimer? _previewRebuildTimer;

        // Задержка подобрана под скорость перебора стрелками: короче — пересчёт
        // снова начнёт срабатывать на каждый шаг, длиннее — превью ощутимо отстаёт.
        private static readonly TimeSpan PreviewRebuildDelay = TimeSpan.FromMilliseconds(90);

        /// <summary>
        /// Запросить пересчёт раскладки превью. Повторные вызовы сдвигают срок,
        /// поэтому подряд идущие шаги перебора дают один пересчёт, а не десять.
        /// </summary>
        private void SchedulePreviewRebuild()
        {
            if (_previewRebuildTimer is null)
            {
                _previewRebuildTimer = new DispatcherTimer(DispatcherPriority.Render)
                {
                    Interval = PreviewRebuildDelay
                };
                _previewRebuildTimer.Tick += (_, _) => RunPreviewRebuild();
            }

            _previewRebuildTimer.Stop();
            _previewRebuildTimer.Start();
        }

        private void RunPreviewRebuild()
        {
            _previewRebuildTimer?.Stop();

            // Сессия могла закончиться, пока срок ждали: коммит и отмена пересчитывают
            // раскладку сами, и второй проход здесь только мешал бы.
            if (!_fontPreviewActive) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            RebuildLayouts();
            long rebuild = sw.ElapsedMilliseconds;

            SnapCaretToCorrectSlice();
            long snap = sw.ElapsedMilliseconds - rebuild;

            InvalidateFull();

            _logger.Debug("[FONT] recalc: RebuildLayouts={R} ms, SnapCaret={S} ms",
                rebuild, snap);
        }

        /// <summary>Снять отложенный пересчёт: сессия закончилась.</summary>
        private void CancelPreviewRebuild() => _previewRebuildTimer?.Stop();

        // ── Точки входа (вызываются делегатами из DocumentViewModel) ───────

        private void BeginFontPreviewSession()
        {
            ClearPreviewState();

            // Режим сравнения (read-only): превью не запускается — сессия остаётся
            // пустой, и выбор шрифта в дропдауне ни на что не влияет.
            if (IsEditingBlocked) return;

            BuildPreviewTargets(_previewTargets);
            _fontPreviewActive = _previewTargets.Count > 0;
            _previewFont = null;
            _logger.Information(
                "[FONT] Begin: targets={T} active={A} caretPara={P} inCell={C} tableCellPara={TC} tableSel={TS}",
                _previewTargets.Count, _fontPreviewActive, _caretPara, IsInCell(_caretPara),
                DocVm?.TableActiveCellParagraph is not null, _tableSelections.Count);
        }

        private void PreviewFontFamilySession(string font)
        {
            _logger.Debug("[FONT] preview enter: font={F} active={A}", font, _fontPreviewActive);
            var swEntry = System.Diagnostics.Stopwatch.StartNew();
            if (!_fontPreviewActive) return;
            if (string.IsNullOrEmpty(font)) return;
            if (_renderer is null) return;
            if (_styleResolver is null && DocVm is not null)
                _styleResolver = CreateStyleResolver();
            if (_styleResolver is null) return;

            // Тот же шрифт приходит повторно: AutoCompleteBox сообщает и о смене
            // SelectedItem, и о смене текста. Пересчитывать одно и то же незачем.
            if (string.Equals(font, _previewFont, StringComparison.Ordinal)) return;

            _previewFont = font;
            int gen = ++_previewGeneration;
            var renderer = _renderer;
            var styleResolver = _styleResolver;
            float widthPt = GetCurrentTextWidthPt();

            // Ячейки таблицы: строим preview-абзац по выделенному диапазону (модель не трогаем).
            // BuildTableLayout подставит его в раскладку ячейки.
            bool anyCell = false;
            foreach (var t in _previewTargets)
                if (t.vm is null)
                {
                    _cellFontPreview[t.block] = BuildPreviewBlock(t.block, t.start, t.end, font);
                    anyCell = true;
                }

            // Целевые обычные абзацы (с VM) + их диапазоны.
            var rangeByVm = new Dictionary<ParagraphViewModel, (int start, int end)>();
            foreach (var t in _previewTargets)
                if (t.vm is not null)
                    rangeByVm[t.vm] = (t.start, t.end);
            if (rangeByVm.Count == 0)
            {
                if (anyCell) SchedulePreviewRebuild();
                return;
            }

            // Строим preview-копии для ВИДИМЫХ целей здесь, на UI-потоке (доступ к модели
            // безопасен только отсюда). Тяжёлый Skia BuildLayout уйдёт на фон.
            var swBuild = System.Diagnostics.Stopwatch.StartNew();
            var jobs = new List<(ParagraphViewModel vm, ParagraphBlock temp)>();
            var queued = new HashSet<ParagraphViewModel>();
            foreach (var pl in _layouts)
            {
                var vm = pl.Vm;
                if (vm is null || pl.Layout is null) continue;
                if (!rangeByVm.TryGetValue(vm, out var r)) continue;
                if (!queued.Add(vm)) continue;
                jobs.Add((vm, BuildPreviewBlock(vm.Model, r.start, r.end, font)));
            }
            _logger.Debug("[FONT] jobs={N}, UI assembly={Ms} ms, enter took {Total} ms",
                jobs.Count, swBuild.ElapsedMilliseconds, swEntry.ElapsedMilliseconds);

            if (jobs.Count == 0) return;

            var swBg = System.Diagnostics.Stopwatch.StartNew();

            Task.Run(() =>
            {
                var built = new List<(ParagraphViewModel vm, SKTextLayout layout)>(jobs.Count);
                foreach (var j in jobs)
                    built.Add((j.vm, renderer.BuildLayout(j.temp, widthPt, styleResolver)));
                return built;
            }).ContinueWith(task =>
            {
                if (!task.IsCompletedSuccessfully)
                {
                    _logger.Debug("[FONT] background layout failed: {E}",
                        task.Exception?.GetBaseException().Message);
                    return;
                }

                long bgMs = swBg.ElapsedMilliseconds;

                Dispatcher.UIThread.Post(() =>
                {
                    if (gen != _previewGeneration || !_fontPreviewActive)
                    {
                        _logger.Debug("[FONT] discarded result of generation {Gen} (current is {Cur})",
                            gen, _previewGeneration);
                        return;
                    }

                    _logger.Debug("[FONT] background took {Ms} ms, results={N}",
                        bgMs, task.Result.Count);

                    foreach (var (vm, layout) in task.Result)
                    {
                        // Один раз за сессию сохраняем исходную запись кэша для отката.
                        if (!_previewSavedLayouts.ContainsKey(vm))
                            _previewSavedLayouts[vm] = _layoutCache.TryGetValue(vm, out var orig)
                                ? orig
                                : ((string, float, SKTextLayout)?)null;

                        _layoutCache[vm] = (vm.PlainText ?? string.Empty, widthPt, layout);
                    }

                    // Настоящая пагинация: высоты, позиции, страницы пересчитываются с
                    // реальными метриками. Skia не вызывается — все цели уже в кэше.
                    // Сам пересчёт откладывается: подряд идущие шаги перебора дают один.
                    SchedulePreviewRebuild();
                });
            });
        }

        private void EndFontPreviewSession(bool commit)
        {
            _logger.Information("[FONT] End: commit={C} previewFont={F} targets={T}",
                commit, _previewFont, _previewTargets.Count);
            // Режим сравнения (read-only): коммит запрещён — ветка отмены ниже
            // восстановит исходные раскладки, модель не изменится.
            if (commit && IsEditingBlocked) commit = false;

            if (commit)
            {
                var font = _previewFont;
                // Снимок целей до очистки сессии.
                var targets = _previewTargets.ToList();
                // Убираем preview-раскладки из кэша — модель сейчас изменится, восстанавливать
                // исходные не нужно.
                foreach (var vm in _previewSavedLayouts.Keys)
                    _layoutCache.Remove(vm);
                ClearPreviewState();

                if (!string.IsNullOrEmpty(font))
                    CommitFontGranular(font!, targets);
                return;
            }

            // Отмена: возвращаем исходные раскладки затронутых абзацев и override ячеек,
            // затем пересобираем (override уже очищен в ClearPreviewState -> исходный шрифт).
            bool hadCells = _cellFontPreview.Count > 0;
            bool any = _previewSavedLayouts.Count > 0;
            foreach (var kv in _previewSavedLayouts)
            {
                if (kv.Value is { } orig) _layoutCache[kv.Key] = orig;
                else _layoutCache.Remove(kv.Key);
            }
            ClearPreviewState();
            if (any || hadCells)
            {
                RebuildLayouts();
                SnapCaretToCorrectSlice();
                InvalidateFull();
            }
        }

        // Применяет выбранный шрифт через гранулярные команды (SetRunPropertyCommand на абзац,
        // объединённые в CompositeCommand) и пишет их в лёгкий TextUndoStack. Тогда Ctrl+Z
        // откатывает только затронутые абзацы — мгновенно, без снапшота всего документа.
        private void CommitFontGranular(
            string font,
            List<(ParagraphBlock block, ParagraphViewModel? vm, int start, int end)> targets)
        {
            // Гранулярные команды мутируют модель напрямую (мимо гейтов DocumentViewModel) —
            // в режиме сравнения выходим до любых изменений.
            if (IsEditingBlocked) return;

            // Гранулярная команда работает по обычным абзацам документа. Если в целях есть
            // ячейки таблицы (vm == null) или нет лёгкого стека — откатываемся на боевой
            // SetFontFamily (полный снапшот), чтобы не оставить ячейки без изменения.
            bool anyCell = targets.Any(t => t.vm is null);
            _logger.Information("[FONT] Commit: font={F} targets={T} anyCell={AC} hasStack={HS} tableCellPara={TC}",
                font, targets.Count, anyCell, TextUndoStack is not null,
                DocVm?.TableActiveCellParagraph is not null);
            if (TextUndoStack is null || DocVm is null || anyCell)
            {
                // Ячейки (одна/диапазон) или смешанное выделение ячеек и обычных абзацев.
                // SetFontFamily применил бы шрифт лишь к активной ячейке, поэтому применяем ко
                // ВСЕМ целям напрямую (FindParagraph резолвит и абзацы ячеек) под одним снапшотом.
                if (DocVm is not null && targets.Count > 0)
                {
                    int applied = 0;
                    BeginEdit("Font");
                    foreach (var t in targets)
                    {
                        if (t.end <= t.start) continue;
                        new SetRunPropertyCommand(
                            t.block.Id, t.start, t.end, p => p.FontFamily = font, "Font")
                            .Apply(DocVm.Document);
                        applied++;
                    }
                    CommitEdit();
                    if (applied > 0)
                    {
                        // Раскладка абзацев ячеек кешируется в _layoutCache по cell-VM, а
                        // GetOrBuildLayout проверяет только текст и ширину, не форматирование.
                        // Без явного сброса смена шрифта/формата в ячейке остаётся невидимой.
                        foreach (var t in targets)
                            if (_cellVmCache.TryGetValue(t.block, out var cvm))
                                _layoutCache.Remove(cvm);
                        InvalidateCellLayoutCaches();
                        RebuildLayouts();
                        InvalidateFull();
                    }
                    _logger.Information("[FONT] Commit all targets (cell/mixed): applied={N}", applied);
                    if (DocVm.TableActiveCellParagraph is { } cpr)
                        FireCellCursorContext(cpr);
                    else
                        DocVm.FireCursorContextChanged();
                    return;
                }

                DocVm?.SetFontFamily(font);
                _logger.Information("[FONT] Commit via SetFontFamily (fallback)");
                if (DocVm?.TableActiveCellParagraph is { } cp)
                    FireCellCursorContext(cp);
                return;
            }

            var cmds = new List<ITextCommand>();
            var ids = new List<Guid>();
            foreach (var t in targets)
            {
                if (t.end <= t.start) continue;
                cmds.Add(new SetRunPropertyCommand(
                    t.block.Id, t.start, t.end, p => p.FontFamily = font, "Font"));
                ids.Add(t.block.Id);
            }
            if (cmds.Count == 0)
            {
                DocVm.SetFontFamily(font);
                return;
            }

            var idsArr = ids.ToArray();
            var composite = new CompositeCommand(
                "Font", cmds, () => RelayoutParagraphsByIds(idsArr));

            // Apply мутирует модель и захватывает оригиналы для undo, затем колбэк пересобирает.
            composite.Apply(DocVm.Document);
            PushTextCommand(composite);

            // Обновляем состояние тулбара под кареткой: снапшотный путь делает это через
            // FireCursorContextChanged, а гранулярный — нет, и поле шрифта восстанавливалось
            // старым значением (выбор «сбрасывался»). Синхронизируем выделение в DocVm и явно,
            // синхронно пере-фаерим контекст — до того как дропдаун прочитает CurrentFontFamily
            // при закрытии.
            UpdateSelectionContext();
            DocVm.FireCursorContextChanged();
        }

        // Гранулярный коммит свойств рана (жирность/цвет/размер) на заданные диапазоны.
        // Строит по одной SetRunPropertyCommand на диапазон, объединяет в CompositeCommand и
        // пишет в лёгкий TextUndoStack — как и шрифт. Тогда отмена этих операций мгновенна и
        // идёт в общем порядке с набором текста. Возвращает true, если обработал.
        private bool CommitRunPropertyGranular(
            IReadOnlyList<(Guid ParaId, int From, int To)> ranges,
            Action<RunProperties> mutate, string desc)
        {
            if (TextUndoStack is null || DocVm is null || ranges.Count == 0)
                return false;

            var cmds = new List<ITextCommand>();
            var ids = new List<Guid>();
            foreach (var r in ranges)
            {
                if (r.To <= r.From) continue;
                cmds.Add(new SetRunPropertyCommand(r.ParaId, r.From, r.To, mutate, desc));
                ids.Add(r.ParaId);
            }
            if (cmds.Count == 0) return false;

            var idsArr = ids.ToArray();
            var composite = new CompositeCommand(desc, cmds, () => RelayoutParagraphsByIds(idsArr));
            composite.Apply(DocVm.Document);
            PushTextCommand(composite);
            return true;
        }

        // Точечный пересбор раскладки только для абзацев с указанными Id.
        // Вызывается как при применении шрифта, так и при undo/redo гранулярной команды.
        // Набор операций повторяет проверенный путь OnParagraphFormatChanged: выделение
        // не схлопываем (SyncSel) и каретку не сбрасываем (ResetCaret) — иначе каретка
        // рассинхронизируется с реальной позицией ввода.
        // Гранулярный коммит изменений текста (смена регистра) на заданные диапазоны.
        // Строит ChangeCaseCommand на каждый диапазон, объединяет в CompositeCommand и пишет
        // в общий TextUndoStack — отмена идёт в одном порядке с набором и форматированием.
        private bool CommitTextEditsGranular(
            IReadOnlyList<(Guid ParaId, int From, string OldText, string NewText)> edits, string desc)
        {
            if (TextUndoStack is null || DocVm is null || edits.Count == 0) return false;

            var cmds = new List<ITextCommand>();
            var ids = new List<Guid>();
            foreach (var e in edits)
            {
                cmds.Add(new ChangeCaseCommand(e.ParaId, e.From, e.OldText, e.NewText));
                ids.Add(e.ParaId);
            }
            var idsArr = ids.ToArray();
            var composite = new CompositeCommand(desc, cmds, () => RelayoutParagraphsByIds(idsArr));
            composite.Apply(DocVm.Document);
            PushTextCommand(composite);
            return true;
        }

        // Гранулярный коммит свойств абзаца (выравнивание/отступы/интервалы): строит
        // SetParagraphPropertyCommand на каждый абзац, объединяет в CompositeCommand и пишет
        // в общий TextUndoStack — отмена идёт в одном порядке с набором/форматированием, без
        // снапшота всего документа.
        private bool CommitParagraphPropertyGranular(
            IReadOnlyList<(Guid ParaId, Action<ParagraphProperties> Apply, Action<ParagraphProperties> Revert)> edits,
            string desc)
        {
            if (TextUndoStack is null || DocVm is null || edits.Count == 0) return false;

            var cmds = new List<ITextCommand>();
            var ids = new List<Guid>();
            foreach (var e in edits)
            {
                cmds.Add(new SetParagraphPropertyCommand(e.ParaId, e.Apply, e.Revert, desc));
                ids.Add(e.ParaId);
            }
            var idsArr = ids.ToArray();
            var composite = new CompositeCommand(desc, cmds, () => RelayoutParagraphsByIds(idsArr));
            composite.Apply(DocVm.Document);
            PushTextCommand(composite);
            return true;
        }

        private void RelayoutParagraphsByIds(IReadOnlyList<Guid> ids)
        {
            if (DocVm is null) return;

            var idSet = new HashSet<Guid>(ids);
            foreach (var pvm in DocVm.Paragraphs)
            {
                if (!idSet.Contains(pvm.Model.Id)) continue;
                pvm.RefreshPlainTextFromModel();
                _layoutCache.Remove(pvm);
            }

            // Абзацы внутри ячеек таблиц не входят в DocVm.Paragraphs — их раскладка
            // кешируется по cell-VM. Без этой инвалидации смена шрифта/формата в ячейке
            // не видна (кеш проверяет только текст и ширину, не форматирование).
            bool cellTouched = false;
            foreach (var kv in _cellVmCache)
            {
                if (!idSet.Contains(kv.Key.Id)) continue;
                kv.Value.RefreshPlainTextFromModel();
                _layoutCache.Remove(kv.Value);
                cellTouched = true;
            }
            if (cellTouched)
                InvalidateCellLayoutCaches();

            RebuildLayouts();
            // Сбрасываем подсказку строки каретки: после смены шрифта абзац мог перетечь
            // по строкам иначе, и старый _caretLineHint указывал бы DrawCaret на неверную
            // строку (каретка рисовалась бы не там, хотя ввод идёт по _caretChar верно).
            _caretLineHint = -1;
            SnapCaretToCorrectSlice();
            UpdatePreferredX();
            InvalidateFull();
        }

        // ── Снимок целей превью ────────────────────────────────────────────

        private void BuildPreviewTargets(
            List<(ParagraphBlock block, ParagraphViewModel? vm, int start, int end)> into)
        {
            into.Clear();
            if (DocVm is null) return;

            var seen = new HashSet<ParagraphBlock>();

            // 1. Диапазон ячеек таблицы — целиком каждый абзац каждой выделенной ячейки.
            //    vm == null: в cache-injection превью не участвуют (применятся при коммите).
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
                            into.Add((para, null, 0, para.TotalLength));
                        }
                    }
                }
            }

            // 1b. Каретка или выделение ТЕКСТА внутри одной ячейки (без выделения диапазона
            //     ячеек). Цель — параграф активной ячейки (vm == null). Коммит уйдёт через
            //     SetFontFamily, который применит шрифт к ячейке по выделению DocVm — тем же
            //     путём, что и остальное форматирование текста в ячейке. Без этого правка
            //     шрифта в ячейке не давала целей и ничего не применялось.
            //
            //     IsInCell спрашивает раскладку под кареткой прямо сейчас, и спрашивать
            //     обязательно. TableActiveCellParagraph — это запомненная ячейка, а не
            //     нынешнее место каретки: обнуляет её один SetActiveParagraph, до которого
            //     доходит только выход из ячейки через NotifyLeftCell. Любой другой путь
            //     наружу оставлял ссылку жить, и дальше она забирала себе весь предпросмотр:
            //     ветка срабатывала раньше обычного выделения, into переставал быть пустым,
            //     и ветка 3 (абзац под кареткой) уже не выполнялась. Человек выделял текст в
            //     теле рукописи, а шрифт примерялся к ячейке, которую он трогал десять минут
            //     назад.
            if (into.Count == 0 && IsInCell(_caretPara)
                && DocVm.TableActiveCellParagraph is { } cellPara
                && seen.Add(cellPara))
            {
                int cs = 0, ce = cellPara.TotalLength;
                // Если внутри ячейки выделена часть текста (одна ячейка = один абзац) — берём
                // именно его диапазон, чтобы и превью, и коммит применялись к выделенному куску.
                if (HasSel())
                {
                    var (sp, sc, ep, ec) = NormalizeSelection();
                    if (sp == ep && GetVmAt(sp)?.Model == cellPara)
                    {
                        cs = Math.Max(0, Math.Min(sc, ce));
                        ce = Math.Max(cs, Math.Min(ec, cellPara.TotalLength));
                    }
                }
                into.Add((cellPara, null, cs, ce));
            }

            // 2. Обычное выделение — берём напрямую из состояния канваса (sp..ep), для каждого
            //    уникального абзаца считаем частичный диапазон по краям. Сохраняем VM.
            if (HasSel())
            {
                var (sp, sc, ep, ec) = NormalizeSelection();
                for (int i = sp; i <= ep && i < _layouts.Count; i++)
                {
                    var vm = GetVmAt(i);
                    var model = vm?.Model;
                    if (model is null || !seen.Add(model)) continue;

                    int len = model.TotalLength;
                    int s = (i == sp) ? sc : 0;
                    int e = (i == ep) ? ec : len;
                    s = Math.Max(0, Math.Min(s, len));
                    e = Math.Max(s, Math.Min(e, len));
                    // Пустой кусок (selStart == selEnd) — селект лишь касается края абзаца.
                    // Пропускаем, как и коммит.
                    if (e <= s) continue;
                    into.Add((model, vm, s, e));
                }
            }

            // 3. Нет выделения вообще — активный абзац целиком.
            if (into.Count == 0)
            {
                var vm = GetVmAt(_caretPara);
                if (vm?.Model is not null) into.Add((vm.Model, vm, 0, vm.Model.TotalLength));
            }
        }

        private void ClearPreviewState()
        {
            CancelPreviewRebuild();
            _cellFontPreview.Clear();
            _fontPreviewActive = false;
            _previewFont = null;
            _previewTargets.Clear();
            _previewSavedLayouts.Clear();
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

                    // Объект в строке шрифт не меняет — копируем целиком вместе со ссылкой
                    // на картинку, иначе в предпросмотре вместо неё оказался бы
                    // символ-заполнитель.
                    if (run.IsInlineObject)
                    {
                        newChunk.Runs.Add(new RunModel
                        {
                            Text = run.Text,
                            Properties = run.Properties,
                            InlineImageId = run.InlineImageId
                        });
                        continue;
                    }

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