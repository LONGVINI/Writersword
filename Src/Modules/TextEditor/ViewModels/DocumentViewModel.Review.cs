using System;
using System.Collections.Generic;
using System.Text.Json;
using ReactiveUI;
using Writersword.Core.Interfaces.Services.UI;
using Writersword.Core.Services;
using Writersword.Modules.TextEditor.Contracts;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Models.Styles;
using Writersword.Modules.TextEditor.Services;

namespace Writersword.Modules.TextEditor.ViewModels
{
    /// <summary>
    /// Рецензирование в документе: запись исправлений, вид их показа, имя
    /// рецензента, запись смены оформления.
    ///
    /// Правку текста под рецензированием ведёт полотно (DocumentCanvas.TrackChanges):
    /// там каретка, раскладка и шаги отмены. Здесь — то, что идёт через вью-модель:
    /// оформление фрагментов и абзацев. Пока запись включена, смена оформления
    /// запоминает прежнее (w:rPrChange, w:pPrChange), и его можно вернуть
    /// отклонением правки.
    /// </summary>
    public sealed partial class DocumentViewModel
    {
        private static readonly JsonSerializerOptions FormattingKeyOptions = new()
        {
            WriteIndented = false
        };

        private string _reviewerName = DefaultReviewerName();

        /// <summary>Вид показа исправлений сменился — полотно пересобирает раскладку.</summary>
        public event Action? RevisionViewChanged;

        /// <summary>Правки в документе или запись исправлений изменились — для ленты.</summary>
        public event Action? RevisionsChanged;

        /// <summary>
        /// Кнопка рецензирования. Ставит полотно: принятие, отклонение и переход идут
        /// по каретке и выделению. False — делать нечего.
        /// </summary>
        public Func<ReviewAction, bool>? ReviewCommandDelegate { get; set; }

        // ── Панель рецензии ───────────────────────────────────────────────
        // Панель живёт в модели модуля и о листе ничего не знает: перейти к месту,
        // подсветить его, принять или отклонить правки она просит полотно через эти
        // делегаты. Ставит их полотно, когда получает документ.

        /// <summary>Выделить плавающую картинку и прокрутить к ней.</summary>
        public Action<ImageBlock>? GoToReviewImageDelegate { get; set; }

        /// <summary>Подсветить плавающую картинку цветом (ARGB); null — снять подсветку.</summary>
        public Action<ImageBlock?, uint>? HighlightReviewImageDelegate { get; set; }

        /// <summary>Принять или отклонить правки плавающих картинок одним шагом отмены.</summary>
        public Action<IReadOnlyList<ImageBlock>, bool>? ResolveImageRevisionsDelegate { get; set; }

        /// <summary>Принять или отклонить правки разрывов и фигур одним шагом отмены.</summary>
        public Action<IReadOnlyList<BlockModel>, bool>? ResolveBlockRevisionsDelegate { get; set; }

        /// <summary>Принять или отклонить правки разрывов и фигур.</summary>
        public void ResolveBlockRevisions(IReadOnlyList<BlockModel> blocks, bool accept)
        {
            if (IsReadOnly || blocks.Count == 0) return;
            if (RefuseLockedResolve()) return;
            ResolveBlockRevisionsDelegate?.Invoke(blocks, accept);
        }

        public void GoToReviewImage(ImageBlock image) => GoToReviewImageDelegate?.Invoke(image);

        public void HighlightReviewImage(ImageBlock? image, uint argb) => HighlightReviewImageDelegate?.Invoke(image, argb);

        public void ResolveImageRevisions(IReadOnlyList<ImageBlock> images, bool accept)
        {
            if (IsReadOnly || images.Count == 0) return;
            if (RefuseLockedResolve()) return;
            ResolveImageRevisionsDelegate?.Invoke(images, accept);
        }

        /// <summary>Выделить место в тексте и прокрутить к нему: абзац, знаки, знак абзаца ли это.</summary>
        public Action<ParagraphBlock, int, int, bool>? GoToReviewPlaceDelegate { get; set; }

        /// <summary>Подсветить места в тексте цветом (ARGB); null — снять подсветку.</summary>
        public Action<IReadOnlyList<(ParagraphBlock Paragraph, int From, int To)>?, uint>? HighlightReviewPlacesDelegate { get; set; }

        /// <summary>Принять (true) или отклонить правки одним шагом отмены.</summary>
        public Action<IReadOnlyList<RevisionService.Hit>, bool>? ResolveRevisionHitsDelegate { get; set; }

        /// <summary>Раскладка пересчитала листы: номера страниц мест могли уехать.</summary>
        public event Action? PagesRecounted;

        public void RaisePagesRecounted() => PagesRecounted?.Invoke();

        /// <summary>Выделить место пункта рецензии в тексте.</summary>
        public void GoToReviewPlace(ParagraphBlock paragraph, int from, int to, bool isMark)
            => GoToReviewPlaceDelegate?.Invoke(paragraph, from, to, isMark);

        /// <summary>Подсветить места пункта рецензии; null — снять.</summary>
        public void HighlightReviewPlaces(IReadOnlyList<(ParagraphBlock Paragraph, int From, int To)>? places, uint argb)
            => HighlightReviewPlacesDelegate?.Invoke(places, argb);

        /// <summary>Принять или отклонить правки.</summary>
        public void ResolveRevisionHits(IReadOnlyList<RevisionService.Hit> hits, bool accept)
        {
            if (IsReadOnly || hits.Count == 0) return;
            if (RefuseLockedResolve()) return;
            ResolveRevisionHitsDelegate?.Invoke(hits, accept);
        }

        /// <summary>
        /// Имя, которым подписываются правки. Пустое — имя пользователя системы,
        /// как у Word без заданного имени.
        /// </summary>
        public string ReviewerName
        {
            get => _reviewerName;
            set => _reviewerName = string.IsNullOrWhiteSpace(value) ? DefaultReviewerName() : value.Trim();
        }

        /// <summary>Имя пользователя системы — рецензент по умолчанию.</summary>
        public static string DefaultReviewerName()
        {
            string name = Environment.UserName;
            return string.IsNullOrWhiteSpace(name) ? "Writersword" : name;
        }

        /// <summary>Запись исправлений включена.</summary>
        public bool TrackRevisions => _document.TrackRevisions;

        /// <summary>
        /// Включает или выключает запись исправлений. Это свойство документа: оно
        /// уходит в .docx (w:trackRevisions) и приходит из него, как у Word.
        /// </summary>
        public void SetTrackRevisions(bool enabled)
        {
            if (_document.TrackRevisions == enabled) return;

            // Под защитой запись не выключается: лента вернёт тумблер по событию.
            if (!enabled && IsRevisionLocked)
            {
                NotifyRevisionLocked();
                RevisionsChanged?.Invoke();
                return;
            }

            _document.TrackRevisions = enabled;
            this.RaisePropertyChanged(nameof(TrackRevisions));
            RaiseContentModified();
            RevisionsChanged?.Invoke();
        }

        /// <summary>Вид показа исправлений.</summary>
        public RevisionView RevisionView => _document.RevisionView;

        /// <summary>Меняет вид показа исправлений. Документ при этом не меняется.</summary>
        public void SetRevisionView(RevisionView view)
        {
            if (_document.RevisionView == view) return;

            _document.RevisionView = view;
            this.RaisePropertyChanged(nameof(RevisionView));
            RevisionViewChanged?.Invoke();
            RevisionsChanged?.Invoke();
        }

        /// <summary>Сообщает ленте, что правки в документе изменились.</summary>
        public void RaiseRevisionsChanged() => RevisionsChanged?.Invoke();

        /// <summary>Сколько правок в документе.</summary>
        public int CountRevisions() => RevisionService.CountRevisions(_document);

        /// <summary>Выполняет кнопку рецензирования через полотно.</summary>
        public bool ExecuteReview(ReviewAction action)
        {
            if (action is ReviewAction.Accept or ReviewAction.Reject or ReviewAction.AcceptAll or ReviewAction.RejectAll
                && RefuseLockedResolve())
                return false;

            bool done = ReviewCommandDelegate?.Invoke(action) ?? false;
            if (done) RevisionsChanged?.Invoke();
            return done;
        }

        /// <summary>
        /// Отметка новой правки: рецензент и время. Время — с точностью до минуты, как
        /// пишет Word: подряд набранное за минуту остаётся одной правкой.
        /// </summary>
        public RevisionInfo NewRevisionInfo()
        {
            var now = DateTime.UtcNow;
            var minute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);

            return new RevisionInfo
            {
                Author = _reviewerName,
                Date = minute
            };
        }

        // ── Защита записи исправлений ─────────────────────────────────────
        // Защита — как «Ограничить редактирование → Исправления» в Word: запись не
        // выключить, правки не принять и не отклонить. Кто правит защищённый
        // документ, может только предлагать — каждая его правка остаётся видна тому,
        // кто снимет защиту и будет их разбирать.

        /// <summary>Защита записи исправлений стоит.</summary>
        public bool IsRevisionLocked => _document.RevisionProtection is not null;

        /// <summary>Защита снимается только паролем.</summary>
        public bool RevisionLockHasPassword => _document.RevisionProtection?.HasPassword == true;

        /// <summary>
        /// Ставит защиту. Запись исправлений включается и остаётся включённой, пока
        /// защита стоит. Пустой пароль — защита без пароля.
        /// </summary>
        public void LockRevisions(string? password)
        {
            if (IsReadOnly || IsRevisionLocked) return;

            _document.RevisionProtection = RevisionLock.Create(password);
            _document.TrackRevisions = true;

            this.RaisePropertyChanged(nameof(TrackRevisions));
            this.RaisePropertyChanged(nameof(IsRevisionLocked));
            RaiseContentModified();
            RevisionsChanged?.Invoke();
        }

        /// <summary>
        /// Снимает защиту. False — пароль не подошёл. Запись исправлений остаётся
        /// включённой: выключить её — отдельное решение.
        /// </summary>
        public bool UnlockRevisions(string? password)
        {
            if (_document.RevisionProtection is not { } protection) return true;
            if (IsReadOnly) return false;
            if (!RevisionLock.Verify(protection, password)) return false;

            _document.RevisionProtection = null;

            this.RaisePropertyChanged(nameof(IsRevisionLocked));
            RaiseContentModified();
            RevisionsChanged?.Invoke();
            return true;
        }

        /// <summary>Сообщает, что документ защищён и правки разбирать нельзя.</summary>
        public void NotifyRevisionLocked()
        {
            CoreServices.GetService<INotificationService>()?.ShowWarning(
                "Документ защищён: правки можно только предлагать. Принять или отклонить их, выключить запись исправлений " +
                "и заменить текст импортом можно после снятия защиты — «Рецензирование» → «Защита»");
        }

        /// <summary>
        /// Действие под защитой откатилось: правкой его не разметить (смена списка,
        /// удаление строки таблицы с текстом и подобное).
        /// </summary>
        public void NotifyLockedActionRefused()
        {
            CoreServices.GetService<INotificationService>()?.ShowWarning(
                "Документ защищён: это действие нельзя записать исправлением, поэтому оно отменено. " +
                "Под защитой текст меняется только правками");
        }

        /// <summary>Отмена или повтор под защитой не прошли: шаг меняет текст мимо правок.</summary>
        public void NotifyLockedUndoRefused(bool wasUndo)
        {
            CoreServices.GetService<INotificationService>()?.ShowWarning(wasUndo
                ? "Документ защищён: отменить правку, сделанную до защиты, нельзя — это изменило бы текст мимо записи исправлений"
                : "Документ защищён: повторить этот шаг нельзя — он меняет текст мимо записи исправлений");
        }

        /// <summary>
        /// Идёт действие, которое само расставляет правки или заменяет документ
        /// целиком (сравнение с файлом, импорт). Полотно не размечает его разницей
        /// «до/после» — оно приняло бы такое действие за обычную правку.
        /// </summary>
        public bool AutoTrackingSuppressed { get; private set; }

        /// <summary>Защита стоит — разбор правок не идёт, человек узнаёт почему.</summary>
        private bool RefuseLockedResolve()
        {
            if (!IsRevisionLocked) return false;
            NotifyRevisionLocked();
            return true;
        }

        // ── Запись смены оформления ───────────────────────────────────────

        /// <summary>
        /// Оборачивает правку оформления фрагмента: при включённой записи прежнее
        /// оформление запоминается в смене оформления (w:rPrChange). Смена,
        /// вернувшая прежнее оформление, снимается. Вставленный под рецензированием
        /// текст смены оформления не получает — его оформление часть вставки.
        /// </summary>
        private Action<RunProperties> TrackRunFormatting(Action<RunProperties> mutate)
        {
            if (!_document.TrackRevisions) return mutate;

            var info = NewRevisionInfo();

            return props =>
            {
                var before = props.WithoutRevisions();
                mutate(props);

                if (props.Inserted is not null) return;

                if (props.FormatChange is { } existing)
                {
                    if (RunProperties.SameFormatting(existing.Previous, props))
                        props.FormatChange = null;
                    return;
                }

                if (RunProperties.SameFormatting(before, props)) return;

                props.FormatChange = new RunFormatChange
                {
                    Info = info.Clone(),
                    Previous = before.IsDefaultFormatting() ? null : before
                };
            };
        }

        /// <summary>
        /// Оборачивает правку оформления абзаца: при включённой записи прежнее
        /// оформление запоминается (w:pPrChange). Вставленный под рецензированием
        /// абзац смены оформления не получает.
        /// </summary>
        private Action<ParagraphProperties, int, int> TrackParagraphFormatting(
            Action<ParagraphProperties, int, int> mutate)
        {
            if (!_document.TrackRevisions) return mutate;

            var info = NewRevisionInfo();

            return (props, index, count) =>
            {
                var before = props.WithoutRevisions();
                mutate(props, index, count);

                if (props.MarkInserted is not null) return;

                string after = FormattingKey(props);

                if (props.FormatChange is { } existing)
                {
                    if (existing.Previous is not null
                        && string.Equals(FormattingKey(existing.Previous), after, StringComparison.Ordinal))
                        props.FormatChange = null;
                    return;
                }

                if (string.Equals(FormattingKey(before), after, StringComparison.Ordinal)) return;

                props.FormatChange = new ParagraphFormatChange
                {
                    Info = info.Clone(),
                    Previous = before
                };
            };
        }

        /// <summary>Оформление абзаца строкой — для сравнения «стало ли оно другим».</summary>
        private static string FormattingKey(ParagraphProperties props)
            => JsonSerializer.Serialize(props.WithoutRevisions(), FormattingKeyOptions);
    }
}
