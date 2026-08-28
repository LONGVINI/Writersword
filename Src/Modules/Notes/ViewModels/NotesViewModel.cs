using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using Writersword.Modules.Notes.Models;

namespace Writersword.Modules.Notes.ViewModels
{
    public sealed class NoteBlockViewModel : ReactiveObject
    {
        private NoteBlockType _type;
        private string _text;
        private bool _isChecked;
        private bool _isHighlighted;
        private bool _isStruckThrough;
        private bool _isSelected;

        public NoteBlockViewModel(NoteBlock model)
        {
            Id = model.Id == Guid.Empty ? Guid.NewGuid() : model.Id;
            _type = model.Type;
            _text = model.Text ?? string.Empty;
            _isChecked = model.IsChecked;
            _isHighlighted = model.IsHighlighted;
            _isStruckThrough = model.IsStruckThrough;
        }

        public Guid Id { get; }
        public NoteBlockType Type
        {
            get => _type;
            set
            {
                this.RaiseAndSetIfChanged(ref _type, value);
                RaisePresentationProperties();
            }
        }

        public string Text
        {
            get => _text;
            set => this.RaiseAndSetIfChanged(ref _text, value ?? string.Empty);
        }

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                this.RaiseAndSetIfChanged(ref _isChecked, value);
                this.RaisePropertyChanged(nameof(IsVisuallyStruck));
            }
        }

        public bool IsHighlighted
        {
            get => _isHighlighted;
            set => this.RaiseAndSetIfChanged(ref _isHighlighted, value);
        }

        public bool IsStruckThrough
        {
            get => _isStruckThrough;
            set
            {
                this.RaiseAndSetIfChanged(ref _isStruckThrough, value);
                this.RaisePropertyChanged(nameof(IsVisuallyStruck));
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => this.RaiseAndSetIfChanged(ref _isSelected, value);
        }

        public bool IsDivider => Type == NoteBlockType.Divider;
        public bool IsEditor => !IsDivider;
        public bool IsChecklist => Type == NoteBlockType.Checklist;
        public bool HasPrefix => Type is NoteBlockType.Bullet or NoteBlockType.Quote;
        public string Prefix => Type == NoteBlockType.Bullet ? "•" : Type == NoteBlockType.Quote ? "│" : string.Empty;
        public double EditorFontSize => Type switch
        {
            NoteBlockType.Heading1 => 24,
            NoteBlockType.Heading2 => 20,
            NoteBlockType.Heading3 => 17,
            _ => 14
        };
        public bool IsHeading => Type is NoteBlockType.Heading1 or NoteBlockType.Heading2 or NoteBlockType.Heading3;
        public bool IsVisuallyStruck => IsChecked || IsStruckThrough;

        public NoteBlock ToModel() => new()
        {
            Id = Id,
            Type = Type,
            Text = Text,
            IsChecked = IsChecked,
            IsHighlighted = IsHighlighted,
            IsStruckThrough = IsStruckThrough
        };

        private void RaisePresentationProperties()
        {
            this.RaisePropertyChanged(nameof(IsDivider));
            this.RaisePropertyChanged(nameof(IsEditor));
            this.RaisePropertyChanged(nameof(IsChecklist));
            this.RaisePropertyChanged(nameof(HasPrefix));
            this.RaisePropertyChanged(nameof(Prefix));
            this.RaisePropertyChanged(nameof(EditorFontSize));
            this.RaisePropertyChanged(nameof(IsHeading));
        }
    }

    public sealed class NotePageViewModel : ReactiveObject
    {
        private string _title;

        public NotePageViewModel(NotePage model)
        {
            Id = model.Id == Guid.Empty ? Guid.NewGuid() : model.Id;
            _title = string.IsNullOrWhiteSpace(model.Title) ? "Без названия" : model.Title;
            CreatedAtUtc = model.CreatedAtUtc == default ? DateTime.UtcNow : model.CreatedAtUtc;
            UpdatedAtUtc = model.UpdatedAtUtc == default ? CreatedAtUtc : model.UpdatedAtUtc;
            Blocks = new ObservableCollection<NoteBlockViewModel>(
                (model.Blocks ?? new()).Select(block => new NoteBlockViewModel(block)));
            if (Blocks.Count == 0)
                Blocks.Add(CreateParagraph());
        }

        public Guid Id { get; }
        public DateTime CreatedAtUtc { get; }
        public DateTime UpdatedAtUtc { get; set; }
        public ObservableCollection<NoteBlockViewModel> Blocks { get; }
        public string Title
        {
            get => _title;
            set
            {
                this.RaiseAndSetIfChanged(ref _title, value ?? string.Empty);
                UpdatedAtUtc = DateTime.UtcNow;
            }
        }

        public NotePage ToModel() => new()
        {
            Id = Id,
            Title = string.IsNullOrWhiteSpace(Title) ? "Без названия" : Title.Trim(),
            CreatedAtUtc = CreatedAtUtc,
            UpdatedAtUtc = UpdatedAtUtc,
            Blocks = Blocks.Select(block => block.ToModel()).ToList()
        };

        public static NoteBlockViewModel CreateParagraph() => new(new NoteBlock());
    }

    public sealed class NotesViewModel : ReactiveObject
    {
        private NotePageViewModel? _selectedPage;
        private NoteBlockViewModel? _selectedBlock;
        private bool _isCompact;
        private bool _isPagePanelOpen = true;
        private bool _isReadOnly;

        public NotesViewModel()
        {
            Pages = new ObservableCollection<NotePageViewModel>();
            LoadData(new NotesData());
        }

        public ObservableCollection<NotePageViewModel> Pages { get; }
        public NotePageViewModel? SelectedPage
        {
            get => _selectedPage;
            set
            {
                if (_selectedPage == value)
                    return;
                this.RaiseAndSetIfChanged(ref _selectedPage, value);
                SelectBlock(value?.Blocks.FirstOrDefault());
            }
        }

        public NoteBlockViewModel? SelectedBlock
        {
            get => _selectedBlock;
            private set
            {
                this.RaiseAndSetIfChanged(ref _selectedBlock, value);
                RaiseSelectedBlockProperties();
            }
        }

        public bool IsParagraphSelected => SelectedBlock?.Type == NoteBlockType.Paragraph;
        public bool IsHeading1Selected => SelectedBlock?.Type == NoteBlockType.Heading1;
        public bool IsHeading2Selected => SelectedBlock?.Type == NoteBlockType.Heading2;
        public bool IsBulletSelected => SelectedBlock?.Type == NoteBlockType.Bullet;
        public bool IsChecklistSelected => SelectedBlock?.Type == NoteBlockType.Checklist;
        public bool IsQuoteSelected => SelectedBlock?.Type == NoteBlockType.Quote;
        public bool IsStrikeSelected => SelectedBlock?.IsStruckThrough == true;
        public bool IsHighlightSelected => SelectedBlock?.IsHighlighted == true;

        public bool IsCompact
        {
            get => _isCompact;
            set
            {
                this.RaiseAndSetIfChanged(ref _isCompact, value);
                this.RaisePropertyChanged(nameof(IsWidePagePanelVisible));
                this.RaisePropertyChanged(nameof(IsCompactHeaderVisible));
            }
        }

        public bool IsPagePanelOpen
        {
            get => _isPagePanelOpen;
            set
            {
                this.RaiseAndSetIfChanged(ref _isPagePanelOpen, value);
                this.RaisePropertyChanged(nameof(IsWidePagePanelVisible));
            }
        }

        public bool IsReadOnly
        {
            get => _isReadOnly;
            set => this.RaiseAndSetIfChanged(ref _isReadOnly, value);
        }

        public bool IsWidePagePanelVisible => !IsCompact && IsPagePanelOpen;
        public bool IsCompactHeaderVisible => IsCompact;

        public NotePageViewModel AddPage()
        {
            var page = new NotePageViewModel(new NotePage { Title = $"Страница {Pages.Count + 1}" });
            Pages.Add(page);
            SelectedPage = page;
            return page;
        }

        public void SelectBlock(NoteBlockViewModel? block)
        {
            if (SelectedBlock != null)
                SelectedBlock.IsSelected = false;
            SelectedBlock = block;
            if (SelectedBlock != null)
                SelectedBlock.IsSelected = true;
        }

        public NoteBlockViewModel CommitLine(NoteBlockViewModel block)
        {
            if (SelectedPage == null)
                return block;

            // Маркер преобразуется только у обычного абзаца, чтобы содержимое
            // заголовка или списка не меняло тип повторно при каждом Enter.
            if (block.Type == NoteBlockType.Paragraph)
                ApplyLineShortcut(block);

            var nextType = block.Type is NoteBlockType.Bullet or NoteBlockType.Checklist
                ? block.Type
                : NoteBlockType.Paragraph;
            var next = new NoteBlockViewModel(new NoteBlock { Type = nextType });
            var index = SelectedPage.Blocks.IndexOf(block);
            SelectedPage.Blocks.Insert(index + 1, next);
            SelectedPage.UpdatedAtUtc = DateTime.UtcNow;
            SelectBlock(next);
            return next;
        }

        public NoteBlockViewModel? RemoveEmptyBlock(NoteBlockViewModel block)
        {
            if (SelectedPage == null || SelectedPage.Blocks.Count <= 1 || block.Text.Length != 0)
                return null;
            var index = SelectedPage.Blocks.IndexOf(block);
            if (index < 0)
                return null;
            SelectedPage.Blocks.RemoveAt(index);
            var target = SelectedPage.Blocks[Math.Max(0, index - 1)];
            SelectedPage.UpdatedAtUtc = DateTime.UtcNow;
            SelectBlock(target);
            return target;
        }

        public void SetSelectedBlockType(NoteBlockType type)
        {
            if (SelectedBlock == null || IsReadOnly)
                return;
            SelectedBlock.Type = type;
            SelectedPage!.UpdatedAtUtc = DateTime.UtcNow;
            RaiseSelectedBlockProperties();
        }

        public void ToggleSelectedHighlight()
        {
            if (SelectedBlock == null || IsReadOnly)
                return;
            SelectedBlock.IsHighlighted = !SelectedBlock.IsHighlighted;
            SelectedPage!.UpdatedAtUtc = DateTime.UtcNow;
            RaiseSelectedBlockProperties();
        }

        public void ToggleSelectedStrikeThrough()
        {
            if (SelectedBlock == null || IsReadOnly)
                return;
            SelectedBlock.IsStruckThrough = !SelectedBlock.IsStruckThrough;
            SelectedPage!.UpdatedAtUtc = DateTime.UtcNow;
            RaiseSelectedBlockProperties();
        }

        public NotesData CreateSnapshot() => new() { Pages = Pages.Select(page => page.ToModel()).ToList() };
        public NotesSessionData CreateSessionSnapshot() => new()
        {
            SelectedPageId = SelectedPage?.Id,
            IsPagePanelOpen = IsPagePanelOpen
        };

        public void LoadData(NotesData? data)
        {
            Pages.Clear();
            foreach (var page in data?.Pages ?? new())
                Pages.Add(new NotePageViewModel(page));
            if (Pages.Count == 0)
                Pages.Add(new NotePageViewModel(new NotePage { Title = "Заметки" }));
            SelectedPage = Pages[0];
        }

        public void RestoreSession(NotesSessionData? session)
        {
            if (session == null)
                return;
            IsPagePanelOpen = session.IsPagePanelOpen;
            SelectedPage = Pages.FirstOrDefault(page => page.Id == session.SelectedPageId) ?? Pages.FirstOrDefault();
        }

        private static void ApplyLineShortcut(NoteBlockViewModel block)
        {
            var text = block.Text.TrimStart();
            var shortcut = text switch
            {
                var value when value.StartsWith("### ", StringComparison.Ordinal) => (NoteBlockType.Heading3, value[4..]),
                var value when value.StartsWith("## ", StringComparison.Ordinal) => (NoteBlockType.Heading2, value[3..]),
                var value when value.StartsWith("# ", StringComparison.Ordinal) => (NoteBlockType.Heading1, value[2..]),
                var value when value.StartsWith("- [ ] ", StringComparison.Ordinal) => (NoteBlockType.Checklist, value[6..]),
                var value when value.StartsWith("- ", StringComparison.Ordinal) => (NoteBlockType.Bullet, value[2..]),
                var value when value.StartsWith("> ", StringComparison.Ordinal) => (NoteBlockType.Quote, value[2..]),
                "---" => (NoteBlockType.Divider, string.Empty),
                _ => (NoteBlockType.Paragraph, block.Text)
            };
            block.Type = shortcut.Item1;
            block.Text = shortcut.Item2;
        }

        private void RaiseSelectedBlockProperties()
        {
            this.RaisePropertyChanged(nameof(IsParagraphSelected));
            this.RaisePropertyChanged(nameof(IsHeading1Selected));
            this.RaisePropertyChanged(nameof(IsHeading2Selected));
            this.RaisePropertyChanged(nameof(IsBulletSelected));
            this.RaisePropertyChanged(nameof(IsChecklistSelected));
            this.RaisePropertyChanged(nameof(IsQuoteSelected));
            this.RaisePropertyChanged(nameof(IsStrikeSelected));
            this.RaisePropertyChanged(nameof(IsHighlightSelected));
        }
    }
}
