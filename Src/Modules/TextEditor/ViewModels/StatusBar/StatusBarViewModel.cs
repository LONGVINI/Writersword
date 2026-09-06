using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Input;
using ReactiveUI;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.ViewModels.StatusBar
{
    public sealed class StatusBarViewModel : ReactiveObject
    {
        private int _wordCount;
        private int _charCount;
        private int _charCountNoSpaces;
        private int _paragraphCount;
        private int _lineCount;
        private int _pageCount = 1;
        private int _currentPage = 1;
        private double _zoom = 1.0;
        private string _language = "ru";
        private bool _isSpellCheckActive;
        private bool _isReadOnly;
        private EditorViewMode _viewMode = EditorViewMode.Page;

        private bool _viewModeChanging;
        private bool _zoomChanging;
        private double _recommendedZoom = 0;

        /// <summary>
        /// Формат больших чисел статистики: разряды отделяются неразрывным пробелом,
        /// как это делает Word. Своя копия формата нужна потому, что культура интерфейса
        /// может быть инвариантной, а там разделителем стоит запятая.
        /// </summary>
        private static readonly NumberFormatInfo GroupedFormat = CreateGroupedFormat();

        private static NumberFormatInfo CreateGroupedFormat()
        {
            var format = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
            format.NumberGroupSeparator = " ";
            format.NumberGroupSizes = new[] { 3 };
            format.NumberDecimalDigits = 0;
            return format;
        }

        /// <summary>Число с разделением разрядов для показа в строке состояния и в окне статистики.</summary>
        public static string FormatNumber(int value) => value.ToString("N0", GroupedFormat);

        // --- Статистика ---
        public int WordCount
        {
            get => _wordCount;
            set
            {
                this.RaiseAndSetIfChanged(ref _wordCount, value);
                this.RaisePropertyChanged(nameof(WordCountText));
            }
        }

        public int CharCount
        {
            get => _charCount;
            set
            {
                this.RaiseAndSetIfChanged(ref _charCount, value);
                this.RaisePropertyChanged(nameof(CharCountText));
            }
        }

        public int CharCountNoSpaces
        {
            get => _charCountNoSpaces;
            set
            {
                this.RaiseAndSetIfChanged(ref _charCountNoSpaces, value);
                this.RaisePropertyChanged(nameof(CharCountNoSpacesText));
            }
        }

        public int ParagraphCount
        {
            get => _paragraphCount;
            set
            {
                this.RaiseAndSetIfChanged(ref _paragraphCount, value);
                this.RaisePropertyChanged(nameof(ParagraphCountText));
            }
        }

        /// <summary>
        /// Число строк текущей раскладки. Считается по разбитым на строки абзацам, а не
        /// по знакам конца абзаца: в статистике Word это именно строки на листе.
        /// </summary>
        public int LineCount
        {
            get => _lineCount;
            set
            {
                this.RaiseAndSetIfChanged(ref _lineCount, value);
                this.RaisePropertyChanged(nameof(LineCountText));
            }
        }

        public int PageCount
        {
            get => _pageCount;
            set
            {
                this.RaiseAndSetIfChanged(ref _pageCount, Math.Max(1, value));
                this.RaisePropertyChanged(nameof(PageCountText));
            }
        }

        public int CurrentPage { get => _currentPage; set => this.RaiseAndSetIfChanged(ref _currentPage, Math.Max(1, value)); }

        public string WordCountText => FormatNumber(_wordCount);
        public string CharCountText => FormatNumber(_charCount);
        public string CharCountNoSpacesText => FormatNumber(_charCountNoSpaces);
        public string ParagraphCountText => FormatNumber(_paragraphCount);
        public string LineCountText => FormatNumber(_lineCount);
        public string PageCountText => FormatNumber(_pageCount);

        public string Language { get => _language; set => this.RaiseAndSetIfChanged(ref _language, value); }
        public bool IsSpellCheckActive { get => _isSpellCheckActive; set => this.RaiseAndSetIfChanged(ref _isSpellCheckActive, value); }
        public bool IsReadOnly { get => _isReadOnly; set => this.RaiseAndSetIfChanged(ref _isReadOnly, value); }

        // --- Масштаб ---
        public double Zoom
        {
            get => _zoom;
            set
            {
                if (_zoomChanging) return;
                _zoomChanging = true;
                try
                {
                    double clamped = Math.Max(0.25, Math.Min(5.0, value));
                    this.RaiseAndSetIfChanged(ref _zoom, clamped);
                    this.RaisePropertyChanged(nameof(ZoomPercent));
                    this.RaisePropertyChanged(nameof(HasRecommendedZoom));
                    ZoomChanged?.Invoke(clamped);
                }
                finally
                {
                    _zoomChanging = false;
                }
            }
        }

        public int ZoomPercent => (int)Math.Round(_zoom * 100);

        public double RecommendedZoom
        {
            get => _recommendedZoom;
            set
            {
                this.RaiseAndSetIfChanged(ref _recommendedZoom, value);
                this.RaisePropertyChanged(nameof(RecommendedZoomPercent));
                this.RaisePropertyChanged(nameof(HasRecommendedZoom));
            }
        }

        public int RecommendedZoomPercent => (int)Math.Round(_recommendedZoom * 100);
        public bool HasRecommendedZoom => _recommendedZoom > 0;

        public Action<double>? ZoomChanged { get; set; }

        // --- Режим отображения ---
        public EditorViewMode ViewMode
        {
            get => _viewMode;
            set
            {
                if (_viewModeChanging || _viewMode == value) return;
                _viewModeChanging = true;
                try
                {
                    this.RaiseAndSetIfChanged(ref _viewMode, value);
                    this.RaisePropertyChanged(nameof(IsPageMode));
                    this.RaisePropertyChanged(nameof(IsDraftMode));
                    this.RaisePropertyChanged(nameof(IsWebMode));
                    this.RaisePropertyChanged(nameof(IsReadingMode));
                    this.RaisePropertyChanged(nameof(IsSinglePageMode));
                    this.RaisePropertyChanged(nameof(IsTwoPagesMode));
                    this.RaisePropertyChanged(nameof(IsAutoPagesMode));
                    ViewModeChanged?.Invoke(value);
                }
                finally
                {
                    _viewModeChanging = false;
                }
            }
        }

        /// <summary>
        /// Приводит индикатор режима к уже установленному режиму документа, не уведомляя
        /// редактор. Применяется при загрузке документа: режим восстановлен в модели,
        /// обратный вызов, который применил бы его повторно, здесь не нужен.
        /// </summary>
        public void SyncViewMode(EditorViewMode mode)
        {
            if (_viewMode == mode) return;
            _viewModeChanging = true;
            try
            {
                this.RaiseAndSetIfChanged(ref _viewMode, mode);
                this.RaisePropertyChanged(nameof(IsPageMode));
                this.RaisePropertyChanged(nameof(IsDraftMode));
                this.RaisePropertyChanged(nameof(IsWebMode));
                this.RaisePropertyChanged(nameof(IsReadingMode));
                this.RaisePropertyChanged(nameof(IsSinglePageMode));
                this.RaisePropertyChanged(nameof(IsTwoPagesMode));
                this.RaisePropertyChanged(nameof(IsAutoPagesMode));
            }
            finally
            {
                _viewModeChanging = false;
            }
        }

        public bool IsPageMode => _viewMode == EditorViewMode.Page;
        public bool IsDraftMode => _viewMode == EditorViewMode.Draft;
        public bool IsWebMode => _viewMode == EditorViewMode.Web;
        public bool IsReadingMode => _viewMode == EditorViewMode.Reading;

        public Action<EditorViewMode>? ViewModeChanged { get; set; }

        // ── Страницы рядом ────────────────────────────────────────────────
        private int _pagesPerRow = 1;

        /// <summary>true — в режиме страниц показываются две страницы в ряду.</summary>
        public bool IsTwoPagesPerRow => _pagesPerRow == 2;

        /// <summary>Кнопка «одна страница»: режим страниц, листы столбиком.</summary>
        public bool IsSinglePageMode => IsPageMode && _pagesPerRow == 1;

        /// <summary>Кнопка «две страницы»: режим страниц, листы рядом.</summary>
        public bool IsTwoPagesMode => IsPageMode && _pagesPerRow == 2;

        /// <summary>
        /// Кнопка «сетка»: режим страниц, листов в ряду столько, сколько влезает при
        /// текущем масштабе. Отдалили — видно больше страниц сразу.
        /// </summary>
        public bool IsAutoPagesMode => IsPageMode && _pagesPerRow == 0;

        /// <summary>
        /// Уведомляет редактор о смене числа страниц в ряду. 0 — авто, иначе
        /// фиксированное число.
        /// </summary>
        public Action<int>? PagesPerRowChanged { get; set; }

        public ICommand SetTwoPagesModeCommand { get; }
        public ICommand SetAutoPagesModeCommand { get; }

        /// <summary>
        /// Приводит индикатор числа страниц в ряду к уже установленному значению, не
        /// уведомляя редактор. Применяется при восстановлении состояния вида из
        /// сессионных данных, где значение уже применено к документу и линейке.
        /// </summary>
        public void SyncPagesPerRow(int value)
        {
            _pagesPerRow = value;
            this.RaisePropertyChanged(nameof(IsTwoPagesPerRow));
            this.RaisePropertyChanged(nameof(IsSinglePageMode));
            this.RaisePropertyChanged(nameof(IsTwoPagesMode));
            this.RaisePropertyChanged(nameof(IsAutoPagesMode));
        }

        private void SetPagesPerRow(int value)
        {
            if (_pagesPerRow != value)
            {
                _pagesPerRow = value;
                PagesPerRowChanged?.Invoke(value);
            }
            this.RaisePropertyChanged(nameof(IsTwoPagesPerRow));
            this.RaisePropertyChanged(nameof(IsSinglePageMode));
            this.RaisePropertyChanged(nameof(IsTwoPagesMode));
            this.RaisePropertyChanged(nameof(IsAutoPagesMode));
        }

        public ICommand SetPageModeCommand { get; }
        public ICommand SetDraftModeCommand { get; }
        public ICommand SetWebModeCommand { get; }
        public ICommand SetReadingModeCommand { get; }
        public ICommand FitToPhysicalSizeCommand { get; }

        /// <summary>Открыть окно полной статистики документа.</summary>
        public ICommand ShowStatisticsCommand { get; }

        /// <summary>
        /// Запрос на показ окна статистики. Ставится редактором: сама строка состояния
        /// окон не открывает, она только сообщает о нажатии на счётчики.
        /// </summary>
        public Action? StatisticsRequested { get; set; }

        public StatusBarViewModel()
        {
            SetPageModeCommand = ReactiveCommand.Create(() =>
            {
                ViewMode = EditorViewMode.Page;
                SetPagesPerRow(1);
            });
            FitToPhysicalSizeCommand = ReactiveCommand.Create(() =>
            {
                Zoom = _recommendedZoom;
            });
            SetDraftModeCommand = ReactiveCommand.Create(() => { ViewMode = EditorViewMode.Draft; });
            SetWebModeCommand = ReactiveCommand.Create(() => { ViewMode = EditorViewMode.Web; });
            SetReadingModeCommand = ReactiveCommand.Create(() => { ViewMode = EditorViewMode.Reading; });
            SetTwoPagesModeCommand = ReactiveCommand.Create(() =>
            {
                ViewMode = EditorViewMode.Page;
                SetPagesPerRow(2);
            });
            SetAutoPagesModeCommand = ReactiveCommand.Create(() =>
            {
                ViewMode = EditorViewMode.Page;
                SetPagesPerRow(0);
            });
            ShowStatisticsCommand = ReactiveCommand.Create(() => { StatisticsRequested?.Invoke(); });
        }

        // --- Обновление статистики ---
        public void UpdateFromText(string fullText, int paragraphCount, int pageCount)
        {
            ParagraphCount = paragraphCount;
            PageCount = pageCount;
            CharCount = fullText.Length;
            CharCountNoSpaces = fullText.Replace(" ", "").Replace("\t", "").Length;
            WordCount = CountWords(fullText);
        }

        /// <summary>
        /// Считает статистику по абзацам. Отдельный подсчёт нужен, чтобы знак конца
        /// абзаца не попадал в число символов: склейка абзацев любым разделителем
        /// прибавляла к счётчику по одному символу на абзац, и на рукописи в три
        /// с половиной тысячи абзацев расхождение с другими редакторами достигало
        /// нескольких тысяч знаков.
        /// </summary>
        public void UpdateFromParagraphs(IReadOnlyList<string> paragraphs, int pageCount)
        {
            PageCount = pageCount;
            UpdateFromParagraphs(paragraphs);
        }

        /// <summary>
        /// Считает статистику по абзацам, не трогая число страниц и строк: их знает
        /// раскладка, и пересчёт текста не должен затирать их своей догадкой.
        /// </summary>
        public void UpdateFromParagraphs(IReadOnlyList<string> paragraphs)
        {
            ParagraphCount = paragraphs.Count;

            int chars = 0, charsNoSpaces = 0, words = 0;

            foreach (string paragraph in paragraphs)
            {
                chars += paragraph.Length;

                foreach (char c in paragraph)
                    if (c != ' ' && c != '\t') charsNoSpaces++;

                words += CountWords(paragraph);
            }

            CharCount = chars;
            CharCountNoSpaces = charsNoSpaces;
            WordCount = words;
        }

        /// <summary>
        /// Принимает от раскладки число страниц и число строк. Оба значения известны
        /// только после разбивки документа по листам, посчитать их по тексту нельзя.
        /// </summary>
        public void UpdatePagination(int pageCount, int lineCount)
        {
            PageCount = pageCount;
            LineCount = lineCount;
        }

        /// <summary>
        /// Слово — последовательность без пробелов, в которой есть хотя бы буква или
        /// цифра. Одиночные знаки препинания словами не считаются: тире прямой речи
        /// стоит отдельным знаком, и без этого условия художественный текст получал
        /// тысячи лишних «слов» против того, что показывают другие редакторы.
        /// </summary>
        private static int CountWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;

            int count = 0;
            foreach (Match match in Regex.Matches(text, @"\S+"))
            {
                foreach (char c in match.Value)
                {
                    if (!char.IsLetterOrDigit(c)) continue;
                    count++;
                    break;
                }
            }

            return count;
        }
    }
}
