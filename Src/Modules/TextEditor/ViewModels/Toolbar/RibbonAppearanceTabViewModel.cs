using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using ReactiveUI;
using Writersword.Modules.TextEditor.Models.Settings;
using Writersword.Modules.TextEditor.ViewModels.Reading;

namespace Writersword.Modules.TextEditor.ViewModels.Toolbar
{
    /// <summary>
    /// Договор вкладки «Вид» с модулем. Отдельно от <see cref="Contracts.ITextEditorCommandTarget"/>
    /// намеренно: тот исполняет правки документа, а здесь речь о том, как рукопись
    /// показана на экране, и знать об этом должен модуль целиком, а не модель
    /// документа.
    /// </summary>
    public interface IEditorViewHost
    {
        /// <summary>Вид рабочей области текущего документа. null — документа нет.</summary>
        EditorViewSettings? EditorView { get; }

        /// <summary>
        /// Все виды, доступные сейчас: встроенные, приложенные к документу и общие
        /// для всех проектов. Список тот же, что и у чтения: вид — это вид, и
        /// заводить их по два комплекта незачем.
        /// </summary>
        IReadOnlyList<ReadingTheme> ReadingThemes();

        /// <summary>
        /// Виды для списка выбора: всё, кроме спрятанных. Вид с указанным
        /// опознавателем остаётся в списке даже спрятанным — это тот, что выбран
        /// сейчас, и показывать имя, которого нет в списке, нельзя.
        /// </summary>
        IReadOnlyList<ReadingTheme> VisibleReadingThemes(string? keepId);


        /// <summary>Правка видна сразу: цвет листа, свет, картинка позади страниц.</summary>
        void ApplyEditorViewVisual();

        /// <summary>Запомнить вид правки так, чтобы он пережил перезапуск.</summary>
        void PersistEditorViewPreferences();

        /// <summary>Открыть окно видов — то же самое, что и у чтения.</summary>
        void OpenReadingThemes();

        /// <summary>
        /// Завести новый вид — то же самое, что и у чтения: окно видов открывается
        /// с уже созданной копией выбранного.
        /// </summary>
        void CreateReadingTheme();

        /// <summary>Убрать картинку из-под страниц.</summary>
        void ClearBackdropImage();

        /// <summary>Режим фокуса: на экране остаётся только рукопись.</summary>
        bool IsFocusMode { get; set; }

        /// <summary>Правка во весь экран: окно забирает экран целиком.</summary>
        bool IsFullscreen { get; set; }

        /// <summary>Показывать линейки.</summary>
        bool ShowRuler { get; set; }

        /// <summary>Показывать строку состояния.</summary>
        bool ShowStatusBar { get; set; }

        /// <summary>
        /// Кареткой правит свой цвет, а не цвет текста под ней. Выключено — цвет берётся
        /// у текста, который каретка пишет.
        /// </summary>
        bool IsCaretColorCustom { get; set; }

        /// <summary>Свой цвет каретки (HEX). Значим при <see cref="IsCaretColorCustom"/>.</summary>
        string CaretColor { get; set; }
    }

    // Пункт списка видов — тот же ReadingThemeItem, что у ленты чтения: список видов
    // один, и плитка вида обязана выглядеть одинаково, где бы её ни показали. Свой
    // класс пункта здесь был копией, и копия отстала — плитки правки остались без
    // картинок, света и двух колонок, которые давно были у чтения.
    //
    // «Без вида» — пункт без вида за ним (Theme = null), не действие и не
    // «Кастомное»: образец у него — белый лист на сером поле, как до вкладки.

    /// <summary>
    /// Вкладка «Вид»: чем залит лист при правке, каким светом, что лежит позади
    /// страниц и что остаётся на экране в режиме фокуса.
    ///
    /// Режимы показа (страницы, черновик, веб, чтение) и масштаб сюда не входят —
    /// они живут в строке состояния, и второй набор тех же кнопок в ленте означал бы
    /// два места, где одно и то же переключается по-разному.
    ///
    /// Лента правит рабочую копию вида, а не сохранённый вид: сдвинутая яркость или
    /// подложенная картинка не должны переписывать то, что человек однажды настроил
    /// и назвал. Как только копия расходится с сохранённым, в списке встаёт
    /// «Кастомное» — называть чужим именем то, что на экране, значит врать.
    /// </summary>
    public sealed class RibbonAppearanceTabViewModel : ReactiveObject
    {
        private readonly IEditorViewHost _host;

        // Пока идёт обновление из модели, обратные записи не выполняются: иначе
        // выставленное значение тут же уходит назад в модель и тянет за собой
        // перерисовку на каждое обновление ленты.
        private bool _suspend;

        // Отдельно от _suspend: выбор в списке подменяется и при пересборке
        // списка, когда остальные поля трогать не нужно.
        private bool _suppressThemeSelection;

        private bool _isThemeGroupExpanded = true;
        private bool _isBackdropGroupExpanded = true;
        private bool _isLightGroupExpanded = true;
        private bool _isShowGroupExpanded = true;

        public const string NoneThemeLabel = "Без вида";
        public const string CustomThemeLabel = "Кастомное";

        public RibbonAppearanceTabViewModel(IEditorViewHost host)
        {
            _host = host;

            ThemeItems = new ObservableCollection<ReadingThemeItem>();
            BackdropFits = new ObservableCollection<BackdropFitItem>
            {
                new(ReadingBackdropFit.Cover,   "Заполнить"),
                new(ReadingBackdropFit.Contain, "Уместить"),
                new(ReadingBackdropFit.Stretch, "Растянуть"),
                new(ReadingBackdropFit.Tile,    "Замостить")
            };

            OpenThemeEditorCommand = ReactiveCommand.Create(() => _host.OpenReadingThemes());
            CreateThemeCommand = ReactiveCommand.Create(() => _host.CreateReadingTheme());
            SelectThemeCommand = ReactiveCommand.Create<ReadingThemeItem?>(item =>
            {
                if (item is null) return;
                SelectedThemeItem = item;
            });
            ResetLightCommand = ReactiveCommand.Create(ResetLight);
            ClearBackdropCommand = ReactiveCommand.Create(() => _host.ClearBackdropImage());
            ResetBackdropCommand = ReactiveCommand.Create(ResetBackdrop);

            RebuildThemeItems();
            RefreshAll();
        }

        // ── Группы ────────────────────────────────────────────────────────

        public bool IsThemeGroupExpanded
        {
            get => _isThemeGroupExpanded;
            set => this.RaiseAndSetIfChanged(ref _isThemeGroupExpanded, value);
        }

        public bool IsBackdropGroupExpanded
        {
            get => _isBackdropGroupExpanded;
            set => this.RaiseAndSetIfChanged(ref _isBackdropGroupExpanded, value);
        }

        public bool IsLightGroupExpanded
        {
            get => _isLightGroupExpanded;
            set => this.RaiseAndSetIfChanged(ref _isLightGroupExpanded, value);
        }

        public bool IsShowGroupExpanded
        {
            get => _isShowGroupExpanded;
            set => this.RaiseAndSetIfChanged(ref _isShowGroupExpanded, value);
        }

        /// <summary>
        /// Сворачивает группы по остатку ширины. Развёрнутая вкладка укладывается
        /// примерно в семьсот сорок точек, поэтому до этой отметки не сворачивается
        /// ничего. Дальше группы уходят в обратном порядке важности: последним
        /// сворачивается вид листа, ради которого вкладку и открывают.
        ///
        /// Фон отдельной группой больше не стоит — он строкой в группе вида, как в
        /// ленте чтения, и сворачивается вместе с ней. Признак его группы остаётся
        /// и повторяет признак вида: отдельной ширины у фона теперь нет.
        ///
        /// Группа «Фокус» не сворачивается вовсе: она шириной в кнопку с тремя
        /// значками, и прятать её во флайаут значит убрать то, к чему на этой
        /// вкладке возвращаются чаще всего.
        /// </summary>
        public void UpdateLayout(double availableWidth)
        {
            if (availableWidth >= 760)
            {
                IsThemeGroupExpanded = true;
                IsBackdropGroupExpanded = true;
                IsLightGroupExpanded = true;
                IsShowGroupExpanded = true;
                return;
            }

            IsShowGroupExpanded = availableWidth >= 700;
            IsLightGroupExpanded = availableWidth >= 600;
            IsThemeGroupExpanded = availableWidth >= 440;
            IsBackdropGroupExpanded = IsThemeGroupExpanded;
        }

        // ── Виды ──────────────────────────────────────────────────────────

        public ObservableCollection<ReadingThemeItem> ThemeItems { get; }

        public ICommand OpenThemeEditorCommand { get; }

        /// <summary>Завести свой вид, не открывая список видов отдельным шагом.</summary>
        public ICommand CreateThemeCommand { get; }

        /// <summary>
        /// Выбрать вид его плиткой. Сетка плиток — не список с выделением: нажатие по
        /// плитке и есть выбор.
        /// </summary>
        public ICommand SelectThemeCommand { get; }
        public ICommand ResetLightCommand { get; }
        public ICommand ClearBackdropCommand { get; }

        /// <summary>Вернуть фон к бумаге: убрать свой цвет поля и картинку.</summary>
        public ICommand ResetBackdropCommand { get; }

        private EditorViewSettings? V => _host.EditorView;

        /// <summary>Вид, которым сейчас рисуется лист.</summary>
        private ReadingTheme? Active => V?.Active;

        private ReadingThemeItem? _selectedThemeItem;

        // Список сейчас разбирает щелчок по строке. Всё, что меняет состав пунктов,
        // на это время откладывается — см. SyncCustomThemeItem.
        private bool _selectionInFlight;

        /// <summary>
        /// Выбранный вид. «Кастомное» видом не становится — выбрать его нечем, оно
        /// и так уже на экране; список просто остаётся на нём.
        /// </summary>
        public ReadingThemeItem? SelectedThemeItem
        {
            get => _selectedThemeItem;
            set
            {
                if (_suppressThemeSelection) return;
                if (value is null) return;
                if (value.IsCustom) return;
                if (V is not { } v) return;

                _selectedThemeItem = value;
                SyncSelectedMark();
                this.RaisePropertyChanged();

                if (IsNoneItem(value))
                {
                    // «Без вида» не удаляет выбранный вид, а снимает его применение:
                    // вернувшись, человек находит свой лист, а не белый.
                    v.ThemeEnabled = false;
                }
                else if (value.Theme is { } theme)
                {
                    v.ThemeEnabled = true;
                    v.ApplyTheme(theme);
                }

                _selectionInFlight = true;
                try
                {
                    RaiseThemeDependent();
                    Apply(persist: true);
                }
                finally
                {
                    _selectionInFlight = false;
                }

                // Состав списка правится следующим тактом — щелчок к нему уже
                // разобран целиком.
                Dispatcher.UIThread.Post(SyncCustomThemeItem, DispatcherPriority.Background);
            }
        }

        /// <summary>
        /// Пункт «Без вида»: за ним нет вида, и это не «Кастомное» — у того за плиткой
        /// стоит рабочая копия.
        /// </summary>
        private static bool IsNoneItem(ReadingThemeItem item)
            => item.Theme is null && !item.IsCustom && !item.IsCommand;

        /// <summary>Пересобирает список: виды могли добавиться, уехать или сменить имя.</summary>
        public void RebuildThemeItems()
        {
            // Та же причина, что и у SyncCustomThemeItem: состав списка не меняется,
            // пока список разбирает щелчок.
            if (_selectionInFlight)
            {
                Dispatcher.UIThread.Post(RebuildThemeItems, DispatcherPriority.Background);
                return;
            }

            _suppressThemeSelection = true;
            try
            {
                ThemeItems.Clear();
                ThemeItems.Add(new ReadingThemeItem(null, NoneThemeLabel));

                foreach (var theme in _host.VisibleReadingThemes(V?.ThemeId))
                    ThemeItems.Add(new ReadingThemeItem(theme, theme.Name));
            }
            finally
            {
                _suppressThemeSelection = false;
            }

            SyncCustomThemeItem();
        }

        /// <summary>
        /// Разошлась ли рабочая копия вида с тем, что сохранено под его именем.
        /// Пока правок нет, на экране сохранённый вид и в списке стоит его имя.
        /// </summary>
        private bool IsThemeCustom()
        {
            if (V is not { ThemeEnabled: true } v) return false;
            if (v.Active is not { } active) return false;

            var source = _host.ReadingThemes()
                .FirstOrDefault(t => string.Equals(t.Id, v.ThemeId, StringComparison.Ordinal));

            // Вида под таким опознавателем больше нет — его удалили или
            // переименовали. То, что на экране, тогда и правда ничьё.
            if (source is null) return true;

            return !ReadingTheme.SameLook(source, active);
        }

        /// <summary>
        /// Держит пункт «Кастомное» и выбор в списке в согласии с тем, что на
        /// экране: добавляет пункт при первой же правке и убирает, когда вид снова
        /// совпал с сохранённым.
        /// </summary>
        private void SyncCustomThemeItem()
        {
            // Пока список разбирает щелчок по строке, трогать его состав нельзя:
            // выбор он доводит по индексу, а вставка или удаление пункта этот индекс
            // обесценивает — падение приходит из недр самого списка, уже после того,
            // как обработчик вернул управление. Правка откладывается на следующий
            // такт: к нему список с выбором закончил.
            if (_selectionInFlight)
            {
                Dispatcher.UIThread.Post(SyncCustomThemeItem, DispatcherPriority.Background);
                return;
            }

            bool custom = IsThemeCustom();
            var existing = ThemeItems.FirstOrDefault(i => i.IsCustom);

            _suppressThemeSelection = true;
            try
            {
                if (custom)
                {
                    if (existing is null)
                    {
                        existing = new ReadingThemeItem(Active, CustomThemeLabel, isCustom: true);
                        ThemeItems.Insert(1, existing);
                    }
                    else
                    {
                        // Та же плитка, но вид под ней уже другой: свет, фон, картинка.
                        existing.RefreshLook();
                    }
                    _selectedThemeItem = existing;
                }
                else
                {
                    if (existing is not null) ThemeItems.Remove(existing);

                    bool off = V is null || !V.ThemeEnabled;

                    _selectedThemeItem = off
                        ? ThemeItems.FirstOrDefault(IsNoneItem)
                        : ThemeItems.FirstOrDefault(
                              i => i.Theme is { } t
                                && string.Equals(t.Id, V?.ThemeId, StringComparison.Ordinal))
                          ?? ThemeItems.FirstOrDefault(IsNoneItem);
                }
            }
            finally
            {
                _suppressThemeSelection = false;
            }

            SyncSelectedMark();
            this.RaisePropertyChanged(nameof(SelectedThemeItem));
        }

        /// <summary>
        /// Держит отметку на плитке выбранного вида: у сетки своего выделения нет.
        /// </summary>
        private void SyncSelectedMark()
        {
            foreach (var item in ThemeItems)
                item.IsSelected = ReferenceEquals(item, _selectedThemeItem);
        }

        /// <summary>Вид назначен правке — от этого зависит, доступны ли свет и фон.</summary>
        public bool IsThemeApplied => V is { ThemeEnabled: true };

        /// <summary>Имя вида для подписи на свёрнутой группе.</summary>
        public string ThemeName
        {
            get
            {
                if (V is not { ThemeEnabled: true }) return NoneThemeLabel;

                return _selectedThemeItem?.Label
                    ?? _host.ReadingThemes()
                        .FirstOrDefault(t => string.Equals(t.Id, V.ThemeId, StringComparison.Ordinal))
                        ?.Name
                    ?? CustomThemeLabel;
            }
        }

        // ── Фон позади страниц ────────────────────────────────────────────

        /// <summary>Пункт списка «как ложится картинка фона».</summary>
        public sealed class BackdropFitItem
        {
            public BackdropFitItem(ReadingBackdropFit fit, string label)
            {
                Fit = fit;
                Label = label;
            }

            public ReadingBackdropFit Fit { get; }
            public string Label { get; }
            public override string ToString() => Label;
        }

        public ObservableCollection<BackdropFitItem> BackdropFits { get; }

        /// <summary>
        /// Картинка выбрана — значит есть чем управлять.
        ///
        /// Отдельного тумблера «показывать картинку» больше нет: картинка либо
        /// выбрана, либо нет, и второе состояние про то же самое было лишним — оно
        /// оставляло вид с выбранной, но невидимой картинкой, о которой нигде не
        /// сказано. Убрать её теперь можно только одним способом — убрать.
        /// </summary>
        public bool HasBackdropImage => Active is { HasBackdropImage: true };

        public BackdropFitItem? SelectedBackdropFit
        {
            get
            {
                var fit = Active?.BackdropImageFit ?? ReadingBackdropFit.Cover;
                return BackdropFits.FirstOrDefault(i => i.Fit == fit) ?? BackdropFits[0];
            }
            set
            {
                if (_suspend || value is null || Active is not { } theme) return;
                if (theme.BackdropImageFit == value.Fit) return;

                theme.BackdropImageFit = value.Fit;
                this.RaisePropertyChanged();
                Apply(persist: true);
                SyncCustomThemeItem();
            }
        }

        /// <summary>
        /// Плотность картинки фона. Приглушённая картинка — обычное дело: она лежит
        /// под рукописью, и спорить с текстом за внимание ей нельзя.
        /// </summary>
        public double BackdropOpacity
        {
            get => Active?.BackdropImageOpacity ?? 1.0;
            set
            {
                if (_suspend || Active is not { } theme) return;

                double clamped = Math.Clamp(value, 0.0, 1.0);
                if (Math.Abs(theme.BackdropImageOpacity - clamped) < 0.001) return;

                theme.BackdropImageOpacity = clamped;
                this.RaisePropertyChanged();
                this.RaisePropertyChanged(nameof(BackdropOpacityText));
                Apply(persist: true);
                SyncCustomThemeItem();
            }
        }

        public string BackdropOpacityText => Percent(BackdropOpacity);

        /// <summary>
        /// Заливка поля вокруг листа — тем же кружком, что и в ленте чтения. Значение
        /// то же, что у любого цвета в программе: HEX или код градиента. Пока своего
        /// не выбрано, кружок показывает цвет, который поле выводит из бумаги.
        ///
        /// Правится рабочая копия вида, а не сохранённый вид: как и у света, вид
        /// после правки становится «Кастомным», сохранить его под именем можно в окне
        /// видов.
        /// </summary>
        public string BackdropColorHex
        {
            get
            {
                string? own = Active?.BackdropColor;
                return string.IsNullOrWhiteSpace(own)
                    ? ReadingTheme.FieldColorHex(Active)
                    : own!;
            }
            set
            {
                if (_suspend || !IsThemeApplied) return;
                if (Active is not { } theme) return;
                if (string.IsNullOrWhiteSpace(value)) return;
                if (string.Equals(value, theme.BackdropColor, StringComparison.OrdinalIgnoreCase)) return;

                // Пока поле выводится из бумаги, кружок показывает выведенный цвет, и
                // двусторонняя привязка тут же отдаёт его обратно сюда. Принять такое
                // значение молча значит выключить «от бумаги», ничего не нажимая.
                if (theme.BackdropColor is null
                    && string.Equals(value, ReadingTheme.FieldColorHex(theme), StringComparison.OrdinalIgnoreCase)) return;

                theme.BackdropColor = value;
                this.RaisePropertyChanged();

                SyncCustomThemeItem();
                this.RaisePropertyChanged(nameof(ThemeName));
                Apply(persist: true);
            }
        }

        /// <summary>
        /// Картинки фона, загруженные из ленты. Набор заменяется целиком: кнопка в
        /// ленте — это «вот этот фон», а собирать набор из нескольких и настраивать,
        /// как они ложатся и насколько плотные, удобнее в окне видов. Правка идёт по
        /// рабочей копии, и вид становится «Кастомным».
        /// </summary>
        public void SetBackdropImages(IReadOnlyList<string> references)
        {
            if (!IsThemeApplied || Active is not { } theme) return;
            if (references.Count == 0) return;
            if (theme.BackdropImagePaths.SequenceEqual(references)) return;

            theme.BackdropImagePaths.Clear();
            theme.BackdropImagePaths.AddRange(references);

            RaiseThemeDependent();
            SyncCustomThemeItem();
            Apply(persist: true);
        }

        /// <summary>Возвращает поле к правилу «выводить из бумаги».</summary>
        private void ResetBackdrop()
        {
            if (!IsThemeApplied || Active is not { } theme) return;
            if (theme.BackdropColor is null && !theme.HasBackdropImage) return;

            theme.BackdropColor = null;
            theme.BackdropImagePaths.Clear();

            RaiseThemeDependent();
            SyncCustomThemeItem();
            Apply(persist: true);
        }

        // ── Свет ──────────────────────────────────────────────────────────

        public double Brightness
        {
            get => Active?.Brightness ?? 1.0;
            set => SetLight(t => t.Brightness = Math.Clamp(value, 0.35, 1.0));
        }

        public double Contrast
        {
            get => Active?.Contrast ?? 1.0;
            set => SetLight(t => t.Contrast = Math.Clamp(value, 0.6, 1.6));
        }

        public double Warmth
        {
            get => Active?.Warmth ?? 0.0;
            set => SetLight(t => t.Warmth = Math.Clamp(value, 0.0, 1.0));
        }

        public string BrightnessText => Percent(Brightness);
        public string ContrastText => Percent(Contrast);
        public string WarmthText => Percent(Warmth);

        private static string Percent(double v) => ((int)Math.Round(v * 100.0)) + "%";

        private void SetLight(Action<ReadingTheme> change)
        {
            if (_suspend || !IsThemeApplied) return;
            if (Active is not { } theme) return;

            change(theme);

            this.RaisePropertyChanged(nameof(Brightness));
            this.RaisePropertyChanged(nameof(Contrast));
            this.RaisePropertyChanged(nameof(Warmth));
            this.RaisePropertyChanged(nameof(BrightnessText));
            this.RaisePropertyChanged(nameof(ContrastText));
            this.RaisePropertyChanged(nameof(WarmthText));

            SyncCustomThemeItem();
            this.RaisePropertyChanged(nameof(ThemeName));

            Apply(persist: true);
        }

        private void ResetLight()
        {
            if (Active is not { } theme || !IsThemeApplied) return;

            theme.Brightness = 1.0;
            theme.Contrast = 1.0;
            theme.Warmth = 0.0;

            RaiseThemeDependent();
            SyncCustomThemeItem();
            Apply(persist: true);
        }

        // ── Показ ─────────────────────────────────────────────────────────

        public bool ShowRuler
        {
            get => _host.ShowRuler;
            set
            {
                if (_suspend || _host.ShowRuler == value) return;
                _host.ShowRuler = value;
                this.RaisePropertyChanged();
            }
        }

        public bool ShowStatusBar
        {
            get => _host.ShowStatusBar;
            set
            {
                if (_suspend || _host.ShowStatusBar == value) return;
                _host.ShowStatusBar = value;
                this.RaisePropertyChanged();
            }
        }

        // ── Каретка ───────────────────────────────────────────────────────

        /// <summary>
        /// Кареткой правит свой цвет. Выключено — она берёт цвет у текста, который пишет,
        /// и потому видна на любом листе сама собой.
        ///
        /// Включение не спрашивает цвет отдельно: у настройки уже есть значение — то, что
        /// стояло в прошлый раз, а в первый раз оранжевое. Спрашивать цвет прежде, чем
        /// человек увидел результат, значит требовать решения вслепую.
        /// </summary>
        public bool IsCaretColorCustom
        {
            get => _host.IsCaretColorCustom;
            set
            {
                if (_suspend || _host.IsCaretColorCustom == value) return;
                _host.IsCaretColorCustom = value;
                this.RaisePropertyChanged();
                this.RaisePropertyChanged(nameof(CaretColor));
            }
        }

        /// <summary>
        /// Свой цвет каретки. Правка цвета сама включает признак: человек тянется к
        /// палитре тогда, когда цвет ему и нужен, и просить его после этого нажать ещё
        /// и переключатель — лишний шаг.
        /// </summary>
        public string CaretColor
        {
            get => _host.CaretColor;
            set
            {
                if (_suspend || string.IsNullOrWhiteSpace(value)) return;
                if (string.Equals(_host.CaretColor, value, StringComparison.OrdinalIgnoreCase)
                    && _host.IsCaretColorCustom) return;

                _host.CaretColor = value;
                _host.IsCaretColorCustom = true;

                this.RaisePropertyChanged();
                this.RaisePropertyChanged(nameof(IsCaretColorCustom));
            }
        }

        // ── Фокус ─────────────────────────────────────────────────────────

        public bool IsFocusMode
        {
            get => _host.IsFocusMode;
            set
            {
                if (_suspend || _host.IsFocusMode == value) return;
                _host.IsFocusMode = value;
                this.RaisePropertyChanged();
            }
        }

        /// <summary>
        /// Правка во весь экран. Рядом с фокусом, потому что делают они одно и то
        /// же дело с разных сторон: фокус убирает лишнее внутри окна, экран
        /// забирает окну весь монитор. Вместе они и включаются чаще всего.
        /// </summary>
        public bool IsFullscreen
        {
            get => _host.IsFullscreen;
            set
            {
                if (_suspend || _host.IsFullscreen == value) return;
                _host.IsFullscreen = value;
                this.RaisePropertyChanged();
            }
        }

        public bool FocusHidesRuler
        {
            get => V is { FocusHidesRuler: true };
            set => SetFocusOption(v => v.FocusHidesRuler = value, nameof(FocusHidesRuler));
        }

        public bool FocusHidesStatusBar
        {
            get => V is { FocusHidesStatusBar: true };
            set => SetFocusOption(v => v.FocusHidesStatusBar = value, nameof(FocusHidesStatusBar));
        }

        public bool FocusRibbonOnHover
        {
            get => V is { FocusRibbonOnHover: true };
            set => SetFocusOption(v => v.FocusRibbonOnHover = value, nameof(FocusRibbonOnHover));
        }

        private void SetFocusOption(Action<EditorViewSettings> change, string property)
        {
            if (_suspend || V is not { } v) return;

            change(v);
            this.RaisePropertyChanged(property);
            _host.PersistEditorViewPreferences();
        }

        // ── Общее ─────────────────────────────────────────────────────────

        /// <summary>Перерисовать лист и, если нужно, запомнить настройки.</summary>
        private void Apply(bool persist)
        {
            _host.ApplyEditorViewVisual();
            if (persist) _host.PersistEditorViewPreferences();
        }

        /// <summary>Сообщает о смене всего, что берётся у вида.</summary>
        private void RaiseThemeDependent()
        {
            this.RaisePropertyChanged(nameof(IsThemeApplied));
            this.RaisePropertyChanged(nameof(ThemeName));
            this.RaisePropertyChanged(nameof(Brightness));
            this.RaisePropertyChanged(nameof(Contrast));
            this.RaisePropertyChanged(nameof(Warmth));
            this.RaisePropertyChanged(nameof(BrightnessText));
            this.RaisePropertyChanged(nameof(ContrastText));
            this.RaisePropertyChanged(nameof(WarmthText));
            this.RaisePropertyChanged(nameof(HasBackdropImage));
            this.RaisePropertyChanged(nameof(SelectedBackdropFit));
            this.RaisePropertyChanged(nameof(BackdropOpacity));
            this.RaisePropertyChanged(nameof(BackdropOpacityText));
            this.RaisePropertyChanged(nameof(BackdropColorHex));
        }

        /// <summary>
        /// Перечитывает всё из модели. Зовётся, когда документ сменился или настройки
        /// пришли извне: из сессии, из окна видов, из выбора картинки фона.
        /// </summary>
        public void RefreshAll()
        {
            _suspend = true;
            try
            {
                this.RaisePropertyChanged(nameof(ShowRuler));
                this.RaisePropertyChanged(nameof(ShowStatusBar));
                this.RaisePropertyChanged(nameof(IsFocusMode));
                this.RaisePropertyChanged(nameof(IsFullscreen));
                this.RaisePropertyChanged(nameof(FocusHidesRuler));
                this.RaisePropertyChanged(nameof(FocusHidesStatusBar));
                this.RaisePropertyChanged(nameof(FocusRibbonOnHover));
                this.RaisePropertyChanged(nameof(IsCaretColorCustom));
                this.RaisePropertyChanged(nameof(CaretColor));
            }
            finally
            {
                _suspend = false;
            }

            SyncCustomThemeItem();
            RaiseThemeDependent();
        }
    }
}
