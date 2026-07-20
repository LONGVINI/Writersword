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
        private bool _isAspectLocked = true;
        private decimal _rotationDegrees;
        private decimal _imageWidth;
        private decimal _imageHeight;
        private bool _unitIsMm = true;
        private decimal _opacityPercent = 100m;
        private decimal _borderThickness;
        private string _borderHexColor = "#00000000";
        private bool _syncing;

        private double UnitToPt(decimal value)
            => _unitIsMm ? (double)value * 72.0 / 25.4 : (double)value * 0.75;

        private decimal PtToUnit(double pt)
            => (decimal)System.Math.Round(_unitIsMm ? pt * 25.4 / 72.0 : pt * 96.0 / 72.0, 2);

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
            }
        }

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
            }
        }

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
                SyncFromTarget();
            }
        }

        public bool UnitIsPx => !_unitIsMm;

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
                    if (!_syncing) _target.SetImageBorder(_borderHexColor, (double)clamped);
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
                    _target.SetImageBorder(_borderHexColor, (double)_borderThickness);
                }
                this.RaisePropertyChanged(nameof(BorderHexColor));
            }
        }

        // ── Выравнивание картинки в колонке ───────────────────────────────
        public ICommand AlignLeftCommand { get; }
        public ICommand AlignCenterCommand { get; }
        public ICommand AlignRightCommand { get; }

        // ── Обтекание текстом ─────────────────────────────────────────────
        public ICommand WrapInlineCommand { get; }
        public ICommand WrapSquareCommand { get; }
        public ICommand WrapBehindCommand { get; }

        // ── Поворот ───────────────────────────────────────────────────────
        public ICommand RotateLeft90Command { get; }
        public ICommand RotateRight90Command { get; }

        // ── Единицы размеров ──────────────────────────────────────────────
        public ICommand UnitMmCommand { get; }
        public ICommand UnitPxCommand { get; }

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

            RotateLeft90Command = ReactiveCommand.Create(() =>
                { RotationDegrees = _rotationDegrees - 90m; });
            RotateRight90Command = ReactiveCommand.Create(() =>
                { RotationDegrees = _rotationDegrees + 90m; });

            UnitMmCommand = ReactiveCommand.Create(() => { UnitIsMm = true; });
            UnitPxCommand = ReactiveCommand.Create(() => { UnitIsMm = false; });

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
            }
            finally
            {
                _syncing = false;
            }
        }
    }
}
