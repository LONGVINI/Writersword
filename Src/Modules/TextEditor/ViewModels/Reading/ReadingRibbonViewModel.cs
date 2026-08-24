using ReactiveUI;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Writersword.Modules.TextEditor.Models.Settings;

namespace Writersword.Modules.TextEditor.ViewModels.Reading
{
    /// <summary>
    /// То, что лента чтения просит у редактора. Отдельный договор нужен затем, что
    /// лента ничего не знает ни про канвас, ни про окно: она правит настройки и
    /// сообщает, какого рода правка произошла — от одних нужно пересобрать раскладку,
    /// от других достаточно перерисовать уже готовую.
    /// </summary>
    public interface IReadingHost
    {
        /// <summary>Настройки чтения текущего документа. null — документа нет.</summary>
        ReadingSettings? Reading { get; }

        /// <summary>
        /// Все виды чтения, доступные сейчас: встроенные, приложенные к документу и
        /// общие для всех проектов. В порядке показа.
        /// </summary>
        IReadOnlyList<ReadingTheme> ReadingThemes();

        /// <summary>Правка требует пересборки раскладки: лист, шрифт, подача.</summary>
        void ApplyReadingLayout();

        /// <summary>Правка видна сразу: свет, цвета, приближение книги, номера страниц.</summary>
        void ApplyReadingVisual();

        /// <summary>Листание: -1 назад, +1 вперёд.</summary>
        void TurnReadingPage(int direction);

        /// <summary>К началу книги.</summary>
        void GoReadingFirst();

        /// <summary>К концу книги.</summary>
        void GoReadingLast();

        /// <summary>
        /// Открыть книгу на странице с указанным номером, считая с нуля.
        /// <paramref name="animate"/> — показать переход листом, а не подменить
        /// страницу мгновенно.
        /// </summary>
        void GoReadingPage(int pageIndex, bool animate);

        /// <summary>Выход из чтения обратно к страницам.</summary>
        void ExitReading();

        /// <summary>Развернуть модуль на весь экран и обратно.</summary>
        void ApplyReadingFullscreen(bool on);

        /// <summary>Открыть окно видов чтения.</summary>
        void OpenReadingThemes();

        /// <summary>
        /// Запомнить предпочтения чтения так, чтобы они пережили и перезапуск, и
        /// переход в другой документ.
        /// </summary>
        void PersistReadingPreferences();
    }

    /// <summary>
    /// Пункт списка видов в ленте. Кроме самих видов в списке есть последний пункт —
    /// «Настроить виды…»: выбор вида и его правка живут в одном месте, и тянуться за
    /// ними в разные концы ленты не приходится.
    /// </summary>
    public sealed class ReadingThemeItem
    {
        public ReadingThemeItem(ReadingTheme? theme, string label, bool isCommand = false)
        {
            Theme = theme;
            Label = label;
            IsCommand = isCommand;
        }

        public ReadingTheme? Theme { get; }
        public string Label { get; }

        /// <summary>Пункт-действие, а не вид: открывает окно настройки.</summary>
        public bool IsCommand { get; }

        /// <summary>Значок шестерни виден только у пункта-действия.</summary>
        public bool ShowGear => IsCommand;

        public override string ToString() => Label;
    }

    /// <summary>Пункт выпадающего списка ленты: подпись и значение.</summary>
    public sealed class ReadingOption<T>
    {
        public ReadingOption(T value, string label)
        {
            Value = value;
            Label = label;
        }

        public T Value { get; }
        public string Label { get; }

        public override string ToString() => Label;
    }

    /// <summary>
    /// Лента чтения. Одна полоса с группами — как обычная лента редактора, но всё
    /// в ней относится к тому, как читателю смотреть на рукопись, а не к самой
    /// рукописи. Ни одно свойство отсюда не доходит до содержания документа.
    /// </summary>
    public sealed class ReadingRibbonViewModel : ReactiveObject
    {
        private readonly IReadingHost _host;

        public ReadingRibbonViewModel(IReadingHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));

            FormatOptions = new[]
            {
                new ReadingOption<ReadingSheetFormat>(ReadingSheetFormat.Document, "Как в документе"),
                new ReadingOption<ReadingSheetFormat>(ReadingSheetFormat.Pocket,   "Карманный"),
                new ReadingOption<ReadingSheetFormat>(ReadingSheetFormat.Square,   "Квадратный"),
                new ReadingOption<ReadingSheetFormat>(ReadingSheetFormat.Wide,     "Широкий")
            };

            AvailableFonts = LoadFontList();
            RebuildThemeItems();

            SetFlowSpreadCommand = ReactiveCommand.Create(() => Flow = ReadingFlow.Spread);
            SetFlowSingleCommand = ReactiveCommand.Create(() => Flow = ReadingFlow.Single);
            SetFlowColumnCommand = ReactiveCommand.Create(() => Flow = ReadingFlow.Column);

            PrevPageCommand = ReactiveCommand.Create(() => _host.TurnReadingPage(-1));
            NextPageCommand = ReactiveCommand.Create(() => _host.TurnReadingPage(1));
            FirstPageCommand = ReactiveCommand.Create(() => _host.GoReadingFirst());
            LastPageCommand = ReactiveCommand.Create(() => _host.GoReadingLast());
            GoToPageCommand = ReactiveCommand.Create(GoToTypedPage);

            FontBiggerCommand = ReactiveCommand.Create(() => FontStep += 1);
            FontSmallerCommand = ReactiveCommand.Create(() => FontStep -= 1);
            ResetTextCommand = ReactiveCommand.Create(ResetText);

            ZoomInCommand = ReactiveCommand.Create(() => Zoom = Zoom * 1.12);
            ZoomOutCommand = ReactiveCommand.Create(() => Zoom = Zoom / 1.12);
            ZoomResetCommand = ReactiveCommand.Create(() => Zoom = 1.0);

            ResetLightCommand = ReactiveCommand.Create(ResetLight);
            ResetBackdropCommand = ReactiveCommand.Create(ResetBackdrop);
            ExitCommand = ReactiveCommand.Create(() => _host.ExitReading());
        }

        private ReadingSettings? S => _host.Reading;
        private ReadingTheme? T => _host.Reading?.Active;

        // ── Виды чтения ───────────────────────────────────────────────────

        /// <summary>Список видов плюс пункт «Настроить виды…» в конце.</summary>
        public ObservableCollection<ReadingThemeItem> ThemeItems { get; } = new();

        public const string ThemeSettingsLabel = "Настроить виды…";

        /// <summary>Пересобирает список: виды могли добавиться, уехать или сменить имя.</summary>
        public void RebuildThemeItems()
        {
            var previous = SelectedThemeItem?.Theme?.Id ?? S?.ThemeId;

            _suppressThemeSelection = true;
            try
            {
                ThemeItems.Clear();
                foreach (var theme in _host.ReadingThemes())
                    ThemeItems.Add(new ReadingThemeItem(theme, theme.Name));

                ThemeItems.Add(new ReadingThemeItem(null, ThemeSettingsLabel, isCommand: true));

                _selectedThemeItem = ThemeItems.FirstOrDefault(
                    i => i.Theme is { } t && string.Equals(t.Id, previous, StringComparison.Ordinal))
                    ?? ThemeItems.FirstOrDefault(i => !i.IsCommand);
            }
            finally
            {
                _suppressThemeSelection = false;
            }

            this.RaisePropertyChanged(nameof(SelectedThemeItem));
        }

        private ReadingThemeItem? _selectedThemeItem;
        private bool _suppressThemeSelection;

        /// <summary>
        /// Выбранный вид. Пункт «Настроить виды…» видом не становится: он открывает
        /// окно и возвращает выбор туда, где тот был.
        /// </summary>
        public ReadingThemeItem? SelectedThemeItem
        {
            get => _selectedThemeItem;
            set
            {
                if (_suppressThemeSelection) return;
                if (value is null) return;

                if (value.IsCommand)
                {
                    // Выбор откатывается сразу, до открытия окна: иначе в списке
                    // на всё время работы окна висел бы пункт-действие.
                    var keep = _selectedThemeItem;
                    _selectedThemeItem = keep;
                    this.RaisePropertyChanged();
                    _host.OpenReadingThemes();
                    return;
                }

                _selectedThemeItem = value;
                this.RaisePropertyChanged();

                if (S is { } s && value.Theme is { } theme)
                {
                    s.ApplyTheme(theme);
                    RaiseThemeDependent();

                    // Шрифт вида участвует в вёрстке, поэтому пересборка, а не
                    // перерисовка: другой шрифт — другие переносы строк.
                    _host.ApplyReadingLayout();
                    _host.PersistReadingPreferences();
                }
            }
        }

        /// <summary>Сообщает о смене всего, что берётся у вида.</summary>
        private void RaiseThemeDependent()
        {
            this.RaisePropertyChanged(nameof(Brightness));
            this.RaisePropertyChanged(nameof(Contrast));
            this.RaisePropertyChanged(nameof(Warmth));
            this.RaisePropertyChanged(nameof(SelectedFont));
            this.RaisePropertyChanged(nameof(TextColorHex));
            this.RaisePropertyChanged(nameof(BackdropColorHex));
            this.RaisePropertyChanged(nameof(UseBackdropImage));
        }

        // ── Списки ────────────────────────────────────────────────────────

        public IReadOnlyList<ReadingOption<ReadingSheetFormat>> FormatOptions { get; }

        /// <summary>
        /// Шрифты для подмены на время чтения. Первым пунктом идёт «как в документе»:
        /// вернуться к авторскому шрифту должно быть так же просто, как уйти от него.
        /// </summary>
        public IReadOnlyList<string> AvailableFonts { get; }

        public const string FontAsInDocument = "Как в документе";

        private static IReadOnlyList<string> LoadFontList()
        {
            var list = new List<string> { FontAsInDocument };
            try
            {
                list.AddRange(SKFontManager.Default.FontFamilies
                    .Where(f => !string.IsNullOrWhiteSpace(f))
                    .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase));
            }
            catch
            {
                list.AddRange(new[] { "Arial", "Times New Roman", "Calibri", "Georgia", "Verdana" });
            }
            return list;
        }

        // ── Подача ────────────────────────────────────────────────────────

        public ReadingFlow Flow
        {
            get => S?.Flow ?? ReadingFlow.Spread;
            set
            {
                if (S is not { } s || s.Flow == value) return;
                s.Flow = value;
                this.RaisePropertyChanged();
                RaiseFlowDependent();
                _host.ApplyReadingLayout();
                _host.PersistReadingPreferences();
            }
        }

        public bool IsFlowSpread => Flow == ReadingFlow.Spread;
        public bool IsFlowSingle => Flow == ReadingFlow.Single;
        public bool IsFlowColumn => Flow == ReadingFlow.Column;

        /// <summary>Листание и формат листа есть только там, где есть страницы.</summary>
        public bool IsPaged => Flow != ReadingFlow.Column;

        private void RaiseFlowDependent()
        {
            this.RaisePropertyChanged(nameof(IsFlowSpread));
            this.RaisePropertyChanged(nameof(IsFlowSingle));
            this.RaisePropertyChanged(nameof(IsFlowColumn));
            this.RaisePropertyChanged(nameof(IsPaged));
        }

        public ReadingOption<ReadingSheetFormat>? SelectedFormat
        {
            get => FormatOptions.FirstOrDefault(o => o.Value == (S?.Format ?? ReadingSheetFormat.Document));
            set
            {
                if (value is null || S is not { } s || s.Format == value.Value) return;
                s.Format = value.Value;
                this.RaisePropertyChanged();
                _host.ApplyReadingLayout();
                _host.PersistReadingPreferences();
            }
        }

        // ── Страницы ──────────────────────────────────────────────────────

        private string _pageLabel = string.Empty;

        /// <summary>Подпись «какие страницы открыты». Заполняет канвас после листания.</summary>
        public string PageLabel
        {
            get => _pageLabel;
            set => this.RaiseAndSetIfChanged(ref _pageLabel, value);
        }

        private int _pageNumber = 1;
        private int _pageCount = 1;
        private string _pageInput = "1";

        // Пока состояние приходит от книги, обратные вызовы не нужны: иначе
        // обновление подписи после листания само же и запустило бы новое листание.
        private bool _suppressPageNav;

        /// <summary>
        /// Сообщает ленте, где книга открыта. Зовётся канвасом после каждого
        /// перехода — и после того, что затеяла сама лента.
        /// </summary>
        public void SetPageState(int number, int count)
        {
            _suppressPageNav = true;
            try
            {
                _pageCount = Math.Max(1, count);
                _pageNumber = Math.Clamp(number, 1, _pageCount);
                _pageInput = FormatPageInput();
            }
            finally
            {
                _suppressPageNav = false;
            }

            this.RaisePropertyChanged(nameof(PageCount));
            this.RaisePropertyChanged(nameof(PageMax));
            this.RaisePropertyChanged(nameof(PagePosition));
            this.RaisePropertyChanged(nameof(PageInput));
        }

        /// <summary>
        /// Строка поля: «3 из 8». Номер и общее число живут в одном поле, а не в двух
        /// подряд — два поля рядом читаются как две отдельные величины, а это одна.
        /// </summary>
        private string FormatPageInput()
            => $"{_pageNumber.ToString(System.Globalization.CultureInfo.CurrentCulture)} из {_pageCount.ToString(System.Globalization.CultureInfo.CurrentCulture)}";

        public int PageCount => _pageCount;

        /// <summary>Верхняя граница ползунка. Не меньше единицы — пустой книги не бывает.</summary>
        public double PageMax => Math.Max(1, _pageCount);

        /// <summary>
        /// Положение ползунка по книге. Переход мгновенный: ползунок тащат, и
        /// анимировать каждое его положение значит превратить перемотку в кашу.
        /// </summary>
        public double PagePosition
        {
            get => _pageNumber;
            set
            {
                if (_suppressPageNav) return;

                int v = Math.Clamp((int)Math.Round(value), 1, _pageCount);
                if (v == _pageNumber) return;

                _pageNumber = v;
                _pageInput = FormatPageInput();
                this.RaisePropertyChanged();
                this.RaisePropertyChanged(nameof(PageInput));
                _host.GoReadingPage(v - 1, animate: false);
            }
        }

        /// <summary>
        /// Содержимое поля страницы — целиком, вместе с «из N». Правится только номер:
        /// щелчок по полю выделяет цифры и ничего кроме них.
        /// </summary>
        public string PageInput
        {
            get => _pageInput;
            set => this.RaiseAndSetIfChanged(ref _pageInput, value ?? string.Empty);
        }

        /// <summary>Возвращает поле к той странице, которая открыта.</summary>
        public void ResetPageInput() => PageInput = FormatPageInput();

        /// <summary>
        /// Одна только страница, без «из N». Это то, что поле показывает, пока его
        /// правят: «из 8» — подпись, а не часть значения, и обходить её курсором,
        /// чтобы набрать номер, читателю незачем.
        /// </summary>
        public string PageNumberText
            => _pageNumber.ToString(System.Globalization.CultureInfo.CurrentCulture);

        /// <summary>
        /// Переходит на введённый номер.
        ///
        /// Негодный ввод не делает ничего: поле возвращается к открытой странице, и
        /// книга остаётся там же. Ни своевольного округления к ближайшей странице, ни
        /// прыжка на первую от случайной буквы — читатель просил определённое место,
        /// и если он его не назвал, идти некуда.
        ///
        /// Книга получает просьбу в любом случае, даже когда ввод негоден и переход
        /// никуда не ведёт: на ней канвас забирает себе клавиатуру. Иначе фокус
        /// остался бы в поле, и стрелки перестали бы листать до первого щелчка мимо.
        /// </summary>
        private void GoToTypedPage()
        {
            string raw = (_pageInput ?? string.Empty).Trim();

            bool parsed = int.TryParse(
                raw,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.CurrentCulture,
                out int n);

            if (!parsed || n < 1 || n > _pageCount)
            {
                ResetPageInput();
                _host.GoReadingPage(_pageNumber - 1, animate: false);
                return;
            }

            bool moved = n != _pageNumber;

            _pageNumber = n;
            PageInput = FormatPageInput();
            this.RaisePropertyChanged(nameof(PagePosition));

            _host.GoReadingPage(n - 1, animate: moved);
        }

        /// <summary>
        /// Рисовать свои номера страниц. Если нумерация уже есть в колонтитулах
        /// документа, свои цифры не рисуются в любом случае.
        /// </summary>
        public bool ShowPageNumbers
        {
            get => S?.ShowPageNumbers ?? true;
            set
            {
                if (S is not { } s || s.ShowPageNumbers == value) return;
                s.ShowPageNumbers = value;
                this.RaisePropertyChanged();
                _host.ApplyReadingVisual();
                _host.PersistReadingPreferences();
            }
        }

        // ── Свет ──────────────────────────────────────────────────────────

        /// <summary>Яркость в процентах — так удобнее ползунку.</summary>
        public double Brightness
        {
            get => (T?.Brightness ?? 1.0) * 100.0;
            set
            {
                if (T is not { } t) return;
                double v = Math.Clamp(value / 100.0, 0.35, 1.0);
                if (Math.Abs(v - t.Brightness) < 0.001) return;
                t.Brightness = v;
                this.RaisePropertyChanged();
                _host.ApplyReadingVisual();
            }
        }

        /// <summary>Контрастность в процентах: 100 — как задано видом.</summary>
        public double Contrast
        {
            get => (T?.Contrast ?? 1.0) * 100.0;
            set
            {
                if (T is not { } t) return;
                double v = Math.Clamp(value / 100.0, 0.6, 1.6);
                if (Math.Abs(v - t.Contrast) < 0.001) return;
                t.Contrast = v;
                this.RaisePropertyChanged();
                _host.ApplyReadingVisual();
            }
        }

        /// <summary>Тёплота в процентах: 0 — цвет как есть.</summary>
        public double Warmth
        {
            get => (T?.Warmth ?? 0.0) * 100.0;
            set
            {
                if (T is not { } t) return;
                double v = Math.Clamp(value / 100.0, 0.0, 1.0);
                if (Math.Abs(v - t.Warmth) < 0.001) return;
                t.Warmth = v;
                this.RaisePropertyChanged();
                _host.ApplyReadingVisual();
            }
        }

        /// <summary>Возвращает свет к тому, что задано выбранным видом.</summary>
        private void ResetLight()
        {
            if (S is not { } s || s.Active is not { } active) return;

            var source = _host.ReadingThemes()
                .FirstOrDefault(t => string.Equals(t.Id, s.ThemeId, StringComparison.Ordinal));

            active.Brightness = source?.Brightness ?? 1.0;
            active.Contrast = source?.Contrast ?? 1.0;
            active.Warmth = source?.Warmth ?? 0.0;

            this.RaisePropertyChanged(nameof(Brightness));
            this.RaisePropertyChanged(nameof(Contrast));
            this.RaisePropertyChanged(nameof(Warmth));
            _host.ApplyReadingVisual();
        }

        // ── Текст ─────────────────────────────────────────────────────────

        /// <summary>
        /// Шрифт чтения. Пункт «как в документе» означает, что подмены нет и каждый
        /// абзац рисуется своим шрифтом.
        /// </summary>
        public string SelectedFont
        {
            get
            {
                string? f = T?.FontFamily;
                return string.IsNullOrWhiteSpace(f) ? FontAsInDocument : f!;
            }
            set
            {
                if (T is not { } t) return;
                string? v = string.IsNullOrWhiteSpace(value) || value == FontAsInDocument ? null : value;
                if (string.Equals(v, t.FontFamily, StringComparison.Ordinal)) return;
                t.FontFamily = v;
                this.RaisePropertyChanged();
                _host.ApplyReadingLayout();
            }
        }

        /// <summary>
        /// Цвет текста чтения. Действует на текст, которому цвет не задан в самом
        /// документе: авторский цвет остаётся авторским.
        /// </summary>
        public string TextColorHex
        {
            get => T?.InkColor ?? "#1A1A1A";
            set
            {
                if (T is not { } t) return;
                if (string.IsNullOrWhiteSpace(value)) return;
                if (string.Equals(value, t.InkColor, StringComparison.OrdinalIgnoreCase)) return;
                t.InkColor = value;
                this.RaisePropertyChanged();
                _host.ApplyReadingVisual();
            }
        }

        /// <summary>
        /// Ступень размера. Своего кегля здесь нет намеренно: читатель делает буквы
        /// чуть крупнее или чуть мельче, но не назначает рукописи другой размер.
        /// </summary>
        public int FontStep
        {
            get => S?.FontStep ?? 0;
            set
            {
                if (S is not { } s) return;
                int v = Math.Clamp(value, ReadingSettings.MinFontStep, ReadingSettings.MaxFontStep);
                if (v == s.FontStep) return;
                s.FontStep = v;
                this.RaisePropertyChanged();
                this.RaisePropertyChanged(nameof(FontStepLabel));
                _host.ApplyReadingLayout();
                _host.PersistReadingPreferences();
            }
        }

        /// <summary>
        /// Подпись ступени. Коротко: она стоит между двумя кнопками, и развёрнутая
        /// фраза растягивала группу вдвое ради того, что и так понятно.
        /// </summary>
        public string FontStepLabel
        {
            get
            {
                int step = FontStep;
                if (step == 0) return "±0";
                return step > 0 ? $"+{step}" : step.ToString(System.Globalization.CultureInfo.CurrentCulture);
            }
        }

        /// <summary>
        /// Заливка поля вокруг книги. Значение здесь то же самое, что и у любого
        /// другого цвета в программе: HEX или код градиента — своих видов заливки у
        /// поля нет, оно принимает выбранное как есть. Пока своего не выбрано,
        /// показывается тот цвет, который поле выводит из бумаги.
        /// </summary>
        public string BackdropColorHex
        {
            get
            {
                string? c = T?.BackdropColor;
                return string.IsNullOrWhiteSpace(c) ? DerivedBackdropHex() : c!;
            }
            set
            {
                if (T is not { } t) return;
                if (string.IsNullOrWhiteSpace(value)) return;
                if (string.Equals(value, t.BackdropColor, StringComparison.OrdinalIgnoreCase)) return;

                // Пока поле выводится из бумаги, образец показывает выведенный цвет, а
                // двусторонняя привязка тут же отдаёт его обратно сюда. Принять такое
                // значение молча значит выключить «от бумаги» ничего не нажимая.
                if (t.BackdropColor is null
                    && string.Equals(value, DerivedBackdropHex(), StringComparison.OrdinalIgnoreCase)) return;

                t.BackdropColor = value;
                this.RaisePropertyChanged();
                _host.ApplyReadingVisual();
            }
        }

        /// <summary>
        /// Класть на поле картинку. Сам файл выбирается в окне видов — в ленте ему не
        /// место, — а включать и выключать его хочется не отходя от книги.
        /// </summary>
        public bool UseBackdropImage
        {
            get => T?.UseBackdropImage ?? false;
            set
            {
                if (T is not { } t || t.UseBackdropImage == value) return;
                t.UseBackdropImage = value;
                this.RaisePropertyChanged();
                _host.ApplyReadingVisual();
            }
        }

        /// <summary>
        /// Цвет, который поле получило бы само. Показывается в кружке, пока читатель
        /// своего не выбрал: пустой образец не объясняет, что там сейчас за цвет.
        /// </summary>
        private string DerivedBackdropHex()
        {
            var sheet = T?.SheetColor;
            if (string.IsNullOrWhiteSpace(sheet) || !SKColor.TryParse(sheet, out var c))
                return "#E8E8E8";

            double luma = (0.2126 * c.Red + 0.7152 * c.Green + 0.0722 * c.Blue) / 255.0;
            bool lighten = luma < 0.14;
            double target = lighten ? 255.0 : 0.0;
            double amount = lighten ? 0.10 : 0.16;

            byte Shift(byte v) => (byte)Math.Clamp(v + (target - v) * amount, 0.0, 255.0);

            return $"#{Shift(c.Red):X2}{Shift(c.Green):X2}{Shift(c.Blue):X2}";
        }

        /// <summary>Возвращает поле к правилу «выводить из бумаги».</summary>
        private void ResetBackdrop()
        {
            if (T is not { } t) return;
            if (t.BackdropColor is null && !t.UseBackdropImage) return;

            t.BackdropColor = null;
            t.UseBackdropImage = false;

            this.RaisePropertyChanged(nameof(BackdropColorHex));
            this.RaisePropertyChanged(nameof(UseBackdropImage));
            _host.ApplyReadingVisual();
        }

        /// <summary>Возвращает текст к тому, что задано выбранным видом.</summary>
        private void ResetText()
        {
            if (S is not { } s || s.Active is not { } active) return;

            var source = _host.ReadingThemes()
                .FirstOrDefault(t => string.Equals(t.Id, s.ThemeId, StringComparison.Ordinal));

            active.FontFamily = source?.FontFamily;
            active.InkColor = source?.InkColor ?? "#1A1A1A";
            s.FontStep = 0;

            this.RaisePropertyChanged(nameof(SelectedFont));
            this.RaisePropertyChanged(nameof(TextColorHex));
            this.RaisePropertyChanged(nameof(FontStep));
            this.RaisePropertyChanged(nameof(FontStepLabel));
            _host.ApplyReadingLayout();
        }

        // ── Показ ─────────────────────────────────────────────────────────

        /// <summary>
        /// Приближение книги. Разбиение на страницы от него не зависит: лист просто
        /// становится больше или меньше на экране.
        /// </summary>
        public double Zoom
        {
            get => S?.Zoom ?? 1.0;
            set
            {
                if (S is not { } s) return;
                double v = Math.Clamp(value, ReadingSettings.MinZoom, ReadingSettings.MaxZoom);
                if (Math.Abs(v - s.Zoom) < 0.0005) return;
                s.Zoom = v;
                this.RaisePropertyChanged();
                this.RaisePropertyChanged(nameof(ZoomPercent));
                this.RaisePropertyChanged(nameof(ZoomLabel));
                _host.ApplyReadingVisual();
            }
        }

        /// <summary>Тот же масштаб в процентах — для ползунка.</summary>
        public double ZoomPercent
        {
            get => Zoom * 100.0;
            set => Zoom = value / 100.0;
        }

        public string ZoomLabel => $"{Math.Round(Zoom * 100.0)}%";

        /// <summary>Ужимать картинки и таблицы вместе с листом.</summary>
        public bool ScaleContent
        {
            get => S?.ScaleContent ?? true;
            set
            {
                if (S is not { } s || s.ScaleContent == value) return;
                s.ScaleContent = value;
                this.RaisePropertyChanged();
                _host.ApplyReadingLayout();
                _host.PersistReadingPreferences();
            }
        }

        // ── Экран ─────────────────────────────────────────────────────────

        public bool Fullscreen
        {
            get => S?.Fullscreen ?? false;
            set
            {
                if (S is not { } s || s.Fullscreen == value) return;
                s.Fullscreen = value;
                this.RaisePropertyChanged();
                _host.ApplyReadingFullscreen(value);
            }
        }

        private bool _ribbonExpanded = true;

        /// <summary>Развёрнута ли лента. Свёрнутая оставляет на виду только язычок.</summary>
        public bool RibbonExpanded
        {
            get => _ribbonExpanded;
            set
            {
                if (_ribbonExpanded == value) return;
                _ribbonExpanded = value;
                if (S is { } s) s.RibbonExpanded = value;
                this.RaisePropertyChanged();
            }
        }

        private bool _vertical;

        /// <summary>
        /// Лента стоит сбоку вертикальной колонкой. Так она уходит, когда по ширине
        /// места на чтение почти не остаётся.
        /// </summary>
        public bool IsVertical
        {
            get => _vertical;
            set
            {
                if (_vertical == value) return;
                _vertical = value;
                this.RaisePropertyChanged();
                this.RaisePropertyChanged(nameof(RibbonOrientation));
                this.RaisePropertyChanged(nameof(GroupOrientation));
            }
        }

        /// <summary>Как идут группы: в строку или в столбец.</summary>
        public Avalonia.Layout.Orientation RibbonOrientation
            => _vertical ? Avalonia.Layout.Orientation.Vertical : Avalonia.Layout.Orientation.Horizontal;

        /// <summary>
        /// Как идёт содержимое внутри группы. В вертикальной ленте оно тоже встаёт
        /// столбиком: иначе группа осталась бы широкой, а вертикальную ленту делают
        /// именно затем, что ширины не хватает.
        /// </summary>
        public Avalonia.Layout.Orientation GroupOrientation
            => _vertical ? Avalonia.Layout.Orientation.Vertical : Avalonia.Layout.Orientation.Horizontal;

        // ── Команды ───────────────────────────────────────────────────────

        public ICommand SetFlowSpreadCommand { get; }
        public ICommand SetFlowSingleCommand { get; }
        public ICommand SetFlowColumnCommand { get; }

        public ICommand PrevPageCommand { get; }
        public ICommand NextPageCommand { get; }
        public ICommand FirstPageCommand { get; }
        public ICommand LastPageCommand { get; }
        public ICommand GoToPageCommand { get; }

        public ICommand FontBiggerCommand { get; }
        public ICommand FontSmallerCommand { get; }
        public ICommand ResetTextCommand { get; }

        public ICommand ZoomInCommand { get; }
        public ICommand ZoomOutCommand { get; }
        public ICommand ZoomResetCommand { get; }

        public ICommand ResetLightCommand { get; }
        public ICommand ResetBackdropCommand { get; }
        public ICommand ExitCommand { get; }

        /// <summary>
        /// Перечитывает всё из настроек. Зовётся после восстановления сессии, смены
        /// документа и правки видов: значения живут в модели чтения, а не в ленте.
        /// </summary>
        public void RefreshAll()
        {
            _ribbonExpanded = S?.RibbonExpanded ?? true;

            RebuildThemeItems();

            this.RaisePropertyChanged(nameof(Flow));
            RaiseFlowDependent();
            this.RaisePropertyChanged(nameof(SelectedFormat));
            RaiseThemeDependent();
            this.RaisePropertyChanged(nameof(FontStep));
            this.RaisePropertyChanged(nameof(FontStepLabel));
            this.RaisePropertyChanged(nameof(Zoom));
            this.RaisePropertyChanged(nameof(ZoomPercent));
            this.RaisePropertyChanged(nameof(ZoomLabel));
            this.RaisePropertyChanged(nameof(ScaleContent));
            this.RaisePropertyChanged(nameof(ShowPageNumbers));
            this.RaisePropertyChanged(nameof(Fullscreen));
            this.RaisePropertyChanged(nameof(RibbonExpanded));
        }
    }
}
