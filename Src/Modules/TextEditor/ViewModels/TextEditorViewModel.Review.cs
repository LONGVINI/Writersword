using System;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using Writersword.Core.Interfaces.Services.UI;
using Writersword.Core.Services;
using Writersword.Modules.TextEditor.Contracts;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Views.Dialogs;

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

            // Панель рецензии и номера страниц боковых панелей — у того же документа.
            AttachReviewPanel(docVm);

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

        bool IReviewHost.IsReviewPanelVisible => IsReviewPanelVisible;

        void IReviewHost.ToggleReviewPanel() => ToggleReviewPanel();

        void IReviewHost.InsertComment(Models.Document.CommentKind kind) => DocumentViewModel?.InsertComment(string.Empty, kind);

        // ── Защита и сравнение ────────────────────────────────────────────

        public bool IsRevisionLocked => DocumentViewModel?.IsRevisionLocked ?? false;

        public void CompareWithFile() => DocumentViewModel?.CompareWithFile();

        public void ToggleRevisionLock() => _ = ToggleRevisionLockAsync();

        /// <summary>
        /// Ставит или снимает защиту записи исправлений. Пароль спрашивается окном;
        /// защиту без пароля окно для снятия не спрашивает — как у Word.
        /// </summary>
        private async Task ToggleRevisionLockAsync()
        {
            var docVm = DocumentViewModel;
            if (docVm is null || docVm.IsReadOnly) return;

            var notifications = CoreServices.GetService<INotificationService>();
            var window = (Avalonia.Application.Current?.ApplicationLifetime
                as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

            if (!docVm.IsRevisionLocked)
            {
                if (window is null) return;

                string? password = await new RevisionLockDialog(protect: true).ShowDialog<string?>(window);
                if (password is null) return;

                // Документ могли сменить, пока открыто окно.
                if (!ReferenceEquals(docVm, DocumentViewModel) || docVm.IsRevisionLocked) return;

                docVm.LockRevisions(password);
                notifications?.ShowSuccess(password.Length > 0
                    ? "Исправления защищены паролем: любая правка записывается, принять или отклонить её нельзя"
                    : "Исправления защищены без пароля: любая правка записывается, принять или отклонить её нельзя");
            }
            else
            {
                string? password = null;

                if (docVm.RevisionLockHasPassword)
                {
                    if (window is null) return;

                    password = await new RevisionLockDialog(protect: false).ShowDialog<string?>(window);
                    if (password is null) return;
                    if (!ReferenceEquals(docVm, DocumentViewModel) || !docVm.IsRevisionLocked) return;
                }

                if (!docVm.UnlockRevisions(password))
                {
                    notifications?.ShowError("Пароль не подходит — защита осталась");
                    return;
                }

                notifications?.ShowSuccess("Защита снята: правки снова можно принимать и отклонять");
            }

            ReviewStateChanged?.Invoke();
        }
    }
}
