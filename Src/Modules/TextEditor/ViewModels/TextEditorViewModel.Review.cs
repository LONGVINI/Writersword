using System;
using Writersword.Modules.TextEditor.Contracts;
using Writersword.Modules.TextEditor.Models.Inline;

namespace Writersword.Modules.TextEditor.ViewModels
{
    /// <summary>
    /// Рецензирование для ленты: запись исправлений и вид показа берутся у
    /// открытого документа, имя рецензента — из общих настроек модуля: оно
    /// принадлежит человеку, а не книге, и одно на все документы.
    /// </summary>
    public sealed partial class TextEditorViewModel : IReviewHost
    {
        private DocumentViewModel? _reviewDocument;

        public event Action? ReviewStateChanged;

        /// <summary>Подключает рецензирование к открытому документу.</summary>
        private void AttachReview(DocumentViewModel docVm)
        {
            if (_reviewDocument is not null)
                _reviewDocument.RevisionsChanged -= OnRevisionsChanged;

            _reviewDocument = docVm;
            docVm.ReviewerName = Settings.ReviewerName ?? string.Empty;
            docVm.RevisionsChanged += OnRevisionsChanged;

            ReviewStateChanged?.Invoke();
        }

        private void OnRevisionsChanged() => ReviewStateChanged?.Invoke();

        public bool TrackRevisions => DocumentViewModel?.TrackRevisions ?? false;

        public void SetTrackRevisions(bool enabled) => DocumentViewModel?.SetTrackRevisions(enabled);

        public RevisionView RevisionView => DocumentViewModel?.RevisionView ?? RevisionView.AllMarkup;

        public void SetRevisionView(RevisionView view) => DocumentViewModel?.SetRevisionView(view);

        public string ReviewerName
            => DocumentViewModel?.ReviewerName
               ?? (string.IsNullOrWhiteSpace(Settings.ReviewerName)
                   ? DocumentViewModel.DefaultReviewerName()
                   : Settings.ReviewerName!);

        public void SetReviewerName(string? name)
        {
            string? stored = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            if (string.Equals(Settings.ReviewerName, stored, StringComparison.Ordinal)) return;

            Settings.ReviewerName = stored;
            if (DocumentViewModel is { } docVm) docVm.ReviewerName = stored ?? string.Empty;

            GlobalSettingsChanged?.Invoke(Settings);
            ReviewStateChanged?.Invoke();
        }

        public int RevisionCount => DocumentViewModel?.CountRevisions() ?? 0;

        public void ExecuteReview(ReviewAction action) => DocumentViewModel?.ExecuteReview(action);
    }
}
