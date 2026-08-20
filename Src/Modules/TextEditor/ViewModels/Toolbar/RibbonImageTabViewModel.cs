using System.Windows.Input;
using ReactiveUI;
using Writersword.Modules.TextEditor.Contracts;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Styles;

namespace Writersword.Modules.TextEditor.ViewModels.Toolbar
{
    /// <summary>
    /// ViewModel контекстной вкладки «Формат» (работа с выделенной картинкой).
    /// Появляется только когда на канвасе выделено изображение.
    /// </summary>
    public sealed class RibbonImageTabViewModel : ReactiveObject
    {
        private readonly ITextEditorCommandTarget _target;

        private TextAlignment _currentAlignment = TextAlignment.Left;
        private WrapMode _currentWrap = WrapMode.Inline;
        private WrapSide _currentWrapSide = WrapSide.LargestOnly;
        private int _pinnedPage;
        private bool _isAspectLocked = true;
        private decimal _rotationDegrees;
        private decimal _imageWidth;
        private decimal _imageHeight;
        private bool _unitIsMm = true;
        private decimal _opacityPercent = 100m;
        private decimal _borderThickness;
        private string _borderHexColor = "#00000000";
        private bool _syncing;

        // Отступы обтекания по сторонам (в текущих единицах отступов — см или px).
        private decimal _padTop;
        private decimal _padBottom;
        private decimal _padLeft;
        private decimal _padRight;
        private bool _padUnitIsMm = true;   // по умолчанию миллиметры
        private bool _wrapPadLinked = true; // замок сторон включён по умолчанию
        private decimal? _selectedPadPreset;

        private double UnitToPt(decimal value)
            => _unitIsMm ? (double)value * 72.0 / 25.4 : (double)value * 0.75;

        private decimal PtToUnit(double pt)
            => (decimal)System.Math.Round(_unitIsMm ? pt * 25.4 / 72.0 : pt * 96.0 / 72.0, 2);

        // Единицы отступов обтекания: см ↔ пункты и px ↔ пункты (px при 96 DPI).
        private double PadUnitToPt(decimal value)
            => _padUnitIsMm ? (double)value * 72.0 / 25.4 : (double)value * 0.75;

        private decimal PtToPadUnit(double pt)
            => (decimal)System.Math.Round(_padUnitIsMm ? pt * 25.4 / 72.0 : pt * 96.0 / 72.0, 2);

        /// <summary>Текущее выравнивание картинки в колонке (для подсветки кнопок).</summary>
        public TextAlignment CurrentAlignment
        {
            get => _currentAlignment;
            private set
            {
                // Уведомляем всегда, даже без изменения значения: клик по уже активному
                // тогглу локально гасит его IsChecked, и только повторный push из
                // источника возвращает кнопке правильное состояние.
                _currentAlignment = value;
                this.RaisePropertyChanged(nameof(CurrentAlignment));
            }
        }

        /// <summary>Текущий режим обтекания (для подсветки кнопок).</summary>
        public WrapMode CurrentWrap
        {
            get => _currentWrap;
            private set
            {
                // Всегда raise — см. CurrentAlignment: переключатели строго взаимоисключающие.
                _currentWrap = value;
                this.RaisePropertyChanged(nameof(CurrentWrap));
                this.RaisePropertyChanged(nameof(IsWrapPaddingEnabled));
                this.RaisePropertyChanged(nameof(WrapPaddingOpacity));
                this.RaisePropertyChanged(nameof(IsPinAvailable));
            }
        }

        /// <summary>
        /// Закреплена ли картинка за страницей. Номер страницы наружу не выносится:
        /// закрепляется та страница, на которой картинка стоит в момент нажатия.
        /// </summary>
        public bool IsPinnedToPage => _pinnedPage > 0;

        /// <summary>
        /// Есть ли смысл в закреплении. У картинки «в тексте» её место определяет текст,
        /// а не номер страницы — группа закрепления для неё не показывается.
        /// </summary>
        public bool IsPinAvailable => _currentWrap != WrapMode.Inline;

        /// <summary>Сторона, с которой текст обходит картинку (для подсветки кнопок).</summary>
        public WrapSide CurrentWrapSide
        {
            get => _currentWrapSide;
            private set
            {
                // Всегда raise — см. CurrentAlignment: переключатели взаимоисключающие.
                _currentWrapSide = value;
                this.RaisePropertyChanged(nameof(CurrentWrapSide));
            }
        }

        /// <summary>
        /// Отступы обтекания имеют смысл только когда текст реально обтекает картинку.
        /// В режимах «В тексте» и «За текстом» группа отступов заблокирована.
        /// Сторона обтекания включается по тому же условию.
        /// </summary>
        public bool IsWrapPaddingEnabled
            => _currentWrap == WrapMode.Square || _currentWrap == WrapMode.Tight;

        /// <summary>Приглушение группы отступов, когда обтекание выключено.</summary>
        public double WrapPaddingOpacity => IsWrapPaddingEnabled ? 1.0 : 0.4;

        /// <summary>Заблокированы ли пропорции при изменении размера.</summary>
        public bool IsAspectLocked
        {
            get => _isAspectLocked;
            private set
            {
                _isAspectLocked = value;
                this.RaisePropertyChanged(nameof(IsAspectLocked));
            }
        }

        private bool _isCropMode;

        /// <summary>Активен ли режим обрезки картинки на канвасе.</summary>
        public bool IsCropMode
        {
            get => _isCropMode;
            private set
            {
                _isCropMode = value;
                this.RaisePropertyChanged(nameof(IsCropMode));
                this.RaisePropertyChanged(nameof(IsSizeEditEnabled));
                this.RaisePropertyChanged(nameof(SizeEditOpacity));
            }
        }

        /// <summary>
        /// Пока идёт обрезка, размер картинки менять нельзя: маркеры и поля размера
        /// заняты рамкой кадрирования. Разблокируется по выходу из режима обрезки.
        /// </summary>
        public bool IsSizeEditEnabled => !_isCropMode;

        /// <summary>Приглушение полей размера на время обрезки.</summary>
        public double SizeEditOpacity => _isCropMode ? 0.4 : 1.0;

        /// <summary>
        /// Угол поворота картинки в градусах по часовой стрелке.
        /// Двусторонний биндинг из NumericUpDown: значение нормализуется в [0;360)
        /// и применяется к выделенной картинке через target.
        /// </summary>
        public decimal RotationDegrees
        {
            get => _rotationDegrees;
            set
            {
                decimal normalized = ((value % 360m) + 360m) % 360m;
                if (_rotationDegrees != normalized)
                {
                    _rotationDegrees = normalized;
                    if (!_syncing) _target.SetImageRotation((double)normalized);
                }
                // Уведомляем всегда: если пользователь ввёл 360 или -15,
                // контрол должен вернуться к нормализованному значению.
                this.RaisePropertyChanged(nameof(RotationDegrees));
                this.RaisePropertyChanged(nameof(RotationAngle));
            }
        }

        /// <summary>Тот же угол для ползунка поворота (Slider работает с double).</summary>
        public double RotationAngle
        {
            get => (double)_rotationDegrees;
            set => RotationDegrees = (decimal)System.Math.Round(value, 1);
        }

        /// <summary>Ширина картинки в текущих единицах (мм или px), 2 знака после запятой.</summary>
        public decimal ImageWidth
        {
            get => _imageWidth;
            set
            {
                if (_imageWidth != value)
                {
                    _imageWidth = value;
                    if (!_syncing) _target.SetImageWidth(UnitToPt(value));
                }
                this.RaisePropertyChanged(nameof(ImageWidth));
            }
        }

        /// <summary>Высота картинки в текущих единицах (мм или px), 2 знака после запятой.</summary>
        public decimal ImageHeight
        {
            get => _imageHeight;
            set
            {
                if (_imageHeight != value)
                {
                    _imageHeight = value;
                    if (!_syncing) _target.SetImageHeight(UnitToPt(value));
                }
                this.RaisePropertyChanged(nameof(ImageHeight));
            }
        }

        /// <summary>Единицы размеров: true — миллиметры, false — пиксели.</summary>
        public bool UnitIsMm
        {
            get => _unitIsMm;
            private set
            {
                if (_unitIsMm == value) return;
                _unitIsMm = value;
                this.RaisePropertyChanged(nameof(UnitIsMm));
                this.RaisePropertyChanged(nameof(UnitIsPx));
                this.RaisePropertyChanged(nameof(UnitLabel));
                SyncFromTarget();
            }
        }

        public bool UnitIsPx => !_unitIsMm;

        /// <summary>
        /// Подпись на кнопке единиц размера. Пара кнопок «мм»/«px» заменена одной
        /// переключающей: в ленте она занимает вдвое меньше места, а состояние
        /// читается прямо с надписи.
        /// </summary>
        public string UnitLabel => _unitIsMm ? "мм" : "px";

        /// <summary>Непрозрачность картинки в процентах (0 — невидима, 100 — полностью видна).</summary>
        public decimal OpacityPercent
        {
            get => _opacityPercent;
            set
            {
                decimal clamped = System.Math.Clamp(value, 0m, 100m);
                if (_opacityPercent != clamped)
                {
                    _opacityPercent = clamped;
                    if (!_syncing) _target.SetImageOpacity((double)clamped / 100.0);
                }
                this.RaisePropertyChanged(nameof(OpacityPercent));
                this.RaisePropertyChanged(nameof(OpacityValue));
            }
        }

        /// <summary>Та же непрозрачность для ползунка (double).</summary>
        public double OpacityValue
        {
            get => (double)_opacityPercent;
            set => OpacityPercent = (decimal)System.Math.Round(value, 0);
        }

        /// <summary>Толщина рамки картинки в пунктах.</summary>
        public decimal BorderThickness
        {
            get => _borderThickness;
            set
            {
                decimal clamped = System.Math.Clamp(value, 0m, 50m);
                if (_borderThickness != clamped)
                {
                    _borderThickness = clamped;
                    if (!_syncing)
                    {
                        // Толщина при «нет цвета» давала невидимую рамку: пользователь
                        // менял число, а на листе ничего не появлялось. Ненулевая
                        // толщина без выбранного цвета включает чёрную рамку.
                        if (clamped > 0m && _borderHexColor == "#00000000")
                        {
                            _borderHexColor = "#000000";
                            this.RaisePropertyChanged(nameof(BorderHexColor));
                        }
                        _target.SetImageBorder(_borderHexColor, (double)clamped);
                    }
                }
                this.RaisePropertyChanged(nameof(BorderThickness));
            }
        }

        /// <summary>Цвет рамки картинки в hex; #00000000 — рамки нет.</summary>
        public string BorderHexColor
        {
            get => _borderHexColor;
            set
            {
                if (_borderHexColor == value) return;
                _borderHexColor = value ?? "#00000000";
                if (!_syncing)
                {
                    // Выбор цвета при нулевой толщине сразу даёт видимую рамку.
                    if (_borderThickness <= 0m && _borderHexColor != "#00000000")
                    {
                        _borderThickness = 1.5m;
                        this.RaisePropertyChanged(nameof(BorderThickness));
                    }
                    // Выбор «нет цвета» — это отказ от рамки: обнуляем и толщину,
                    // иначе поле показывало бы толщину несуществующей рамки.
                    else if (_borderHexColor == "#00000000" && _borderThickness > 0m)
                    {
                        _borderThickness = 0m;
                        this.RaisePropertyChanged(nameof(BorderThickness));
                    }
                    _target.SetImageBorder(_borderHexColor, (double)_borderThickness);
                }
                this.RaisePropertyChanged(nameof(BorderHexColor));
            }
        }

        // ── Отступы обтекания ─────────────────────────────────────────────

        /// <summary>
        /// Связывает четыре стороны: при включённом замке правка любого поля
        /// раскладывается на остальные три. Заменяет прежний список готовых
        /// значений — он умел только применять одно число ко всем сторонам сразу.
        /// </summary>
        public bool IsWrapPadLinked
        {
            get => _wrapPadLinked;
            private set
            {
                if (_wrapPadLinked == value) return;
                _wrapPadLinked = value;
                this.RaisePropertyChanged(nameof(IsWrapPadLinked));
            }
        }

        /// <summary>
        /// Раскладывает значение на все четыре стороны, когда включён замок.
        /// Возвращает true, если сторонами занялся этот метод и вызывающему
        /// остаётся только поднять уведомление по своему свойству.
        /// </summary>
        private bool SpreadWrapPad(decimal value)
        {
            if (!_wrapPadLinked || _syncing) return false;

            _padTop = value;
            _padBottom = value;
            _padLeft = value;
            _padRight = value;

            this.RaisePropertyChanged(nameof(WrapPadTop));
            this.RaisePropertyChanged(nameof(WrapPadBottom));
            this.RaisePropertyChanged(nameof(WrapPadLeft));
            this.RaisePropertyChanged(nameof(WrapPadRight));

            ApplyWrapPadding();
            return true;
        }

        /// <summary>Отступ обтекания сверху в текущих единицах отступов (мм/px).</summary>
        public decimal WrapPadTop
        {
            get => _padTop;
            set { if (_padTop != value) { if (!SpreadWrapPad(value)) { _padTop = value; if (!_syncing) ApplyWrapPadding(); } } this.RaisePropertyChanged(nameof(WrapPadTop)); }
        }

        /// <summary>Отступ обтекания снизу в текущих единицах отступов (мм/px).</summary>
        public decimal WrapPadBottom
        {
            get => _padBottom;
            set { if (_padBottom != value) { if (!SpreadWrapPad(value)) { _padBottom = value; if (!_syncing) ApplyWrapPadding(); } } this.RaisePropertyChanged(nameof(WrapPadBottom)); }
        }

        /// <summary>Отступ обтекания слева в текущих единицах отступов (мм/px).</summary>
        public decimal WrapPadLeft
        {
            get => _padLeft;
            set { if (_padLeft != value) { if (!SpreadWrapPad(value)) { _padLeft = value; if (!_syncing) ApplyWrapPadding(); } } this.RaisePropertyChanged(nameof(WrapPadLeft)); }
        }

        /// <summary>Отступ обтекания справа в текущих единицах отступов (мм/px).</summary>
        public decimal WrapPadRight
        {
            get => _padRight;
            set { if (_padRight != value) { if (!SpreadWrapPad(value)) { _padRight = value; if (!_syncing) ApplyWrapPadding(); } } this.RaisePropertyChanged(nameof(WrapPadRight)); }
        }

        /// <summary>Единицы отступов обтекания: true — миллиметры (по умолчанию), false — пиксели.</summary>
        public bool PadUnitIsMm
        {
            get => _padUnitIsMm;
            private set
            {
                if (_padUnitIsMm == value) return;
                _padUnitIsMm = value;
                this.RaisePropertyChanged(nameof(PadUnitIsMm));
                this.RaisePropertyChanged(nameof(PadUnitIsPx));
                this.RaisePropertyChanged(nameof(PadUnitLabel));
                SyncFromTarget();
            }
        }

        public bool PadUnitIsPx => !_padUnitIsMm;

        /// <summary>Подпись на кнопке единиц отступов обтекания.</summary>
        public string PadUnitLabel => _padUnitIsMm ? "мм" : "px";

        /// <summary>
        /// Быстрые значения отступа для выпадающего списка, в миллиметрах.
        /// Прежний набор был в сантиметрах (0,1 … 1,0) и после перевода единиц
        /// давал бы отступы в десять раз меньше подписанных.
        /// </summary>
        public System.Collections.Generic.IReadOnlyList<decimal> WrapPadPresets { get; }
            = new decimal[] { 0m, 1m, 2m, 3m, 5m, 10m };

        /// <summary>Выбранный пресет: применяется сразу ко всем 4 сторонам одной операцией.</summary>
        public decimal? SelectedWrapPadPreset
        {
            get => _selectedPadPreset;
            set
            {
                _selectedPadPreset = value;
                this.RaisePropertyChanged(nameof(SelectedWrapPadPreset));
                if (value is decimal v && !_syncing)
                {
                    _syncing = true;
                    _padTop = _padBottom = _padLeft = _padRight = v;
                    this.RaisePropertyChanged(nameof(WrapPadTop));
                    this.RaisePropertyChanged(nameof(WrapPadBottom));
                    this.RaisePropertyChanged(nameof(WrapPadLeft));
                    this.RaisePropertyChanged(nameof(WrapPadRight));
                    _syncing = false;
                    ApplyWrapPadding();
                }
            }
        }

        private void ApplyWrapPadding()
            => _target.SetImageWrapPadding(
                PadUnitToPt(_padTop), PadUnitToPt(_padBottom),
                PadUnitToPt(_padLeft), PadUnitToPt(_padRight));

        // ── Выравнивание картинки в колонке ───────────────────────────────
        public ICommand AlignLeftCommand { get; }
        public ICommand AlignCenterCommand { get; }
        public ICommand AlignRightCommand { get; }

        // ── Обтекание текстом ─────────────────────────────────────────────
        public ICommand WrapInlineCommand { get; }
        public ICommand WrapSquareCommand { get; }
        public ICommand WrapBehindCommand { get; }

        // ── Привязка к странице ───────────────────────────────────────────
        public ICommand TogglePinToPageCommand { get; }

        // ── Сторона обтекания ─────────────────────────────────────────────
        public ICommand WrapSideLargestCommand { get; }
        public ICommand WrapSideBothCommand { get; }
        public ICommand WrapSideLeftCommand { get; }
        public ICommand WrapSideRightCommand { get; }

        // ── Поворот ───────────────────────────────────────────────────────
        public ICommand RotateLeft90Command { get; }
        public ICommand RotateRight90Command { get; }

        // ── Единицы размеров ──────────────────────────────────────────────
        public ICommand ToggleUnitCommand { get; }

        // ── Единицы отступов обтекания ────────────────────────────────────
        public ICommand TogglePadUnitCommand { get; }

        // ── Связь сторон отступа ──────────────────────────────────────────
        public ICommand ToggleWrapPadLinkCommand { get; }

        // ── Обрезка и отражение ───────────────────────────────────────────
        public ICommand ToggleCropModeCommand { get; }
        public ICommand FlipHorizontalCommand { get; }
        public ICommand FlipVerticalCommand { get; }

        // ── Прочее ────────────────────────────────────────────────────────
        public ICommand ToggleAspectCommand { get; }
        public ICommand DeleteImageCommand { get; }

        public RibbonImageTabViewModel(ITextEditorCommandTarget target)
        {
            _target = target ?? throw new System.ArgumentNullException(nameof(target));

            AlignLeftCommand = ReactiveCommand.Create(() =>
                { _target.SetAlignment(TextAlignment.Left); SyncFromTarget(); });
            AlignCenterCommand = ReactiveCommand.Create(() =>
                { _target.SetAlignment(TextAlignment.Center); SyncFromTarget(); });
            AlignRightCommand = ReactiveCommand.Create(() =>
                { _target.SetAlignment(TextAlignment.Right); SyncFromTarget(); });

            WrapInlineCommand = ReactiveCommand.Create(() =>
                { _target.SetImageWrapMode(WrapMode.Inline); SyncFromTarget(); });
            WrapSquareCommand = ReactiveCommand.Create(() =>
                { _target.SetImageWrapMode(WrapMode.Square); SyncFromTarget(); });
            WrapBehindCommand = ReactiveCommand.Create(() =>
                { _target.SetImageWrapMode(WrapMode.Behind); SyncFromTarget(); });

            // Включение привязывает картинку к странице, на которой она сейчас лежит;
            // выключение возвращает обычный перенос вслед за текстом.
            TogglePinToPageCommand = ReactiveCommand.Create(() =>
            {
                int page = _pinnedPage > 0
                    ? 0
                    : (_target.GetSelectedImageCurrentPage() ?? 1);
                _target.SetImagePinnedPage(page);
                SyncFromTarget();
            });

            WrapSideLargestCommand = ReactiveCommand.Create(() =>
                { _target.SetImageWrapSide(WrapSide.LargestOnly); SyncFromTarget(); });
            WrapSideBothCommand = ReactiveCommand.Create(() =>
                { _target.SetImageWrapSide(WrapSide.BothSides); SyncFromTarget(); });
            WrapSideLeftCommand = ReactiveCommand.Create(() =>
                { _target.SetImageWrapSide(WrapSide.LeftOnly); SyncFromTarget(); });
            WrapSideRightCommand = ReactiveCommand.Create(() =>
                { _target.SetImageWrapSide(WrapSide.RightOnly); SyncFromTarget(); });

            RotateLeft90Command = ReactiveCommand.Create(() =>
                { RotationDegrees = _rotationDegrees - 90m; });
            RotateRight90Command = ReactiveCommand.Create(() =>
                { RotationDegrees = _rotationDegrees + 90m; });

            ToggleUnitCommand = ReactiveCommand.Create(() => { UnitIsMm = !UnitIsMm; });

            TogglePadUnitCommand = ReactiveCommand.Create(() => { PadUnitIsMm = !PadUnitIsMm; });

            ToggleWrapPadLinkCommand = ReactiveCommand.Create(() =>
            {
                IsWrapPadLinked = !IsWrapPadLinked;

                // Включение замка при разных сторонах сразу приводит их к общему
                // значению — иначе связь висела бы включённой, но ни на что не влияла
                // до первой правки поля.
                if (IsWrapPadLinked) SpreadWrapPad(_padTop);
            });

            ToggleCropModeCommand = ReactiveCommand.Create(() =>
                { _target.SetImageCropMode(!_isCropMode); SyncFromTarget(); });
            FlipHorizontalCommand = ReactiveCommand.Create(() =>
                { _target.ToggleImageFlipHorizontal(); });
            FlipVerticalCommand = ReactiveCommand.Create(() =>
                { _target.ToggleImageFlipVertical(); });

            ToggleAspectCommand = ReactiveCommand.Create(() =>
                { _target.SetImageLockAspect(!IsAspectLocked); SyncFromTarget(); });
            DeleteImageCommand = ReactiveCommand.Create(() => _target.DeleteSelectedImage());
        }

        /// <summary>
        /// Читает параметры выделенной картинки из target и обновляет состояние вкладки.
        /// Вызывается при выделении картинки и после каждой команды.
        /// </summary>
        public void SyncFromTarget()
        {
            var info = _target.GetSelectedImageInfo();
            if (info is null) return;

            _syncing = true;
            try
            {
                CurrentAlignment = info.Value.Align;
                CurrentWrap = info.Value.Wrap;
                CurrentWrapSide = _target.GetSelectedImageWrapSide() ?? WrapSide.LargestOnly;

                _pinnedPage = _target.GetSelectedImagePinnedPage() ?? 0;
                this.RaisePropertyChanged(nameof(IsPinnedToPage));
                IsAspectLocked = info.Value.LockAspect;
                IsCropMode = _target.GetImageCropMode();

                var rotation = _target.GetSelectedImageRotation();
                if (rotation is not null)
                {
                    decimal deg = (decimal)System.Math.Round(rotation.Value, 1);
                    if (_rotationDegrees != deg)
                    {
                        _rotationDegrees = deg;
                        this.RaisePropertyChanged(nameof(RotationDegrees));
                        this.RaisePropertyChanged(nameof(RotationAngle));
                    }
                }

                var style = _target.GetSelectedImageStyle();
                if (style is not null)
                {
                    decimal w = PtToUnit(style.Value.WidthPt);
                    decimal h = PtToUnit(style.Value.HeightPt);
                    if (_imageWidth != w)
                    {
                        _imageWidth = w;
                        this.RaisePropertyChanged(nameof(ImageWidth));
                    }
                    if (_imageHeight != h)
                    {
                        _imageHeight = h;
                        this.RaisePropertyChanged(nameof(ImageHeight));
                    }

                    decimal op = (decimal)System.Math.Round(style.Value.Opacity * 100.0, 0);
                    if (_opacityPercent != op)
                    {
                        _opacityPercent = op;
                        this.RaisePropertyChanged(nameof(OpacityPercent));
                        this.RaisePropertyChanged(nameof(OpacityValue));
                    }

                    decimal bt = (decimal)System.Math.Round(style.Value.BorderThicknessPt, 1);
                    if (_borderThickness != bt)
                    {
                        _borderThickness = bt;
                        this.RaisePropertyChanged(nameof(BorderThickness));
                    }

                    string bc = style.Value.BorderColor ?? "#00000000";
                    if (_borderHexColor != bc)
                    {
                        _borderHexColor = bc;
                        this.RaisePropertyChanged(nameof(BorderHexColor));
                    }
                }

                var pad = _target.GetSelectedImageWrapPadding();
                if (pad is not null)
                {
                    decimal t = PtToPadUnit(pad.Value.TopPt);
                    decimal b = PtToPadUnit(pad.Value.BottomPt);
                    decimal l = PtToPadUnit(pad.Value.LeftPt);
                    decimal r = PtToPadUnit(pad.Value.RightPt);
                    if (_padTop != t) { _padTop = t; this.RaisePropertyChanged(nameof(WrapPadTop)); }
                    if (_padBottom != b) { _padBottom = b; this.RaisePropertyChanged(nameof(WrapPadBottom)); }
                    if (_padLeft != l) { _padLeft = l; this.RaisePropertyChanged(nameof(WrapPadLeft)); }
                    if (_padRight != r) { _padRight = r; this.RaisePropertyChanged(nameof(WrapPadRight)); }
                }
            }
            finally
            {
                _syncing = false;
            }
        }
    }
}
