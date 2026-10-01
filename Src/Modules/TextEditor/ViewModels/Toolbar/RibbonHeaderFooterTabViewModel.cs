using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using ReactiveUI;
using Writersword.Modules.TextEditor.Models.Page;

namespace Writersword.Modules.TextEditor.ViewModels.Toolbar
{
    /// <summary>
    /// Что вкладке «Колонтитулы» нужно от модуля: документ, чьи колонтитулы правятся.
    ///
    /// Договор отдельный от <see cref="Contracts.ITextEditorCommandTarget"/> по той же
    /// причине, что и у оглавления: правка идёт не текста под кареткой, а настроек
    /// всего документа, и засорять общий договор полутора десятками команд одной
    /// вкладки незачем.
    /// </summary>
    public interface IHeaderFooterHost
    {
        /// <summary>Документ, чьи колонтитулы правятся. Null — документа нет.</summary>
        DocumentViewModel? HeaderFooterDocument { get; }
    }

    /// <summary>
    /// Контекстная вкладка «Колонтитулы». Видна, пока идёт работа с колонтитулами:
    /// после двойного щелчка по полю листа и после кнопок «Колонтитулы» на «Вставке».
    ///
    /// Группы вкладки — от общего к частному: сами колонтитулы и номер, вид номера,
    /// параметры всего документа, затем то, что относится к одной странице, и список
    /// правил страниц. Кнопки группы «Эта страница» — ответ на главную боль Word: убрать
    /// номер с одного листа, вернуть его, «дальше не надо» — без разрывов разделов.
    /// </summary>
    public sealed class RibbonHeaderFooterTabViewModel : ReactiveObject
    {
        private readonly IHeaderFooterHost _host;

        // Поле «начать счёт отсюда с» живёт своей жизнью: это не настройка документа,
        // а число для кнопки.
        private double _restartValue = 1;

        public RibbonHeaderFooterTabViewModel(IHeaderFooterHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));

            EditHeaderCommand = ReactiveCommand.Create(() => Doc?.RequestHeaderFooterEdit(true));
            EditFooterCommand = ReactiveCommand.Create(() => Doc?.RequestHeaderFooterEdit(false));

            InsertPageNumberCommand = ReactiveCommand.Create<string>(param =>
            {
                if (Enum.TryParse(param, out PageNumberPosition position))
                    Doc?.InsertPageNumber(position);
            });

            InsertPageOfPagesCommand = ReactiveCommand.Create(() =>
                Doc?.InsertPageNumber(PageNumberPosition.BottomCenter,
                    "Страница " + HeaderFooterSettings.PageToken + " из " + HeaderFooterSettings.PagesToken));

            InsertPageFieldCommand = ReactiveCommand.Create(() =>
                Doc?.RequestHeaderFooterFieldInsert(HeaderFooterSettings.PageToken));
            InsertPagesFieldCommand = ReactiveCommand.Create(() =>
                Doc?.RequestHeaderFooterFieldInsert(HeaderFooterSettings.PagesToken));

            RemovePageNumbersCommand = ReactiveCommand.Create(() => Doc?.RemovePageNumbers());
            RemoveAllCommand = ReactiveCommand.Create(() => Doc?.RemoveHeaderFooter());

            SetFormatCommand = ReactiveCommand.Create<string>(param =>
            {
                if (Enum.TryParse(param, out PageNumberFormat format))
                    Doc?.SetPageNumberFormat(format);
            });

            SetFormatHereCommand = ReactiveCommand.Create<string>(param =>
            {
                if (Enum.TryParse(param, out PageNumberFormat format))
                    Doc?.SetFormatFromPage(format);
            });

            ToggleNumberHereCommand = ReactiveCommand.Create(() => Doc?.ToggleNumberOnPage());
            ToggleBandsHereCommand = ReactiveCommand.Create(() => Doc?.ToggleBandsOnPage());
            StopNumbersCommand = ReactiveCommand.Create(() => Doc?.StopNumbersFromPage());
            ResumeNumbersCommand = ReactiveCommand.Create(() => Doc?.ResumeNumbersFromPage());
            StopBandsCommand = ReactiveCommand.Create(() => Doc?.StopBandsFromPage());
            ResumeBandsCommand = ReactiveCommand.Create(() => Doc?.ResumeBandsFromPage());
            RestartHereCommand = ReactiveCommand.Create(() =>
                Doc?.RestartNumberingFromPage((int)Math.Round(_restartValue)));

            RemoveRuleCommand = ReactiveCommand.Create<Guid>(id => Doc?.RemovePageRule(id));
            ClearRulesCommand = ReactiveCommand.Create(() => Doc?.ClearPageRules());

            CloseCommand = ReactiveCommand.Create(() => Doc?.ExitHeaderFooterMode());
        }

        private DocumentViewModel? Doc => _host.HeaderFooterDocument;

        private HeaderFooterSettings? Settings => Doc?.HeaderFooter;

        // ── Команды ───────────────────────────────────────────────────────

        public ICommand EditHeaderCommand { get; }
        public ICommand EditFooterCommand { get; }

        /// <summary>CommandParameter — место номера (<see cref="PageNumberPosition"/>).</summary>
        public ICommand InsertPageNumberCommand { get; }

        /// <summary>«Страница N из M» внизу по центру.</summary>
        public ICommand InsertPageOfPagesCommand { get; }

        /// <summary>Номер страницы в место колонтитула, где стоит каретка.</summary>
        public ICommand InsertPageFieldCommand { get; }

        /// <summary>Число страниц в место колонтитула, где стоит каретка.</summary>
        public ICommand InsertPagesFieldCommand { get; }

        public ICommand RemovePageNumbersCommand { get; }
        public ICommand RemoveAllCommand { get; }

        /// <summary>CommandParameter — вид номера (<see cref="PageNumberFormat"/>) для всего документа.</summary>
        public ICommand SetFormatCommand { get; }

        /// <summary>CommandParameter — вид номера с этой страницы.</summary>
        public ICommand SetFormatHereCommand { get; }

        public ICommand ToggleNumberHereCommand { get; }
        public ICommand ToggleBandsHereCommand { get; }
        public ICommand StopNumbersCommand { get; }
        public ICommand ResumeNumbersCommand { get; }
        public ICommand StopBandsCommand { get; }
        public ICommand ResumeBandsCommand { get; }
        public ICommand RestartHereCommand { get; }

        /// <summary>CommandParameter — Id правила.</summary>
        public ICommand RemoveRuleCommand { get; }

        public ICommand ClearRulesCommand { get; }
        public ICommand CloseCommand { get; }

        // ── Параметры документа ───────────────────────────────────────────

        public bool DifferentFirstPage
        {
            get => Settings?.DifferentFirstPage ?? false;
            set
            {
                if (value == DifferentFirstPage) return;
                Doc?.SetDifferentFirstPage(value);
            }
        }

        public bool DifferentOddEven
        {
            get => Settings?.DifferentOddEven ?? false;
            set
            {
                if (value == DifferentOddEven) return;
                Doc?.SetDifferentOddEven(value);
            }
        }

        public bool HideNumberOnChapterStart
        {
            get => Settings?.HideNumberOnChapterStart ?? false;
            set
            {
                if (value == HideNumberOnChapterStart) return;
                Doc?.SetHideNumberOnChapterStart(value);
            }
        }

        public bool HideHeaderFooterOnChapterStart
        {
            get => Settings?.HideHeaderFooterOnChapterStart ?? false;
            set
            {
                if (value == HideHeaderFooterOnChapterStart) return;
                Doc?.SetHideHeaderFooterOnChapterStart(value);
            }
        }

        /// <summary>Номер первой страницы документа.</summary>
        public double StartNumber
        {
            get => Settings?.StartNumber ?? 1;
            set
            {
                int start = (int)Math.Round(value);
                if (start == (Settings?.StartNumber ?? 1)) return;
                Doc?.SetPageNumberStart(start);
            }
        }

        /// <summary>Вид номера ключом — для отметки в меню.</summary>
        public string NumberFormatKey => (Settings?.NumberFormat ?? PageNumberFormat.Arabic).ToString();

        /// <summary>Вид номера подписью на кнопке.</summary>
        public string NumberFormatLabel => DocumentViewModel.FormatName(Settings?.NumberFormat ?? PageNumberFormat.Arabic);

        /// <summary>От верхнего края листа до верхнего колонтитула, мм.</summary>
        public double HeaderDistance
        {
            get => Doc?.PageSettings.HeaderDistanceMm ?? 12.5;
            set
            {
                if (Math.Abs(value - HeaderDistance) < 0.01) return;
                Doc?.SetHeaderFooterDistance(true, value);
                this.RaisePropertyChanged();
            }
        }

        /// <summary>От нижнего края листа до нижнего колонтитула, мм.</summary>
        public double FooterDistance
        {
            get => Doc?.PageSettings.FooterDistanceMm ?? 12.5;
            set
            {
                if (Math.Abs(value - FooterDistance) < 0.01) return;
                Doc?.SetHeaderFooterDistance(false, value);
                this.RaisePropertyChanged();
            }
        }

        public double FontSize
        {
            get => Settings?.FontSizePt ?? 10;
            set
            {
                if (value <= 0 || Math.Abs(value - FontSize) < 0.01) return;
                Doc?.SetHeaderFooterFont(null, value, null, null);
            }
        }

        public bool IsBold
        {
            get => Settings?.IsBold ?? false;
            set
            {
                if (value == IsBold) return;
                Doc?.SetHeaderFooterFont(null, null, value, null);
            }
        }

        public bool IsItalic
        {
            get => Settings?.IsItalic ?? false;
            set
            {
                if (value == IsItalic) return;
                Doc?.SetHeaderFooterFont(null, null, null, value);
            }
        }

        // ── Эта страница ──────────────────────────────────────────────────

        /// <summary>Подпись группы: какая страница и какой на ней номер.</summary>
        public string PageCaption
        {
            get
            {
                var doc = Doc;
                if (doc is null) return "Эта страница";

                int page = doc.CaretPageIndex;
                var decoration = doc.GetPageDecoration(page);
                string sheet = (page + 1).ToString(CultureInfo.CurrentCulture);
                if (decoration is null) return "Эта страница (" + sheet + ")";

                string number = decoration.NumberVisible ? "№ " + decoration.ShownNumber : "без номера";
                return "Эта страница (" + sheet + ", " + number + ")";
            }
        }

        /// <summary>На странице каретки номер печатается.</summary>
        public bool IsNumberVisibleHere
        {
            get
            {
                var doc = Doc;
                return doc?.GetPageDecoration(doc.CaretPageIndex)?.NumberVisible ?? true;
            }
        }

        /// <summary>На странице каретки колонтитулы печатаются.</summary>
        public bool AreBandsVisibleHere
        {
            get
            {
                var doc = Doc;
                return doc?.GetPageDecoration(doc.CaretPageIndex)?.BandsVisible ?? true;
            }
        }

        public string ToggleNumberHereLabel => IsNumberVisibleHere ? "Скрыть номер" : "Вернуть номер";

        public string ToggleBandsHereLabel => AreBandsVisibleHere ? "Скрыть колонтит." : "Вернуть колонтит.";

        /// <summary>С какого числа начать счёт кнопкой «Начать счёт отсюда».</summary>
        public double RestartValue
        {
            get => _restartValue;
            set => this.RaiseAndSetIfChanged(ref _restartValue, Math.Max(0, Math.Round(value)));
        }

        /// <summary>
        /// Правила кнопками «Эта страница» ставятся меткой в тексте у абзаца каретки, а не
        /// номером листа. Метка едет вместе с текстом и видна при включённых знаках ¶.
        /// </summary>
        public bool AnchorRules
        {
            get => Doc?.AnchorPageRules ?? false;
            set
            {
                var doc = Doc;
                if (doc is null || doc.AnchorPageRules == value) return;
                doc.AnchorPageRules = value;
                this.RaisePropertyChanged();
            }
        }

        // ── Правила ───────────────────────────────────────────────────────

        /// <summary>Правила страниц документа.</summary>
        public ObservableCollection<PageRuleItem> Rules { get; } = new();

        public bool HasRules => Rules.Count > 0;

        public string RulesCaption => Rules.Count == 0
            ? "Исключений нет"
            : "Исключения: " + Rules.Count.ToString(CultureInfo.CurrentCulture);

        // ── Обновление ────────────────────────────────────────────────────

        /// <summary>Перечитывает всё: настройки поменялись или сменился документ.</summary>
        public void RefreshAll()
        {
            Rules.Clear();
            var doc = Doc;
            if (doc is not null)
                foreach (var item in doc.GetPageRuleItems())
                    Rules.Add(item);

            this.RaisePropertyChanged(nameof(HasRules));
            this.RaisePropertyChanged(nameof(RulesCaption));
            this.RaisePropertyChanged(nameof(DifferentFirstPage));
            this.RaisePropertyChanged(nameof(DifferentOddEven));
            this.RaisePropertyChanged(nameof(HideNumberOnChapterStart));
            this.RaisePropertyChanged(nameof(HideHeaderFooterOnChapterStart));
            this.RaisePropertyChanged(nameof(StartNumber));
            this.RaisePropertyChanged(nameof(NumberFormatKey));
            this.RaisePropertyChanged(nameof(NumberFormatLabel));
            this.RaisePropertyChanged(nameof(FontSize));
            this.RaisePropertyChanged(nameof(HeaderDistance));
            this.RaisePropertyChanged(nameof(FooterDistance));
            this.RaisePropertyChanged(nameof(IsBold));
            this.RaisePropertyChanged(nameof(IsItalic));
            this.RaisePropertyChanged(nameof(AnchorRules));
            RefreshPage();
        }

        /// <summary>Перечитывает то, что зависит от страницы каретки.</summary>
        public void RefreshPage()
        {
            this.RaisePropertyChanged(nameof(PageCaption));
            this.RaisePropertyChanged(nameof(IsNumberVisibleHere));
            this.RaisePropertyChanged(nameof(AreBandsVisibleHere));
            this.RaisePropertyChanged(nameof(ToggleNumberHereLabel));
            this.RaisePropertyChanged(nameof(ToggleBandsHereLabel));

            // Предлагаемое число для «начать счёт отсюда» — номер, который лист носит
            // сейчас: чаще всего его правят на единицу-другую, а не набирают с нуля.
            var doc = Doc;
            var decoration = doc?.GetPageDecoration(doc.CaretPageIndex);
            if (decoration is not null)
                RestartValue = decoration.Number;
        }
    }
}
