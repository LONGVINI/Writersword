using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using SkiaSharp;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Rendering;
using Writersword.Styles.UserControls;

namespace Writersword.Modules.TextEditor.Views.Dialogs
{
    /// <summary>
    /// Окно «Эффекты текста»: свой вид контура, тени, свечения, отражения и рамки
    /// вокруг знаков у выделения.
    ///
    /// Образец наверху рисует SKTextRenderer — тот же движок, что и документ, — и
    /// перерисовывается на каждое движение ползунка: что видно в образце, то и ляжет
    /// в текст.
    ///
    /// Окно работает с копией настроек и отдаёт их по «Применить» одной правкой.
    /// Крестик, Escape и щелчок мимо окна — отказ: рукопись не меняется.
    /// </summary>
    public partial class TextEffectsOverlay : UserControl
    {
        // Текст и кегль образца.
        private const string PreviewText = "Образец текста Аа";
        private const float PreviewFontSizePt = 30f;

        // Размер образца в точках интерфейса — как у Image в разметке.
        private const double PreviewWidthDip = 716;
        private const double PreviewHeightDip = 120;

        private Border _scrim = null!;
        private Image _preview = null!;

        private TextBlock _outlineOff = null!;
        private Button _outlineClear = null!;
        private ColorPickerButton _outlineColor = null!;
        private Slider _outlineWidth = null!;
        private TextBlock _outlineWidthText = null!;
        private ComboBox _outlineDash = null!;
        private ComboBox _outlinePlacement = null!;
        private CheckBox _outlineHollow = null!;

        private TextBlock _shadowOff = null!;
        private Button _shadowClear = null!;
        private ColorPickerButton _shadowColor = null!;
        private Slider _shadowTransparency = null!;
        private TextBlock _shadowTransparencyText = null!;
        private Slider _shadowBlur = null!;
        private TextBlock _shadowBlurText = null!;
        private Slider _shadowDistance = null!;
        private TextBlock _shadowDistanceText = null!;
        private Slider _shadowAngle = null!;
        private TextBlock _shadowAngleText = null!;
        private CheckBox _shadowLong = null!;

        private TextBlock _glowOff = null!;
        private Button _glowClear = null!;
        private ColorPickerButton _glowColor = null!;
        private Slider _glowTransparency = null!;
        private TextBlock _glowTransparencyText = null!;
        private Slider _glowRadius = null!;
        private TextBlock _glowRadiusText = null!;

        private TextBlock _reflectionOff = null!;
        private Button _reflectionClear = null!;
        private Slider _reflectionTransparency = null!;
        private TextBlock _reflectionTransparencyText = null!;
        private Slider _reflectionSize = null!;
        private TextBlock _reflectionSizeText = null!;
        private Slider _reflectionDistance = null!;
        private TextBlock _reflectionDistanceText = null!;
        private Slider _reflectionBlur = null!;
        private TextBlock _reflectionBlurText = null!;

        private TextBlock _borderOff = null!;
        private Button _borderClear = null!;
        private ColorPickerButton _borderColor = null!;
        private CheckBox _borderAutoColor = null!;
        private Slider _borderWidth = null!;
        private TextBlock _borderWidthText = null!;
        private ComboBox _borderStyle = null!;

        // Последние цвета эффектов в #RRGGBB. Кнопка цвета может отдать и градиент,
        // и «нет цвета» — эффекты рисуются одним цветом, и такой выбор пропускается.
        private string _outlineHex = "#000000";
        private string _shadowHex = "#000000";
        private string _glowHex = "#FFC000";
        private string _borderHex = "#000000";

        private string _fontFamily = "Times New Roman";
        private SKColor _textColor = SKColors.Black;

        // Пока поля заполняются из настроек, их события не перерисовывают образец.
        private bool _suspend;

        private TaskCompletionSource<TextEffectsDialogState?>? _pending;
        private TextEffectsDialogState? _source;

        // «Сохранить как мой эффект»: откуда взять свободное имя и куда отдать набор.
        // Без них кнопки нет — окно открыли там, где своих наборов не ведут.
        private Func<string>? _suggestPresetName;
        private Action<TextEffectPreset>? _savePreset;

        private Button _savePresetBtn = null!;
        private TextBlock _savePresetNote = null!;

        public TextEffectsOverlay()
        {
            InitializeComponent();
            IsVisible = false;

            _scrim = this.FindControl<Border>("Scrim")!;
            _preview = this.FindControl<Image>("PreviewImage")!;

            _outlineOff = this.FindControl<TextBlock>("OutlineOff")!;
            _outlineClear = this.FindControl<Button>("OutlineClear")!;
            _outlineColor = this.FindControl<ColorPickerButton>("OutlineColor")!;
            _outlineWidth = this.FindControl<Slider>("OutlineWidth")!;
            _outlineWidthText = this.FindControl<TextBlock>("OutlineWidthText")!;
            _outlineDash = this.FindControl<ComboBox>("OutlineDash")!;
            _outlinePlacement = this.FindControl<ComboBox>("OutlinePlacement")!;
            _outlineHollow = this.FindControl<CheckBox>("OutlineHollow")!;

            _shadowOff = this.FindControl<TextBlock>("ShadowOff")!;
            _shadowClear = this.FindControl<Button>("ShadowClear")!;
            _shadowColor = this.FindControl<ColorPickerButton>("ShadowColor")!;
            _shadowTransparency = this.FindControl<Slider>("ShadowTransparency")!;
            _shadowTransparencyText = this.FindControl<TextBlock>("ShadowTransparencyText")!;
            _shadowBlur = this.FindControl<Slider>("ShadowBlur")!;
            _shadowBlurText = this.FindControl<TextBlock>("ShadowBlurText")!;
            _shadowDistance = this.FindControl<Slider>("ShadowDistance")!;
            _shadowDistanceText = this.FindControl<TextBlock>("ShadowDistanceText")!;
            _shadowAngle = this.FindControl<Slider>("ShadowAngle")!;
            _shadowAngleText = this.FindControl<TextBlock>("ShadowAngleText")!;
            _shadowLong = this.FindControl<CheckBox>("ShadowLong")!;

            _glowOff = this.FindControl<TextBlock>("GlowOff")!;
            _glowClear = this.FindControl<Button>("GlowClear")!;
            _glowColor = this.FindControl<ColorPickerButton>("GlowColor")!;
            _glowTransparency = this.FindControl<Slider>("GlowTransparency")!;
            _glowTransparencyText = this.FindControl<TextBlock>("GlowTransparencyText")!;
            _glowRadius = this.FindControl<Slider>("GlowRadius")!;
            _glowRadiusText = this.FindControl<TextBlock>("GlowRadiusText")!;

            _reflectionOff = this.FindControl<TextBlock>("ReflectionOff")!;
            _reflectionClear = this.FindControl<Button>("ReflectionClear")!;
            _reflectionTransparency = this.FindControl<Slider>("ReflectionTransparency")!;
            _reflectionTransparencyText = this.FindControl<TextBlock>("ReflectionTransparencyText")!;
            _reflectionSize = this.FindControl<Slider>("ReflectionSize")!;
            _reflectionSizeText = this.FindControl<TextBlock>("ReflectionSizeText")!;
            _reflectionDistance = this.FindControl<Slider>("ReflectionDistance")!;
            _reflectionDistanceText = this.FindControl<TextBlock>("ReflectionDistanceText")!;
            _reflectionBlur = this.FindControl<Slider>("ReflectionBlur")!;
            _reflectionBlurText = this.FindControl<TextBlock>("ReflectionBlurText")!;

            _borderOff = this.FindControl<TextBlock>("BorderOff")!;
            _borderClear = this.FindControl<Button>("BorderClear")!;
            _borderColor = this.FindControl<ColorPickerButton>("BorderColor")!;
            _borderAutoColor = this.FindControl<CheckBox>("BorderAutoColor")!;
            _borderWidth = this.FindControl<Slider>("BorderWidth")!;
            _borderWidthText = this.FindControl<TextBlock>("BorderWidthText")!;
            _borderStyle = this.FindControl<ComboBox>("BorderStyle")!;

            foreach (var toggle in new[]
                     {
                         _outlineHollow, _shadowLong, _borderAutoColor
                     })
                toggle.IsCheckedChanged += OnFieldChanged;

            foreach (var slider in new[]
                     {
                         _outlineWidth, _shadowTransparency, _shadowBlur, _shadowDistance, _shadowAngle,
                         _glowTransparency, _glowRadius,
                         _reflectionTransparency, _reflectionSize, _reflectionDistance, _reflectionBlur,
                         _borderWidth
                     })
                slider.ValueChanged += OnFieldChanged;

            foreach (var combo in new[] { _outlineDash, _outlinePlacement, _borderStyle })
                combo.SelectionChanged += OnFieldChanged;

            foreach (var picker in new[] { _outlineColor, _shadowColor, _glowColor, _borderColor })
                picker.PropertyChanged += OnColorPickerChanged;

            _outlineClear.Click += (_, _) => ClearSection(_outlineWidth);
            _shadowClear.Click += (_, _) => ClearSection(_shadowDistance, _shadowBlur);
            _glowClear.Click += (_, _) => ClearSection(_glowRadius);
            _reflectionClear.Click += (_, _) => ClearSection(_reflectionSize);
            _borderClear.Click += (_, _) => ClearSection(_borderWidth);

            this.FindControl<Button>("ApplyBtn")!.Click += OnApply;
            this.FindControl<Button>("CancelBtn")!.Click += OnCancel;
            this.FindControl<Button>("CloseBtn")!.Click += OnCancel;
            this.FindControl<Button>("ResetBtn")!.Click += OnReset;
            _savePresetBtn = this.FindControl<Button>("SavePresetBtn")!;
            _savePresetNote = this.FindControl<TextBlock>("SavePresetNote")!;
            _savePresetBtn.Click += OnSavePreset;
            _scrim.PointerPressed += OnScrimPressed;
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            TopLevel.GetTopLevel(this)?.AddHandler(KeyDownEvent, OnOverlayKeyDown, RoutingStrategies.Tunnel);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            TopLevel.GetTopLevel(this)?.RemoveHandler(KeyDownEvent, OnOverlayKeyDown);
            base.OnDetachedFromVisualTree(e);
        }

        private void OnOverlayKeyDown(object? sender, KeyEventArgs e)
        {
            if (!IsVisible) return;
            if (e.Key == Key.Escape)
            {
                Finish(null);
                e.Handled = true;
            }
        }

        /// <summary>
        /// Открывает окно на эффектах под кареткой. Возвращает настройки по «Применить»
        /// либо null — окно закрыли без применения.
        /// </summary>
        public Task<TextEffectsDialogState?> ShowAsync(TextEffectsDialogState state)
            => ShowAsync(state, null, null);

        /// <summary>
        /// То же, с кнопкой «Сохранить как мой эффект»: <paramref name="suggestPresetName"/>
        /// даёт свободное имя нового набора, <paramref name="savePreset"/> заводит набор.
        /// </summary>
        public Task<TextEffectsDialogState?> ShowAsync(TextEffectsDialogState state,
            Func<string>? suggestPresetName, Action<TextEffectPreset>? savePreset)
        {
            _pending?.TrySetResult(null);
            _pending = new TaskCompletionSource<TextEffectsDialogState?>();
            _source = state;

            _suggestPresetName = suggestPresetName;
            _savePreset = savePreset;
            _savePresetBtn.IsVisible = savePreset is not null;
            _savePresetNote.IsVisible = false;

            _fontFamily = string.IsNullOrWhiteSpace(state.FontFamily) ? "Times New Roman" : state.FontFamily;
            _textColor = SKColor.TryParse(state.TextColor, out var textColor) ? textColor : SKColors.Black;

            Fill(state.Effects, state.Border);

            IsVisible = true;
            UpdatePreview();
            return _pending.Task;
        }

        // ── Заполнение полей ─────────────────────────────────────────────

        /// <summary>
        /// Поля из настроек. У выключенного эффекта главная величина — ноль, остальные
        /// поля показывают вид Word по умолчанию: тронул любое — эффект включился
        /// сразу с этим видом.
        /// </summary>
        private void Fill(TextEffects? effects, CharBorderSettings border)
        {
            _suspend = true;
            try
            {
                var outline = effects?.Outline ?? new TextOutlineEffect { Color = HexOf(_textColor) };
                _outlineHex = NormalizeHex(outline.Color) ?? "#000000";
                _outlineColor.HexColor = _outlineHex;
                _outlineWidth.Value = effects?.Outline is not null ? outline.WidthPt : 0;
                _outlineDash.SelectedIndex = (int)outline.Dash;
                _outlinePlacement.SelectedIndex = (int)outline.Placement;
                _outlineHollow.IsChecked = outline.Hollow;

                var shadow = effects?.Shadow ?? new TextShadowEffect();
                _shadowHex = NormalizeHex(shadow.Color) ?? "#000000";
                _shadowColor.HexColor = _shadowHex;
                _shadowTransparency.Value = Math.Round(shadow.Transparency * 100.0);
                _shadowBlur.Value = effects?.Shadow is not null ? shadow.BlurPt : 0;
                _shadowDistance.Value = effects?.Shadow is not null ? shadow.DistancePt : 0;
                _shadowAngle.Value = ((shadow.AngleDeg % 360.0) + 360.0) % 360.0;
                _shadowLong.IsChecked = shadow.IsLong;

                var glow = effects?.Glow ?? new TextGlowEffect();
                _glowHex = NormalizeHex(glow.Color) ?? "#FFC000";
                _glowColor.HexColor = _glowHex;
                _glowTransparency.Value = Math.Round(glow.Transparency * 100.0);
                _glowRadius.Value = effects?.Glow is not null ? glow.RadiusPt : 0;

                var reflection = effects?.Reflection ?? new TextReflectionEffect();
                _reflectionTransparency.Value = Math.Round(reflection.Transparency * 100.0);
                _reflectionSize.Value = effects?.Reflection is not null ? Math.Round(reflection.Size * 100.0) : 0;
                _reflectionDistance.Value = reflection.DistancePt;
                _reflectionBlur.Value = reflection.BlurPt;

                _borderAutoColor.IsChecked = string.IsNullOrWhiteSpace(border.Color);
                _borderHex = NormalizeHex(border.Color) ?? HexOf(_textColor);
                _borderColor.HexColor = _borderHex;
                _borderWidth.Value = border.Enabled && border.WidthPt > 0 ? border.WidthPt : 0;
                _borderStyle.SelectedIndex = (int)border.Style;
            }
            finally
            {
                _suspend = false;
            }

            RefreshLabels();
        }

        // ── Сборка настроек ──────────────────────────────────────────────

        private TextEffects? BuildEffects()
        {
            var effects = new TextEffects
            {
                Outline = OutlineActive
                    ? new TextOutlineEffect
                    {
                        Color = _outlineHex,
                        WidthPt = _outlineWidth.Value,
                        Dash = (OutlineDash)Math.Max(0, _outlineDash.SelectedIndex),
                        Placement = (OutlinePlacement)Math.Max(0, _outlinePlacement.SelectedIndex),
                        Hollow = _outlineHollow.IsChecked == true
                    }
                    : null,
                Shadow = ShadowActive
                    ? new TextShadowEffect
                    {
                        Color = _shadowHex,
                        Transparency = _shadowTransparency.Value / 100.0,
                        BlurPt = _shadowBlur.Value,
                        DistancePt = _shadowDistance.Value,
                        AngleDeg = _shadowAngle.Value,
                        IsLong = _shadowLong.IsChecked == true
                    }
                    : null,
                Glow = GlowActive
                    ? new TextGlowEffect
                    {
                        Color = _glowHex,
                        Transparency = _glowTransparency.Value / 100.0,
                        RadiusPt = _glowRadius.Value
                    }
                    : null,
                Reflection = ReflectionActive
                    ? new TextReflectionEffect
                    {
                        Transparency = _reflectionTransparency.Value / 100.0,
                        Size = _reflectionSize.Value / 100.0,
                        DistancePt = _reflectionDistance.Value,
                        BlurPt = _reflectionBlur.Value
                    }
                    : null
            };

            return TextEffects.Normalize(effects);
        }

        private CharBorderSettings BuildBorder()
            => new(
                BorderActive,
                _borderAutoColor.IsChecked == true ? null : _borderHex,
                _borderWidth.Value,
                (CharBorderStyle)Math.Max(0, _borderStyle.SelectedIndex));

        // ── События полей ────────────────────────────────────────────────

        private void OnFieldChanged(object? sender, EventArgs e)
        {
            if (_suspend) return;
            if (sender is Control field) ActivateSectionOf(field);
            RefreshLabels();
            UpdatePreview();
        }

        // ── Включение эффектов ───────────────────────────────────────────
        // Эффект есть, пока его главная величина больше нуля. Флажков нет: поля
        // всегда открыты, а выключенный эффект включается правкой любого своего поля.

        private bool OutlineActive => _outlineWidth.Value > 0;
        private bool ShadowActive => _shadowDistance.Value > 0 || _shadowBlur.Value > 0;
        private bool GlowActive => _glowRadius.Value > 0;
        private bool ReflectionActive => _reflectionSize.Value > 0;
        private bool BorderActive => _borderWidth.Value > 0;

        /// <summary>
        /// Правка поля выключенного эффекта (цвет, штрих, угол, прозрачность…) его
        /// включает: главная величина встаёт на вид Word по умолчанию. Иначе выбор
        /// цвета у выключенной тени ничего бы не показал.
        /// </summary>
        private void ActivateSectionOf(Control field)
        {
            _suspend = true;
            try
            {
                if (!OutlineActive && IsOneOf(field, _outlineColor, _outlineDash, _outlinePlacement, _outlineHollow))
                    _outlineWidth.Value = new TextOutlineEffect().WidthPt;

                if (!ShadowActive && IsOneOf(field, _shadowColor, _shadowTransparency, _shadowAngle, _shadowLong))
                {
                    var defaults = new TextShadowEffect();
                    _shadowDistance.Value = defaults.DistancePt;
                    _shadowBlur.Value = defaults.BlurPt;
                }

                if (!GlowActive && IsOneOf(field, _glowColor, _glowTransparency))
                    _glowRadius.Value = new TextGlowEffect().RadiusPt;

                if (!ReflectionActive && IsOneOf(field, _reflectionTransparency, _reflectionDistance, _reflectionBlur))
                    _reflectionSize.Value = Math.Round(new TextReflectionEffect().Size * 100.0);

                if (!BorderActive && IsOneOf(field, _borderColor, _borderAutoColor, _borderStyle))
                    _borderWidth.Value = DefaultBorderWidthPt;
            }
            finally
            {
                _suspend = false;
            }
        }

        /// <summary>«Снять» в заголовке раздела: главная величина эффекта — в ноль.</summary>
        private void ClearSection(params Slider[] strength)
        {
            _suspend = true;
            try
            {
                foreach (var slider in strength) slider.Value = 0;
            }
            finally
            {
                _suspend = false;
            }

            RefreshLabels();
            UpdatePreview();
        }

        private static bool IsOneOf(Control field, params Control[] candidates)
        {
            foreach (var candidate in candidates)
                if (ReferenceEquals(field, candidate)) return true;
            return false;
        }

        // Толщина рамки знаков, с которой она включается, — как у Word по умолчанию.
        private const double DefaultBorderWidthPt = 0.5;

        private void OnColorPickerChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property != ColorPickerButton.HexColorProperty) return;
            if (_suspend || sender is not ColorPickerButton picker) return;

            string? hex = NormalizeHex(picker.HexColor);
            if (hex is null) return;

            if (ReferenceEquals(picker, _outlineColor)) _outlineHex = hex;
            else if (ReferenceEquals(picker, _shadowColor)) _shadowHex = hex;
            else if (ReferenceEquals(picker, _glowColor)) _glowHex = hex;
            else if (ReferenceEquals(picker, _borderColor))
            {
                _borderHex = hex;

                // Выбор своего цвета рамки снимает «как у текста» — иначе выбор бы не сработал.
                _suspend = true;
                _borderAutoColor.IsChecked = false;
                _suspend = false;
            }

            ActivateSectionOf(picker);
            RefreshLabels();
            UpdatePreview();
        }

        /// <summary>
        /// Подписи величин рядом с ползунками и состояние разделов: у выключенного
        /// эффекта в заголовке «нет», у включённого — кнопка «Снять».
        /// </summary>
        private void RefreshLabels()
        {
            SetSectionState(_outlineOff, _outlineClear, OutlineActive);
            SetSectionState(_shadowOff, _shadowClear, ShadowActive);
            SetSectionState(_glowOff, _glowClear, GlowActive);
            SetSectionState(_reflectionOff, _reflectionClear, ReflectionActive);
            SetSectionState(_borderOff, _borderClear, BorderActive);

            _outlineWidthText.Text = Points(_outlineWidth.Value);
            _shadowTransparencyText.Text = PercentText(_shadowTransparency.Value);
            _shadowBlurText.Text = Points(_shadowBlur.Value);
            _shadowDistanceText.Text = Points(_shadowDistance.Value);
            _shadowAngleText.Text = _shadowAngle.Value.ToString("0", CultureInfo.CurrentCulture) + "°";
            _glowTransparencyText.Text = PercentText(_glowTransparency.Value);
            _glowRadiusText.Text = Points(_glowRadius.Value);
            _reflectionTransparencyText.Text = PercentText(_reflectionTransparency.Value);
            _reflectionSizeText.Text = PercentText(_reflectionSize.Value);
            _reflectionDistanceText.Text = Points(_reflectionDistance.Value);
            _reflectionBlurText.Text = Points(_reflectionBlur.Value);
            _borderWidthText.Text = Points(_borderWidth.Value);
        }

        private static void SetSectionState(TextBlock off, Button clear, bool active)
        {
            off.IsVisible = !active;
            clear.IsVisible = active;
        }

        // ── Образец ──────────────────────────────────────────────────────

        /// <summary>
        /// Рисует образец движком документа в картинку и ставит её в окно. Картинка
        /// рисуется в пикселях экрана, чтобы образец был чётким и на экранах с
        /// увеличением.
        /// </summary>
        private void UpdatePreview()
        {
            if (!IsVisible) return;

            double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            int widthPx = Math.Max(1, (int)Math.Round(PreviewWidthDip * scaling));
            int heightPx = Math.Max(1, (int)Math.Round(PreviewHeightDip * scaling));

            var effects = BuildEffects();
            var border = BuildBorder();

            using var bitmap = new SKBitmap(widthPx, heightPx, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.White);

                // Холст — в пунктах, как у документа: 72 пункта на 96 точек интерфейса.
                float pointScale = (float)(scaling * 96.0 / 72.0);
                canvas.Scale(pointScale);

                float widthPt = (float)(PreviewWidthDip * 72.0 / 96.0);
                float heightPt = (float)(PreviewHeightDip * 72.0 / 96.0);

                float textWidth = SKTextRenderer.MeasureEffectsPreview(PreviewText, _fontFamily, PreviewFontSizePt);
                float x = Math.Max(8f, (widthPt - textWidth) / 2f);

                // Базовая линия выше середины: под буквами остаётся место для отражения.
                float baseline = heightPt * 0.55f;

                SKTextRenderer.DrawEffectsPreview(
                    canvas, PreviewText, _fontFamily, PreviewFontSizePt, _textColor,
                    effects,
                    border.Enabled ? (float)border.WidthPt : 0f,
                    border.Color,
                    border.Style,
                    x, baseline);

                canvas.Flush();
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = data.AsStream();

            var previous = _preview.Source as IDisposable;
            _preview.Source = new Bitmap(stream);
            previous?.Dispose();
        }

        // ── Кнопки ───────────────────────────────────────────────────────

        private void OnApply(object? sender, RoutedEventArgs e)
        {
            var source = _source;
            if (source is null)
            {
                Finish(null);
                return;
            }

            Finish(source with { Effects = BuildEffects(), Border = BuildBorder() });
        }

        private void OnCancel(object? sender, RoutedEventArgs e) => Finish(null);

        /// <summary>
        /// Заводит из собранного вида набор «Мои эффекты». Окно, эффекты текста и
        /// остальные поля остаются как были: сохранить — не значит применить.
        ///
        /// Окно отвечает за контур, тень, свечение, отражение и рамку знаков. Старые
        /// включатели контура и тени оно при применении снимает — набор снимает их
        /// так же; рельеф и гравировку снимает только вместе с настроенным контуром
        /// или тенью, как и само окно. Знака ударения в окне нет, и набор его не трогает.
        /// </summary>
        private async void OnSavePreset(object? sender, RoutedEventArgs e)
        {
            if (_savePreset is null) return;

            var effects = BuildEffects();
            var border = BuildBorder();
            bool clearsRelief = effects?.Outline is not null || effects?.Shadow is not null;

            var preset = new TextEffectPreset
            {
                Effects = effects,
                Border = border.Enabled ? border : null,
                IsOutline = false,
                IsShadow = false,
                IsEmboss = clearsRelief ? false : null,
                IsImprint = clearsRelief ? false : null,
                EmphasisMark = null
            };

            if (preset.IsBlank)
            {
                ShowSavePresetNote("Нечего сохранять: включите хотя бы один эффект");
                return;
            }

            if (TopLevel.GetTopLevel(this) is not Window owner) return;

            string suggested = _suggestPresetName?.Invoke() ?? TextEffectPreset.DefaultName;
            var dialog = new InputDialog("Мой эффект", "Название набора", suggested);
            string? name = await dialog.ShowDialog<string?>(owner);
            if (string.IsNullOrWhiteSpace(name)) return;

            // Окно могли закрыть, пока спрашивалось имя.
            if (_savePreset is null) return;

            _savePreset(preset with { Name = name.Trim() });
            ShowSavePresetNote("Сохранено: «" + name.Trim() + "»");
        }

        private void ShowSavePresetNote(string text)
        {
            _savePresetNote.Text = text;
            _savePresetNote.IsVisible = true;
        }

        private void OnScrimPressed(object? sender, PointerPressedEventArgs e) => Finish(null);

        /// <summary>Снимает все эффекты: главные величины — в ноль, остальные поля остаются под рукой.</summary>
        private void OnReset(object? sender, RoutedEventArgs e)
        {
            _suspend = true;
            try
            {
                _outlineWidth.Value = 0;
                _shadowDistance.Value = 0;
                _shadowBlur.Value = 0;
                _glowRadius.Value = 0;
                _reflectionSize.Value = 0;
                _borderWidth.Value = 0;
            }
            finally
            {
                _suspend = false;
            }

            RefreshLabels();
            UpdatePreview();
        }

        private void Finish(TextEffectsDialogState? result)
        {
            IsVisible = false;

            var previous = _preview.Source as IDisposable;
            _preview.Source = null;
            previous?.Dispose();

            var pending = _pending;
            _pending = null;
            _source = null;
            _suggestPresetName = null;
            _savePreset = null;
            pending?.TrySetResult(result);
        }

        // ── Мелочи ───────────────────────────────────────────────────────

        private static string Points(double value)
            => value.ToString("0.##", CultureInfo.CurrentCulture) + " пт";

        private static string PercentText(double value)
            => value.ToString("0", CultureInfo.CurrentCulture) + " %";

        private static string HexOf(SKColor color)
            => string.Create(CultureInfo.InvariantCulture, $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}");

        /// <summary>
        /// Сплошной цвет #RRGGBB из кода кнопки цвета. Градиент и «нет цвета» — null:
        /// эффект рисуется одним цветом, и такой выбор пропускается.
        /// </summary>
        private static string? NormalizeHex(string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;

            string value = code.Trim();
            if (!value.StartsWith("#", StringComparison.Ordinal)) value = "#" + value;
            if (!SKColor.TryParse(value, out var color)) return null;
            if (value.Length == 9 && color.Alpha == 0) return null;

            return HexOf(color);
        }
    }
}
