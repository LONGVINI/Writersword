using System;
using System.Collections.Generic;
using Avalonia.Threading;
using ReactiveUI;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Services;
using Writersword.Modules.TextEditor.ViewModels.Review;

namespace Writersword.Modules.TextEditor.ViewModels
{
    /// <summary>
    /// Панель рецензии справа от листов (Views/Review/ReviewPanel) и номера страниц
    /// боковых панелей.
    ///
    /// Панель, как и навигатор, живёт всю сессию модуля: открыта она или нет — выбор
    /// человека, а не свойство рукописи. Пока закрыта, документ не обходится. Открыта
    /// — список пересобирается после паузы в правке, а номера страниц обновляются,
    /// когда раскладка пересчитала листы.
    /// </summary>
    public sealed partial class TextEditorViewModel : IReviewPanelHost
    {
        // Пауза перед пересборкой списка: правки и примечания меняются на каждом знаке
        // набора под рецензированием, а панель — не то место, куда человек смотрит,
        // пока набирает.
        private static readonly TimeSpan ReviewRebuildDelay = TimeSpan.FromMilliseconds(300);

        // Пауза перед обновлением номеров страниц после пересчёта листов.
        private static readonly TimeSpan SidePagesDelay = TimeSpan.FromMilliseconds(250);

        private ReviewPanelViewModel? _reviewPanel;
        private DocumentViewModel? _reviewPanelDocument;
        private DispatcherTimer? _reviewRebuildTimer;
        private DispatcherTimer? _sidePagesTimer;

        /// <summary>Панель рецензии.</summary>
        public ReviewPanelViewModel ReviewPanel => _reviewPanel ??= new ReviewPanelViewModel(this);

        /// <summary>Панель рецензии показана.</summary>
        public bool IsReviewPanelVisible
        {
            get => ReviewPanel.IsVisible;
            set
            {
                if (ReviewPanel.IsVisible == value) return;
                ReviewPanel.IsVisible = value;
                this.RaisePropertyChanged();

                if (value) RefreshReviewPanel();
                else ReviewPanel.Hover(null);

                ReviewStateChanged?.Invoke();
            }
        }

        /// <summary>Показать или убрать панель рецензии.</summary>
        public void ToggleReviewPanel() => IsReviewPanelVisible = !IsReviewPanelVisible;

        /// <summary>Подключает панель рецензии и номера страниц боковых панелей к документу.</summary>
        private void AttachReviewPanel(DocumentViewModel docVm)
        {
            if (_reviewPanelDocument is { } previous)
            {
                previous.RevisionsChanged -= ScheduleReviewRefresh;
                previous.CommentsChanged -= ScheduleReviewRefresh;
                previous.StructureChanged -= ScheduleReviewRefresh;
                previous.PagesRecounted -= OnPagesRecounted;
                previous.HighlightReviewPlaces(null, 0);
            }

            _reviewPanelDocument = docVm;
            docVm.RevisionsChanged += ScheduleReviewRefresh;
            docVm.CommentsChanged += ScheduleReviewRefresh;
            docVm.StructureChanged += ScheduleReviewRefresh;
            docVm.PagesRecounted += OnPagesRecounted;

            if (ReviewPanel.IsVisible) RefreshReviewPanel();
        }

        private void ScheduleReviewRefresh()
        {
            if (!ReviewPanel.IsVisible) return;

            _reviewRebuildTimer ??= CreateTimer(ReviewRebuildDelay, RefreshReviewPanel);
            _reviewRebuildTimer.Stop();
            _reviewRebuildTimer.Start();
        }

        private void OnPagesRecounted()
        {
            // Раскладка пересчитана после любой правки — и после отмены, которая о
            // правках отдельно не сообщает. Список панели сверяется с документом
            // заново: принятая по ошибке правка, возвращённая Ctrl+Z, снова в нём.
            // Неизменившиеся карточки при этом остаются на месте.
            ScheduleReviewRefresh();

            if (!Navigator.IsVisible) return;

            _sidePagesTimer ??= CreateTimer(SidePagesDelay, RefreshNavigatorPages);
            _sidePagesTimer.Stop();
            _sidePagesTimer.Start();
        }

        private static DispatcherTimer CreateTimer(TimeSpan interval, Action action)
        {
            var timer = new DispatcherTimer { Interval = interval };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                action();
            };
            return timer;
        }

        /// <summary>Пересобирает список панели по текущему документу.</summary>
        public void RefreshReviewPanel()
        {
            _reviewRebuildTimer?.Stop();

            var docVm = DocumentViewModel;
            if (docVm is null)
            {
                ReviewPanel.Rebuild(Array.Empty<ReviewEntry>(), new Dictionary<Guid, int>());
                return;
            }

            long startTs = System.Diagnostics.Stopwatch.GetTimestamp();
            ReviewPanel.Rebuild(ReviewCollector.Collect(docVm.Document), docVm.GetBlockPageNumbers());

            // Разбор задержки набора: пересборка панели идёт на потоке интерфейса.
            double ms = System.Diagnostics.Stopwatch.GetElapsedTime(startTs).TotalMilliseconds;
            if (ms >= 15)
                _logger.Information("[PERF] review.refresh {Ms:F1} мс", ms);
        }

        // ── IReviewPanelHost ──────────────────────────────────────────────

        public void ReviewGoTo(ReviewEntry entry)
        {
            if (DocumentViewModel is not { } docVm) return;

            if (entry.Image is { } image)
            {
                docVm.GoToReviewImage(image);
                return;
            }

            if (entry.Kind is ReviewKind.ParagraphInserted or ReviewKind.ParagraphDeleted)
            {
                int length = entry.Paragraph.TotalLength;
                docVm.GoToReviewPlace(entry.Paragraph, length, length, true);
                return;
            }

            docVm.GoToReviewPlace(entry.Paragraph, entry.From, entry.To, false);
        }

        public void ReviewHighlight(ReviewEntry? entry, uint argb)
        {
            if (DocumentViewModel is not { } docVm) return;

            if (entry?.Image is { } image)
            {
                docVm.HighlightReviewPlaces(null, 0);
                docVm.HighlightReviewImage(image, argb);
                return;
            }

            docVm.HighlightReviewImage(null, 0);
            docVm.HighlightReviewPlaces(entry?.Ranges, argb);
        }

        public void ReviewResolveImages(IReadOnlyList<ImageBlock> images, bool accept)
        {
            DocumentViewModel?.ResolveImageRevisions(images, accept);
            ScheduleReviewRefresh();
        }

        public void ReviewResolveBlocks(IReadOnlyList<BlockModel> blocks, bool accept)
        {
            DocumentViewModel?.ResolveBlockRevisions(blocks, accept);
            ScheduleReviewRefresh();
        }

        public void ReviewResolve(IReadOnlyList<RevisionService.Hit> hits, bool accept)
        {
            DocumentViewModel?.ResolveRevisionHits(hits, accept);
            ScheduleReviewRefresh();
        }

        public void ReviewSetCommentKind(CommentModel comment, CommentKind kind)
            => DocumentViewModel?.SetCommentKind(comment, kind);

        public void ReviewReply(CommentModel comment, string text)
            => DocumentViewModel?.ReplyToComment(comment, text);

        public void ReviewToggleDone(CommentModel comment)
            => DocumentViewModel?.ToggleCommentDone(comment);

        public void ReviewDeleteThread(CommentModel comment)
            => DocumentViewModel?.DeleteCommentThread(comment);

        public void CloseReviewPanel() => IsReviewPanelVisible = false;
    }
}
