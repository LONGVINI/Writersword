using System;
using Writersword.Modules.TextEditor.Models.Inline;

namespace Writersword.Modules.TextEditor.Contracts
{
    /// <summary>Действие кнопки вкладки «Рецензирование».</summary>
    public enum ReviewAction
    {
        /// <summary>Принять правку под кареткой или все правки в выделении и перейти к следующей.</summary>
        Accept,

        /// <summary>Отклонить правку под кареткой или все правки в выделении и перейти к следующей.</summary>
        Reject,

        /// <summary>Принять все правки документа.</summary>
        AcceptAll,

        /// <summary>Отклонить все правки документа.</summary>
        RejectAll,

        /// <summary>Перейти к следующей правке.</summary>
        Next,

        /// <summary>Перейти к предыдущей правке.</summary>
        Previous
    }

    /// <summary>
    /// Договор вкладки «Рецензирование» с модулем: запись исправлений, вид их
    /// показа, имя рецензента и кнопки принятия, отклонения и перехода.
    ///
    /// Отдельно от <see cref="ITextEditorCommandTarget"/>: вкладке нужно не только
    /// отдавать команды, но и знать состояние документа — включена ли запись, какой
    /// вид выбран, сколько правок, — и узнавать о его смене.
    /// </summary>
    public interface IReviewHost
    {
        /// <summary>Запись исправлений включена в открытом документе.</summary>
        bool TrackRevisions { get; }

        /// <summary>Включает или выключает запись исправлений.</summary>
        void SetTrackRevisions(bool enabled);

        /// <summary>Вид показа исправлений.</summary>
        RevisionView RevisionView { get; }

        /// <summary>Меняет вид показа исправлений.</summary>
        void SetRevisionView(RevisionView view);

        /// <summary>Имя, которым подписываются правки.</summary>
        string ReviewerName { get; }

        /// <summary>Меняет имя рецензента; пустое — имя пользователя системы.</summary>
        void SetReviewerName(string? name);

        /// <summary>Сколько правок в документе.</summary>
        int RevisionCount { get; }

        /// <summary>Выполняет действие кнопки рецензирования.</summary>
        void ExecuteReview(ReviewAction action);

        /// <summary>
        /// Состояние рецензирования сменилось: запись, вид, правки в документе или
        /// сам документ.
        /// </summary>
        event Action? ReviewStateChanged;
    }
}
