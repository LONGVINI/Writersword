using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Writersword.Modules.Notes.Models;
using Writersword.Modules.Notes.Resources;
using Writersword.Modules.Notes.Services;

namespace Writersword.Modules.Notes.ViewModels
{
    /// <summary>
    /// Страница заметок в представлении: заголовок и список строк.
    /// Следит за своими строками и сама отмечает время правки — корневой
    /// модели представления не нужно подписываться на каждую строку отдельно.
    /// </summary>
    public sealed class NotePageViewModel : ReactiveObject
    {
        private string _title;
        private DateTime _updatedAtUtc;
        private bool _isReadOnly;

        /// <summary>Собрать страницу из модели.</summary>
        public NotePageViewModel(NotePage model)
        {
            ArgumentNullException.ThrowIfNull(model);

            Id = model.Id == Guid.Empty ? Guid.NewGuid() : model.Id;
            _title = model.Title ?? string.Empty;
            CreatedAtUtc = model.CreatedAtUtc == default ? DateTime.UtcNow : model.CreatedAtUtc;
            _updatedAtUtc = model.UpdatedAtUtc == default ? CreatedAtUtc : model.UpdatedAtUtc;

            Blocks = new ObservableCollection<NoteBlockViewModel>();
            foreach (var block in model.Blocks ?? new())
                AttachBlock(new NoteBlockViewModel(block));

            // Пустая страница всё равно должна во что-то принимать текст.
            if (Blocks.Count == 0)
                AttachBlock(new NoteBlockViewModel(new NoteBlock()));

            Renumber();
            Blocks.CollectionChanged += OnBlocksChanged;
        }

        /// <summary>Содержимое страницы изменилось: правка текста, вида строки, состава строк или заголовка.</summary>
        public event EventHandler? ContentChanged;

        /// <summary>Устойчивый идентификатор страницы.</summary>
        public Guid Id { get; }

        /// <summary>Момент создания в UTC.</summary>
        public DateTime CreatedAtUtc { get; }

        /// <summary>Строки страницы по порядку.</summary>
        public ObservableCollection<NoteBlockViewModel> Blocks { get; }

        /// <summary>Заголовок страницы.</summary>
        public string Title
        {
            get => _title;
            set
            {
                var next = value ?? string.Empty;
                if (_title == next)
                    return;
                this.RaiseAndSetIfChanged(ref _title, next);
                this.RaisePropertyChanged(nameof(DisplayTitle));
                Touch();
            }
        }

        /// <summary>
        /// Заголовок для списка страниц.
        /// Страница без заголовка показывается не пустой строкой, а понятной
        /// подписью: иначе в списке остаётся полоска, на которую нечем попасть
        /// глазом.
        /// </summary>
        public string DisplayTitle =>
            string.IsNullOrWhiteSpace(_title) ? NotesStrings.Page_Untitled : _title;

        /// <summary>Момент последнего изменения в UTC.</summary>
        public DateTime UpdatedAtUtc
        {
            get => _updatedAtUtc;
            set
            {
                if (_updatedAtUtc == value)
                    return;
                this.RaiseAndSetIfChanged(ref _updatedAtUtc, value);
                this.RaisePropertyChanged(nameof(UpdatedAtText));
            }
        }

        /// <summary>Подпись «Изменено …» для шапки страницы.</summary>
        public string UpdatedAtText =>
            string.Format(NotesStrings.Page_UpdatedAt, _updatedAtUtc.ToLocalTime());

        /// <summary>Сколько задач отмечено выполненными.</summary>
        public int DoneCount => Blocks.Count(block => block.IsChecklist && block.IsChecked);

        /// <summary>Сколько на странице задач.</summary>
        public int TaskCount => Blocks.Count(block => block.IsChecklist);

        /// <summary>Есть ли на странице задачи — от этого зависит показ счётчика.</summary>
        public bool HasTasks => TaskCount > 0;

        /// <summary>Подпись счётчика задач вида «3 / 7».</summary>
        public string TaskCountText => string.Format(NotesStrings.Page_Tasks, DoneCount, TaskCount);

        /// <summary>Первая непустая строка — показывается в списке страниц под заголовком.</summary>
        public string Preview
        {
            get
            {
                var text = Blocks.FirstOrDefault(block => block.HasText && block.Text.Trim().Length > 0)?.Text.Trim();
                if (string.IsNullOrEmpty(text))
                    return string.Empty;
                return text.Length <= PreviewLength ? text : text.Substring(0, PreviewLength) + "…";
            }
        }

        /// <summary>На странице нет ничего, кроме одной пустой строки.</summary>
        public bool IsEmpty =>
            string.IsNullOrWhiteSpace(_title) &&
            Blocks.Count == 1 &&
            NotesService.IsEmpty(Blocks[0].ToModel());

        /// <summary>Правка запрещена — режим сравнения версий.</summary>
        public bool IsReadOnly
        {
            get => _isReadOnly;
            set
            {
                if (_isReadOnly == value)
                    return;
                this.RaiseAndSetIfChanged(ref _isReadOnly, value);
                this.RaisePropertyChanged(nameof(IsEditable));
                foreach (var block in Blocks)
                    block.IsReadOnly = value;
            }
        }

        /// <summary>Страницу можно править.</summary>
        public bool IsEditable => !_isReadOnly;

        /// <summary>Снять модель со страницы.</summary>
        public NotePage ToModel() => new()
        {
            Id = Id,
            Title = _title.Trim(),
            CreatedAtUtc = CreatedAtUtc,
            UpdatedAtUtc = _updatedAtUtc,
            Blocks = Blocks.Select(block => block.ToModel()).ToList()
        };

        /// <summary>
        /// Вставить строку в заданное место.
        /// Единственный способ добавить строку: только здесь она получает
        /// признак «только чтение» и подписку на изменения.
        /// </summary>
        public NoteBlockViewModel InsertBlock(int index, NoteBlock model)
        {
            var block = new NoteBlockViewModel(model) { IsReadOnly = _isReadOnly };
            block.PropertyChanged += OnBlockPropertyChanged;
            Blocks.Insert(Math.Clamp(index, 0, Blocks.Count), block);
            return block;
        }

        /// <summary>Убрать строку и отписаться от неё.</summary>
        public void RemoveBlock(NoteBlockViewModel block)
        {
            if (!Blocks.Remove(block))
                return;
            block.PropertyChanged -= OnBlockPropertyChanged;
        }

        /// <summary>Отметить страницу изменённой сейчас.</summary>
        public void Touch()
        {
            UpdatedAtUtc = DateTime.UtcNow;
            RaiseSummaryProperties();
            ContentChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Длина предпросмотра в списке страниц.</summary>
        private const int PreviewLength = 60;

        /// <summary>Взять строку под надзор: признак «только чтение» и подписка.</summary>
        private void AttachBlock(NoteBlockViewModel block)
        {
            block.IsReadOnly = _isReadOnly;
            block.PropertyChanged += OnBlockPropertyChanged;
            Blocks.Add(block);
        }

        private void OnBlocksChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            Renumber();
            Touch();
        }

        /// <summary>
        /// Пересчитать номера нумерованного списка.
        /// Номер строки зависит не от неё самой, а от того, сколько
        /// нумерованных строк идёт перед ней подряд: строка другого вида
        /// начинает счёт заново.
        /// </summary>
        private void Renumber()
        {
            var number = 0;
            foreach (var block in Blocks)
            {
                if (block.IsNumbered)
                    block.Number = ++number;
                else
                    number = 0;
            }
        }

        private void OnBlockPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Выделение и режим правки — состояние интерфейса, а не содержимого:
            // от них страница не считается изменённой и не переписывает дату.
            if (e.PropertyName is nameof(NoteBlockViewModel.IsSelected)
                or nameof(NoteBlockViewModel.IsEditing)
                or nameof(NoteBlockViewModel.ShowFormatted)
                or nameof(NoteBlockViewModel.Number)
                or nameof(NoteBlockViewModel.Marker)
                or nameof(NoteBlockViewModel.IsReadOnly)
                or nameof(NoteBlockViewModel.IsEditable))
                return;

            UpdatedAtUtc = DateTime.UtcNow;

            // Сводка страницы пересчитывается обходом всех строк, а правка
            // текста приходит на каждое нажатие клавиши. Поэтому пересчёт
            // привязан к тому, что на сводку действительно влияет.
            switch (e.PropertyName)
            {
                case nameof(NoteBlockViewModel.Text):
                    this.RaisePropertyChanged(nameof(Preview));
                    this.RaisePropertyChanged(nameof(IsEmpty));
                    break;
                case nameof(NoteBlockViewModel.Type):
                    Renumber();
                    RaiseSummaryProperties();
                    break;
                case nameof(NoteBlockViewModel.IsChecked):
                    RaiseSummaryProperties();
                    break;
            }

            ContentChanged?.Invoke(this, EventArgs.Empty);
        }

        private void RaiseSummaryProperties()
        {
            this.RaisePropertyChanged(nameof(DoneCount));
            this.RaisePropertyChanged(nameof(TaskCount));
            this.RaisePropertyChanged(nameof(HasTasks));
            this.RaisePropertyChanged(nameof(TaskCountText));
            this.RaisePropertyChanged(nameof(Preview));
            this.RaisePropertyChanged(nameof(IsEmpty));
            this.RaisePropertyChanged(nameof(DisplayTitle));
        }
    }
}
