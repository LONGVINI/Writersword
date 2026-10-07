using System;
using System.Windows.Input;
using Avalonia.Threading;
using ReactiveUI;
using Writersword.Modules.TextEditor.Contracts;
using Writersword.Modules.TextEditor.Models.Inline;

namespace Writersword.Modules.TextEditor.ViewModels.Toolbar
{
    /// <summary>
    /// Вкладка «Рецензирование»: запись исправлений, вид их показа и работа с
    /// правками — принять, отклонить, перейти к соседней.
    ///
    /// Три группы, и в каждой одно дело:
    ///   «Запись» — включена ли запись и чьим именем подписываются правки;
    ///   «Показ» — как правки видны на листе, четыре вида Word кнопками, а не
    ///   списком: какой выбран, видно сразу;
    ///   «Правки» — принять, отклонить, все сразу, назад и далее, и сколько правок
    ///   осталось.
    /// </summary>
    public sealed class RibbonReviewTabViewModel : ReactiveObject
    {
        // Счётчик правок обходит весь документ, а правки меняются на каждом знаке
        // набора. Пересчёт откладывается до паузы, иначе набор с записью исправлений
        // на большой рукописи шёл бы рывками.
        private static readonly TimeSpan CountDelay = TimeSpan.FromMilliseconds(400);

        private readonly IReviewHost _host;
        private readonly DispatcherTimer _countTimer;

        private bool _isRecordGroupExpanded = true;
        private bool _isViewGroupExpanded = true;
        private bool _isChangesGroupExpanded = true;

        private int _revisionCount;
        private string _reviewerName;

        public RibbonReviewTabViewModel(IReviewHost host)
        {
            _host = host;
            _reviewerName = host.ReviewerName;

            _countTimer = new DispatcherTimer { Interval = CountDelay };
            _countTimer.Tick += OnCountTimerTick;

            AcceptCommand = ReactiveCommand.Create(() => _host.ExecuteReview(ReviewAction.Accept));
            RejectCommand = ReactiveCommand.Create(() => _host.ExecuteReview(ReviewAction.Reject));
            AcceptAllCommand = ReactiveCommand.Create(() => _host.ExecuteReview(ReviewAction.AcceptAll));
            RejectAllCommand = ReactiveCommand.Create(() => _host.ExecuteReview(ReviewAction.RejectAll));
            NextCommand = ReactiveCommand.Create(() => _host.ExecuteReview(ReviewAction.Next));
            PreviousCommand = ReactiveCommand.Create(() => _host.ExecuteReview(ReviewAction.Previous));

            _host.ReviewStateChanged += OnReviewStateChanged;
            RecountNow();
        }

        // ── Группы ────────────────────────────────────────────────────────

        public bool IsRecordGroupExpanded
        {
            get => _isRecordGroupExpanded;
            set => this.RaiseAndSetIfChanged(ref _isRecordGroupExpanded, value);
        }

        public bool IsViewGroupExpanded
        {
            get => _isViewGroupExpanded;
            set => this.RaiseAndSetIfChanged(ref _isViewGroupExpanded, value);
        }

        public bool IsChangesGroupExpanded
        {
            get => _isChangesGroupExpanded;
            set => this.RaiseAndSetIfChanged(ref _isChangesGroupExpanded, value);
        }

        // ── Запись ────────────────────────────────────────────────────────

        /// <summary>Запись исправлений включена.</summary>
        public bool IsTrackRevisions
        {
            get => _host.TrackRevisions;
            set
            {
                if (_host.TrackRevisions != value) _host.SetTrackRevisions(value);
                this.RaisePropertyChanged();
            }
        }

        /// <summary>
        /// Имя рецензента. Пустое поле — имя пользователя системы; поле показывает
        /// его после ухода фокуса.
        /// </summary>
        public string ReviewerName
        {
            get => _reviewerName;
            set
            {
                string text = value ?? string.Empty;
                if (string.Equals(_reviewerName, text, StringComparison.Ordinal)) return;

                _host.SetReviewerName(text);
                _reviewerName = _host.ReviewerName;
                this.RaisePropertyChanged();
            }
        }

        // ── Показ ─────────────────────────────────────────────────────────

        /// <summary>Все исправления: вставленное подчёркнуто, удалённое зачёркнуто.</summary>
        public bool IsViewAllMarkup
        {
            get => _host.RevisionView == RevisionView.AllMarkup;
            set => SelectView(RevisionView.AllMarkup, value, nameof(IsViewAllMarkup));
        }

        /// <summary>Простая разметка: текст как после принятия, на поле красная черта.</summary>
        public bool IsViewSimpleMarkup
        {
            get => _host.RevisionView == RevisionView.SimpleMarkup;
            set => SelectView(RevisionView.SimpleMarkup, value, nameof(IsViewSimpleMarkup));
        }

        /// <summary>Без исправлений: текст как после принятия, без пометок.</summary>
        public bool IsViewNoMarkup
        {
            get => _host.RevisionView == RevisionView.NoMarkup;
            set => SelectView(RevisionView.NoMarkup, value, nameof(IsViewNoMarkup));
        }

        /// <summary>Исходный документ: текст как до правок.</summary>
        public bool IsViewOriginal
        {
            get => _host.RevisionView == RevisionView.Original;
            set => SelectView(RevisionView.Original, value, nameof(IsViewOriginal));
        }

        /// <summary>
        /// Кнопки вида ведут себя как переключатель: нажатие выбирает вид, повторное
        /// нажатие на выбранный ничего не снимает — один вид выбран всегда.
        /// </summary>
        private void SelectView(RevisionView view, bool isChecked, string propertyName)
        {
            if (isChecked && _host.RevisionView != view)
            {
                _host.SetRevisionView(view);
                RaiseViewProperties();
                return;
            }

            this.RaisePropertyChanged(propertyName);
        }

        private void RaiseViewProperties()
        {
            this.RaisePropertyChanged(nameof(IsViewAllMarkup));
            this.RaisePropertyChanged(nameof(IsViewSimpleMarkup));
            this.RaisePropertyChanged(nameof(IsViewNoMarkup));
            this.RaisePropertyChanged(nameof(IsViewOriginal));
            this.RaisePropertyChanged(nameof(ViewTitle));
        }

        /// <summary>Название выбранного вида — для свёрнутой группы.</summary>
        public string ViewTitle => _host.RevisionView switch
        {
            RevisionView.SimpleMarkup => "Простая разметка",
            RevisionView.NoMarkup => "Без исправлений",
            RevisionView.Original => "Исходный документ",
            _ => "Все исправления"
        };

        // ── Правки ────────────────────────────────────────────────────────

        /// <summary>В документе есть правки — кнопки работы с ними доступны.</summary>
        public bool HasRevisions => _revisionCount > 0;

        /// <summary>Сколько правок в документе — подписью под кнопками.</summary>
        public string RevisionCountText => _revisionCount switch
        {
            0 => "Правок нет",
            _ => $"Правок: {_revisionCount}"
        };

        public ICommand AcceptCommand { get; }
        public ICommand RejectCommand { get; }
        public ICommand AcceptAllCommand { get; }
        public ICommand RejectAllCommand { get; }
        public ICommand NextCommand { get; }
        public ICommand PreviousCommand { get; }

        // ── Обновление ────────────────────────────────────────────────────

        private void OnReviewStateChanged()
        {
            this.RaisePropertyChanged(nameof(IsTrackRevisions));
            RaiseViewProperties();

            string name = _host.ReviewerName;
            if (!string.Equals(_reviewerName, name, StringComparison.Ordinal))
            {
                _reviewerName = name;
                this.RaisePropertyChanged(nameof(ReviewerName));
            }

            _countTimer.Stop();
            _countTimer.Start();
        }

        private void OnCountTimerTick(object? sender, EventArgs e)
        {
            _countTimer.Stop();
            RecountNow();
        }

        private void RecountNow()
        {
            int count = _host.RevisionCount;
            if (count == _revisionCount) return;

            _revisionCount = count;
            this.RaisePropertyChanged(nameof(HasRevisions));
            this.RaisePropertyChanged(nameof(RevisionCountText));
        }

        /// <summary>Сворачивает группы по ширине окна: сначала «Показ», затем «Запись».</summary>
        public void UpdateLayout(double availableWidth)
        {
            if (availableWidth >= 640)
            {
                IsRecordGroupExpanded = true;
                IsViewGroupExpanded = true;
                IsChangesGroupExpanded = true;
                return;
            }

            IsViewGroupExpanded = false;

            if (availableWidth >= 480)
            {
                IsRecordGroupExpanded = true;
                IsChangesGroupExpanded = true;
                return;
            }

            IsRecordGroupExpanded = false;
            IsChangesGroupExpanded = availableWidth >= 300;
        }
    }
}
