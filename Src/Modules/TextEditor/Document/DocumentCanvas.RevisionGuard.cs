using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using Writersword.Modules.TextEditor.Commands;
using Writersword.Modules.TextEditor.Services;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Запись исправлений для действий, которые сами её не ведут, и защита записи.
    ///
    /// Набор, удаление, вставка из буфера, оформление записываются правкой каждое
    /// своим путём. Остальные действия редактора — вставка таблицы и разрыва,
    /// правка строк и столбцов, обновление оглавления, смена регистра и прочие —
    /// меняли документ мимо записи. Теперь при включённой записи каждое такое
    /// действие после выполнения проверяется: если оно изменило документ, каким он
    /// станет при отклонении всех правок (<see cref="RevisionSignature"/>), разница
    /// «до» и «после» размечается правкой (<see cref="CompareMarkup.Options.TrackEdit"/>),
    /// а шаг отмены становится снимком уже размеченного документа.
    ///
    /// Под защитой записи (<see cref="ViewModels.DocumentViewModel.IsRevisionLocked"/>)
    /// правило строже: шаг, после которого вид «правки отклонены» всё равно другой —
    /// то есть то, что правкой не размечается (смена списка, удаление строки
    /// таблицы с текстом), — откатывается, и человек узнаёт почему. Так же
    /// проверяются отмена и повтор: Ctrl+Z правки, сделанной до защиты, под защитой
    /// не проходит. Итог: под защитой документ меняется только правками.
    ///
    /// Проверка идёт сразу после действия, тем же проходом диспетчера: действие
    /// успевает закончить свою работу с кареткой и раскладкой, а человек — ещё
    /// ничего не нажать.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        private enum GuardItemKind
        {
            /// <summary>Снимок документа обычного действия.</summary>
            Snapshot,

            /// <summary>Снимок действия рецензирования (запись правками, принятие, отклонение).</summary>
            TrackedSnapshot,

            /// <summary>Снимок, который сам ставит правки или заменяет документ (сравнение, импорт).</summary>
            SelfMarkedSnapshot,

            /// <summary>Лёгкая команда операционного стека.</summary>
            Text
        }

        private readonly record struct GuardItem(GuardItemKind Kind, object? Command);

        private readonly List<GuardItem> _guardQueue = new();
        private bool _guardPosted;
        private bool _guardBusy;

        // Отпечаток вида «все правки отклонены» после последней проверки. Null — запись
        // исправлений выключена, следить не за чем.
        private string? _rejectViewSignature;

        // Состояние, для которого посчитан отпечаток: смена записи или защиты
        // пересчитывает его.
        private bool _guardTracking;
        private bool _guardLocked;

        // Идёт завершение снимка действия рецензирования.
        private bool _endingTrackedSnapshot;

        private bool GuardTracking => DocVm?.Document.TrackRevisions == true;

        private bool GuardLocked => DocVm?.IsRevisionLocked == true;

        /// <summary>
        /// Запись или защита включились или выключились: отпечаток считается заново —
        /// это новая точка отсчёта.
        /// </summary>
        private void OnRevisionStateForGuard()
        {
            bool tracking = GuardTracking;
            bool locked = GuardLocked;
            if (tracking == _guardTracking && locked == _guardLocked && (_rejectViewSignature is not null) == tracking)
                return;

            _guardTracking = tracking;
            _guardLocked = locked;
            ResyncRejectView();
        }

        /// <summary>Отпечаток заново — без проверки, как точка отсчёта.</summary>
        private void ResyncRejectView()
        {
            _rejectViewSignature = DocVm is { } vm && vm.Document.TrackRevisions
                ? RevisionSignature.Compute(vm.Document)
                : null;
        }

        /// <summary>Новый документ на полотне: отслеживание начинается с него.</summary>
        private void ResetRevisionGuard()
        {
            _guardQueue.Clear();
            _guardTracking = GuardTracking;
            _guardLocked = GuardLocked;
            ResyncRejectView();
        }

        // ── Постановка в очередь ──────────────────────────────────────────

        /// <summary>Шаг снимочного стека положен (вызывается из RecordSnapshotInOrder).</summary>
        private void OnSnapshotStepRecorded()
        {
            if (_guardBusy || _rejectViewSignature is null || UndoStack is null) return;

            var top = UndoStack.CanUndo ? UndoStack.AllCommands.FirstOrDefault() : null;

            var kind = _endingTrackedSnapshot
                ? GuardItemKind.TrackedSnapshot
                : DocVm?.AutoTrackingSuppressed == true
                    ? GuardItemKind.SelfMarkedSnapshot
                    : GuardItemKind.Snapshot;

            QueueGuard(new GuardItem(kind, top));
        }

        /// <summary>Лёгкая команда положена в операционный стек (вызывается из PushTextCommand).</summary>
        private void OnTextStepPushed(ITextCommand command)
        {
            if (_guardBusy || _rejectViewSignature is null) return;

            // Набор и удаление под записью — правки сами по себе: проверять их на каждой
            // букве незачем, а отпечаток большой рукописи — десятки миллисекунд.
            if (command is RevisionParagraphCommand) return;

            // Внутри открытого снимка шаг войдёт в снимок — проверяется он.
            if (_pendingSnapshot is not null) return;

            QueueGuard(new GuardItem(GuardItemKind.Text, command));
        }

        private void QueueGuard(GuardItem item)
        {
            _guardQueue.Add(item);
            if (_guardPosted) return;

            _guardPosted = true;
            Dispatcher.UIThread.Post(RunRevisionGuard, DispatcherPriority.Normal);
        }

        // ── Проверка ──────────────────────────────────────────────────────

        private void RunRevisionGuard()
        {
            _guardPosted = false;
            if (_guardQueue.Count == 0) return;

            var items = _guardQueue.ToArray();
            _guardQueue.Clear();

            if (DocVm is null || _rejectViewSignature is null) return;

            _guardBusy = true;
            bool refresh = false;
            bool refused = false;

            try
            {
                foreach (var item in items)
                {
                    if (_rejectViewSignature is null) break;
                    ProcessGuardItem(item, ref refresh, ref refused);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[REVISIONS] Проверка действия под записью исправлений упала; отсчёт взят заново");
                ResyncRejectView();
            }
            finally
            {
                _guardBusy = false;
            }

            if (refresh) RefreshAfterAutoTrack();
            if (refused) DocVm?.NotifyLockedActionRefused();
        }

        private void ProcessGuardItem(GuardItem item, ref bool refresh, ref bool refused)
        {
            var vm = DocVm!;
            var doc = vm.Document;

            switch (item.Kind)
            {
                case GuardItemKind.SelfMarkedSnapshot:
                    // Сравнение и импорт сами решают, что в документе правка.
                    ResyncRejectView();
                    return;

                case GuardItemKind.TrackedSnapshot:
                    if (GuardLocked)
                        ValidateLockedStep(ref refresh, ref refused);
                    else
                        ResyncRejectView();
                    return;
            }

            string after = RevisionSignature.Compute(doc);
            if (after == _rejectViewSignature) return;

            bool marked = item.Kind == GuardItemKind.Snapshot
                ? item.Command is ImagePropertiesCommand or ShapePropertiesCommand
                    ? AutoTrackObjectCommand(item.Command)
                    : AutoTrackSnapshot(item.Command as DocumentSnapshotCommand)
                : AutoTrackTextCommand(item.Command as ITextCommand);

            if (marked)
            {
                refresh = true;
                after = RevisionSignature.Compute(doc);
                if (after == _rejectViewSignature)
                {
                    _logger.Debug("[REVISIONS] Действие размечено правкой по разнице «до/после»");
                    return;
                }
            }

            if (GuardLocked)
            {
                // Под защитой изменение мимо правок не проходит.
                if (UndoNewestStepForGuard())
                {
                    refresh = true;
                    refused = true;

                    string restored = RevisionSignature.Compute(doc);
                    if (restored == _rejectViewSignature) return;

                    _logger.Warning("[REVISIONS] После отката действия под защитой документ не вернулся к прежнему виду — отсчёт взят заново");
                    _rejectViewSignature = restored;
                    return;
                }

                _logger.Warning("[REVISIONS] Действие под защитой изменило документ мимо правок, а шага для отката нет — отсчёт взят заново");
                _rejectViewSignature = after;
                return;
            }

            // Без защиты то, что правкой не размечается, остаётся как сделано.
            _logger.Information("[REVISIONS] Действие изменило документ так, что правкой это не размечается; оставлено как есть");
            _rejectViewSignature = after;
        }

        /// <summary>
        /// Шаг рецензирования под защитой: принимать и отклонять под ней нельзя, а
        /// запись правками вид «правки отклонены» не меняет. Изменил — откат.
        /// </summary>
        private void ValidateLockedStep(ref bool refresh, ref bool refused)
        {
            var doc = DocVm!.Document;
            string after = RevisionSignature.Compute(doc);
            if (after == _rejectViewSignature) return;

            if (UndoNewestStepForGuard())
            {
                refresh = true;
                refused = true;

                string restored = RevisionSignature.Compute(doc);
                if (restored != _rejectViewSignature)
                {
                    _logger.Warning("[REVISIONS] Откат шага рецензирования под защитой не вернул документ — отсчёт взят заново");
                    _rejectViewSignature = restored;
                }
                return;
            }

            _rejectViewSignature = after;
        }

        // ── Разметка действия ─────────────────────────────────────────────

        /// <summary>Параметры разметки действия: автор — рецензент документа.</summary>
        private CompareMarkup.Options AutoTrackOptions() => new()
        {
            Info = DocVm!.NewRevisionInfo(),
            ShowMarkup = false,
            TrackEdit = true
        };

        /// <summary>
        /// Снимок действия: документ до него восстанавливается из снимка, разница
        /// размечается, и снимок запоминает уже размеченный документ «после».
        /// </summary>
        private bool AutoTrackSnapshot(DocumentSnapshotCommand? command)
        {
            if (command is null || UndoStack is null || !UndoStack.CanUndo) return false;
            if (!ReferenceEquals(UndoStack.AllCommands.FirstOrDefault(), command)) return false;

            var before = command.DeserializeBefore();
            if (before is null) return false;

            var result = CompareMarkup.Apply(DocVm!.Document, before, AutoTrackOptions());
            if (result.Total == 0) return false;

            command.Commit(CurrentEditPlace());
            return true;
        }

        /// <summary>
        /// Шаг вида картинки или фигуры (сдвиг, размер, поворот, обрезка, обтекание,
        /// рамка): прежний вид запоминается сменой вида объекта, и шаг отмены
        /// запоминает объект уже с отметкой — отмена снимает её вместе с видом.
        /// </summary>
        private bool AutoTrackObjectCommand(object? command)
        {
            if (DocVm is null) return false;

            switch (command)
            {
                case ImagePropertiesCommand imageCommand:
                    if (!ObjectAppearance.Track(imageCommand.Target, imageCommand.BeforeAppearance, DocVm.NewRevisionInfo()))
                        return false;
                    imageCommand.Commit();
                    _inlineRevisionMarks = null;
                    return true;

                case ShapePropertiesCommand shapeCommand:
                    if (!ObjectAppearance.Track(shapeCommand.Target, shapeCommand.BeforeAppearance, DocVm.NewRevisionInfo()))
                        return false;
                    shapeCommand.Commit();
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Лёгкая команда: откатывается, документ «до» запоминается копией и
        /// снимком, команда применяется снова, разница размечается. В истории отмены
        /// команду заменяет снимок: откат лёгкой команды по размеченному документу
        /// дал бы не тот текст.
        /// </summary>
        private bool AutoTrackTextCommand(ITextCommand? command)
        {
            if (command is null || TextUndoStack is null || UndoStack is null || DocVm is null) return false;
            if (!ReferenceEquals(TextUndoStack.PeekUndo, command)) return false;
            if (_undoOrder.Last?.Value != UndoSource.Text) return false;

            var doc = DocVm.Document;

            command.Revert(doc);
            var before = DocumentCloner.Clone(doc);
            var snapshot = new DocumentSnapshotCommand(DocVm, command.Description, CurrentEditPlace());
            snapshot.RestorePlaceCallback = place => _pendingRestorePlace = place;
            command.Apply(doc);

            var result = CompareMarkup.Apply(doc, before, AutoTrackOptions());
            snapshot.Commit(CurrentEditPlace());

            // Команда уходит из лёгкого стека без отката: её действие уже в снимке.
            TextUndoStack.DiscardLast();
            _undoOrder.RemoveLast();

            UndoStack.Push(snapshot);
            _undoOrder.AddLast(UndoSource.Snapshot);
            _redoOrder.Clear();

            _logger.Debug("[REVISIONS] Команда «{D}» заменена размеченным снимком: вставок {I}, удалений {Del}, оформления {F}",
                command.Description, result.InsertedPlaces, result.DeletedPlaces, result.FormatPlaces);
            return true;
        }

        /// <summary>Откатывает последний шаг истории — без вопросов и фантомов обычной отмены.</summary>
        private bool UndoNewestStepForGuard()
        {
            while (_undoOrder.Last is not null)
            {
                var source = _undoOrder.Last.Value;
                _undoOrder.RemoveLast();

                bool undone = source == UndoSource.Text ? UndoTextStep() : UndoSnapshotStep();
                if (undone)
                {
                    _redoOrder.Push(source);
                    return true;
                }
            }

            return false;
        }

        /// <summary>Раскладка и вью-модели после разметки или отката.</summary>
        private void RefreshAfterAutoTrack()
        {
            if (DocVm is null) return;

            DocVm.RebuildParagraphViewModelsPublic();
            DocVm.FireParagraphFormatChanged();
            RefreshAfterTableCommand();
            ApplyPendingRestorePlace();
            DocVm.RaiseRevisionsChanged();
            DocVm.RaiseContentModified();
        }

        // ── Отмена и повтор ───────────────────────────────────────────────

        /// <summary>
        /// После отмены или повтора. Под защитой шаг, изменивший документ мимо
        /// правок (правка, сделанная до защиты), возвращается обратно. Без защиты
        /// отсчёт берётся заново: отмена принятия правок законно меняет вид.
        /// </summary>
        /// <param name="source">Стек, в котором прошёл шаг.</param>
        /// <param name="wasUndo">Это была отмена (иначе повтор).</param>
        /// <returns>true — шаг возвращён обратно.</returns>
        private bool GuardUndoRedo(UndoSource source, bool wasUndo)
        {
            if (_guardBusy || DocVm is null || _rejectViewSignature is null) return false;

            if (!GuardLocked)
            {
                ResyncRejectView();
                return false;
            }

            string now = RevisionSignature.Compute(DocVm.Document);
            if (now == _rejectViewSignature) return false;

            _guardBusy = true;
            try
            {
                if (wasUndo)
                {
                    if (_redoOrder.Count > 0) _redoOrder.Pop();
                    bool redone = source == UndoSource.Text ? RedoTextStep() : RedoSnapshotStep();
                    if (redone) _undoOrder.AddLast(source);
                }
                else
                {
                    if (_undoOrder.Last is not null) _undoOrder.RemoveLast();
                    bool undone = source == UndoSource.Text ? UndoTextStep() : UndoSnapshotStep();
                    if (undone) _redoOrder.Push(source);
                }
            }
            finally
            {
                _guardBusy = false;
            }

            string restored = RevisionSignature.Compute(DocVm.Document);
            if (restored != _rejectViewSignature)
            {
                _logger.Warning("[REVISIONS] Возврат отмены под защитой не вернул документ — отсчёт взят заново");
                _rejectViewSignature = restored;
            }

            RefreshAfterAutoTrack();
            DocVm.NotifyLockedUndoRefused(wasUndo);
            return true;
        }
    }
}
