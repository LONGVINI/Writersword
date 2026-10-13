using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Threading;
using ReactiveUI;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Services;

namespace Writersword.Modules.TextEditor.ViewModels.Review
{
    /// <summary>
    /// Что панель рецензии просит у модуля: перейти к месту, подсветить его, принять
    /// или отклонить правки, ответить в обсуждении, закрыть его или удалить.
    /// </summary>
    public interface IReviewPanelHost
    {
        void ReviewGoTo(ReviewEntry entry);
        void ReviewHighlight(ReviewEntry? entry, uint argb);
        void ReviewResolve(IReadOnlyList<RevisionService.Hit> hits, bool accept);
        void ReviewResolveImages(IReadOnlyList<ImageBlock> images, bool accept);
        void ReviewSetCommentKind(CommentModel comment, CommentKind kind);
        void ReviewReply(CommentModel comment, string text);
        void ReviewToggleDone(CommentModel comment);
        void ReviewDeleteThread(CommentModel comment);
        void CloseReviewPanel();
    }

    /// <summary>Какие пункты показывать.</summary>
    public enum ReviewFilter
    {
        All,
        Revisions,
        Comments
    }

    /// <summary>Строка-заголовок группы: глава, под которой стоят пункты, и её страница.</summary>
    public sealed class ReviewGroupRow : ReactiveObject
    {
        private string _pageText;

        public ReviewGroupRow(string title, string pageText)
        {
            Title = title;
            _pageText = pageText;
        }

        public string Title { get; }

        public string PageText
        {
            get => _pageText;
            set => this.RaiseAndSetIfChanged(ref _pageText, value);
        }
    }

    /// <summary>Ответ в обсуждении примечания.</summary>
    public sealed class ReviewReplyViewModel
    {
        public ReviewReplyViewModel(CommentModel reply)
        {
            Author = string.IsNullOrWhiteSpace(reply.Author) ? "Без автора" : reply.Author;
            Initials = ReviewPanelViewModel.InitialsOf(Author);
            AvatarBrush = ReviewPanelViewModel.AvatarBrushOf(Author);
            When = ReviewPanelViewModel.WhenText(reply.Date);
            Text = reply.PlainText.Trim();
        }

        public string Author { get; }
        public string Initials { get; }
        public IBrush AvatarBrush { get; }
        public string When { get; }
        public string Text { get; }
    }

    /// <summary>
    /// Пункт рецензии в панели — карточка, как уведомление: что сделано, отрывок
    /// текста с самим изменением, кто, когда и где, и кнопки решения. Карточка не
    /// раскрывается по щелчку и не меняет высоту: всё нужное видно сразу, а поле
    /// ответа в обсуждении открывается только кнопкой «Ответить».
    /// </summary>
    public sealed class ReviewItemViewModel : ReactiveObject
    {
        private readonly ReviewPanelViewModel _owner;
        private bool _isSelected;
        private bool _isReplying;
        private string _replyText = string.Empty;
        private int _pageNumber;

        // Подача отрывка: что убрано, что добавлено, что отмечено.
        private static readonly IBrush RemovedForeground = new SolidColorBrush(Color.FromRgb(0xF2, 0x8B, 0x82));
        private static readonly IBrush RemovedBackground = new SolidColorBrush(Color.FromArgb(0x26, 0xF0, 0x7A, 0x72));
        private static readonly IBrush AddedForeground = new SolidColorBrush(Color.FromRgb(0x81, 0xD8, 0xA4));
        private static readonly IBrush AddedBackground = new SolidColorBrush(Color.FromArgb(0x2E, 0x5C, 0xCB, 0x86));
        private static readonly IBrush FormatForeground = new SolidColorBrush(Color.FromRgb(0x8A, 0xB4, 0xF8));
        private static readonly IBrush FormatBackground = new SolidColorBrush(Color.FromArgb(0x22, 0x4F, 0x8E, 0xF7));
        private static readonly IBrush CommentMarkForeground = new SolidColorBrush(Color.FromRgb(0xF2, 0xD3, 0x9B));
        private static readonly IBrush CommentMarkBackground = new SolidColorBrush(Color.FromArgb(0x3A, 0xE0, 0xA3, 0x3A));

        public ReviewItemViewModel(ReviewPanelViewModel owner, ReviewEntry entry, int pageNumber)
        {
            _owner = owner;
            Entry = entry;
            _pageNumber = pageNumber;

            CommentKind = entry.Comment?.Kind ?? CommentKind.Note;
            KindColor = entry.Done ? ReviewPanelViewModel.DoneColor : ColorOf(entry.Kind, CommentKind);
            KindBrush = new SolidColorBrush(KindColor);
            KindSoftBrush = new SolidColorBrush(Color.FromArgb(0x30, KindColor.R, KindColor.G, KindColor.B));
            Icon = Geometry.Parse(IconData(entry.Kind, CommentKind));
            KindLabel = LabelOf(entry.Kind, CommentKind) + (entry.Done ? " · закрыто" : string.Empty);

            Author = string.IsNullOrWhiteSpace(entry.Author) ? "Без автора" : entry.Author;
            Initials = ReviewPanelViewModel.InitialsOf(Author);
            AvatarBrush = ReviewPanelViewModel.AvatarBrushOf(Author);
            When = ReviewPanelViewModel.WhenText(entry.Date);

            bool comment = entry.Kind == ReviewKind.Comment;
            MarkedForeground = comment ? CommentMarkForeground : FormatForeground;
            MarkedBackground = comment ? CommentMarkBackground : FormatBackground;
            MarkedDecorations = comment ? null : TextDecorations.Underline;

            Replies = entry.Replies.Select(r => new ReviewReplyViewModel(r)).ToList();
            Signature = SignatureOf(entry);

            AcceptCommand = ReactiveCommand.Create(() => _owner.Resolve(this, accept: true));
            RejectCommand = ReactiveCommand.Create(() => _owner.Resolve(this, accept: false));
            ReplyCommand = ReactiveCommand.Create(SendReply);
            StartReplyCommand = ReactiveCommand.Create(() => { IsReplying = !IsReplying; });
            ToggleDoneCommand = ReactiveCommand.Create(() => _owner.ToggleDone(this));
            CycleKindCommand = ReactiveCommand.Create(() => _owner.SetKind(this, NextKind(CommentKind)));
            DeleteCommand = ReactiveCommand.Create(() => _owner.DeleteThread(this));
        }

        public ReviewEntry Entry { get; }

        public object Key => Entry.Key;

        /// <summary>Всё, что видно в карточке: совпало — карточку можно оставить прежней.</summary>
        internal string Signature { get; }

        public bool IsComment => Entry.Kind == ReviewKind.Comment;

        /// <summary>Вид обсуждения: примечание, предложение, требование. У правки — примечание.</summary>
        public CommentKind CommentKind { get; }

        public bool IsRequirement => IsComment && CommentKind == CommentKind.Requirement;

        /// <summary>Подсказка кнопки вида: на что он сменится.</summary>
        public string CycleKindTip => "Сменить вид на «" + LabelOf(ReviewKind.Comment, NextKind(CommentKind)).ToLowerInvariant() + "»";
        public bool IsRevision => !IsComment;
        public bool IsDone => Entry.Done;

        internal Color KindColor { get; }
        public IBrush KindBrush { get; }
        public IBrush KindSoftBrush { get; }
        public Geometry Icon { get; }

        /// <summary>Что сделано: «Добавлен текст», «Замена», «Примечание»…</summary>
        public string KindLabel { get; }

        // ── Отрывок ───────────────────────────────────────────────────────

        public string ContextBefore => Entry.ContextBefore;
        public string Removed => Entry.Removed;
        public string Gap => Entry.Removed.Length > 0 && Entry.Added.Length > 0 ? " " : string.Empty;
        public string Added => Entry.Added;
        public string Marked => Entry.Marked;
        public string ContextAfter => Entry.ContextAfter;

        public bool HasSnippet => Entry.ContextBefore.Length > 0 || Entry.Removed.Length > 0 || Entry.Added.Length > 0
                                  || Entry.Marked.Length > 0 || Entry.ContextAfter.Length > 0;

        public IBrush RemovedForegroundBrush => RemovedForeground;
        public IBrush RemovedBackgroundBrush => RemovedBackground;
        public IBrush AddedForegroundBrush => AddedForeground;
        public IBrush AddedBackgroundBrush => AddedBackground;
        public IBrush MarkedForeground { get; }
        public IBrush MarkedBackground { get; }
        public TextDecorationCollection? MarkedDecorations { get; }

        // ── Оформление: было / стало ──────────────────────────────────────

        public bool HasDiff => Entry.Before.Length > 0 || Entry.After.Length > 0;
        public string BeforeText => Entry.Before.Length > 0 ? Entry.Before : "—";
        public string AfterText => Entry.After.Length > 0 ? Entry.After : "—";

        // ── Примечание ────────────────────────────────────────────────────

        public string CommentText => IsComment ? Entry.Text : string.Empty;
        public bool HasCommentText => IsComment && Entry.Text.Length > 0;

        public IReadOnlyList<ReviewReplyViewModel> Replies { get; }
        public bool HasReplies => Replies.Count > 0;

        public string DoneButtonText => IsDone ? "Открыть" : "Закрыть";

        // ── Кто, когда, где ───────────────────────────────────────────────

        public string Author { get; }
        public string Initials { get; }
        public IBrush AvatarBrush { get; }
        public string When { get; }

        public int PageNumber
        {
            get => _pageNumber;
            set
            {
                if (_pageNumber == value) return;
                this.RaiseAndSetIfChanged(ref _pageNumber, value);
                this.RaisePropertyChanged(nameof(PageText));
            }
        }

        public string PageText => _pageNumber > 0 ? "стр. " + _pageNumber.ToString(CultureInfo.InvariantCulture) : string.Empty;

        // ── Состояние ─────────────────────────────────────────────────────

        public bool IsSelected
        {
            get => _isSelected;
            set => this.RaiseAndSetIfChanged(ref _isSelected, value);
        }

        /// <summary>Открыто поле ответа.</summary>
        public bool IsReplying
        {
            get => _isReplying;
            set => this.RaiseAndSetIfChanged(ref _isReplying, value);
        }

        public string ReplyText
        {
            get => _replyText;
            set => this.RaiseAndSetIfChanged(ref _replyText, value);
        }

        public ICommand AcceptCommand { get; }
        public ICommand RejectCommand { get; }
        public ICommand ReplyCommand { get; }
        public ICommand StartReplyCommand { get; }
        public ICommand ToggleDoneCommand { get; }
        public ICommand CycleKindCommand { get; }
        public ICommand DeleteCommand { get; }

        /// <summary>Цвет подсветки места на листе, пока указатель над карточкой (ARGB).</summary>
        internal uint HighlightArgb => (0x55u << 24) | ((uint)KindColor.R << 16) | ((uint)KindColor.G << 8) | KindColor.B;

        private void SendReply()
        {
            string text = _replyText.Trim();
            if (text.Length == 0) return;
            _owner.Reply(this, text);
            ReplyText = string.Empty;
            IsReplying = false;
        }

        /// <summary>Совпадает ли пункт с поиском: текст, автор, вид, оформление, ответы.</summary>
        public bool Matches(string query)
        {
            bool Has(string? s) => s is not null && s.Contains(query, StringComparison.CurrentCultureIgnoreCase);
            return Has(Entry.Text) || Has(Author) || Has(KindLabel) || Has(Entry.Before) || Has(Entry.After)
                   || Has(Entry.ContextBefore) || Has(Entry.ContextAfter) || Has(Entry.Removed) || Has(Entry.Added)
                   || Has(Entry.Marked) || Has(Entry.Group) || Replies.Any(r => Has(r.Text) || Has(r.Author));
        }

        private static string SignatureOf(ReviewEntry entry)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append((int)entry.Kind).Append('\u001F')
              .Append(entry.Author).Append('\u001F')
              .Append(entry.Date?.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\u001F')
              .Append(entry.Text).Append('\u001F')
              .Append(entry.ContextBefore).Append('\u001F')
              .Append(entry.Removed).Append('\u001F')
              .Append(entry.Added).Append('\u001F')
              .Append(entry.Marked).Append('\u001F')
              .Append(entry.ContextAfter).Append('\u001F')
              .Append(entry.Before).Append('\u001F')
              .Append(entry.After).Append('\u001F')
              .Append(entry.Done ? '1' : '0').Append('\u001F')
              .Append((int)(entry.Comment?.Kind ?? CommentKind.Note)).Append('\u001F')
              .Append(entry.Group);

            foreach (var reply in entry.Replies)
                sb.Append('\u001E').Append(reply.Author).Append('\u001F').Append(reply.PlainText);

            return sb.ToString();
        }

        internal static CommentKind NextKind(CommentKind kind) => kind switch
        {
            CommentKind.Note => CommentKind.Suggestion,
            CommentKind.Suggestion => CommentKind.Requirement,
            _ => CommentKind.Note
        };

        internal static Color ColorOf(ReviewKind kind, CommentKind commentKind) => kind switch
        {
            ReviewKind.Comment when commentKind == CommentKind.Suggestion => ReviewPanelViewModel.SuggestionColor,
            ReviewKind.Comment when commentKind == CommentKind.Requirement => ReviewPanelViewModel.RequirementColor,
            ReviewKind.Insertion or ReviewKind.ParagraphInserted => Color.FromRgb(0x5C, 0xCB, 0x86),
            ReviewKind.Deletion or ReviewKind.ParagraphDeleted => Color.FromRgb(0xF0, 0x7A, 0x72),
            ReviewKind.Replacement => Color.FromRgb(0xB4, 0x8C, 0xF2),
            ReviewKind.MoveFrom or ReviewKind.MoveTo => Color.FromRgb(0x4F, 0xC3, 0xD9),
            ReviewKind.Format or ReviewKind.ParagraphFormat => Color.FromRgb(0x4F, 0x8E, 0xF7),
            _ => ReviewPanelViewModel.CommentColor
        };

        private static string LabelOf(ReviewKind kind, CommentKind commentKind) => kind switch
        {
            ReviewKind.Comment when commentKind == CommentKind.Suggestion => "Предложение",
            ReviewKind.Comment when commentKind == CommentKind.Requirement => "Требование",
            ReviewKind.Insertion => "Добавлен текст",
            ReviewKind.Deletion => "Удалён текст",
            ReviewKind.Replacement => "Замена",
            ReviewKind.Format => "Изменено оформление",
            ReviewKind.ParagraphFormat => "Оформление абзаца",
            ReviewKind.MoveFrom => "Перенесено отсюда",
            ReviewKind.MoveTo => "Перенесено сюда",
            ReviewKind.ParagraphInserted => "Новый абзац",
            ReviewKind.ParagraphDeleted => "Абзацы объединены",
            _ => "Примечание"
        };

        // Значки нарисованы в поле 24×24; вид рисует их без растяжения, поэтому
        // и прямой чёрточке, у которой нет высоты, есть где стоять.
        private static string IconData(ReviewKind kind, CommentKind commentKind) => kind switch
        {
            ReviewKind.Comment when commentKind == CommentKind.Suggestion => "M9.5 18h5M10.5 21h3M12 3.5a5.5 5.5 0 0 0-3.2 10V15.5h6.4V13.5A5.5 5.5 0 0 0 12 3.5z",
            ReviewKind.Comment when commentKind == CommentKind.Requirement => "M12 5.5v8.5M12 18.2v.3",
            ReviewKind.Insertion => "M12 6.5v11M6.5 12h11",
            ReviewKind.Deletion => "M6.5 12h11",
            ReviewKind.Replacement => "M6 9h10.5l-3-3M18 15H7.5l3 3",
            ReviewKind.Format => "M7.5 18L12 6l4.5 12M9.2 13.8h5.6",
            ReviewKind.ParagraphFormat => "M6 7h12M6 10.7h8M6 14.3h12M6 18h8",
            ReviewKind.MoveFrom or ReviewKind.MoveTo => "M6 12h11M13.5 8l4 4-4 4",
            ReviewKind.ParagraphInserted or ReviewKind.ParagraphDeleted => "M11 6h7M14.5 6v12M17 6v12M11 6a3.4 3.4 0 0 0 0 6.8h3.5",
            _ => "M6 7h12v8.5h-7.5L6 19z"
        };

        internal static string Plural(int n, string one, string few, string many)
        {
            int mod100 = n % 100, mod10 = n % 10;
            if (mod100 is >= 11 and <= 14) return many;
            return mod10 switch { 1 => one, 2 or 3 or 4 => few, _ => many };
        }
    }

    /// <summary>
    /// Панель рецензии: все правки и обсуждения документа одним списком по главам,
    /// с отбором по виду, автору и поиску, с переходом к месту, принятием и
    /// отклонением прямо из списка и обсуждением без отдельного окна.
    ///
    /// Список пересобирается из документа (ReviewCollector) после паузы в правке:
    /// на большой рукописи обход на каждое нажатие не нужен. Номера страниц
    /// обновляются отдельно, когда раскладка пересчитала листы.
    /// </summary>
    public sealed class ReviewPanelViewModel : ReactiveObject
    {
        internal static readonly Color RevisionColor = Color.FromRgb(0x4F, 0x8E, 0xF7);
        internal static readonly Color CommentColor = Color.FromRgb(0xE0, 0xA3, 0x3A);
        internal static readonly Color SuggestionColor = Color.FromRgb(0x3F, 0xB5, 0x8A);
        internal static readonly Color RequirementColor = Color.FromRgb(0xE5, 0x60, 0x5A);
        internal static readonly Color DoneColor = Color.FromRgb(0x8C, 0x8C, 0x8C);

        private const string AllAuthors = "Все авторы";

        private readonly IReviewPanelHost _host;
        private readonly DispatcherTimer _searchTimer;

        private List<ReviewItemViewModel> _items = new();
        private List<ReviewItemViewModel> _visible = new();
        private Dictionary<Guid, int> _pages = new();

        private bool _isVisible;
        private ReviewFilter _filter = ReviewFilter.All;
        private string _searchText = string.Empty;
        private string _selectedAuthor = AllAuthors;
        private bool _showClosed;
        private ReviewItemViewModel? _selectedItem;
        private ReviewItemViewModel? _hoveredItem;

        public ReviewPanelViewModel(IReviewPanelHost host)
        {
            _host = host;

            _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _searchTimer.Tick += (_, _) =>
            {
                _searchTimer.Stop();
                ApplyFilter();
            };

            NextCommand = ReactiveCommand.Create(() => Step(+1));
            PreviousCommand = ReactiveCommand.Create(() => Step(-1));
            AcceptVisibleCommand = ReactiveCommand.Create(() => ResolveVisible(accept: true));
            RejectVisibleCommand = ReactiveCommand.Create(() => ResolveVisible(accept: false));
            CloseCommand = ReactiveCommand.Create(() => _host.CloseReviewPanel());
        }

        // ── Видимость ─────────────────────────────────────────────────────

        public bool IsVisible
        {
            get => _isVisible;
            set => this.RaiseAndSetIfChanged(ref _isVisible, value);
        }

        // ── Список ────────────────────────────────────────────────────────

        /// <summary>Строки списка: заголовки групп и пункты.</summary>
        public ObservableCollection<object> Rows { get; } = new();

        public ObservableCollection<string> Authors { get; } = new() { AllAuthors };

        public int RevisionCount => _items.Count(i => i.IsRevision);
        public int CommentCount => _items.Count(i => i.IsComment && !i.IsDone);
        public int ClosedCount => _items.Count(i => i.IsComment && i.IsDone);

        /// <summary>Сколько требует внимания: правки и открытые обсуждения.</summary>
        public int OpenCount => RevisionCount + CommentCount;

        public string OpenCountText
        {
            get
            {
                if (OpenCount == 0) return "всё решено";

                string text = OpenCount + " " + ReviewItemViewModel.Plural(OpenCount, "открыт", "открыто", "открыто");
                int requirements = RequirementCount;
                if (requirements > 0)
                    text += " · " + requirements + " " + ReviewItemViewModel.Plural(requirements, "требование", "требования", "требований");
                return text;
            }
        }

        // Открытые обсуждения по видам — для отбора по виду.
        public int NoteCount => _items.Count(i => i.IsComment && !i.IsDone && i.CommentKind == CommentKind.Note);
        public int SuggestionCount => _items.Count(i => i.IsComment && !i.IsDone && i.CommentKind == CommentKind.Suggestion);
        public int RequirementCount => _items.Count(i => i.IsComment && !i.IsDone && i.CommentKind == CommentKind.Requirement);

        /// <summary>Отбор по виду обсуждения показывается, когда обсуждения вообще есть и фильтр их не прячет.</summary>
        public bool ShowKindFilter => _items.Any(i => i.IsComment) && _filter != ReviewFilter.Revisions;

        private bool _showNotes = true;
        private bool _showSuggestions = true;
        private bool _showRequirements = true;

        public bool ShowNotes
        {
            get => _showNotes;
            set { if (_showNotes == value) return; this.RaiseAndSetIfChanged(ref _showNotes, value); ApplyFilter(); }
        }

        public bool ShowSuggestions
        {
            get => _showSuggestions;
            set { if (_showSuggestions == value) return; this.RaiseAndSetIfChanged(ref _showSuggestions, value); ApplyFilter(); }
        }

        public bool ShowRequirements
        {
            get => _showRequirements;
            set { if (_showRequirements == value) return; this.RaiseAndSetIfChanged(ref _showRequirements, value); ApplyFilter(); }
        }

        public bool HasClosed => ClosedCount > 0;

        public string ShowClosedText => "Закрытые (" + ClosedCount.ToString(CultureInfo.InvariantCulture) + ")";

        public bool IsEmpty => _visible.Count == 0;

        public string EmptyText => _items.Count == 0
            ? "В документе нет правок и примечаний."
            : "Под этот отбор ничего не подходит.";

        public string PositionText
        {
            get
            {
                if (_visible.Count == 0) return string.Empty;
                int index = _selectedItem is null ? -1 : _visible.IndexOf(_selectedItem);
                return index < 0
                    ? _visible.Count.ToString(CultureInfo.InvariantCulture)
                    : (index + 1).ToString(CultureInfo.InvariantCulture) + " из " + _visible.Count.ToString(CultureInfo.InvariantCulture);
            }
        }

        public bool HasVisibleRevisions => _visible.Any(i => i.IsRevision);

        // ── Отбор ─────────────────────────────────────────────────────────

        public bool IsFilterAll
        {
            get => _filter == ReviewFilter.All;
            set { if (value) SetFilter(ReviewFilter.All); else RaiseFilter(); }
        }

        public bool IsFilterRevisions
        {
            get => _filter == ReviewFilter.Revisions;
            set { if (value) SetFilter(ReviewFilter.Revisions); else RaiseFilter(); }
        }

        public bool IsFilterComments
        {
            get => _filter == ReviewFilter.Comments;
            set { if (value) SetFilter(ReviewFilter.Comments); else RaiseFilter(); }
        }

        private void SetFilter(ReviewFilter filter)
        {
            if (_filter == filter)
            {
                RaiseFilter();
                return;
            }

            _filter = filter;
            RaiseFilter();
            ApplyFilter();
        }

        private void RaiseFilter()
        {
            this.RaisePropertyChanged(nameof(IsFilterAll));
            this.RaisePropertyChanged(nameof(IsFilterRevisions));
            this.RaisePropertyChanged(nameof(IsFilterComments));
            this.RaisePropertyChanged(nameof(ShowKindFilter));
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (string.Equals(_searchText, value, StringComparison.Ordinal)) return;
                this.RaiseAndSetIfChanged(ref _searchText, value ?? string.Empty);
                _searchTimer.Stop();
                _searchTimer.Start();
            }
        }

        public string SelectedAuthor
        {
            get => _selectedAuthor;
            set
            {
                string author = string.IsNullOrEmpty(value) ? AllAuthors : value;
                if (string.Equals(_selectedAuthor, author, StringComparison.Ordinal)) return;
                this.RaiseAndSetIfChanged(ref _selectedAuthor, author);
                ApplyFilter();
            }
        }

        /// <summary>Показывать закрытые обсуждения.</summary>
        public bool ShowClosed
        {
            get => _showClosed;
            set
            {
                if (_showClosed == value) return;
                this.RaiseAndSetIfChanged(ref _showClosed, value);
                ApplyFilter();
            }
        }

        // ── Выбор ─────────────────────────────────────────────────────────

        public ReviewItemViewModel? SelectedItem
        {
            get => _selectedItem;
            private set
            {
                if (ReferenceEquals(_selectedItem, value)) return;
                if (_selectedItem is not null) _selectedItem.IsSelected = false;
                _selectedItem = value;
                if (_selectedItem is not null) _selectedItem.IsSelected = true;
                this.RaisePropertyChanged();
                this.RaisePropertyChanged(nameof(PositionText));
            }
        }

        /// <summary>Выбрать пункт и перейти к его месту в тексте.</summary>
        public void Select(ReviewItemViewModel item)
        {
            SelectedItem = item;
            _host.ReviewGoTo(item.Entry);
        }

        /// <summary>Соседний пункт списка; по кругу.</summary>
        public void Step(int direction)
        {
            if (_visible.Count == 0) return;

            int index = _selectedItem is null ? -1 : _visible.IndexOf(_selectedItem);
            index = index < 0
                ? (direction > 0 ? 0 : _visible.Count - 1)
                : (index + direction + _visible.Count) % _visible.Count;

            Select(_visible[index]);
            ScrollIntoViewRequested?.Invoke(_visible[index]);
        }

        /// <summary>Вид просит показать пункт в списке — после перехода клавишами.</summary>
        public event Action<ReviewItemViewModel>? ScrollIntoViewRequested;

        /// <summary>Указатель над пунктом: его место подсвечивается в тексте; null — ушёл.</summary>
        public void Hover(ReviewItemViewModel? item)
        {
            if (ReferenceEquals(_hoveredItem, item)) return;
            _hoveredItem = item;

            if (item is null)
            {
                _host.ReviewHighlight(null, 0);
                return;
            }

            _host.ReviewHighlight(item.Entry, item.HighlightArgb);
        }

        // ── Действия ──────────────────────────────────────────────────────

        public ICommand NextCommand { get; }
        public ICommand PreviousCommand { get; }
        public ICommand AcceptVisibleCommand { get; }
        public ICommand RejectVisibleCommand { get; }
        public ICommand CloseCommand { get; }

        internal void Resolve(ReviewItemViewModel item, bool accept)
        {
            if (!item.IsRevision) return;
            if (item.Entry.Image is null && item.Entry.Hits.Count == 0) return;

            // Выбор переходит к следующему пункту — как у Word «Принять и перейти дальше».
            int index = _visible.IndexOf(item);
            _pendingSelectIndex = index;

            Hover(null);
            if (item.Entry.Image is { } image)
                _host.ReviewResolveImages(new[] { image }, accept);
            else
                _host.ReviewResolve(item.Entry.Hits, accept);
        }

        private void ResolveVisible(bool accept)
        {
            var revisions = _visible.Where(i => i.IsRevision).ToList();
            var hits = revisions.Where(i => i.Entry.Image is null).SelectMany(i => i.Entry.Hits).ToList();
            var images = revisions.Select(i => i.Entry.Image).OfType<ImageBlock>().ToList();
            if (hits.Count == 0 && images.Count == 0) return;

            Hover(null);
            if (images.Count > 0) _host.ReviewResolveImages(images, accept);
            if (hits.Count > 0) _host.ReviewResolve(hits, accept);
        }

        internal void Reply(ReviewItemViewModel item, string text)
        {
            if (item.Entry.Comment is { } comment) _host.ReviewReply(comment, text);
        }

        internal void SetKind(ReviewItemViewModel item, CommentKind kind)
        {
            if (item.Entry.Comment is { } comment) _host.ReviewSetCommentKind(comment, kind);
        }

        internal void ToggleDone(ReviewItemViewModel item)
        {
            if (item.Entry.Comment is { } comment) _host.ReviewToggleDone(comment);
        }

        internal void DeleteThread(ReviewItemViewModel item)
        {
            if (item.Entry.Comment is not { } comment) return;
            _pendingSelectIndex = _visible.IndexOf(item);
            Hover(null);
            _host.ReviewDeleteThread(comment);
        }

        // После принятия или удаления выбор встаёт на пункт с тем же местом в списке.
        private int _pendingSelectIndex = -1;

        // ── Пересборка ────────────────────────────────────────────────────

        /// <summary>
        /// Пересобирает список по пунктам документа. Карточка, у которой ничего не
        /// поменялось, остаётся той же: список не перерисовывается целиком, прокрутка
        /// не прыгает, начатый ответ и выбор держатся. Уходят и появляются только
        /// изменившиеся карточки.
        /// </summary>
        public void Rebuild(IReadOnlyList<ReviewEntry> entries, Dictionary<Guid, int> pages)
        {
            _pages = pages;
            var selectedKey = _selectedItem?.Key;
            string? keptReply = _selectedItem?.ReplyText;

            var previous = new Dictionary<object, ReviewItemViewModel>();
            foreach (var old in _items) previous.TryAdd(old.Key, old);

            var items = new List<ReviewItemViewModel>(entries.Count);
            foreach (var entry in entries)
            {
                var item = new ReviewItemViewModel(this, entry, PageOf(entry));
                if (previous.TryGetValue(item.Key, out var old) && string.Equals(old.Signature, item.Signature, StringComparison.Ordinal))
                {
                    old.PageNumber = item.PageNumber;
                    item = old;
                }
                items.Add(item);
            }

            bool selectionKept = _selectedItem is not null && items.Contains(_selectedItem);
            if (_hoveredItem is not null && !items.Contains(_hoveredItem)) Hover(null);

            _items = items;

            var authors = _items.Select(i => i.Author).Distinct(StringComparer.Ordinal).OrderBy(a => a, StringComparer.CurrentCulture).ToList();
            authors.Insert(0, AllAuthors);
            if (!authors.SequenceEqual(Authors))
            {
                string selected = _selectedAuthor;
                Authors.Clear();
                foreach (var author in authors) Authors.Add(author);
                if (!Authors.Contains(selected)) selected = AllAuthors;
                _selectedAuthor = selected;
                this.RaisePropertyChanged(nameof(SelectedAuthor));
            }

            if (!selectionKept)
            {
                if (_selectedItem is not null) _selectedItem.IsSelected = false;
                _selectedItem = null;
            }
            ApplyFilter();

            ReviewItemViewModel? restore = _selectedItem;
            if (restore is null && selectedKey is not null)
                restore = _visible.FirstOrDefault(i => Equals(i.Key, selectedKey));

            if (restore is null && _pendingSelectIndex >= 0 && _visible.Count > 0)
                restore = _visible[Math.Min(_pendingSelectIndex, _visible.Count - 1)];
            _pendingSelectIndex = -1;

            SelectedItem = restore;
            if (!selectionKept && restore is not null && keptReply is { Length: > 0 } && Equals(restore.Key, selectedKey))
            {
                restore.ReplyText = keptReply;
                restore.IsReplying = true;
            }

            this.RaisePropertyChanged(nameof(RevisionCount));
            this.RaisePropertyChanged(nameof(CommentCount));
            this.RaisePropertyChanged(nameof(NoteCount));
            this.RaisePropertyChanged(nameof(SuggestionCount));
            this.RaisePropertyChanged(nameof(RequirementCount));
            this.RaisePropertyChanged(nameof(ShowKindFilter));
            this.RaisePropertyChanged(nameof(ClosedCount));
            this.RaisePropertyChanged(nameof(OpenCount));
            this.RaisePropertyChanged(nameof(OpenCountText));
            this.RaisePropertyChanged(nameof(HasClosed));
            this.RaisePropertyChanged(nameof(ShowClosedText));
        }

        /// <summary>Только номера страниц: раскладка пересчитала листы.</summary>
        public void UpdatePages(Dictionary<Guid, int> pages)
        {
            _pages = pages;
            foreach (var item in _items) item.PageNumber = PageOf(item.Entry);

            // Строки списка остаются теми же — прокрутка и раскрытый пункт не прыгают;
            // у заголовка группы меняется только страница её первого пункта.
            ReviewGroupRow? group = null;
            foreach (var row in Rows)
            {
                if (row is ReviewGroupRow header)
                {
                    group = header;
                    continue;
                }

                if (group is not null && row is ReviewItemViewModel first)
                {
                    group.PageText = first.PageNumber > 0
                        ? "стр. " + first.PageNumber.ToString(CultureInfo.InvariantCulture)
                        : string.Empty;
                    group = null;
                }
            }
        }

        private int PageOf(ReviewEntry entry)
            => _pages.TryGetValue(entry.OwnerBlockId, out int page) ? page : 0;

        private void ApplyFilter()
        {
            string query = _searchText.Trim();

            _visible = _items.Where(item =>
            {
                if (_filter == ReviewFilter.Revisions && !item.IsRevision) return false;
                if (_filter == ReviewFilter.Comments && !item.IsComment) return false;
                if (item.IsComment)
                {
                    if (item.CommentKind == CommentKind.Note && !_showNotes) return false;
                    if (item.CommentKind == CommentKind.Suggestion && !_showSuggestions) return false;
                    if (item.CommentKind == CommentKind.Requirement && !_showRequirements) return false;
                }
                if (item.IsDone && !_showClosed) return false;
                if (_selectedAuthor != AllAuthors && !string.Equals(item.Author, _selectedAuthor, StringComparison.Ordinal)) return false;
                if (query.Length > 0 && !item.Matches(query)) return false;
                return true;
            }).ToList();

            if (_selectedItem is not null && !_visible.Contains(_selectedItem))
                SelectedItem = null;

            RebuildRows();

            this.RaisePropertyChanged(nameof(IsEmpty));
            this.RaisePropertyChanged(nameof(EmptyText));
            this.RaisePropertyChanged(nameof(PositionText));
            this.RaisePropertyChanged(nameof(HasVisibleRevisions));
        }

        // Заголовки групп прошлой сборки: название с номером повтора — заголовок.
        private Dictionary<string, ReviewGroupRow> _groupRows = new(StringComparer.Ordinal);

        private void RebuildRows()
        {
            var rows = new List<object>(_visible.Count + 16);
            var groups = new Dictionary<string, ReviewGroupRow>(StringComparer.Ordinal);
            var repeats = new Dictionary<string, int>(StringComparer.Ordinal);
            string? group = null;

            foreach (var item in _visible)
            {
                string title = item.Entry.Group.Length > 0 ? item.Entry.Group : "Начало документа";
                if (!string.Equals(group, title, StringComparison.Ordinal))
                {
                    group = title;
                    repeats.TryGetValue(title, out int n);
                    repeats[title] = n + 1;
                    string key = title + "\u001F" + n.ToString(CultureInfo.InvariantCulture);

                    string page = item.PageText;
                    if (_groupRows.TryGetValue(key, out var header)) header.PageText = page;
                    else header = new ReviewGroupRow(title, page);

                    groups[key] = header;
                    rows.Add(header);
                }
                rows.Add(item);
            }

            _groupRows = groups;
            SyncRows(rows);
        }

        /// <summary>
        /// Приводит строки списка к новому составу правками на месте: ушедшие
        /// удаляются, новые вставляются, оставшиеся не трогаются.
        /// </summary>
        private void SyncRows(List<object> rows)
        {
            var keep = new HashSet<object>(rows, ReferenceEqualityComparer.Instance);
            for (int i = Rows.Count - 1; i >= 0; i--)
                if (!keep.Contains(Rows[i])) Rows.RemoveAt(i);

            for (int i = 0; i < rows.Count; i++)
            {
                if (i < Rows.Count && ReferenceEquals(Rows[i], rows[i])) continue;

                int found = -1;
                for (int j = i + 1; j < Rows.Count; j++)
                {
                    if (!ReferenceEquals(Rows[j], rows[i])) continue;
                    found = j;
                    break;
                }

                if (found >= 0) Rows.Move(found, i);
                else Rows.Insert(i, rows[i]);
            }

            while (Rows.Count > rows.Count) Rows.RemoveAt(Rows.Count - 1);
        }

        // ── Общее ─────────────────────────────────────────────────────────

        internal static string InitialsOf(string author)
        {
            var parts = author.Split(new[] { ' ', '.', '_' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "?";
            if (parts.Length == 1) return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpper(CultureInfo.CurrentCulture);
            return (parts[0].Substring(0, 1) + parts[^1].Substring(0, 1)).ToUpper(CultureInfo.CurrentCulture);
        }

        private static readonly Color[] AvatarColors =
        {
            Color.FromRgb(0x3F, 0x7D, 0x5C), Color.FromRgb(0xB3, 0x53, 0x6B), Color.FromRgb(0x3D, 0x5F, 0xA8),
            Color.FromRgb(0x8A, 0x5C, 0xB8), Color.FromRgb(0xB0, 0x6A, 0x2C), Color.FromRgb(0x2F, 0x86, 0x8C),
            Color.FromRgb(0x7A, 0x6B, 0x3A), Color.FromRgb(0xA3, 0x44, 0x44)
        };

        internal static IBrush AvatarBrushOf(string author)
        {
            int hash = 17;
            foreach (char c in author) hash = unchecked(hash * 31 + c);
            return new SolidColorBrush(AvatarColors[(hash & 0x7FFFFFFF) % AvatarColors.Length]);
        }

        /// <summary>Когда: «сегодня, 12:00», «вчера, 12:00», «15 янв, 12:00», «15 янв 2024».</summary>
        internal static string WhenText(DateTime? date)
        {
            if (date is not DateTime value) return string.Empty;

            var local = value.Kind == DateTimeKind.Unspecified ? value : value.ToLocalTime();
            var culture = CultureInfo.GetCultureInfo("ru-RU");
            var today = DateTime.Now.Date;

            if (local.Date == today) return "сегодня, " + local.ToString("HH:mm", culture);
            if (local.Date == today.AddDays(-1)) return "вчера, " + local.ToString("HH:mm", culture);

            string month = culture.DateTimeFormat.GetAbbreviatedMonthName(local.Month).TrimEnd('.');
            return local.Year == today.Year
                ? local.Day.ToString(culture) + " " + month + ", " + local.ToString("HH:mm", culture)
                : local.Day.ToString(culture) + " " + month + " " + local.Year.ToString(culture);
        }
    }
}
