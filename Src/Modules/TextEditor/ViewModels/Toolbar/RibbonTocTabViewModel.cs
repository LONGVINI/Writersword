using System;
using System.Windows.Input;
using ReactiveUI;
using Writersword.Modules.TextEditor.Models.Toc;

namespace Writersword.Modules.TextEditor.ViewModels.Toolbar
{
    /// <summary>
    /// Что вкладка «Оглавление» умеет спросить и попросить у модуля.
    ///
    /// Договор отдельный от <see cref="Contracts.ITextEditorCommandTarget"/> намеренно:
    /// оглавление правится не командой над текстом под кареткой, а настройками целого
    /// блока, и после каждой правки его надо пересобрать. Класть это в общий договор
    /// команд значило бы засорить его десятком свойств, нужных одной вкладке.
    /// </summary>
    public interface ITocHost
    {
        /// <summary>Оглавление под кареткой. null — каретка вне оглавления.</summary>
        TocSettings? ActiveToc { get; }

        /// <summary>Пересобрать оглавление после правки его настроек.</summary>
        void RebuildActiveToc();

        /// <summary>Убрать оглавление из рукописи.</summary>
        void RemoveActiveToc();

        /// <summary>
        /// Вставить ещё одно оглавление на место каретки. Частное оглавление части или
        /// книги в книге — обычное дело, и уходить за ним на вкладку «Ссылки», стоя в
        /// уже существующем списке, незачем.
        /// </summary>
        void InsertToc();

        /// <summary>Перейти к тому месту книги, откуда взята строка под кареткой.</summary>
        void GoToTocTarget();

        /// <summary>Показать или убрать навигатор по заголовкам.</summary>
        void ToggleNavigator();

        /// <summary>
        /// Как выглядят строки оглавления этого уровня. null — рукописи нет или уровень
        /// вне 1…9.
        /// </summary>
        (double FontSizePt, bool IsBold, bool IsItalic)? GetTocLevelStyle(int level);

        /// <summary>
        /// Правит стиль строк уровня. Передаются только меняющиеся свойства: остальные
        /// остаются такими, какими их даёт цепочка BasedOn.
        /// </summary>
        void SetTocLevelStyle(int level, double? fontSizePt, bool? isBold, bool? isItalic);

        /// <summary>Вернуть стилям оглавления встроенный вид.</summary>
        void ResetTocStyles();

        /// <summary>
        /// Название по умолчанию над списком — то, что стоит в рукописи, пока человек не
        /// задал своё. Показывается в поле подсказкой.
        /// </summary>
        string TocDefaultTitle { get; }
    }

    /// <summary>
    /// Контекстная вкладка «Оглавление»: появляется, когда каретка стоит внутри
    /// оглавления, и собирает в одном месте всё, что к нему относится.
    ///
    /// Вкладка контекстная, а не постоянная, по той же причине, по какой в программе
    /// контекстны «Работа с таблицами» и «Формат»: эти инструменты нужны, только пока
    /// человек стоит в своём объекте, а в остальное время занимают место в ленте.
    ///
    /// Каждая правка сразу пересобирает оглавление. Кнопки «Применить» здесь нет
    /// намеренно: человек меняет вид списка и хочет видеть результат, а не объяснять
    /// программе, что он закончил.
    /// </summary>
    public sealed class RibbonTocTabViewModel : ReactiveObject
    {
        private readonly ITocHost _host;

        // Пока настройки раскладываются по вкладке, обратные записи глушатся: иначе
        // каждое присваивание в UI считалось бы правкой человека и пересобирало бы
        // оглавление — на каждое поле по разу.
        private bool _suspend;

        // Уровень, чьё оформление сейчас правится группой «Оформление». К настройкам
        // оглавления отношения не имеет: это состояние самой ленты, и в рукопись оно
        // не уезжает.
        private int _styleLevel = 1;

        private bool _isMainGroupExpanded = true;
        private bool _isLevelsGroupExpanded = true;
        private bool _isLineGroupExpanded = true;
        private bool _isSourceGroupExpanded = true;
        private bool _isStyleGroupExpanded = true;

        public RibbonTocTabViewModel(ITocHost host)
        {
            _host = host;

            UpdateCommand = ReactiveCommand.Create(() => _host.RebuildActiveToc());
            InsertCommand = ReactiveCommand.Create(() => _host.InsertToc());
            RemoveCommand = ReactiveCommand.Create(() => _host.RemoveActiveToc());
            GoToTargetCommand = ReactiveCommand.Create(() => _host.GoToTocTarget());
            ToggleNavigatorCommand = ReactiveCommand.Create(() => _host.ToggleNavigator());

            SetLevels1Command = ReactiveCommand.Create(() => SetMaxLevel(1));
            SetLevels3Command = ReactiveCommand.Create(() => SetMaxLevel(3));
            SetLevels5Command = ReactiveCommand.Create(() => SetMaxLevel(5));
            SetLevels9Command = ReactiveCommand.Create(() => SetMaxLevel(9));

            LeaderNoneCommand = ReactiveCommand.Create(() => SetLeader(TocLeader.None));
            LeaderDotsCommand = ReactiveCommand.Create(() => SetLeader(TocLeader.Dots));
            LeaderDashesCommand = ReactiveCommand.Create(() => SetLeader(TocLeader.Dashes));
            LeaderLineCommand = ReactiveCommand.Create(() => SetLeader(TocLeader.Line));

            ResetStylesCommand = ReactiveCommand.Create(() =>
            {
                _host.ResetTocStyles();
                RefreshAll();
            });
        }

        // ── Команды ───────────────────────────────────────────────────────

        public ICommand UpdateCommand { get; }
        public ICommand InsertCommand { get; }
        public ICommand RemoveCommand { get; }

        /// <summary>Уйти к главе, на которую указывает строка под кареткой.</summary>
        public ICommand GoToTargetCommand { get; }

        public ICommand ToggleNavigatorCommand { get; }

        public ICommand SetLevels1Command { get; }
        public ICommand SetLevels3Command { get; }
        public ICommand SetLevels5Command { get; }
        public ICommand SetLevels9Command { get; }

        public ICommand LeaderNoneCommand { get; }
        public ICommand LeaderDotsCommand { get; }
        public ICommand LeaderDashesCommand { get; }
        public ICommand LeaderLineCommand { get; }

        public ICommand ResetStylesCommand { get; }

        // ── Свойства оглавления ───────────────────────────────────────────

        private TocSettings? Toc => _host.ActiveToc;

        /// <summary>Показывать номера страниц.</summary>
        public bool ShowPageNumbers
        {
            get => Toc?.ShowPageNumbers ?? true;
            set => Apply(toc => toc.ShowPageNumbers = value);
        }

        /// <summary>Сдвигать строки вложенных уровней вправо.</summary>
        public bool IndentByLevel
        {
            get => Toc?.IndentByLevel ?? true;
            set => Apply(toc => toc.IndentByLevel = value);
        }

        /// <summary>Показывать название над списком.</summary>
        public bool ShowTitle
        {
            get => Toc?.ShowTitle ?? true;
            set => Apply(toc => toc.ShowTitle = value);
        }

        /// <summary>
        /// Брать в оглавление абзацы, помеченные вручную, а не только те, что носят
        /// стиль заголовка.
        /// </summary>
        public bool IncludeManualEntries
        {
            get => Toc?.IncludeManualEntries ?? true;
            set => Apply(toc => toc.IncludeManualEntries = value);
        }

        /// <summary>
        /// Обновлять номера страниц, когда книга перекомпонована.
        ///
        /// Выключенное самообновление не значит «оглавление устарело навсегда»: кнопка
        /// «Обновить» пересобирает его в любом случае. Значит оно ровно одно — программа
        /// не трогает список сама.
        /// </summary>
        public bool AutoUpdate
        {
            get => Toc?.AutoUpdate ?? true;
            set => Apply(toc => toc.AutoUpdate = value);
        }

        /// <summary>
        /// Название над списком. Пустая строка — взять название по умолчанию: так
        /// оглавление переезжает между языками, не унося с собой чужое слово.
        /// </summary>
        public string Title
        {
            get => Toc?.Title ?? string.Empty;
            set => Apply(toc => toc.Title = value ?? string.Empty);
        }

        /// <summary>Подсказка в пустом поле названия — то, что встанет над списком.</summary>
        public string TitlePlaceholder => _host.TocDefaultTitle;

        /// <summary>Самый верхний уровень, попадающий в оглавление.</summary>
        public int MinLevel
        {
            get => Toc?.MinLevel ?? 1;
            set => SetMinLevel(value);
        }

        /// <summary>Самый глубокий уровень, попадающий в оглавление.</summary>
        public int MaxLevel
        {
            get => Toc?.MaxLevel ?? 3;
            set => SetMaxLevel(value);
        }

        /// <summary>
        /// Верхняя граница уровней для числового поля ленты.
        ///
        /// Поле ленты работает дробными числами, уровень же целый. Перевод сделан
        /// свойством, а не конвертером в разметке: конвертер ради двух полей читается
        /// хуже, чем пара строк здесь.
        /// </summary>
        public double MinLevelValue
        {
            get => MinLevel;
            set => MinLevel = (int)Math.Round(value);
        }

        /// <summary>Шаг сдвига строки на один уровень в пунктах.</summary>
        public double LevelIndentPt
        {
            get => Toc?.LevelIndentPt ?? 18.0;
            set
            {
                double clamped = value < 0 ? 0 : (value > 144 ? 144 : value);
                Apply(toc => toc.LevelIndentPt = clamped);
            }
        }

        /// <summary>Надпись на кнопке глубины: «1-3», «1-5» и так далее.</summary>
        public string LevelsText => MaxLevel <= MinLevel
            ? MinLevel.ToString()
            : MinLevel.ToString() + "-" + MaxLevel.ToString();

        // Отметки на кнопках заполнителя: показывают, какой выбран сейчас.
        public bool IsLeaderNone => (Toc?.Leader ?? TocLeader.Dots) == TocLeader.None;
        public bool IsLeaderDots => (Toc?.Leader ?? TocLeader.Dots) == TocLeader.Dots;
        public bool IsLeaderDashes => (Toc?.Leader ?? TocLeader.Dots) == TocLeader.Dashes;
        public bool IsLeaderLine => (Toc?.Leader ?? TocLeader.Dots) == TocLeader.Line;

        public bool IsLevels1 => MaxLevel == 1;
        public bool IsLevels3 => MaxLevel == 3;
        public bool IsLevels5 => MaxLevel == 5;
        public bool IsLevels9 => MaxLevel == 9;

        // ── Оформление строк ──────────────────────────────────────────────

        /// <summary>
        /// Уровень, чьё оформление правится: строки Toc1…Toc9.
        ///
        /// Правится именно стиль, а не абзацы: строк в книге сотни, они пересобираются
        /// при каждом обновлении, и форматирование, положенное на абзац, исчезло бы
        /// вместе с ними. Стиль пересборку переживает.
        /// </summary>
        public int StyleLevel
        {
            get => _styleLevel;
            set
            {
                int clamped = value < 1 ? 1 : (value > 9 ? 9 : value);
                if (_styleLevel == clamped) return;

                this.RaiseAndSetIfChanged(ref _styleLevel, clamped);
                RefreshStyleFields();
            }
        }

        /// <summary>Уровень оформления для числового поля ленты.</summary>
        public double StyleLevelValue
        {
            get => StyleLevel;
            set => StyleLevel = (int)Math.Round(value);
        }

        /// <summary>Кегль строк выбранного уровня в пунктах.</summary>
        public double StyleFontSize
        {
            get => _host.GetTocLevelStyle(_styleLevel)?.FontSizePt ?? 0;
            set
            {
                if (_suspend) return;
                if (value < 1) return;

                _host.SetTocLevelStyle(_styleLevel, value, null, null);
                RefreshStyleFields();
            }
        }

        /// <summary>Полужирное начертание строк выбранного уровня.</summary>
        public bool StyleBold
        {
            get => _host.GetTocLevelStyle(_styleLevel)?.IsBold ?? false;
            set
            {
                if (_suspend) return;

                _host.SetTocLevelStyle(_styleLevel, null, value, null);
                RefreshStyleFields();
            }
        }

        /// <summary>Курсив строк выбранного уровня.</summary>
        public bool StyleItalic
        {
            get => _host.GetTocLevelStyle(_styleLevel)?.IsItalic ?? false;
            set
            {
                if (_suspend) return;

                _host.SetTocLevelStyle(_styleLevel, null, null, value);
                RefreshStyleFields();
            }
        }

        // ── Сворачивание групп при узкой ленте ────────────────────────────

        public bool IsMainGroupExpanded
        {
            get => _isMainGroupExpanded;
            set => this.RaiseAndSetIfChanged(ref _isMainGroupExpanded, value);
        }

        public bool IsLevelsGroupExpanded
        {
            get => _isLevelsGroupExpanded;
            set => this.RaiseAndSetIfChanged(ref _isLevelsGroupExpanded, value);
        }

        public bool IsLineGroupExpanded
        {
            get => _isLineGroupExpanded;
            set => this.RaiseAndSetIfChanged(ref _isLineGroupExpanded, value);
        }

        public bool IsSourceGroupExpanded
        {
            get => _isSourceGroupExpanded;
            set => this.RaiseAndSetIfChanged(ref _isSourceGroupExpanded, value);
        }

        public bool IsStyleGroupExpanded
        {
            get => _isStyleGroupExpanded;
            set => this.RaiseAndSetIfChanged(ref _isStyleGroupExpanded, value);
        }

        /// <summary>
        /// Прячет группы по очереди, когда ленте не хватает ширины.
        ///
        /// Порядок исчезновения обратен нужности: первым уходит оформление — его правят
        /// раз на книгу, — последней остаётся группа с «Обновить» и «Перейти», ради
        /// которых вкладку и открывают.
        /// </summary>
        public void UpdateLayout(double availableWidth)
        {
            if (availableWidth >= 1180)
            {
                IsMainGroupExpanded = true;
                IsLevelsGroupExpanded = true;
                IsLineGroupExpanded = true;
                IsSourceGroupExpanded = true;
                IsStyleGroupExpanded = true;
                return;
            }

            IsStyleGroupExpanded = false;

            if (availableWidth >= 960)
            {
                IsMainGroupExpanded = true;
                IsLevelsGroupExpanded = true;
                IsLineGroupExpanded = true;
                IsSourceGroupExpanded = true;
                return;
            }

            IsSourceGroupExpanded = false;

            if (availableWidth >= 740)
            {
                IsMainGroupExpanded = true;
                IsLevelsGroupExpanded = true;
                IsLineGroupExpanded = true;
                return;
            }

            IsLineGroupExpanded = false;

            if (availableWidth >= 520)
            {
                IsMainGroupExpanded = true;
                IsLevelsGroupExpanded = true;
                return;
            }

            IsLevelsGroupExpanded = false;
            IsMainGroupExpanded = availableWidth >= 240;
        }

        // ── Внутреннее ────────────────────────────────────────────────────

        /// <summary>
        /// Ставит нижнюю границу уровней. Верхняя подтягивается следом, если оказалась
        /// выше нижней: диапазон, у которого начало ниже конца, не отбирает заголовки
        /// вовсе, и человек получил бы пустой список без объяснения.
        /// </summary>
        private void SetMaxLevel(int level)
        {
            int clamped = level < 1 ? 1 : (level > 9 ? 9 : level);

            Apply(toc =>
            {
                toc.MaxLevel = clamped;
                if (toc.MinLevel > clamped) toc.MinLevel = clamped;
            });
        }

        /// <summary>Ставит верхнюю границу уровней, не давая ей уйти ниже нижней.</summary>
        private void SetMinLevel(int level)
        {
            int clamped = level < 1 ? 1 : (level > 9 ? 9 : level);

            Apply(toc =>
            {
                toc.MinLevel = clamped;
                if (toc.MaxLevel < clamped) toc.MaxLevel = clamped;
            });
        }

        private void SetLeader(TocLeader leader) => Apply(toc => toc.Leader = leader);

        /// <summary>
        /// Правит настройки оглавления под кареткой и тут же пересобирает его.
        /// Вне оглавления вкладки не видно, но проверка нужна: каретка могла уйти,
        /// пока висел щелчок.
        /// </summary>
        private void Apply(Action<TocSettings> change)
        {
            if (_suspend) return;

            var toc = Toc;
            if (toc is null) return;

            change(toc);
            _host.RebuildActiveToc();
            RefreshAll();
        }

        /// <summary>
        /// Перечитывает поля оформления. Вынесено отдельно от <see cref="RefreshAll"/>:
        /// смена уровня в списке настроек оглавления не трогает, и гонять из-за неё всю
        /// вкладку незачем.
        /// </summary>
        private void RefreshStyleFields()
        {
            _suspend = true;
            try
            {
                this.RaisePropertyChanged(nameof(StyleLevelValue));
                this.RaisePropertyChanged(nameof(StyleFontSize));
                this.RaisePropertyChanged(nameof(StyleBold));
                this.RaisePropertyChanged(nameof(StyleItalic));
            }
            finally
            {
                _suspend = false;
            }
        }

        /// <summary>
        /// Перечитывает вкладку из настроек оглавления под кареткой. Зовётся модулем,
        /// когда каретка перешла в другое оглавление или человек нажал «Обновить».
        /// </summary>
        public void RefreshAll()
        {
            _suspend = true;
            try
            {
                this.RaisePropertyChanged(nameof(ShowPageNumbers));
                this.RaisePropertyChanged(nameof(IndentByLevel));
                this.RaisePropertyChanged(nameof(ShowTitle));
                this.RaisePropertyChanged(nameof(IncludeManualEntries));
                this.RaisePropertyChanged(nameof(AutoUpdate));
                this.RaisePropertyChanged(nameof(Title));
                this.RaisePropertyChanged(nameof(TitlePlaceholder));
                this.RaisePropertyChanged(nameof(MinLevel));
                this.RaisePropertyChanged(nameof(MinLevelValue));
                this.RaisePropertyChanged(nameof(MaxLevel));
                this.RaisePropertyChanged(nameof(LevelIndentPt));
                this.RaisePropertyChanged(nameof(LevelsText));

                this.RaisePropertyChanged(nameof(IsLeaderNone));
                this.RaisePropertyChanged(nameof(IsLeaderDots));
                this.RaisePropertyChanged(nameof(IsLeaderDashes));
                this.RaisePropertyChanged(nameof(IsLeaderLine));

                this.RaisePropertyChanged(nameof(IsLevels1));
                this.RaisePropertyChanged(nameof(IsLevels3));
                this.RaisePropertyChanged(nameof(IsLevels5));
                this.RaisePropertyChanged(nameof(IsLevels9));

                this.RaisePropertyChanged(nameof(StyleLevelValue));
                this.RaisePropertyChanged(nameof(StyleFontSize));
                this.RaisePropertyChanged(nameof(StyleBold));
                this.RaisePropertyChanged(nameof(StyleItalic));
            }
            finally
            {
                _suspend = false;
            }
        }
    }
}
