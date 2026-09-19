using ReactiveUI;
using System;
using Writersword.Modules.Notes.Models;
using Writersword.Modules.Notes.Services;

namespace Writersword.Modules.Notes.ViewModels
{
    /// <summary>
    /// Одна строка заметки в представлении.
    ///
    /// Вид строки нигде не подмешивается в текст: маркер списка, номер,
    /// коробочка задачи и полоска цитаты — отдельные элементы разметки, а
    /// свойства ниже говорят разметке, что именно показывать. Поэтому каретка
    /// не может встать «внутрь маркера», а скопированный текст приходит без
    /// служебных символов.
    /// </summary>
    public sealed class NoteBlockViewModel : ReactiveObject
    {
        private NoteBlockType _type;
        private string _text;
        private int _number = 1;
        private bool _isChecked;
        private bool _isHighlighted;
        private bool _isStruckThrough;
        private bool _isSelected;
        private bool _isEditing;
        private bool _isReadOnly;

        /// <summary>
        /// Собрать строку из модели.
        /// Неизвестный вид считается ошибкой данных: молча привести его к
        /// абзацу значит потерять оформление, записанное более новой сборкой.
        /// </summary>
        public NoteBlockViewModel(NoteBlock model)
        {
            ArgumentNullException.ThrowIfNull(model);
            if (!Enum.IsDefined(model.Type))
                throw new ArgumentException("Unknown Notes block type", nameof(model));

            Id = model.Id == Guid.Empty ? Guid.NewGuid() : model.Id;
            _type = model.Type;
            _text = model.Text ?? string.Empty;
            _isChecked = model.IsChecked;
            _isHighlighted = model.IsHighlighted;
            _isStruckThrough = model.IsStruckThrough;
        }

        /// <summary>Устойчивый идентификатор строки.</summary>
        public Guid Id { get; }

        /// <summary>Вид строки.</summary>
        public NoteBlockType Type
        {
            get => _type;
            set
            {
                if (_type == value)
                    return;
                this.RaiseAndSetIfChanged(ref _type, value);
                RaisePresentationProperties();
            }
        }

        /// <summary>Текст строки без маркера вида, но с разметкой внутри текста.</summary>
        public string Text
        {
            get => _text;
            set
            {
                var next = value ?? string.Empty;
                if (_text == next)
                    return;
                this.RaiseAndSetIfChanged(ref _text, next);
                this.RaisePropertyChanged(nameof(ShowFormatted));
            }
        }

        /// <summary>
        /// Номер строки в нумерованном списке.
        /// Считает страница: номер зависит не от самой строки, а от того,
        /// сколько нумерованных строк идёт перед ней без разрыва.
        /// </summary>
        public int Number
        {
            get => _number;
            set
            {
                if (_number == value)
                    return;
                this.RaiseAndSetIfChanged(ref _number, value);
                this.RaisePropertyChanged(nameof(Marker));
            }
        }

        /// <summary>Задача отмечена выполненной.</summary>
        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value)
                    return;
                this.RaiseAndSetIfChanged(ref _isChecked, value);
                this.RaisePropertyChanged(nameof(IsVisuallyStruck));
                this.RaisePropertyChanged(nameof(ShowFormatted));
            }
        }

        /// <summary>Строка подсвечена целиком.</summary>
        public bool IsHighlighted
        {
            get => _isHighlighted;
            set => this.RaiseAndSetIfChanged(ref _isHighlighted, value);
        }

        /// <summary>Строка зачёркнута целиком.</summary>
        public bool IsStruckThrough
        {
            get => _isStruckThrough;
            set
            {
                if (_isStruckThrough == value)
                    return;
                this.RaiseAndSetIfChanged(ref _isStruckThrough, value);
                this.RaisePropertyChanged(nameof(IsVisuallyStruck));
                this.RaisePropertyChanged(nameof(ShowFormatted));
            }
        }

        /// <summary>Строка выделена — на ней стоит каретка или её выбрали щелчком.</summary>
        public bool IsSelected
        {
            get => _isSelected;
            set => this.RaiseAndSetIfChanged(ref _isSelected, value);
        }

        /// <summary>
        /// В строке стоит каретка. Признак ставит представление по получению и
        /// потере ввода полем строки.
        /// </summary>
        public bool IsEditing
        {
            get => _isEditing;
            set
            {
                if (_isEditing == value)
                    return;
                this.RaiseAndSetIfChanged(ref _isEditing, value);
                this.RaisePropertyChanged(nameof(ShowFormatted));
            }
        }

        /// <summary>
        /// Показывать поверх поля отрисованную надпись.
        ///
        /// Строка без каретки показывается набело: «**важно**» становится
        /// жирным «важно». Строка с кареткой показывает то, что записано, —
        /// иначе знаки разметки нельзя было бы поправить.
        ///
        /// Надпись лежит поверх поля, не участвует в проверке попадания мышью
        /// и появляется только тогда, когда отрисованное отличается от
        /// записанного. Щелчок проходит сквозь неё в поле, каретка встаёт
        /// туда, куда ткнули, надпись исчезает.
        /// </summary>
        public bool ShowFormatted => !_isEditing &&
            (IsVisuallyStruck || NotesService.HasInlineMarkup(_text));

        /// <summary>
        /// Правка запрещена — модуль открыт в режиме сравнения версий.
        /// Признак спускается сверху в каждую строку, чтобы разметка строки
        /// не искала корневую модель представления через дерево элементов.
        /// </summary>
        public bool IsReadOnly
        {
            get => _isReadOnly;
            set
            {
                if (_isReadOnly == value)
                    return;
                this.RaiseAndSetIfChanged(ref _isReadOnly, value);
                this.RaisePropertyChanged(nameof(IsEditable));
            }
        }

        /// <summary>Строку можно править.</summary>
        public bool IsEditable => !_isReadOnly;

        /// <summary>Строка — горизонтальная черта и текста не содержит.</summary>
        public bool IsDivider => Type == NoteBlockType.Divider;

        /// <summary>Строка содержит текст.</summary>
        public bool HasText => Type != NoteBlockType.Divider;

        /// <summary>Строка маркированного списка.</summary>
        public bool IsBullet => Type == NoteBlockType.Bullet;

        /// <summary>Строка нумерованного списка.</summary>
        public bool IsNumbered => Type == NoteBlockType.Numbered;

        /// <summary>Задача с коробочкой.</summary>
        public bool IsChecklist => Type == NoteBlockType.Checklist;

        /// <summary>Цитата.</summary>
        public bool IsQuote => Type == NoteBlockType.Quote;

        /// <summary>Строка кода.</summary>
        public bool IsCode => Type == NoteBlockType.Code;

        public bool IsHeading1 => Type == NoteBlockType.Heading1;
        public bool IsHeading2 => Type == NoteBlockType.Heading2;
        public bool IsHeading3 => Type == NoteBlockType.Heading3;
        public bool IsHeading4 => Type == NoteBlockType.Heading4;
        public bool IsHeading5 => Type == NoteBlockType.Heading5;
        public bool IsHeading6 => Type == NoteBlockType.Heading6;

        /// <summary>Строка любого заголовочного уровня.</summary>
        public bool IsHeading => Type is NoteBlockType.Heading1 or NoteBlockType.Heading2
            or NoteBlockType.Heading3 or NoteBlockType.Heading4
            or NoteBlockType.Heading5 or NoteBlockType.Heading6;

        /// <summary>Слева от текста стоит маркер — точка списка или номер.</summary>
        public bool HasMarker => IsBullet || IsNumbered;

        /// <summary>Сам маркер: точка для списка, номер с точкой для нумерации.</summary>
        public string Marker => Type switch
        {
            NoteBlockType.Bullet => "•",
            NoteBlockType.Numbered => _number + ".",
            _ => string.Empty
        };

        /// <summary>
        /// Строка выглядит зачёркнутой: либо её зачеркнули целиком, либо это
        /// выполненная задача.
        /// </summary>
        public bool IsVisuallyStruck => (IsChecklist && IsChecked) || IsStruckThrough;

        /// <summary>Снять модель со строки.</summary>
        public NoteBlock ToModel() => new()
        {
            Id = Id,
            Type = Type,
            Text = Text,
            IsChecked = IsChecked,
            IsHighlighted = IsHighlighted,
            IsStruckThrough = IsStruckThrough
        };

        /// <summary>
        /// Перенести в строку содержимое модели, не меняя её на другой объект.
        /// Нужно отмене: строка остаётся той же, ссылки на неё в выделении и в
        /// коллекции остаются целыми, меняется только содержимое.
        /// </summary>
        public void Apply(NoteBlock model)
        {
            ArgumentNullException.ThrowIfNull(model);
            if (!Enum.IsDefined(model.Type))
                throw new ArgumentException("Unknown Notes block type", nameof(model));

            Type = model.Type;
            Text = model.Text ?? string.Empty;
            IsChecked = model.IsChecked;
            IsHighlighted = model.IsHighlighted;
            IsStruckThrough = model.IsStruckThrough;
        }

        /// <summary>Оповестить разметку обо всех свойствах, зависящих от вида строки.</summary>
        private void RaisePresentationProperties()
        {
            this.RaisePropertyChanged(nameof(IsDivider));
            this.RaisePropertyChanged(nameof(HasText));
            this.RaisePropertyChanged(nameof(IsBullet));
            this.RaisePropertyChanged(nameof(IsNumbered));
            this.RaisePropertyChanged(nameof(IsChecklist));
            this.RaisePropertyChanged(nameof(IsQuote));
            this.RaisePropertyChanged(nameof(IsCode));
            this.RaisePropertyChanged(nameof(IsHeading1));
            this.RaisePropertyChanged(nameof(IsHeading2));
            this.RaisePropertyChanged(nameof(IsHeading3));
            this.RaisePropertyChanged(nameof(IsHeading4));
            this.RaisePropertyChanged(nameof(IsHeading5));
            this.RaisePropertyChanged(nameof(IsHeading6));
            this.RaisePropertyChanged(nameof(IsHeading));
            this.RaisePropertyChanged(nameof(HasMarker));
            this.RaisePropertyChanged(nameof(Marker));
            this.RaisePropertyChanged(nameof(IsVisuallyStruck));
            this.RaisePropertyChanged(nameof(ShowFormatted));
        }
    }
}
