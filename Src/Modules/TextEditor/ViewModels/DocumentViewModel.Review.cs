using System;
using System.Text.Json;
using ReactiveUI;
using Writersword.Modules.TextEditor.Contracts;
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
