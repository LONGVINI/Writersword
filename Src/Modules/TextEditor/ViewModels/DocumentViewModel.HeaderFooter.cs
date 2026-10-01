using System;
using System.Collections.Generic;
using System.Globalization;
using ReactiveUI;
using Writersword.Modules.TextEditor.Commands;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Page;
using Writersword.Modules.TextEditor.Services;

namespace Writersword.Modules.TextEditor.ViewModels
{
    /// <summary>
    /// Строка списка правил страниц на вкладке «Колонтитулы».
    /// </summary>
    public sealed class PageRuleItem
    {
        public Guid Id { get; init; }

        /// <summary>Где действует: «Стр. 7», «Со стр. 12», «У метки (стр. 30)».</summary>
        public string Where { get; init; } = string.Empty;

        /// <summary>Что делает: «без номера», «счёт с 10».</summary>
        public string What { get; init; } = string.Empty;

        /// <summary>Метка правила пропала из текста — правило ни на что не действует.</summary>
        public bool IsLost { get; init; }

        /// <summary>Строка целиком.</summary>
        public string Text => Where + " — " + What;

        /// <summary>Прозрачность строки: потерянное правило бледнее.</summary>
        public double TextOpacity => IsLost ? 0.5 : 1.0;
    }

    /// <summary>
    /// Колонтитулы и нумерация страниц: шаблоны, вид номера, исключения для листов
    /// и правка прямо на листе.
    ///
    /// Сведения о листах (сколько их, где метки, где главы) знает только полотно — оно
    /// раскладывало. Оно же и ставит делегаты ниже. Без полотна считается, что лист
    /// один: команды продолжают работать, просто без привязки к раскладке.
    /// </summary>
    public sealed partial class DocumentViewModel
    {
        /// <summary>Колонтитулы или правила страниц изменились — полотну пора перерисовать листы.</summary>
        public event Action? HeaderFooterChanged;

        /// <summary>Режим колонтитулов включили или выключили — лента показывает или прячет свою вкладку.</summary>
        public event Action? HeaderFooterModeChanged;

        /// <summary>
        /// Просьба открыть правку колонтитула на листе: лист, верхний ли, место.
        /// Слушает вид редактора — он кладёт поле правки поверх листа.
        /// </summary>
        public event Action<int, bool, int>? HeaderFooterEditRequested;

        /// <summary>
        /// Вставить поле в место колонтитула, где стоит каретка поля правки: номер
        /// страницы или число страниц. Исполняет вид — поле правки живёт у него.
        /// </summary>
        public event Action<string>? HeaderFooterFieldInsertRequested;

        /// <summary>
        /// Закончить набор в колонтитуле, оставаясь в работе с колонтитулами: щелчок по
        /// тексту документа. Поле правки закрывается, текст документа снова яркий, а
        /// вкладка «Колонтитулы» остаётся — каретку можно поставить в нужный абзац и
        /// нажать «Скрыть номер» или «Отсюда и дальше».
        /// </summary>
        public event Action? HeaderFooterBandEditEndRequested;

        public void EndHeaderFooterBandEdit() => HeaderFooterBandEditEndRequested?.Invoke();

        /// <summary>Лист каретки с нуля. Ставит полотно.</summary>
        public Func<int>? GetCaretPageIndexDelegate { get; set; }

        /// <summary>Сведения о листах последней раскладки. Ставит полотно.</summary>
        public Func<PageFacts>? GetPageFactsDelegate { get; set; }

        /// <summary>Колонтитулы документа. Null — их нет.</summary>
        public HeaderFooterSettings? HeaderFooter => _document.HeaderFooter;

        private bool _isHeaderFooterMode;

        /// <summary>
        /// Идёт работа с колонтитулами: видна их вкладка ленты. Входят в режим кнопками
        /// «Колонтитулы» и двойным щелчком по полю листа, выходят кнопкой «Закрыть».
        /// </summary>
        public bool IsHeaderFooterMode
        {
            get => _isHeaderFooterMode;
            private set
            {
                if (_isHeaderFooterMode == value) return;
                _isHeaderFooterMode = value;
                HeaderFooterModeChanged?.Invoke();
            }
        }

        public void RaiseHeaderFooterChanged() => HeaderFooterChanged?.Invoke();

        public void EnterHeaderFooterMode() => IsHeaderFooterMode = true;

        public void ExitHeaderFooterMode() => IsHeaderFooterMode = false;

        /// <summary>Лист каретки с нуля.</summary>
        public int CaretPageIndex => Math.Max(GetCaretPageIndexDelegate?.Invoke() ?? 0, 0);

        /// <summary>Сведения о листах последней раскладки.</summary>
        public PageFacts CurrentPageFacts => GetPageFactsDelegate?.Invoke() ?? PageFacts.Empty;

        /// <summary>
        /// Открыть правку колонтитула на листе. Лист не задан — лист каретки.
        /// Колонтитулы есть только у листов, поэтому черновик сменяется режимом
        /// страниц — как в Word, где правка колонтитула переводит в разметку.
        /// </summary>
        public void RequestHeaderFooterEdit(bool header, int? page = null, int slot = 1)
        {
            if (IsReadOnly) return;
            if (ViewMode != EditorViewMode.Page) SetViewMode(EditorViewMode.Page);
            EnterHeaderFooterMode();
            HeaderFooterEditRequested?.Invoke(page ?? CaretPageIndex, header, Math.Clamp(slot, 0, 2));
        }

        /// <summary>
        /// Поле в место правки колонтитула. Правка не открыта — открывается нижний
        /// колонтитул листа каретки, и поле встаёт в его середину.
        /// </summary>
        public void RequestHeaderFooterFieldInsert(string token)
        {
            if (IsReadOnly || string.IsNullOrEmpty(token)) return;
            HeaderFooterFieldInsertRequested?.Invoke(token);
        }

        // ── Расчёт листа ──────────────────────────────────────────────────

        /// <summary>Оформление листа при текущих настройках.</summary>
        public PageDecoration? GetPageDecoration(int page)
            => EvaluatePage(_document.HeaderFooter, page);

        /// <summary>
        /// Оформление листа при других настройках — для разбора правки: каким был бы
        /// лист без правил, которые правка снимает.
        /// </summary>
        public PageDecoration? EvaluatePage(HeaderFooterSettings? settings, int page)
        {
            var facts = CurrentPageFacts;
            int count = Math.Max(facts.PageCount, page + 1);
            if (page < 0) return null;

            var all = PageNumbering.Compute(settings ?? new HeaderFooterSettings(), count,
                facts.ParagraphStartPages, facts.ChapterStartPages);
            return page < all.Length ? all[page] : null;
        }

        // ── Правка настроек с отменой ─────────────────────────────────────

        /// <summary>
        /// Правит колонтитулы одним шагом отмены. Правка идёт по копии: пока она не
        /// закончена, живые настройки не меняются, и полотно не рисует полуготовое.
        /// </summary>
        public void UpdateHeaderFooter(string description, Action<HeaderFooterSettings> mutate)
        {
            if (IsReadOnly) return;

            var before = _document.HeaderFooter?.Clone();
            var working = _document.HeaderFooter?.Clone() ?? CreateDefaultHeaderFooter();
            mutate(working);

            _document.HeaderFooter = working;
            PushHeaderFooterUndo(before, working, description);
            RaiseHeaderFooterChanged();
        }

        /// <summary>Подмена настроек целиком — для отмены и повтора.</summary>
        public void RestoreHeaderFooter(HeaderFooterSettings? settings)
        {
            _document.HeaderFooter = settings;
            RaiseHeaderFooterChanged();
            RaiseContentModified();
        }

        private void PushHeaderFooterUndo(HeaderFooterSettings? before, HeaderFooterSettings? after, string description)
        {
            if (PushUndoCommandDelegate is { } push)
                push(new HeaderFooterChangeCommand(this, before, after, description));
            else
                RaiseContentModified();
        }

        private static HeaderFooterSettings CreateDefaultHeaderFooter() => new()
        {
            FontSizePt = 10
        };

        // ── Шаблоны и номер ───────────────────────────────────────────────

        /// <summary>
        /// Ставит номер страницы в указанное место. Прежний номер убирается отовсюду:
        /// номер на листе один, и новое место заменяет старое, как в Word.
        /// </summary>
        public void InsertPageNumber(PageNumberPosition position, string? pattern = null)
        {
            string token = string.IsNullOrWhiteSpace(pattern) ? HeaderFooterSettings.PageToken : pattern!;

            UpdateHeaderFooter("Номер страницы", s =>
            {
                StripPageNumbers(s);

                bool header = position is PageNumberPosition.TopLeft or PageNumberPosition.TopCenter
                    or PageNumberPosition.TopRight or PageNumberPosition.TopOutside or PageNumberPosition.TopInside;
                bool outside = position == PageNumberPosition.TopOutside || position == PageNumberPosition.BottomOutside;
                bool inside = position == PageNumberPosition.TopInside || position == PageNumberPosition.BottomInside;

                int slot = position switch
                {
                    PageNumberPosition.TopLeft or PageNumberPosition.BottomLeft => 0,
                    PageNumberPosition.TopCenter or PageNumberPosition.BottomCenter => 1,
                    _ => 2
                };

                if (outside || inside)
                {
                    // Разворот книги: у внешнего края — на нечётных справа, на чётных
                    // слева; у внутреннего (у корешка) — наоборот. Без разных чётных и
                    // нечётных этого не выразить — они включаются здесь же.
                    if (!s.DifferentOddEven)
                    {
                        s.DifferentOddEven = true;
                        CopyMirrored(s.Header, s.EvenHeader);
                        CopyMirrored(s.Footer, s.EvenFooter);
                    }

                    AppendToSlot(s.GetBand(HeaderFooterVariant.Default, header), outside ? 2 : 0, token);
                    AppendToSlot(s.GetBand(HeaderFooterVariant.Even, header), outside ? 0 : 2, token);
                    return;
                }

                AppendToSlot(s.GetBand(HeaderFooterVariant.Default, header), slot, token);
                if (s.DifferentOddEven)
                    AppendToSlot(s.GetBand(HeaderFooterVariant.Even, header), slot, token);
            });

            EnterHeaderFooterMode();
        }

        /// <summary>Убирает номер страницы из всех колонтитулов.</summary>
        public void RemovePageNumbers()
        {
            if (_document.HeaderFooter is null) return;
            UpdateHeaderFooter("Убрать номера страниц", StripPageNumbers);
        }

        /// <summary>Убирает колонтитулы и нумерацию из документа целиком.</summary>
        public void RemoveHeaderFooter()
        {
            if (IsReadOnly || _document.HeaderFooter is null) return;

            var before = _document.HeaderFooter.Clone();
            _document.HeaderFooter = null;
            PushHeaderFooterUndo(before, null, "Удалить колонтитулы");
            RaiseHeaderFooterChanged();
        }

        /// <summary>Текст места колонтитула для всех листов варианта.</summary>
        public void SetHeaderFooterSlot(HeaderFooterVariant variant, bool header, int slot, string text)
            => UpdateHeaderFooter("Текст колонтитула",
                s => s.GetBand(variant, header).SetSlot(slot, text ?? string.Empty));

        public void SetDifferentFirstPage(bool value)
            => UpdateHeaderFooter("Особая первая страница", s => s.DifferentFirstPage = value);

        /// <summary>
        /// Разные чётные и нечётные. При первом включении чётные получают зеркальную
        /// копию обычных: левое и правое меняются местами, как на развороте книги.
        /// </summary>
        public void SetDifferentOddEven(bool value)
            => UpdateHeaderFooter("Разные чётные и нечётные", s =>
            {
                if (value && !s.DifferentOddEven && s.EvenHeader.IsEmpty && s.EvenFooter.IsEmpty)
                {
                    CopyMirrored(s.Header, s.EvenHeader);
                    CopyMirrored(s.Footer, s.EvenFooter);
                }
                s.DifferentOddEven = value;
            });

        public void SetPageNumberFormat(PageNumberFormat format)
            => UpdateHeaderFooter("Вид номера", s => s.NumberFormat = format);

        public void SetPageNumberStart(int start)
            => UpdateHeaderFooter("Начать нумерацию с", s => s.StartNumber = start);

        public void SetHideNumberOnChapterStart(bool value)
            => UpdateHeaderFooter("Номер у начала главы", s => s.HideNumberOnChapterStart = value);

        public void SetHideHeaderFooterOnChapterStart(bool value)
            => UpdateHeaderFooter("Колонтитулы у начала главы", s => s.HideHeaderFooterOnChapterStart = value);

        /// <summary>Шрифт колонтитулов. Null в любом поле — поле не меняется.</summary>
        public void SetHeaderFooterFont(string? family, double? sizePt, bool? bold, bool? italic)
            => UpdateHeaderFooter("Шрифт колонтитулов", s =>
            {
                if (family is not null) s.FontFamily = family.Length == 0 ? null : family;
                if (sizePt is double size && size > 0) s.FontSizePt = size;
                if (bold is bool b) s.IsBold = b;
                if (italic is bool i) s.IsItalic = i;
            });

        /// <summary>
        /// Расстояние от края листа до колонтитула, мм: верхнего — от верхнего края,
        /// нижнего — от нижнего. Так колонтитул двигают выше или ниже, как в Word.
        /// </summary>
        public void SetHeaderFooterDistance(bool header, double mm)
        {
            if (IsReadOnly) return;
            mm = Math.Clamp(mm, 0, 100);

            var ps = _document.PageSettings;
            if (header)
            {
                if (Math.Abs(ps.HeaderDistanceMm - mm) < 0.01) return;
                ps.HeaderDistanceMm = mm;
            }
            else
            {
                if (Math.Abs(ps.FooterDistanceMm - mm) < 0.01) return;
                ps.FooterDistanceMm = mm;
            }

            this.RaisePropertyChanged(nameof(PageSettings));
            RaiseHeaderFooterChanged();
            RaiseContentModified();
        }

        /// <summary>
        /// Позиции среднего и правого мест колонтитула, мм от левого края текста — их
        /// двигают на линейке, пока пишут в колонтитуле. Позиция на своём обычном месте
        /// (середина, правый край) хранится пустой: сменится поле листа — она пойдёт следом.
        /// </summary>
        public void SetHeaderFooterTabs(double centerMm, double rightMm)
        {
            if (IsReadOnly) return;

            double width = Math.Max(_document.PageSettings.GetTextWidthMm(), 1.0);
            double center = Math.Clamp(centerMm, 0, width);
            double right = Math.Clamp(rightMm, 0, width);

            double? centerValue = Math.Abs(center - width / 2.0) < 0.3 ? null : Math.Round(center, 1);
            double? rightValue = Math.Abs(right - width) < 0.3 ? null : Math.Round(right, 1);

            var current = _document.HeaderFooter;
            if (current is not null && current.CenterTabMm == centerValue && current.RightTabMm == rightValue) return;

            UpdateHeaderFooter("Позиции колонтитула", s =>
            {
                s.CenterTabMm = centerValue;
                s.RightTabMm = rightValue;
            });
        }

        public void SetHeaderFooterColor(string? color)
            => UpdateHeaderFooter("Цвет колонтитулов", s => s.TextColor = string.IsNullOrWhiteSpace(color) ? null : color);

        private static void StripPageNumbers(HeaderFooterSettings s)
        {
            foreach (var band in new[] { s.Header, s.Footer, s.FirstHeader, s.FirstFooter, s.EvenHeader, s.EvenFooter })
                for (int slot = 0; slot < 3; slot++)
                {
                    string text = band.GetSlot(slot);
                    if (!text.Contains(HeaderFooterSettings.PageToken, StringComparison.Ordinal)) continue;

                    string stripped = text.Replace(HeaderFooterSettings.PageToken, string.Empty, StringComparison.Ordinal);

                    // От «— {PAGE} —» и «Стр. {PAGE}» без номера остаётся мусор, а не текст:
                    // место без букв и цифр очищается целиком.
                    band.SetSlot(slot, HasLettersOrDigits(stripped) ? stripped.Trim() : string.Empty);
                }
        }

        private static bool HasLettersOrDigits(string text)
        {
            foreach (char c in text.Replace(HeaderFooterSettings.PagesToken, string.Empty, StringComparison.Ordinal))
                if (char.IsLetterOrDigit(c)) return true;
            return false;
        }

        private static void AppendToSlot(HeaderFooterBand band, int slot, string token)
        {
            string current = band.GetSlot(slot);
            band.SetSlot(slot, string.IsNullOrWhiteSpace(current) ? token : current.TrimEnd() + "  " + token);
        }

        private static void CopyMirrored(HeaderFooterBand source, HeaderFooterBand target)
        {
            target.Left = source.Right;
            target.Center = source.Center;
            target.Right = source.Left;
        }

        // ── Правила листов ────────────────────────────────────────────────

        private bool _anchorRules;

        /// <summary>
        /// Правила кнопками «Эта страница» ставятся меткой в тексте (у абзаца каретки),
        /// а не номером листа. Метка едет вместе с текстом.
        /// </summary>
        public bool AnchorPageRules
        {
            get => _anchorRules;
            set => _anchorRules = value;
        }

        /// <summary>
        /// Номер на этом листе: был — убрать, не было — вернуть. Счёт не трогается.
        /// Прежние правила этого листа о номере снимаются, и решение принимается от
        /// того, каким номер был бы без них.
        /// </summary>
        public void ToggleNumberOnPage(int? page = null)
        {
            int p = page ?? CaretPageIndex;
            var current = GetPageDecoration(p);
            bool hide = current?.NumberVisible ?? true;

            UpdateHeaderFooter(hide ? "Убрать номер на странице" : "Вернуть номер на странице", s =>
            {
                RemoveSingleRules(s, p, PageRuleAction.HideNumber, PageRuleAction.ShowNumber, PageRuleAction.ManualNumber);

                bool visibleNow = EvaluatePage(s, p)?.NumberVisible ?? true;
                if (hide && visibleNow) s.Rules.Add(NewRule(p, false, PageRuleAction.HideNumber));
                else if (!hide && !visibleNow) s.Rules.Add(NewRule(p, false, PageRuleAction.ShowNumber));
            });
        }

        /// <summary>Колонтитулы на этом листе: были — убрать, не было — вернуть.</summary>
        public void ToggleBandsOnPage(int? page = null)
        {
            int p = page ?? CaretPageIndex;
            var current = GetPageDecoration(p);
            bool hide = current?.BandsVisible ?? true;

            UpdateHeaderFooter(hide ? "Убрать колонтитулы на странице" : "Вернуть колонтитулы на странице", s =>
            {
                RemoveSingleRules(s, p, PageRuleAction.HideHeaderFooter, PageRuleAction.ShowHeaderFooter);

                bool visibleNow = EvaluatePage(s, p)?.BandsVisible ?? true;
                if (hide && visibleNow) s.Rules.Add(NewRule(p, false, PageRuleAction.HideHeaderFooter));
                else if (!hide && !visibleNow) s.Rules.Add(NewRule(p, false, PageRuleAction.ShowHeaderFooter));
            });
        }

        /// <summary>«Дальше не надо»: с этого листа номер не печатается.</summary>
        public void StopNumbersFromPage(int? page = null)
            => AddRangeRule(page, PageRuleAction.HideNumber, "Дальше без номера");

        /// <summary>«Дальше снова»: с этого листа номер печатается.</summary>
        public void ResumeNumbersFromPage(int? page = null)
            => AddRangeRule(page, PageRuleAction.ShowNumber, "Дальше с номером");

        /// <summary>С этого листа колонтитулы не печатаются.</summary>
        public void StopBandsFromPage(int? page = null)
            => AddRangeRule(page, PageRuleAction.HideHeaderFooter, "Дальше без колонтитулов");

        /// <summary>С этого листа колонтитулы снова печатаются.</summary>
        public void ResumeBandsFromPage(int? page = null)
            => AddRangeRule(page, PageRuleAction.ShowHeaderFooter, "Дальше с колонтитулами");

        /// <summary>С этого листа счёт начинается заново.</summary>
        public void RestartNumberingFromPage(int start, int? page = null)
        {
            int p = page ?? CaretPageIndex;
            UpdateHeaderFooter("Начать счёт отсюда", s =>
            {
                RemoveRulesAt(s, p, PageRuleAction.RestartNumbering);
                var rule = NewRule(p, true, PageRuleAction.RestartNumbering);
                rule.StartNumber = start;
                s.Rules.Add(rule);
            });
        }

        /// <summary>С этого листа номер печатается другим видом.</summary>
        public void SetFormatFromPage(PageNumberFormat format, int? page = null)
        {
            int p = page ?? CaretPageIndex;
            UpdateHeaderFooter("Вид номера отсюда", s =>
            {
                RemoveRulesAt(s, p, PageRuleAction.SetFormat);
                var rule = NewRule(p, true, PageRuleAction.SetFormat);
                rule.Format = format;
                s.Rules.Add(rule);
            });
        }

        /// <summary>
        /// Номер, набранный руками на листе, становится началом счёта: лист печатает это
        /// число, следующие продолжают от него.
        /// </summary>
        public void ConvertManualNumberToRestart(int page, int start)
            => UpdateHeaderFooter("Продолжить счёт отсюда", s =>
            {
                RemoveSingleRules(s, page, PageRuleAction.ManualNumber, PageRuleAction.HideNumber);
                RemoveRulesAt(s, page, PageRuleAction.RestartNumbering);

                s.Rules.Add(new PageRule
                {
                    Scope = PageRuleScope.FromPage,
                    PageIndex = page,
                    Action = PageRuleAction.RestartNumbering,
                    StartNumber = start
                });
            });

        /// <summary>Снимает правило из списка.</summary>
        public void RemovePageRule(Guid id)
        {
            if (_document.HeaderFooter?.Rules.Exists(r => r.Id == id) != true) return;
            UpdateHeaderFooter("Снять правило страницы", s => s.Rules.RemoveAll(r => r.Id == id));
        }

        /// <summary>Снимает все правила листов.</summary>
        public void ClearPageRules()
        {
            if (_document.HeaderFooter is not { Rules.Count: > 0 }) return;
            UpdateHeaderFooter("Снять все правила страниц", s => s.Rules.Clear());
        }

        /// <summary>Правила документа для списка на вкладке.</summary>
        public IReadOnlyList<PageRuleItem> GetPageRuleItems()
        {
            var settings = _document.HeaderFooter;
            if (settings is null || settings.Rules.Count == 0) return Array.Empty<PageRuleItem>();

            var facts = CurrentPageFacts;
            var items = new List<PageRuleItem>(settings.Rules.Count);

            foreach (var rule in settings.Rules)
            {
                int target = PageNumbering.TargetPage(rule, facts.ParagraphStartPages);
                bool lost = rule.IsAnchored && target < 0;
                string page = (target + 1).ToString(CultureInfo.CurrentCulture);

                string where = rule.Scope switch
                {
                    PageRuleScope.Page => "Стр. " + page,
                    PageRuleScope.FromPage => "Со стр. " + page,
                    PageRuleScope.Anchor => lost ? "Метка потеряна" : "У метки, стр. " + page,
                    _ => lost ? "Метка потеряна" : "С метки, стр. " + page
                };

                string what = DescribeRuleAction(rule);

                items.Add(new PageRuleItem { Id = rule.Id, Where = where, What = what, IsLost = lost });
            }

            return items;
        }

        /// <summary>Что делает правило: «без номера», «счёт с 10».</summary>
        public static string DescribeRuleAction(PageRule rule) => rule.Action switch
        {
            PageRuleAction.HideNumber => "без номера",
            PageRuleAction.ShowNumber => "номер виден",
            PageRuleAction.HideHeaderFooter => "без колонтитулов",
            PageRuleAction.ShowHeaderFooter => "колонтитулы видны",
            PageRuleAction.RestartNumbering => "счёт с " + (rule.StartNumber ?? 1).ToString(CultureInfo.CurrentCulture),
            PageRuleAction.ManualNumber => "номер «" + rule.ManualText + "»",
            _ => "вид номера: " + FormatName(rule.Format ?? PageNumberFormat.Arabic)
        };

        /// <summary>Подпись вида номера для списка и ленты.</summary>
        public static string FormatName(PageNumberFormat format) => format switch
        {
            PageNumberFormat.RomanLower => "i, ii, iii",
            PageNumberFormat.RomanUpper => "I, II, III",
            PageNumberFormat.LetterLower => "a, b, c",
            PageNumberFormat.LetterUpper => "A, B, C",
            _ => "1, 2, 3"
        };

        private void AddRangeRule(int? page, PageRuleAction action, string description)
        {
            int p = page ?? CaretPageIndex;
            UpdateHeaderFooter(description, s =>
            {
                // У листа одно правило каждого рода: повторное нажатие заменяет прежнее,
                // а «дальше не надо» и «дальше снова» на одном листе гасят друг друга.
                var opposite = action switch
                {
                    PageRuleAction.HideNumber => PageRuleAction.ShowNumber,
                    PageRuleAction.ShowNumber => PageRuleAction.HideNumber,
                    PageRuleAction.HideHeaderFooter => PageRuleAction.ShowHeaderFooter,
                    _ => PageRuleAction.HideHeaderFooter
                };
                RemoveRulesAt(s, p, action, opposite);
                s.Rules.Add(NewRule(p, true, action));
            });
        }

        /// <summary>
        /// Новое правило листа. При включённых метках — метка у абзаца каретки, иначе
        /// номер листа.
        /// </summary>
        private PageRule NewRule(int page, bool range, PageRuleAction action)
        {
            if (_anchorRules && _activeParagraph?.Model is { } anchor)
            {
                return new PageRule
                {
                    Scope = range ? PageRuleScope.FromAnchor : PageRuleScope.Anchor,
                    AnchorParagraphId = anchor.Id,
                    PageIndex = page,
                    Action = action
                };
            }

            return new PageRule
            {
                Scope = range ? PageRuleScope.FromPage : PageRuleScope.Page,
                PageIndex = page,
                Action = action
            };
        }

        /// <summary>Снимает правила одного листа (не отрезков), указывающие на лист.</summary>
        private void RemoveSingleRules(HeaderFooterSettings s, int page, params PageRuleAction[] actions)
        {
            var facts = CurrentPageFacts;
            s.Rules.RemoveAll(r => !r.IsRange
                && Array.IndexOf(actions, r.Action) >= 0
                && PageNumbering.TargetPage(r, facts.ParagraphStartPages) == page);
        }

        /// <summary>Снимает правила-отрезки, начинающиеся на листе.</summary>
        private void RemoveRulesAt(HeaderFooterSettings s, int page, params PageRuleAction[] actions)
        {
            var facts = CurrentPageFacts;
            s.Rules.RemoveAll(r => r.IsRange
                && Array.IndexOf(actions, r.Action) >= 0
                && PageNumbering.TargetPage(r, facts.ParagraphStartPages) == page);
        }

        // ── Правка на листе ───────────────────────────────────────────────

        /// <summary>
        /// Принимает правку полосы колонтитула на листе: все три места разом, одним
        /// шагом отмены. Возвращает разбор места, где набранный номер разошёлся со
        /// счётом, — вид предложит продолжить счёт от набранного. Null — предлагать нечего.
        /// </summary>
        public HeaderFooterEditResult? ApplyHeaderFooterBandEdit(int page, bool header, IReadOnlyList<string> slots)
        {
            if (IsReadOnly || slots is null || slots.Count < 3) return null;

            var settings = _document.HeaderFooter ?? CreateDefaultHeaderFooter();
            var facts = CurrentPageFacts;

            // Сначала разбор на копии: если ни одно место не поменялось, шага отмены нет.
            var working = settings.Clone();
            HeaderFooterEditResult? mismatch = null;
            bool changed = false;

            for (int slot = 0; slot < 3; slot++)
            {
                var decoration = EvaluatePage(working, page);
                if (decoration is null) continue;

                var result = HeaderFooterEditing.Resolve(
                    working, decoration, header, slot, slots[slot] ?? string.Empty,
                    facts.ParagraphStartPages, probe => EvaluatePage(probe, page));

                if (result.IsEmpty) continue;
                changed = true;

                if (result.NewTemplate is not null)
                    working.GetBand(decoration.Variant, header).SetSlot(slot, result.NewTemplate);

                working.Rules.RemoveAll(r => result.RemovedRuleIds.Contains(r.Id));
                working.Rules.AddRange(result.AddedRules);

                if (result.MismatchNumber is not null) mismatch = result;
            }

            if (!changed) return null;

            var before = _document.HeaderFooter?.Clone();
            _document.HeaderFooter = working;
            PushHeaderFooterUndo(before, working, header ? "Верхний колонтитул" : "Нижний колонтитул");
            RaiseHeaderFooterChanged();

            return mismatch;
        }

        /// <summary>
        /// Текст мест полосы для правки на листе — со значениями полей этого листа.
        /// </summary>
        public string[] GetHeaderFooterEditTexts(int page, bool header)
        {
            var settings = _document.HeaderFooter ?? CreateDefaultHeaderFooter();
            var decoration = EvaluatePage(settings, page);
            if (decoration is null) return new[] { string.Empty, string.Empty, string.Empty };

            var band = settings.GetBand(decoration.Variant, header);
            return new[]
            {
                PageNumbering.RenderForEditing(band.Left, decoration).Text,
                PageNumbering.RenderForEditing(band.Center, decoration).Text,
                PageNumbering.RenderForEditing(band.Right, decoration).Text
            };
        }

        /// <summary>Подпись варианта шаблона, который правится на этом листе.</summary>
        public string DescribeHeaderFooterVariant(int page, bool header)
        {
            var decoration = GetPageDecoration(page);
            string band = header ? "Верхний колонтитул" : "Нижний колонтитул";
            string scope = decoration?.Variant switch
            {
                HeaderFooterVariant.First => "первая страница",
                HeaderFooterVariant.Even => "все чётные страницы",
                _ => _document.HeaderFooter?.DifferentOddEven == true
                    ? "все нечётные страницы"
                    : "все страницы"
            };

            string pageText = "стр. " + (page + 1).ToString(CultureInfo.CurrentCulture);
            string hidden = decoration is { BandsVisible: false } ? " · на этой странице скрыт" : string.Empty;

            return band + " · текст для: " + scope + " · номер — только " + pageText + hidden;
        }
    }
}
